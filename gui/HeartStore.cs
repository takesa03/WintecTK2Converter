using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tk2Gui
{
    // =====================================================================
    //  心拍 (腕時計などの FIT ファイルから) を、ログごとに保存する (ログ名.heart.json)
    //  FIT 1 つにつき 1 項目。心拍の列は FIT の時刻のまま持ち、ログの時刻 = FIT の時刻 + offset。
    //  offset は「走行動画の時刻合わせ」の画面で、FIT の速度とログの速度を比べて自動で求め、手で直せる。
    //  どのセッションで合わせたかを持ち、表示のときはそのセッションの項目を優先する
    //  (ドライバーごとに別の腕時計の FIT があるとき用)。無ければ時刻の重なる項目を使う
    // =====================================================================
    public static class HeartStore
    {
        public sealed class Entry
        {
            [JsonPropertyName("fit")] public string Fit { get; set; }             // 元の FIT ファイル
            [JsonPropertyName("device")] public string Device { get; set; }       // 機器の名前 (Polar Ignite など)
            [JsonPropertyName("session")] public int Session { get; set; }        // 合わせたセッションの番号
            [JsonPropertyName("offset")] public double Offset { get; set; }       // ログの時刻 = FIT の時刻 + offset [s]
            [JsonPropertyName("auto")] public double? AutoOffset { get; set; }    // 速度から自動で求めた offset (参考)
            [JsonPropertyName("candidates"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public List<Candidate> Candidates { get; set; }                       // 自動の候補 (相関の高い順)
            [JsonPropertyName("t")] public double[] T { get; set; }               // FIT の時刻 (2000-01-01 UTC からの秒)
            [JsonPropertyName("bpm")] public double[] Bpm { get; set; }           // 心拍 [bpm]

            [JsonIgnore] public double From { get { return T != null && T.Length > 0 ? T[0] + Offset : double.NaN; } }
            [JsonIgnore] public double To { get { return T != null && T.Length > 0 ? T[T.Length - 1] + Offset : double.NaN; } }
        }

        sealed class FileData
        {
            [JsonPropertyName("format")] public string Format { get; set; } = "WintecTK2Heart";
            [JsonPropertyName("version")] public int Version { get; set; } = 1;
            [JsonPropertyName("log")] public string Log { get; set; }
            [JsonPropertyName("entries")] public List<Entry> Entries { get; set; } = new List<Entry>();
        }

        static readonly JsonSerializerOptions Opt = new JsonSerializerOptions
        {
            WriteIndented = false,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        public static string SidecarPath(string log) { return log + ".heart.json"; }

        public static List<Entry> Load(string log)
        {
            try
            {
                string p = SidecarPath(log);
                if (string.IsNullOrEmpty(log) || !File.Exists(p)) return new List<Entry>();
                FileData d = JsonSerializer.Deserialize<FileData>(File.ReadAllText(p, Encoding.UTF8), Opt);
                if (d == null || d.Format != "WintecTK2Heart" || d.Entries == null) return new List<Entry>();
                return d.Entries.Where(e => e.T != null && e.Bpm != null && e.T.Length == e.Bpm.Length && e.T.Length > 0).ToList();
            }
            catch (Exception)
            {
                return new List<Entry>();
            }
        }

        public static void Save(string log, List<Entry> list)
        {
            string p = SidecarPath(log);
            if (list.Count == 0) { if (File.Exists(p)) File.Delete(p); return; }
            FileData d = new FileData { Log = Path.GetFileName(log), Entries = list };
            File.WriteAllText(p, JsonSerializer.Serialize(d, Opt), new UTF8Encoding(false));
        }

        /// <summary>そのセッションの項目を置き換える</summary>
        public static void Put(string log, Entry e)
        {
            List<Entry> list = Load(log);
            list.RemoveAll(x => x.Session == e.Session);
            list.Add(e);
            Save(log, list);
        }

        /// <summary>そのセッションの項目を消す (無くなればファイルも消す)</summary>
        public static void Remove(string log, int session)
        {
            List<Entry> list = Load(log);
            if (list.RemoveAll(x => x.Session == session) > 0) Save(log, list);
        }

        public static Entry Get(string log, int session)
        {
            return Load(log).FirstOrDefault(x => x.Session == session);
        }

        /// <summary>FIT の record から項目を作る (心拍のある点だけ)</summary>
        public static Entry FromFit(FitFile fit, int session, double offset)
        {
            List<FitRecord> rs = fit.Records.Where(r => !double.IsNaN(r.HeartRate) && r.HeartRate > 0).OrderBy(r => r.T).ToList();
            return new Entry
            {
                Fit = fit.Path, Device = fit.DeviceText, Session = session, Offset = offset,
                T = rs.Select(r => r.T).ToArray(), Bpm = rs.Select(r => r.HeartRate).ToArray()
            };
        }

        /// <summary>自動の時刻合わせの候補</summary>
        public sealed class Candidate
        {
            [JsonPropertyName("offset")] public double Offset { get; set; }   // ログの時刻 = FIT の時刻 + offset
            [JsonPropertyName("corr")] public double Corr { get; set; }       // 速度の相関 (-1〜1)
            [JsonPropertyName("agree")] public int Agree { get; set; }        // 走った所を区切って合わせ直したとき、±8 秒で揃った区間の数 (形が合わない区間は数えない)
            [JsonPropertyName("windows")] public int Windows { get; set; }    // 区間の数
        }

        /// <summary>
        /// FIT とログの時計のずれを、速度の形を比べて求める (ログの時刻 = FIT の時刻 + offset)。
        /// 腕時計の時計は日付ごと狂っていることがある (Polar はスマホとの同期で時計を合わせ、GPS では合わせない) ので、
        /// FIT の時刻は信用せず、FIT がログの [logFrom, logTo] に 半分以上重なる、すべてのずれを 1 秒刻みで調べる。
        /// 腕時計の GPS は速度の大きさが合わない (鈍る) ので、差ではなく相関 (形が合うか) で比べる。
        /// 周回ごとに似た形が繰り返すため 1 周ずれた所も高くなる: 山を相関の高い順に最大 3 つ返す (20 秒以上離れたもの)。
        /// 比べるのは FIT のうちカートに乗っていた所だけ (DrivingPart)。
        /// それぞれ 0.1 秒刻みで詰め、数分ごとの区間で合わせ直して揃うか (確かさの目安) を数える。
        /// logSpeed: ログの速度 [km/h] (記録の無い所は 0)。比べられないときは空。
        /// sessFrom / sessTo (セッションの範囲) を渡すと、FIT の走った所がこのセッションと半分以上 (短い方の) 重なるずれだけを候補にする。
        /// 腕時計を着けた人はこのセッションを走ったはずなので、ほかのセッション・別のカート (ログに無い走行) の形に合わせない
        /// </summary>
        public static List<Candidate> EstimateOffsets(FitFile fit, Func<double, double> logSpeed, double logFrom, double logTo,
            double sessFrom = double.NaN, double sessTo = double.NaN)
        {
            List<Candidate> res = new List<Candidate>();
            List<FitRecord> rs = DrivingPart(fit.Records.Where(r => !double.IsNaN(r.SpeedKmh)).OrderBy(r => r.T).ToList());
            if (rs.Count < 60 || !(logTo - logFrom > 60)) return res;
            // ログの速度を 1 秒ごとに
            double L0 = Math.Floor(logFrom);
            int nL = (int)Math.Ceiling(logTo - L0) + 1;
            double[] lv = new double[nL];
            for (int i = 0; i < nL; i++) lv[i] = logSpeed(L0 + i);
            double F0 = rs[0].T, F1 = rs[rs.Count - 1].T;
            int[] fi = rs.Select(r => (int)Math.Round(r.T - F0)).ToArray();
            double[] fv = rs.Select(r => r.SpeedKmh).ToArray();
            int need = Math.Max(60, Math.Min(rs.Count, nL) / 2);       // FIT とログの範囲の短い方の半分以上が重なること

            // o = FIT の先頭がログの L0 + o 秒
            int oMin = -(int)Math.Ceiling(F1 - F0), oMax = nL;
            double[] score = new double[oMax - oMin + 1];
            bool sess = !double.IsNaN(sessFrom) && sessTo > sessFrom;
            double sessNeed = sess ? Math.Min(F1 - F0, sessTo - sessFrom) / 2 : 0;
            for (int o = oMin; o <= oMax; o++)
            {
                if (sess)
                {
                    double a = L0 + o, b = a + (F1 - F0);                      // FIT の走った所 (ログの時刻)
                    if (Math.Min(b, sessTo) - Math.Max(a, sessFrom) < sessNeed) { score[o - oMin] = double.NaN; continue; }
                }
                int n;
                double c = Corr(fi, fv, 0, fi.Length, lv, o, out n);
                score[o - oMin] = n >= need ? c : double.NaN;
            }
            // 山 (前後 20 秒で最大) を高い順に
            List<int> peaks = new List<int>();
            for (int k = 0; k < score.Length; k++)
            {
                if (double.IsNaN(score[k])) continue;
                bool top = true;
                for (int j = Math.Max(0, k - 20); j <= Math.Min(score.Length - 1, k + 20) && top; j++)
                    if (j != k && !double.IsNaN(score[j]) && (score[j] > score[k] || (score[j] == score[k] && j < k))) top = false;
                if (top) peaks.Add(k);
            }
            foreach (int k in peaks.OrderByDescending(k => score[k]).Take(3))
            {
                int o = k + oMin;
                double baseOff = L0 + o - F0;              // ログの時刻 = FIT の時刻 + baseOff
                // 0.1 秒刻み (ログの速度は補間)
                double best = baseOff, bestC = score[k];
                for (double d = -1; d <= 1.0001; d += 0.1)
                {
                    double c = CorrFine(rs, logSpeed, baseOff + d, logFrom, logTo);
                    if (c > bestC) { bestC = c; best = baseOff + d; }
                }
                // 区間 (5 分。走った所が短いときは 1/4 ずつ、2 分まで) ごとに、±30 秒の中のいちばん合う所が ±8 秒に揃うか。
                // 形が合わない区間 (いちばん合う所でも相関 0.5 未満。ピットにいる間など) は数えない。
                // 腕時計の GPS の遅れは区間ごとに数秒変わる (11/07 の FIT で −7〜+8 秒)。1 周 (約 40 秒) ずれた候補は ±30 秒の端に寄るので揃わない
                int win = 0, agree = 0;
                int len = (int)Math.Max(120, Math.Min(300, (fi[fi.Length - 1] - fi[0]) / 4.0));
                for (int s = 0; s < fi.Length; )
                {
                    int e = s;
                    while (e < fi.Length && fi[e] < fi[s] + len) e++;
                    if (e - s >= len * 0.4)
                    {
                        int bo = 0; double bc = double.NegativeInfinity; bool any = false;
                        for (int d = -30; d <= 30; d++)
                        {
                            int n; double c = Corr(fi, fv, s, e, lv, o + d, out n);
                            if (n >= (e - s) / 2 && c > bc) { bc = c; bo = d; any = true; }
                        }
                        if (any && bc >= 0.5) { win++; if (Math.Abs(bo) <= 8) agree++; }
                    }
                    s = e;
                }
                res.Add(new Candidate { Offset = Math.Round(best, 1), Corr = Math.Round(bestC, 3), Agree = agree, Windows = win });
            }
            return res;
        }

        /// <summary>
        /// FIT のうち、カートに乗っていた所だけを返す: 10 秒の平均が 15 km/h 以上の所 (90 秒未満の途切れはつなぐ) のうち、
        /// 1 分以上続いたものの最初から最後まで (前後 30 秒を足す)。
        /// 腕時計は人に、ロガーはカートに付いているので、同じ動きをするのは乗っている間だけ。
        /// 乗る前・降りた後に立っている間 (速度 0) も比べると、そのあいだカートを走らせているほかのドライバーの走行と食い違い、
        /// 正しいずれの所の相関が下がる (11/07 の竹佐さん 最終枠.FIT は 24 分のうち乗っていたのは 8 分で、1 周以上ずれた所を選んでいた)。
        /// 1 分未満の動き (乗る前の短い移動など) は数えない。走った所が無い・短すぎるときは全部を返す
        /// </summary>
        static List<FitRecord> DrivingPart(List<FitRecord> rs)
        {
            const double Kmh = 15, Avg = 10, Pad = 30, Join = 90, MinLen = 60;
            double first = double.NaN, last = double.NaN, s0 = double.NaN, s1 = double.NaN;
            int a = 0;
            double sum = 0;
            for (int i = 0; i <= rs.Count; i++)
            {
                bool moving = false;
                if (i < rs.Count)
                {
                    sum += rs[i].SpeedKmh;
                    while (rs[i].T - rs[a].T > Avg) sum -= rs[a++].SpeedKmh;
                    moving = sum / (i - a + 1) >= Kmh;
                }
                // 走っている所のひと続き [s0, s1]。途切れが Join を超えたら閉じる
                if (i == rs.Count || (moving && !double.IsNaN(s1) && rs[i].T - s1 > Join))
                {
                    if (!double.IsNaN(s0) && s1 - s0 >= MinLen) { if (double.IsNaN(first)) first = s0; last = s1; }
                    s0 = s1 = double.NaN;
                }
                if (moving) { if (double.IsNaN(s0)) s0 = rs[a].T; s1 = rs[i].T; }
            }
            if (double.IsNaN(first)) return rs;
            double t0 = first - Pad, t1 = last + Pad;
            List<FitRecord> part = rs.Where(r => r.T >= t0 && r.T <= t1).ToList();
            return part.Count >= 60 ? part : rs;
        }

        // FIT の [s, e) と、ログの 1 秒ごとの速度 (FIT の先頭 = ログの添字 o) の相関
        static double Corr(int[] fi, double[] fv, int s, int e, double[] lv, int o, out int n)
        {
            double sx = 0, sy = 0, sxx = 0, syy = 0, sxy = 0;
            n = 0;
            for (int k = s; k < e; k++)
            {
                int i = o + fi[k];
                if (i < 0 || i >= lv.Length) continue;
                double x = fv[k], y = lv[i];
                sx += x; sy += y; sxx += x * x; syy += y * y; sxy += x * y; n++;
            }
            if (n < 2) return double.NaN;
            double v = (n * sxx - sx * sx) * (n * syy - sy * sy);
            return v > 0 ? (n * sxy - sx * sy) / Math.Sqrt(v) : double.NaN;
        }

        static double CorrFine(List<FitRecord> rs, Func<double, double> logSpeed, double off, double from, double to)
        {
            double sx = 0, sy = 0, sxx = 0, syy = 0, sxy = 0;
            int n = 0;
            foreach (FitRecord r in rs)
            {
                double t = r.T + off;
                if (t < from || t > to) continue;
                double x = r.SpeedKmh, y = logSpeed(t);
                sx += x; sy += y; sxx += x * x; syy += y * y; sxy += x * y; n++;
            }
            double v = (n * sxx - sx * sx) * (n * syy - sy * sy);
            return n >= 2 && v > 0 ? (n * sxy - sx * sy) / Math.Sqrt(v) : double.NegativeInfinity;
        }

        /// <summary>候補の説明 (画面用)。ログの時刻で FIT の先頭がいつになるか</summary>
        public static string CandidateText(Candidate c, double fitStartT)
        {
            return string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:MM/dd HH:mm:ss} 開始  相関 {1:0.00}  揃い {2}/{3}",
                new DateTime(2000, 1, 1).AddSeconds(fitStartT + c.Offset).AddHours(9), c.Corr, c.Agree, c.Windows);
        }
    }
    /// <summary>
    /// 表示用の心拍: 時刻 t の心拍と、鼓動の位相 (拍の直後 0 → 次の拍の直前 1)。
    /// 位相は心拍を時間で積分した拍数の端数 (心拍が上がれば明滅も速くなる)
    /// </summary>
    public sealed class HeartTrack
    {
        readonly double[] _t, _bpm, _beats;
        const double MaxGap = 10;      // 記録の間がこれより空いたら、その間は表示しない [s]

        public readonly string Device;

        public HeartTrack(HeartStore.Entry e)
        {
            int n = e.T.Length;
            _t = new double[n]; _bpm = new double[n]; _beats = new double[n];
            for (int i = 0; i < n; i++) { _t[i] = e.T[i] + e.Offset; _bpm[i] = e.Bpm[i]; }
            for (int i = 1; i < n; i++)
            {
                double dt = _t[i] - _t[i - 1];
                _beats[i] = _beats[i - 1] + (dt > MaxGap ? 0 : (_bpm[i] + _bpm[i - 1]) / 2 / 60 * dt);
            }
            Device = e.Device ?? "";
        }

        public double From { get { return _t[0]; } }
        public double To { get { return _t[_t.Length - 1]; } }

        /// <summary>t の心拍 (無ければ false)。phase = 鼓動の位相 0〜1</summary>
        public bool At(double t, out double bpm, out double phase)
        {
            bpm = double.NaN; phase = 0;
            int n = _t.Length;
            if (n == 0 || t < _t[0] - 1 || t > _t[n - 1] + 1) return false;
            if (t <= _t[0]) { bpm = _bpm[0]; phase = Frac(_beats[0] - (_t[0] - t) * bpm / 60); return true; }
            if (t >= _t[n - 1]) { bpm = _bpm[n - 1]; phase = Frac(_beats[n - 1] + (t - _t[n - 1]) * bpm / 60); return true; }
            int lo = 0, hi = n - 1;
            while (hi - lo > 1) { int m = (lo + hi) / 2; if (_t[m] <= t) lo = m; else hi = m; }
            double dt = _t[hi] - _t[lo];
            if (dt > MaxGap) return false;
            double f = dt > 0 ? (t - _t[lo]) / dt : 0;
            bpm = _bpm[lo] + (_bpm[hi] - _bpm[lo]) * f;
            phase = Frac(_beats[lo] + (_beats[hi] - _beats[lo]) * f);
            return true;
        }

        static double Frac(double x) { return x - Math.Floor(x); }

        /// <summary>
        /// そのセッションで使う心拍 (そのセッションで合わせた項目だけ。無ければ null = 表示しない)。
        /// ほかのセッションの FIT は使わない (腕時計は交代のときに次のドライバーへ渡すので、時刻が重なっても別の人の心拍のことがある)
        /// </summary>
        public static HeartTrack For(string log, int session)
        {
            HeartStore.Entry e = HeartStore.Get(log, session);
            return e != null ? new HeartTrack(e) : null;
        }
    }
}
