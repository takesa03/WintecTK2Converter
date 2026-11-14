using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;

namespace Tk2Gui
{
    /// <summary>距離ごとの比較の 1 本 (周回・走行)。スタート (コントロール) ラインからの距離 Step [m] ごとの、経過時間と速度。</summary>
    public sealed class CompareRun
    {
        public string Name;          // 凡例の名前
        public Color Color;
        public double LapTime;       // タイム [s]
        public double[] T;           // 距離 i*Step での、スタートからの経過時間 [s]
        public double[] V;           // 同じく速度 [km/h]
    }

    // =====================================================================
    //  距離ごとの比較 (ジムカーナの走行・サーキットの周回)
    //  横軸は「スタート (コントロール) ラインからの中心線の距離」。各ラインを通った時刻 (タイム計算の結果) を
    //  中心線上のラインの位置に合わせ、ラインとラインの間は、その走行が実際に走った距離の割合で中心線の距離に割り振る。
    //  (走行の点を 1 点ずつ中心線に投影する方法は、サイドターンなど中心線が折り返す所で先の線へ飛んだり、
    //   遅れて追いつけなくなったりしたのでやめた。この方法は必ず前に進み、ラインの所では正確に合う)
    // =====================================================================
    public static class RunCompare
    {
        public const double Step = 1;
        const double MoveKmh = 2;     // これ未満の区間は走行距離に足さない (止まっている間の位置のふらつき)

        /// <summary>比較する区間: スタート (コントロール) ラインの距離と、そこからの長さ [m]。</summary>
        public static void Span(CourseDefinition def, out double startS, out double span)
        {
            if (def.IsGymkhana)
            {
                List<Gate> o = def.Gates.OrderBy(g => g.S).ToList();
                startS = o[0].S; span = Math.Max(1, o[o.Count - 1].S - o[0].S);
            }
            else
            {
                Gate ctrl = def.Gates.FirstOrDefault(g => g.Name == "ControlLine") ?? def.Gates[0];
                startS = ctrl.S; span = def.LengthM;
            }
        }

        /// <summary>ラインのスタートからの距離 (区間の並び = LapRecord.Sector の並び。最後の区間の終わり = span)。</summary>
        static double[] GateDist(CourseDefinition def, double startS)
        {
            if (def.IsGymkhana) return def.Gates.OrderBy(g => g.S).Select(g => g.S - startS).ToArray();
            Gate ctrl = def.Gates.FirstOrDefault(g => g.Name == "ControlLine") ?? def.Gates[0];
            List<double> d = new List<double> { 0 };
            d.AddRange(def.Gates.Where(g => !ReferenceEquals(g, ctrl)).Select(g => (((g.S - ctrl.S) % def.LengthM) + def.LengthM) % def.LengthM).OrderBy(x => x));
            d.Add(def.LengthM);
            return d.ToArray();
        }

