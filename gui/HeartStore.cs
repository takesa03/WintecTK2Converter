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
    //  心拍 (腕時計などの FIT ファイルから) を、ログごとに保存する (ログの付属情報 ログ名.log.json の heart)
    //  FIT 1 つにつき 1 項目。ログの時刻 = FIT の時刻 + offset。
    //  offset は「走行動画の時刻合わせ」の画面で、FIT の速度とログの速度を比べて自動で求め、手で直せる。
    //  心拍の記録は写さず、毎回 FIT から読む。そのため FIT はログの横の heart\ にコピーしてから登録する (元の FIT は動かさない)。
    //  どのセッションで合わせたかは、セッションの時刻の範囲で持つ (番号はログの組み合わせや条件で変わるので使わない)。
    //  表示はそのセッションで合わせた項目だけ (ドライバーごとに別の腕時計の FIT があるとき、ほかの人の心拍を出さない)
    // =====================================================================
    public static class HeartStore
    {
        public const string Folder = "heart";

        public sealed class Entry
        {
            [JsonPropertyName("fit")] public string Fit { get; set; }             // FIT ファイル (ログの横の heart\ の中。保存は相対パス)
            [JsonPropertyName("device")] public string Device { get; set; }       // 機器の名前 (Polar Ignite など)
            [JsonPropertyName("startT")] public double StartT { get; set; }       // 合わせたセッションの範囲 (ログの通算秒)
            [JsonPropertyName("endT")] public double EndT { get; set; }
            [JsonPropertyName("offset")] public double Offset { get; set; }       // ログの時刻 = FIT の時刻 + offset [s]
            [JsonPropertyName("auto")] public double? AutoOffset { get; set; }    // 速度から自動で求めた offset (参考)
            [JsonPropertyName("candidates"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public List<Candidate> Candidates { get; set; }                       // 自動の候補 (相関の高い順)

            [JsonIgnore] public double[] T;                                       // FIT の時刻 (2000-01-01 UTC からの秒)。FIT から読む
            [JsonIgnore] public double[] Bpm;                                     // 心拍 [bpm]。FIT から読む

            [JsonIgnore] public double From { get { return T != null && T.Length > 0 ? T[0] + Offset : double.NaN; } }
            [JsonIgnore] public double To { get { return T != null && T.Length > 0 ? T[T.Length - 1] + Offset : double.NaN; } }
        }

        /// <summary>保存先 (画面の案内用)</summary>
        public static string SidecarPath(string log) { return LogFile.PathOf(log); }

        /// <summary>
        /// 項目がこのセッション [s0, s1] のものか: 重なりが、短い方の長さの半分以上。
        /// セッションの分け方が変わっても (分割・結合・停止の秒数)、同じ所を走ったセッションに付いていく
        /// </summary>
        static double Match(Entry e, double s0, double s1)
        {
            double ov = Math.Min(e.EndT, s1) - Math.Max(e.StartT, s0);
            double shorter = Math.Min(e.EndT - e.StartT, s1 - s0);
            if (shorter <= 0) return e.StartT >= s0 - 1 && e.StartT <= s1 + 1 && e.EndT >= s0 - 1 && e.EndT <= s1 + 1 ? 1e-6 : -1;
            return ov > 0 && ov >= shorter * 0.5 ? ov : -1;
        }

        static Entry Pick(List<Entry> list, double s0, double s1)
        {
            if (list == null) return null;
            Entry best = null; double bestOv = -1;
            foreach (Entry e in list) { double m = Match(e, s0, s1); if (m >= 0 && m > bestOv) { bestOv = m; best = e; } }
            return best;
        }

        /// <summary>そのセッションの心拍 (FIT を読んで T・Bpm を入れて返す)。無い・FIT を読めなければ null</summary>
        public static Entry Get(string log, SessionRecord sess)
        {
            if (string.IsNullOrEmpty(log) || sess == null) return null;
            Entry e = Pick(LogFile.Load(log).Heart, sess.StartT, sess.EndT);
            return e != null && Fill(e) ? e : null;
        }

        /// <summary>そのセッションの項目を置き換える (FIT は heart\ にコピーしてから)。書けなければ例外</summary>
        public static void Put(string log, Entry e)
        {
            e.Fit = CopyIn(log, e.Fit);
            LogFile.Update(log, d =>
            {
                List<Entry> list = d.Heart ?? new List<Entry>();
                list.RemoveAll(x => Match(x, e.StartT, e.EndT) >= 0);
                list.Add(e);
                d.Heart = list;
            });
        }

        /// <summary>そのセッションの項目を外す (FIT のコピーは消さない)</summary>
        public static void Remove(string log, SessionRecord sess)
        {
            LogFile.Update(log, d =>
            {
                if (d.Heart == null) return;
                Entry e = Pick(d.Heart, sess.StartT, sess.EndT);
                if (e != null) d.Heart.Remove(e);
                if (d.Heart.Count == 0) d.Heart = null;
            });
        }

        /// <summary>FIT の record から項目を作る (心拍のある点だけ)</summary>
        public static Entry FromFit(FitFile fit, SessionRecord sess, double offset)
        {
            double[] t, bpm;
            Beats(fit, out t, out bpm);
            return new Entry { Fit = fit.Path, Device = fit.DeviceText, StartT = sess.StartT, EndT = sess.EndT, Offset = offset, T = t, Bpm = bpm };
        }

        static void Beats(FitFile fit, out double[] t, out double[] bpm)
        {
            List<FitRecord> rs = fit.Records.Where(r => !double.IsNaN(r.HeartRate) && r.HeartRate > 0).OrderBy(r => r.T).ToList();
            t = rs.Select(r => r.T).ToArray();
            bpm = rs.Select(r => r.HeartRate).ToArray();
        }

        // 読んだ FIT の心拍 (パス → 更新日時・大きさ・列)。表示のたびに読み直さない
        sealed class Cached { public DateTime Time; public long Length; public double[] T, Bpm; public string Device; }
        static readonly Dictionary<string, Cached> Cache = new Dictionary<string, Cached>(StringComparer.OrdinalIgnoreCase);

        /// <summary>FIT を読んで、心拍の列を入れる。読めなければ false</summary>
        static bool Fill(Entry e)
        {
            if (string.IsNullOrEmpty(e.Fit)) return false;
            try
            {
                FileInfo fi = new FileInfo(e.Fit);
                if (!fi.Exists) return false;
                Cached c;
                lock (Cache)
                {
                    if (!Cache.TryGetValue(fi.FullName, out c) || c.Time != fi.LastWriteTimeUtc || c.Length != fi.Length) c = null;
                }
                if (c == null)
                {
                    FitFile fit = FitReader.Load(fi.FullName);
                    c = new Cached { Time = fi.LastWriteTimeUtc, Length = fi.Length, Device = fit.DeviceText };
                    Beats(fit, out c.T, out c.Bpm);
                    lock (Cache) Cache[fi.FullName] = c;
                }
                if (c.T.Length == 0) return false;
                e.T = c.T; e.Bpm = c.Bpm;
                if (string.IsNullOrEmpty(e.Device)) e.Device = c.Device;
                return true;
            }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// FIT をログの横の heart\ にコピーして、コピーのパスを返す。もう heart\ にあればそのまま。
        /// 同じ名前で中身の違うファイルがあれば「名前 (2).FIT」のように名前を変える。同じ中身ならそれを使う
        /// </summary>
        public static string CopyIn(string log, string src)
        {
            string dir = Path.Combine(LogFile.DirOf(log), Folder);
            string full = Path.GetFullPath(src);
            if (string.Equals(Path.GetDirectoryName(full), dir, StringComparison.OrdinalIgnoreCase)) return full;
            Directory.CreateDirectory(dir);
            string name = Path.GetFileName(full);
            string dst = Path.Combine(dir, name);
            for (int i = 2; File.Exists(dst); i++)
            {
                if (SameContent(full, dst)) return dst;
                dst = Path.Combine(dir, Path.GetFileNameWithoutExtension(name) + " (" + i + ")" + Path.GetExtension(name));
            }
            File.Copy(full, dst);
            return dst;
        }

        /// <summary>
        /// 前の版の取り込み用: FIT のうちカートに乗っていた所 (DrivingPart) の範囲をログの時刻で。
        /// 速度が無ければ心拍の記録の範囲。どちらも無ければ false
        /// </summary>
        internal static bool DrivingRange(string fitPath, double offset, out double s0, out double s1)
        {
            s0 = s1 = double.NaN;
            FitFile fit = FitReader.Load(fitPath);
            List<FitRecord> rs = DrivingPart(fit.Records.Where(r => !double.IsNaN(r.SpeedKmh)).OrderBy(r => r.T).ToList());
            if (rs.Count < 2)
            {
                double[] t, bpm;
                Beats(fit, out t, out bpm);
                if (t.Length < 2) return false;
                s0 = t[0] + offset; s1 = t[t.Length - 1] + offset;
                return true;
            }
            s0 = rs[0].T + offset; s1 = rs[rs.Count - 1].T + offset;
            return true;
        }

        static bool SameContent(string a, string b)
        {
            if (new FileInfo(a).Length != new FileInfo(b).Length) return false;
            return File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
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
        public static HeartTrack For(string log, SessionRecord sess)
        {
            HeartStore.Entry e = HeartStore.Get(log, sess);
            return e != null ? new HeartTrack(e) : null;
        }
    }
}
