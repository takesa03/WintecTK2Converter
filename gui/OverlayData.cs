using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace Tk2Gui
{
    // =====================================================================
    //  動画素材の 1 フレーム分のデータ (OverlayState) を、ログとラップ解析から作る。
    //  - 位置・速度・高度は点の間を直線で補間。記録の途切れ (MaxGapSec 超) の間は前の位置のまま、速度 0
    //  - G は加速度センサーが無いので、速度の変化 (前後 G) と、速度 × 向きの変化 (横 G) から求める
    //  - ラップ・セクターの差分はリアルタイム: 今の経過時間 − 基準の周回が同じ進み具合 (走った距離の割合) に来た時刻。
    //    基準は、その時点までに走り終えた周回のセッション内ベスト
    // =====================================================================

    public sealed class OverlayOptions
    {
        public double LapHoldSec = 3;       // ラインを通ったあと、その周回のタイムと差分を表示しておく秒数
        public double SectorHoldSec = 3;
        public double GSmoothSec = 1.0;     // G を求めるときの時間幅 [s] (大きいほど滑らか)
        public double SpeedSmoothSec = 2.0; // 速度メーターをならす時間幅 [s] (0 で折れ線補間のまま)
        public double JstOffsetHours = 9;   // 文字データの日時 (ログは UTC)
        public double MaxGapSec = 3;        // これを超えて途切れた点の間は補間しない (停止扱い)
    }

    public sealed class LapLine
    {
        public string Label, Text;
        public bool Best;
    }

    public sealed class SectorLine
    {
        public string Label;
        public string[] Text;
        public bool[] Best;
    }

    public sealed class OverlayState
    {
        public double T;                     // ログの通算秒
        public double Elapsed;               // 書き出し範囲の先頭からの秒
        public DateTime Local;               // 日本時間
        public double SpeedKmh, Gx, Gy;      // Gx = 横 (右が正), Gy = 前後 (加速が正)
        public double Lat, Lon, Alt, X, Y;   // X, Y = コース定義の中心を原点にした東・北 [m]
        public double DistM;                 // 書き出し範囲の先頭から走った距離
        public Point[] GTrail;               // 過去の G (古い順。最後が今)

        public int SessionNo;
        public string Driver = "";

        public string LapTime;               // 走行中は経過時間、ラインを通った直後はその周回のタイム
        public bool LapHolding;              // ラインを通った直後 (タイムを確定表示中)
        public double? LapDelta;
        public string LapBest;
        public List<LapLine> LapHistory = new List<LapLine>();   // 古い順

        public string[] SecNames;
        public string[] SecCur;              // 今の周回の区間タイム (走行中の区間は経過時間, まだの区間は空)
        public bool[] SecRunning;
        public double?[] SecDelta;
        public string[] SecBest;
        public List<SectorLine> SecHistory = new List<SectorLine>();   // 古い順
    }

    public sealed class OverlayData
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        const double G0 = 9.80665;

        public readonly CourseDefinition Def;
        public readonly SessionRecord Sess;
        public readonly string[] SectorNames;
        public readonly OverlayOptions Opt;
        public readonly double Lat0, Lon0, Kx, Ky;
        public readonly double StartT, EndT;           // 書き出す範囲 (ログの通算秒)
        public readonly double MaxSpeedKmh;            // セッション内の最高速 (速度計の目盛りの最大値を決める)

        readonly List<GpsSample> _gps;
        readonly double[] _t, _x, _y, _v, _alt, _lat, _lon, _cum;
        readonly List<LapRecord> _laps;
        readonly Dictionary<string, ReplayLap> _ref = new Dictionary<string, ReplayLap>();

        public OverlayData(CourseDefinition def, SessionRecord sess, string[] sectorNames, List<GpsSample> gps, OverlayOptions opt,
                           double beforeSec, double afterSec)
        {
            Def = def; Sess = sess; SectorNames = sectorNames; Opt = opt ?? new OverlayOptions(); _gps = gps;
            Lat0 = def.Lat0; Lon0 = def.Lon0;
            Ky = 111132.0; Kx = 111320.0 * Math.Cos(Lat0 * Math.PI / 180);
            int n = gps.Count;
            _t = new double[n]; _x = new double[n]; _y = new double[n]; _v = new double[n];
            _alt = new double[n]; _lat = new double[n]; _lon = new double[n]; _cum = new double[n];
            for (int i = 0; i < n; i++)
            {
                GpsSample g = gps[i];
                _t[i] = g.T; _lat[i] = g.Lat; _lon[i] = g.Lon; _alt[i] = g.Alt;
                _x[i] = (g.Lon - Lon0) * Kx; _y[i] = (g.Lat - Lat0) * Ky;
            }
            for (int i = 0; i < n; i++)
            {
                if (!double.IsNaN(gps[i].SpeedKmh)) { _v[i] = gps[i].SpeedKmh; continue; }
                // ロガーの速度が無ければ、前後の点の距離から
                int a = Math.Max(0, i - 1), b = Math.Min(n - 1, i + 1);
                double dt = _t[b] - _t[a];
                bool gap = (i > 0 && _t[i] - _t[i - 1] > Opt.MaxGapSec) || (i < n - 1 && _t[i + 1] - _t[i] > Opt.MaxGapSec);
                _v[i] = dt > 0 && !gap ? Math.Sqrt(Sq(_x[b] - _x[a]) + Sq(_y[b] - _y[a])) / dt * 3.6 : 0;
            }
            for (int i = 1; i < n; i++)
            {
                double dt = _t[i] - _t[i - 1];
                _cum[i] = _cum[i - 1] + (dt <= Opt.MaxGapSec ? Math.Sqrt(Sq(_x[i] - _x[i - 1]) + Sq(_y[i] - _y[i - 1])) : 0);
            }

            _laps = sess.Laps.OrderBy(l => l.StartT).ToList();
            double s0 = _laps.Count > 0 ? _laps[0].StartT : sess.StartT;
            double s1 = _laps.Count > 0 ? _laps.Max(l => l.EndT) : sess.EndT;
            StartT = Math.Max(_t[0], s0 - beforeSec);
            EndT = Math.Min(_t[n - 1], s1 + afterSec);
            if (EndT <= StartT) EndT = StartT + 1;

            double mx = 0;
            for (int i = 0; i < n; i++) if (_t[i] >= s0 && _t[i] <= s1) mx = Math.Max(mx, _v[i]);
            if (sess.BestMaxKmh.HasValue) mx = Math.Max(mx, sess.BestMaxKmh.Value);
            MaxSpeedKmh = mx;
        }

        static double Sq(double v) { return v * v; }

        // ---- 補間 -------------------------------------------------------

        /// <summary>t を挟む点 i (T[i] ≦ t &lt; T[i+1]) と割合 f。途切れの間は f = 0 (前の点のまま)</summary>
        void Locate(double t, out int i, out double f, out bool gap)
        {
            int n = _t.Length;
            gap = false;
            if (t <= _t[0]) { i = 0; f = 0; gap = true; return; }
            if (t >= _t[n - 1]) { i = n - 1; f = 0; gap = true; return; }
            int lo = 0, hi = n - 1;
            while (hi - lo > 1) { int m = (lo + hi) / 2; if (_t[m] <= t) lo = m; else hi = m; }
            i = lo;
            double dt = _t[lo + 1] - _t[lo];
            if (dt > Opt.MaxGapSec) { f = 0; gap = true; return; }
            f = dt > 0 ? (t - _t[lo]) / dt : 0;
        }

        double Lerp(double[] a, int i, double f) { return f > 0 ? a[i] + (a[i + 1] - a[i]) * f : a[i]; }

        public double SpeedAt(double t)
        {
            int i; double f; bool gap;
            Locate(t, out i, out f, out gap);
            return gap ? 0 : Lerp(_v, i, f);
        }

        /// <summary>
        /// メーター表示用の速度: t の前後 SpeedSmoothSec の幅で、ハン窓の重みを付けて平均する。
        /// 折れ線の補間より角が取れ、GPS のばらつきで針が細かく揺れない
        /// </summary>
        public double SmoothSpeedAt(double t)
        {
            double w = Opt.SpeedSmoothSec;
            if (w <= 0) return SpeedAt(t);
            const int N = 20;
            double sum = 0, ws = 0;
            for (int j = 0; j <= N; j++)
            {
                double u = (double)j / N;                    // 0..1
                double wt = 0.5 - 0.5 * Math.Cos(2 * Math.PI * u);
                if (wt <= 0) continue;
                sum += wt * SpeedAt(t + (u - 0.5) * w);
                ws += wt;
            }
            return ws > 0 ? sum / ws : SpeedAt(t);
        }

        void PosAt(double t, out double x, out double y)
        {
            int i; double f; bool gap;
            Locate(t, out i, out f, out gap);
            x = Lerp(_x, i, f); y = Lerp(_y, i, f);
        }

        double HeadingAt(double t, double d)
        {
            double x0, y0, x1, y1;
            PosAt(t - d, out x0, out y0); PosAt(t + d, out x1, out y1);
            return Math.Atan2(x1 - x0, y1 - y0);   // 北から時計回り [rad]
        }

        /// <summary>横 G (右旋回が正), 前後 G (加速が正)</summary>
        public void GAt(double t, out double gx, out double gy)
        {
            double h = Math.Max(Opt.GSmoothSec / 2, 0.5);
            double v = SpeedAt(t) / 3.6;
            gy = (SpeedAt(t + h) - SpeedAt(t - h)) / 3.6 / (2 * h) / G0;
            gx = 0;
            if (v > 10 / 3.6)
            {
                double d = Math.Max(0.5, h / 2);
                double dpsi = HeadingAt(t + h, d) - HeadingAt(t - h, d);
                while (dpsi > Math.PI) dpsi -= 2 * Math.PI;
                while (dpsi < -Math.PI) dpsi += 2 * Math.PI;
                gx = v * dpsi / (2 * h) / G0;
            }
            gx = Math.Max(-3, Math.Min(3, gx));
            gy = Math.Max(-3, Math.Min(3, gy));
        }

        /// <summary>コース図用: 中心線を局所座標 [m] で</summary>
        public Point[] CenterlineXY()
        {
            return Def.Centerline.Select(p => new Point((p.Lon - Lon0) * Kx, (p.Lat - Lat0) * Ky)).ToArray();
        }

        public Point ToXY(LatLon p) { return new Point((p.Lon - Lon0) * Kx, (p.Lat - Lat0) * Ky); }

        // ---- 時刻の書式 -------------------------------------------------

        /// <summary>ラップ: 0'41.611</summary>
        public static string FmtLap(double sec)
        {
            if (sec < 0) sec = 0;
            long ms = (long)Math.Round(sec * 1000);
            return (ms / 60000).ToString(Inv) + "'" + ((ms % 60000) / 1000.0).ToString("00.000", Inv);
        }

        /// <summary>区間: 12.345 (60 秒以上は 1'02.345)</summary>
        public static string FmtSec(double sec)
        {
            if (sec < 0) sec = 0;
            if (Math.Round(sec * 1000) >= 60000) return FmtLap(sec);
            return sec.ToString("0.000", Inv);
        }

        public static string FmtLapDelta(double d) { return (d < 0 ? "-" : "+") + FmtLap(Math.Abs(d)); }
        public static string FmtSecDelta(double d) { return (d < 0 ? "-" : "+") + FmtSec(Math.Abs(d)); }

        // ---- ラップ・セクター -------------------------------------------

        // ベストの対象 (時間が進むにつれて、それまでで最も速いタイムに更新していく)
        // 周回: 有効な 1 周 (停止を含む周回は除く)。区間: 有効な周回で測れた区間すべて (OUT・遅い周回の区間も含む)
        static bool CountsForBest(LapRecord l)
        {
            if (l.IsGymRun) return l.Valid && l.LapTime.HasValue && !l.MissCourse && !l.Dnf;     // ジムカーナ: ミスコース・DNF は対象外 (停止を含む走行は対象)
            return l.Valid && l.Type == LapType.Normal && l.LapTime.HasValue && !l.ContainsStop;
        }

        static bool CountsForSectorBest(LapRecord l, int k)
        {
            return l.Valid && !l.ContainsStop && l.Sector != null && l.Sector[k].HasValue && l.Sector[k].Value > 0;
        }

        static string LapLabel(LapRecord l)
        {
            if (l.IsGymRun) return l.Dnf ? "DNF" : l.LapNo.ToString(Inv);     // ジムカーナ: 何本目
            if (!l.Valid) return "×";
            if (l.Type == LapType.Out) return "OUT";
            if (l.Type == LapType.Partial || l.Type == LapType.Incomplete) return "-";
            return l.IsIn ? "IN" : l.LapNo.ToString(Inv);
        }

        /// <summary>区間 k の開始・終了時刻。わからなければ NaN</summary>
        void SectorTimes(LapRecord l, out double[] start, out double[] end)
        {
            int M = SectorNames.Length;
            start = new double[M]; end = new double[M];
            for (int k = 0; k < M; k++) { start[k] = double.NaN; end[k] = double.NaN; }
            double t0 = l.StartT;
            for (int k = Math.Max(0, l.StartGate); k < M; k++)
            {
                start[k] = t0;
                if (l.Sector == null || !l.Sector[k].HasValue) break;
                t0 += l.Sector[k].Value;
                end[k] = t0;
            }
        }

        /// <summary>t までに走り終えた周回の中のベスト (無ければ null)</summary>
        LapRecord BestLapBefore(double t)
        {
            LapRecord best = null;
            foreach (LapRecord l in _laps)
                if (l.EndT <= t + 1e-9 && CountsForBest(l) && (best == null || l.LapTime.Value < best.LapTime.Value)) best = l;
            return best;
        }

        /// <summary>t までに走り終えた区間 k のベスト (周回と、その区間の開始時刻)</summary>
        LapRecord BestSectorBefore(int k, double t)
        {
            LapRecord best = null;
            foreach (LapRecord l in _laps)
            {
                if (!CountsForSectorBest(l, k)) continue;
                // ベストはラップが終わった時点でまとめて更新する（走行中の差分表示と食い違わないように）
                if (l.EndT > t + 1e-9) continue;
                if (best == null || l.Sector[k].Value < best.Sector[k].Value) best = l;
            }
            return best;
        }

        ReplayLap Ref(string key, double start, double dur)
        {
            ReplayLap r;
            lock (_ref)   // 書き出しは複数のスレッドから呼ぶ
            {
                if (!_ref.TryGetValue(key, out r))
                {
                    try { r = ReplayLap.Build(key, key, start, dur, _gps, Lat0, Lon0, Colors.White); }
                    catch (Exception) { r = null; }
                    _ref[key] = r;
                }
            }
            return r;
        }

        ReplayLap LapRef(LapRecord l) { return Ref("L" + l.StartT.ToString("R", Inv), l.StartT, l.EndT - l.StartT); }
        ReplayLap SecRef(LapRecord l, int k, double start, double dur) { return Ref("S" + k + "@" + start.ToString("R", Inv), start, dur); }

        /// <summary>走行中の差分: 経過 el で、基準 rf が同じ進み具合に来た時刻との差</summary>
        static double? LiveDelta(ReplayLap cur, ReplayLap rf, double el)
        {
            if (cur == null || rf == null) return null;
            return el - rf.TimeAtProgress(cur.ProgressAt(el));
        }

        // ---- 1 フレーム ------------------------------------------------

        public OverlayState At(double t, double fps, int trail)
        {
            OverlayState st = new OverlayState { T = t, Elapsed = t - StartT };
            st.Local = new DateTime(2000, 1, 1).AddSeconds(t).AddHours(Opt.JstOffsetHours);
            int i; double f; bool gap;
            Locate(t, out i, out f, out gap);
            st.SpeedKmh = SmoothSpeedAt(t);
            st.X = Lerp(_x, i, f); st.Y = Lerp(_y, i, f);
            st.Lat = Lerp(_lat, i, f); st.Lon = Lerp(_lon, i, f);
            st.Alt = double.IsNaN(_alt[i]) ? double.NaN : (f > 0 && !double.IsNaN(_alt[i + 1]) ? Lerp(_alt, i, f) : _alt[i]);
            int i0; double f0; bool g0;
            Locate(StartT, out i0, out f0, out g0);
            st.DistM = Math.Max(0, Lerp(_cum, i, f) - Lerp(_cum, i0, f0));
            double gx, gy;
            GAt(t, out gx, out gy);
            st.Gx = gx; st.Gy = gy;
            trail = Math.Max(1, trail);
            st.GTrail = new Point[trail];
            for (int j = 0; j < trail; j++)
            {
                double a, b;
                GAt(t - (trail - 1 - j) / fps, out a, out b);
                st.GTrail[j] = new Point(a, b);
            }

            st.SessionNo = Sess.No;
            st.Driver = Sess.Driver ?? "";
            // ジムカーナ: 走行ごとのドライバーがあれば、いま走っている (または最後に走った) 走行の名前
            LapRecord now = _laps.LastOrDefault(l => l.StartT <= t);
            if (now != null && now.IsGymRun) st.Driver = LapAnalyzer.DriverOf(Sess, now);
            FillLap(st, t);
            FillSector(st, t);
            return st;
        }

        void FillLap(OverlayState st, double t)
        {
            LapRecord cur = null, done = null;   // 走行中の周回, 最後に走り終えた周回
            foreach (LapRecord l in _laps)
            {
                if (l.StartT <= t && t < l.EndT) cur = l;
                if (l.EndT <= t) done = l;
            }
            LapRecord bestNow = BestLapBefore(t);
            st.LapBest = bestNow != null ? FmtLap(bestNow.LapTime.Value) : "-'--.---";

            bool hold = done != null && (cur == null || t - done.EndT < Opt.LapHoldSec);
            if (hold)
            {
                double v = done.LapTime ?? done.PartialTime ?? (done.EndT - done.StartT);
                st.LapTime = FmtLap(v);
                st.LapHolding = true;
                if (CountsForBest(done) || (done.Valid && done.Type == LapType.Normal && done.LapTime.HasValue))
                {
                    LapRecord prev = BestLapBefore(done.EndT - 1e-6);
                    if (prev != null) st.LapDelta = done.LapTime.Value - prev.LapTime.Value;
                }
            }
            else if (cur != null)
            {
                double el = t - cur.StartT;
                st.LapTime = FmtLap(el);
                if (cur.Type == LapType.Normal && cur.Valid && cur.StartGate == 0 && cur.LapTime.HasValue && bestNow != null)
                    st.LapDelta = LiveDelta(LapRef(cur), LapRef(bestNow), el);
            }
            else st.LapTime = "-'--.---";

            foreach (LapRecord l in _laps)
            {
                if (l.EndT > t || l.Type == LapType.Incomplete) continue;
                double? v = l.LapTime ?? l.PartialTime;
                if (!v.HasValue) continue;
                st.LapHistory.Add(new LapLine { Label = LapLabel(l), Text = FmtLap(v.Value), Best = bestNow != null && ReferenceEquals(l, bestNow) });
            }
        }

        void FillSector(OverlayState st, double t)
        {
            int M = SectorNames.Length;
            st.SecNames = SectorNames;
            st.SecCur = new string[M]; st.SecRunning = new bool[M]; st.SecDelta = new double?[M]; st.SecBest = new string[M];
            LapRecord[] best = new LapRecord[M];
            for (int k = 0; k < M; k++)
            {
                best[k] = BestSectorBefore(k, t);
                st.SecBest[k] = best[k] != null ? FmtSec(best[k].Sector[k].Value) : "-";
            }

            LapRecord cur = null, done = null;
            foreach (LapRecord l in _laps)
            {
                if (l.StartT <= t && t < l.EndT) cur = l;
                if (l.EndT <= t) done = l;
            }
            // 今の周回の区間を出す。周回が変わった直後 (hold 秒) は、まだ通っていない区間の欄に前の周回の値と差分を残す
            if (cur != null) FillSectorRow(st, cur, t, best);
            if (done != null && (cur == null || t - done.EndT < Opt.SectorHoldSec)) FillSectorRow(st, done, t, best);

            foreach (LapRecord l in _laps)
            {
                if (l.EndT > t || l.Type == LapType.Incomplete || l.Sector == null) continue;
                if (l.Sector.All(v => !v.HasValue)) continue;
                SectorLine ln = new SectorLine { Label = LapLabel(l), Text = new string[M], Best = new bool[M] };
                for (int k = 0; k < M; k++)
                {
                    ln.Text[k] = l.Sector[k].HasValue ? FmtSec(l.Sector[k].Value) : "";
                    ln.Best[k] = best[k] != null && ReferenceEquals(best[k], l);
                }
                st.SecHistory.Add(ln);
            }
        }

        /// <summary>周回 show の区間を、まだ空いている欄に入れる (走り終えた区間は値と差分、走行中の区間は経過時間とリアルタイムの差分)</summary>
        void FillSectorRow(OverlayState st, LapRecord show, double t, LapRecord[] best)
        {
            int M = SectorNames.Length;
            {
                double[] s, e; SectorTimes(show, out s, out e);
                for (int k = 0; k < M; k++)
                {
                    if (st.SecCur[k] != null) continue;
                    if (double.IsNaN(s[k]) || s[k] > t) continue;
                    bool fin = !double.IsNaN(e[k]) && e[k] <= t;
                    if (fin)
                    {
                        double v = show.Sector[k].Value;
                        st.SecCur[k] = FmtSec(v);
                        LapRecord prev = BestSectorBefore(k, show.StartT);   // この周回の走行中に表示していたベストと比べる
                        if (prev != null && show.Valid) st.SecDelta[k] = v - prev.Sector[k].Value;
                    }
                    else if (t < show.EndT)
                    {
                        double el = t - s[k];
                        st.SecCur[k] = FmtSec(el);
                        st.SecRunning[k] = true;
                        if (best[k] != null && show.Valid && !double.IsNaN(e[k]))
                        {
                            double[] bs, be; SectorTimes(best[k], out bs, out be);
                            st.SecDelta[k] = LiveDelta(SecRef(show, k, s[k], e[k] - s[k]), SecRef(best[k], k, bs[k], be[k] - bs[k]), el);
                        }
                    }
                }
            }
        }
    }
}