        public static CompareRun Build(CourseDefinition def, List<GpsSample> gps, double startT, double lapTime, double?[] sectors, string name, Color color)
        {
            double startS, span;
            Span(def, out startS, out span);
            double lat0 = def.Lat0, lon0 = def.Lon0, ky = 111132.0, kx = 111320.0 * Math.Cos(lat0 * Math.PI / 180);

            // 走行の点: 経過時間・走った距離・速度
            double endT = startT + lapTime;
            List<double> pt = new List<double>(), pd = new List<double>(), pv = new List<double>();
            double dist = 0, px = 0, py = 0, ptPrev = double.NaN;
            for (int i = 0; i < gps.Count; i++)
            {
                GpsSample g = gps[i];
                if (g.T < startT - 3) continue;
                if (g.T > endT + 3) break;
                double x = (g.Lon - lon0) * kx, y = (g.Lat - lat0) * ky;
                double v = g.SpeedKmh;
                if (!double.IsNaN(ptPrev))
                {
                    double dt = g.T - ptPrev, d = Math.Sqrt((x - px) * (x - px) + (y - py) * (y - py));
                    double vc = dt > 0 ? d / dt * 3.6 : 0;
                    if (double.IsNaN(v)) v = vc;
                    if (dt > 0 && dt <= 3 && v >= MoveKmh) dist += d;
                }
                if (double.IsNaN(v)) v = 0;
                pt.Add(g.T - startT); pd.Add(dist); pv.Add(v);
                px = x; py = y; ptPrev = g.T;
            }

            // 目印: ラインを通った時刻と、中心線上のラインの位置
            double[] gd = GateDist(def, startS);
            List<double> at = new List<double> { 0 }, asd = new List<double> { 0 };
            double cum = 0;
            int M = Math.Min(sectors == null ? 0 : sectors.Length, gd.Length - 1);
            for (int k = 0; k < M - 1; k++)
            {
                if (!sectors[k].HasValue) break;       // 区間タイムが欠けたら、そこから先はフィニッシュまでまとめて割り振る
                cum += sectors[k].Value;
                at.Add(cum); asd.Add(gd[k + 1]);
            }
            at.Add(lapTime); asd.Add(span);

            int n = (int)Math.Floor(span / Step) + 1;
            CompareRun run = new CompareRun { Name = name, Color = color, LapTime = lapTime, T = new double[n], V = new double[n] };
            if (pt.Count < 2)
            {
                for (int k = 0; k < n; k++) { run.T[k] = k * Step / span * lapTime; run.V[k] = double.NaN; }
                return run;
            }
            // 時刻 → 中心線の距離 (目印の間を、走った距離の割合で割り振る) を、走行の各点について求める
            List<double> ss = new List<double>(), st = new List<double>(), sv = new List<double>();
            for (int a = 0; a + 1 < at.Count; a++)
            {
                double ta = at[a], tb = at[a + 1], da = Interp(pt, pd, ta), db = Interp(pt, pd, tb);
                ss.Add(asd[a]); st.Add(ta); sv.Add(Interp(pt, pv, ta));
                for (int i = 0; i < pt.Count; i++)
                {
                    if (pt[i] <= ta || pt[i] >= tb) continue;
                    double f = db > da ? (pd[i] - da) / (db - da) : (pt[i] - ta) / (tb - ta);
                    ss.Add(asd[a] + (asd[a + 1] - asd[a]) * Math.Max(0, Math.Min(1, f))); st.Add(pt[i]); sv.Add(pv[i]);
                }
            }
            ss.Add(span); st.Add(lapTime); sv.Add(Interp(pt, pv, lapTime));

            // 距離 Step ごとに (距離が同じ点が続く = 止まっている間は、最初に着いた時刻)
            int j = 0;
            for (int k = 0; k < n; k++)
            {
                double s = k * Step;
                while (j < ss.Count && ss[j] < s) j++;
                if (j == 0) { run.T[k] = st[0]; run.V[k] = sv[0]; continue; }
                if (j >= ss.Count) { run.T[k] = st[ss.Count - 1]; run.V[k] = sv[ss.Count - 1]; continue; }
                double a0 = ss[j - 1], b0 = ss[j], u = b0 > a0 ? (s - a0) / (b0 - a0) : 1;
                run.T[k] = st[j - 1] + (st[j] - st[j - 1]) * u;
                run.V[k] = sv[j - 1] + (sv[j] - sv[j - 1]) * u;
            }
            return run;
        }

        static double Interp(List<double> xs, List<double> ys, double x)
        {
            int n = xs.Count;
            if (x <= xs[0]) return ys[0];
            if (x >= xs[n - 1]) return ys[n - 1];
            int i = xs.BinarySearch(x);
            if (i >= 0) return ys[i];
            i = ~i;
            double a = xs[i - 1], b = xs[i];
            return b > a ? ys[i - 1] + (ys[i] - ys[i - 1]) * (x - a) / (b - a) : ys[i];
        }
    }
}
