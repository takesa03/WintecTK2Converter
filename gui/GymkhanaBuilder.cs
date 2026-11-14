using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Tk2Gui
{
    // ジムカーナのコース定義の作成。
    //  サーキットのように「同じ所へ戻ってくる周回」を平均化するのではなく、
    //  1. ログから走行 (止まった所からの発進 → フィニッシュ後のスロー走行に入るまで) を切り出し、
    //  2. 同じコースの走行のうち 1 本を基準にして中心線 (始点と終点のある線) にし、
    //  3. スタートライン (発進前に止まっていた所の少し先) とフィニッシュライン (最後に速く走っていた所) を自動で置く。
    //  方式は docs/gymkhana_design.md。

    public sealed class GymkhanaParams
    {
        public string Name = "Gymkhana";
        public double StopKmh = 3;            // これ未満は止まっているとみなす
        public double LaunchKmh = 40;         // 止まった所から LaunchWithinSec 秒以内にこの速度まで上がったら発進 (待機列で少し進むのは発進にしない)
        public double LaunchWithinSec = 10;
        public double FastKmh = 30;           // フィニッシュの目安 = 走行の中で最後にこの速度を超えた所
        public double SlowKmh = 25;           // これ未満が SlowSec 秒続いたら走行の終わり (フィニッシュ後のスロー走行・停止)。サイドターンは短いので続かない
        public double SlowSec = 8;
        public double MinRunM = 200;          // これより短い走行は数えない
        public double MaxGapSec = 2;          // この秒数を超えて途切れたら別の区間
        public double ClusterRadiusM = 15;    // 発進位置がこの範囲に集まっている走行を同じコースとみなす
        public double LengthTolerance = 0.15; // 距離が中央値からこの割合以内の走行を同じコースとみなす
        public int SectorCount = 0;           // スタートとフィニッシュの間に置くセクターライン数
        public double GateHalfWidthM = 12;
    }

    /// <summary>切り出した 1 本の走行。インデックスは GymkhanaData.Samples の位置。</summary>
    public sealed class GymRun
    {
        public int No;              // 1〜 (ログの中の順番)
        public int StopIdx;         // 発進前に最後に止まっていた点
        public int MoveIdx;         // 動き出した点
        public int FinishIdx;       // 最後に FastKmh を超えた点 (フィニッシュの目安)
        public int EndIdx;          // 走行の終わり
        public double DistM;        // 動き出し → フィニッシュの目安 の距離
        public double TimeSec;      // 同じく時間 (目安。正式のタイムはラインの通過で計算する)
        public double MaxKmh;
        public bool HasStop;        // 途中で止まった (スピン等)
        public bool Same;           // 同じコースの走行と自動で判定した
        public bool Use;            // コース定義に使う (画面で変えられる)
        public string Note = "";    // 判定の理由
        public double StartT;       // 動き出した時刻 (GpsSample.T)
        public int FileIndex;
    }

    /// <summary>切り出しの結果。基準の走行や使う走行を変えて作り直すときに使う。</summary>
    public sealed class GymkhanaData
    {
        public List<GpsSample> Samples;
        public int[] FileOf;
        public double[] T, X, Y, V;
        public bool[] Brk;              // この点の直前で記録が途切れている
        public double Lat0, Lon0, Kx, Ky;
        public List<GymRun> Runs = new List<GymRun>();
        public int ReferenceIndex = -1; // Runs のインデックス
        public double AutoStartS, AutoFinishS;
        public List<string> SourceNames = new List<string>();
        // 基準の走行を中心線に投影したもの (中心線の始点からの距離 [m] の順)。ラインの置き方の補助 (速度のグラフ・時刻での指定・速度の極小点) に使う
        public double[] ProfS, ProfT, ProfV;    // 距離 [m], 時刻 (GpsSample.T), 速度 [km/h]
    }

    /// <summary>速度の極小点 (サイドターン・スラロームなど。セクターの区切りの目安)。</summary>
    public sealed class SpeedMin
    {
        public double S;        // 中心線の始点からの距離 [m]
        public double Kmh;
    }

    public static class GymkhanaBuilder
    {
        const double Step = 2.0;        // 中心線の点間隔 [m]
        const double TailM = 40;        // フィニッシュの目安より先に中心線を伸ばす距離 (フィニッシュラインを先へ動かせるように)

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        struct Pt { public double X, Y; public Pt(double x, double y) { X = x; Y = y; } }

        // ================= 1. 走行の切り出し =================

        public static CourseResult Build(IList<List<GpsSample>> files, IList<string> sourceNames, GymkhanaParams prm)
        {
            GymkhanaData g = Detect(files, sourceNames, prm);
            CourseResult res = new CourseResult { Gym = g, Samples = g.Samples };
            res.Log.Add(string.Format(Inv, "走行 {0} 本を検出 (同じコースの走行 {1} 本)", g.Runs.Count, g.Runs.Count(r => r.Same)));
            foreach (GymRun r in g.Runs) res.Log.Add("  " + RunText(r, true));
            if (!g.Runs.Any(r => r.Use))
                throw new InvalidOperationException("走行を検出できませんでした (止まった所から発進し、" + prm.MinRunM.ToString("0", Inv) + " m 以上走った区間がありません)");
            g.ReferenceIndex = DefaultReference(g);
            Compose(res, prm.Name, prm.SectorCount, prm.GateHalfWidthM, null);
            return res;
        }

        public static GymkhanaData Detect(IList<List<GpsSample>> files, IList<string> sourceNames, GymkhanaParams prm)
        {
            GymkhanaData g = new GymkhanaData();
            g.SourceNames = sourceNames.Select(Path.GetFileName).ToList();
            List<GpsSample> all = new List<GpsSample>();
            List<int> fileOf = new List<int>();
            for (int fi = 0; fi < files.Count; fi++)
                foreach (GpsSample s in files[fi]) { all.Add(s); fileOf.Add(fi); }
            if (all.Count < 100) throw new InvalidOperationException("データが少なすぎます (100 点以上必要)");
            int n = all.Count;
            g.Samples = all; g.FileOf = fileOf.ToArray();
            g.Lat0 = all.Average(s => s.Lat); g.Lon0 = all.Average(s => s.Lon);
            g.Ky = 111132.0; g.Kx = 111320.0 * Math.Cos(g.Lat0 * Math.PI / 180);
            g.T = new double[n]; g.X = new double[n]; g.Y = new double[n]; g.V = new double[n]; g.Brk = new bool[n];
            for (int i = 0; i < n; i++)
            {
                g.T[i] = all[i].T; g.X[i] = (all[i].Lon - g.Lon0) * g.Kx; g.Y[i] = (all[i].Lat - g.Lat0) * g.Ky;
                double dt = i > 0 ? g.T[i] - g.T[i - 1] : 0;
                g.Brk[i] = i == 0 || g.FileOf[i] != g.FileOf[i - 1] || dt <= 0 || dt > prm.MaxGapSec;
            }
            // 速度: ロガーの記録があればそれ (位置の差より安定)。無ければ前後の点から
            for (int i = 0; i < n; i++)
            {
                double v = all[i].SpeedKmh;
                if (double.IsNaN(v))
                {
                    int a = i > 0 && !g.Brk[i] ? i - 1 : i, b = i + 1 < n && !g.Brk[i + 1] ? i + 1 : i;
                    double dt = g.T[b] - g.T[a];
                    v = dt > 0 ? Dist(g, a, b) / dt * 3.6 : 0;
                }
                g.V[i] = v;
            }

            int no = 0;
            int idx = 1;
            while (idx < n)
            {
                if (g.V[idx] < prm.LaunchKmh) { idx++; continue; }
                // 少し前に止まっていたか (途切れの直後も、止まっていた扱い: Wintec は停止中に記録しない)
                int stop = -1;
                for (int k = idx; k >= 0 && g.T[idx] - g.T[k] <= prm.LaunchWithinSec; k--)
                {
                    if (g.V[k] < prm.StopKmh) { stop = k; break; }
                    if (g.Brk[k]) { stop = k; break; }
                }
                if (stop < 0) { idx++; continue; }      // 走りながらの加速 (発進ではない)
                int move = stop;
                while (move < idx && g.V[move] < prm.StopKmh && !g.Brk[move + 1]) move++;

                // 走行の終わり: 遅い状態が SlowSec 続いた所 / 記録の途切れ
                int lastFast = idx, end = n - 1, slowFrom = -1;
                double max = 0;
                for (int j = idx; j < n; j++)
                {
                    if (j > idx && g.Brk[j]) { end = j - 1; break; }
                    max = Math.Max(max, g.V[j]);
                    if (g.V[j] >= prm.FastKmh) lastFast = j;
                    if (g.V[j] < prm.SlowKmh)
                    {
                        if (slowFrom < 0) slowFrom = j;
                        if (g.T[j] - g.T[slowFrom] >= prm.SlowSec) { end = j; break; }
                    }
                    else slowFrom = -1;
                }
                double dist = 0;
                for (int j = move + 1; j <= lastFast; j++) dist += Dist(g, j - 1, j);
                bool hasStop = false;
                for (int j = idx; j <= lastFast; j++) if (g.V[j] < prm.StopKmh) { hasStop = true; break; }
                if (dist >= prm.MinRunM)
                {
                    g.Runs.Add(new GymRun
                    {
                        No = ++no, StopIdx = stop, MoveIdx = move, FinishIdx = lastFast, EndIdx = end,
                        DistM = dist, TimeSec = g.T[lastFast] - g.T[move], MaxKmh = max, HasStop = hasStop,
                        StartT = g.T[move], FileIndex = g.FileOf[move]
                    });
                }
                idx = end + 1;
            }
            Classify(g, prm);
            return g;
        }

        // 発進位置が集まり、距離が近い走行を「同じコース」とする
        static void Classify(GymkhanaData g, GymkhanaParams prm)
        {
            if (g.Runs.Count == 0) return;
            List<GymRun> best = null;
            foreach (GymRun r in g.Runs)
            {
                List<GymRun> near = g.Runs.Where(o => Dist(g, o.StopIdx, r.StopIdx) <= prm.ClusterRadiusM).ToList();
                if (best == null || near.Count > best.Count) best = near;
            }
            double med = Median(best.Select(r => r.DistM));
            for (int i = 0; i < g.Runs.Count; i++)
            {
                GymRun r = g.Runs[i];
                bool restart = i > 0 && g.Runs[i - 1].FileIndex == r.FileIndex && g.T[r.StopIdx] - g.T[g.Runs[i - 1].EndIdx] < 60;
                if (!best.Contains(r)) r.Note = restart ? "前の走行の途中から再発進 (スピン等の後？)" : "発進位置が違う";
                else if (Math.Abs(r.DistM - med) > med * prm.LengthTolerance)
                    r.Note = r.DistM < med ? "距離が短い (途中で終わった？)" : "距離が長い";
                else { r.Same = true; r.Note = r.HasStop ? "途中で停止あり" : ""; }
                r.Use = r.Same;
            }
        }

        /// <summary>既定の基準の走行: 使う走行のうち、途中で止まっていない最速のもの。</summary>
        public static int DefaultReference(GymkhanaData g)
        {
            IEnumerable<GymRun> c = g.Runs.Where(r => r.Use && !r.HasStop);
            if (!c.Any()) c = g.Runs.Where(r => r.Use);
            if (!c.Any()) c = g.Runs;
            GymRun best = c.OrderBy(r => r.TimeSec).FirstOrDefault();
            return best == null ? -1 : g.Runs.IndexOf(best);
        }

        public static string RunText(GymRun r, bool withNote)
        {
            DateTime t = new DateTime(2000, 1, 1).AddSeconds(r.StartT).AddHours(9);
            string s = string.Format(Inv, "{0} 本目  {1:HH:mm:ss}  {2,5:F0} m  {3,5:F1} 秒  最高 {4:F0} km/h", r.No, t, r.DistM, r.TimeSec, r.MaxKmh);
            if (withNote) s += r.Same ? (r.Note.Length > 0 ? "  (" + r.Note + ")" : "") : "  [除外: " + r.Note + "]";
            return s;
        }

        /// <summary>画面の一覧用の短い表示 (理由はツールチップに出す)。</summary>
        public static string RunShortText(GymRun r)
        {
            DateTime t = new DateTime(2000, 1, 1).AddSeconds(r.StartT).AddHours(9);
            return string.Format(Inv, "{0} 本目 {1:HH:mm}  {2:F0} m  {3:F1} 秒{4}", r.No, t, r.DistM, r.TimeSec,
                r.Same ? (r.HasStop ? "  停止あり" : "") : "  ※");
        }

        // ================= 2. 中心線とライン =================

        /// <summary>
        /// res.Gym の基準の走行・使う走行から、コース定義 (中心線・ライン) を作り直す。
        /// keepGates: 今のラインの位置を残す場合のコース定義 (null = 自動の位置に置き直す)。
        /// </summary>
        public static void Compose(CourseResult res, string name, int sectorCount, double halfWidthM, CourseDefinition keepGates)
        {
            GymkhanaData g = res.Gym;
            if (g.ReferenceIndex < 0 || g.ReferenceIndex >= g.Runs.Count) throw new InvalidOperationException("基準の走行がありません");
            GymRun rf = g.Runs[g.ReferenceIndex];

            // 基準の走行: 止まっていた点 → 動き出し以降 → フィニッシュの目安の TailM 先まで
            List<Pt> raw = new List<Pt> { new Pt(g.X[rf.StopIdx], g.Y[rf.StopIdx]) };
            double tail = 0;
            for (int j = rf.MoveIdx + 1; j <= rf.EndIdx; j++)
            {
                if (j > rf.FinishIdx) { tail += Dist(g, j - 1, j); if (tail > TailM) break; }
                raw.Add(new Pt(g.X[j], g.Y[j]));
            }
            List<Pt> line = Smooth(ResampleOpen(raw, Step), 2);
            double[] cum = Cum(line);
            double L = cum[cum.Length - 1];
            MakeProfile(g, rf, line, cum, L);

            // スタート: 使う走行の「止まっていた位置」を中心線の始めの方に投影し、いちばん先の位置の 1 m 先
            // (どの走行もラインの手前から発進するように。中央値だと、前に止まった走行がラインを越えずに始まってしまう)
            List<GymRun> used = g.Runs.Where(r => r.Use).ToList();
            if (!used.Contains(rf)) used.Add(rf);
            double startS = used.Max(r => ProjectS(line, cum, g.X[r.StopIdx], g.Y[r.StopIdx], 0, 40));
            startS = Math.Max(0, startS) + 1;
            // フィニッシュ: 各走行の「最後に速く走っていた点」を、基準の走行のその点の前後に投影した中央値
            double refFin = ProjectS(line, cum, g.X[rf.FinishIdx], g.Y[rf.FinishIdx], Math.Max(0, L - TailM - 60), L);
            double finishS = Median(used.Select(r => ProjectS(line, cum, g.X[r.FinishIdx], g.Y[r.FinishIdx], refFin - 60, refFin + TailM)));
            finishS = Math.Max(startS + 10, Math.Min(L - 1, finishS));
            g.AutoStartS = startS; g.AutoFinishS = finishS;

            CourseDefinition def = new CourseDefinition();
            def.Mode = CourseDefinition.GymkhanaMode;
            def.Version = 2;
            def.Name = name;
            def.Created = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", Inv);
            def.Sources = new List<string>(g.SourceNames);
            def.LengthM = L;
            foreach (Pt p in line) def.Centerline.Add(new LatLon(g.Lat0 + p.Y / g.Ky, g.Lon0 + p.X / g.Kx));
            def.Stats = new CourseStats
            {
                LapsUsed = used.Count, LapsTotal = g.Runs.Count, PointsUsed = rf.EndIdx - rf.StopIdx + 1, PointsTotal = g.Samples.Count,
                MedianLapSec = Median(used.Select(r => r.TimeSec)),
                ScatterStdevM = Scatter(g, used.Where(r => r != rf), line, cum)
            };
            if (keepGates != null && keepGates.Gates.Count >= 2)
            {
                def.GateHalfWidthM = halfWidthM;
                foreach (Gate old in keepGates.Gates) def.Gates.Add(old);
                for (int i = 0; i < def.Gates.Count; i++) def.UpdateGate(i, def.Gates[i].S, halfWidthM);
            }
            else def.MakeGymkhanaGates(startS, finishS, sectorCount, halfWidthM);
            res.Def = def;

            // 確認用の表示: 基準の走行 = Used、ほかの走行 = ExcludedLap
            int n = g.Samples.Count;
            res.Classes = new PointClass[n];
            res.LapIds = new int[n];
            for (int i = 0; i < n; i++) res.LapIds[i] = -1;
            foreach (GymRun r in g.Runs)
                for (int j = r.StopIdx; j <= r.EndIdx; j++)
                {
                    res.Classes[j] = r == rf ? PointClass.Used : PointClass.ExcludedLap;
                    res.LapIds[j] = r.No;
                }
        }

        // 基準の走行の各点を中心線に投影して、距離・時刻・速度の表にする (前にだけ進む向きに探す。交差する所で取り違えないため)
        static void MakeProfile(GymkhanaData g, GymRun rf, List<Pt> line, double[] cum, double L)
        {
            List<double> ps = new List<double>(), pt = new List<double>(), pv = new List<double>();
            double last = 0;
            List<int> idx = new List<int> { rf.StopIdx };
            for (int j = rf.MoveIdx + 1; j <= rf.EndIdx; j++) idx.Add(j);
            foreach (int j in idx)
            {
                double s = ProjectS(line, cum, g.X[j], g.Y[j], Math.Max(0, last - 5), last + 60);
                if (s < last) s = last;
                double v = g.V[j];
                if (double.IsNaN(v)) v = pv.Count > 0 ? pv[pv.Count - 1] : 0;
                ps.Add(s); pt.Add(g.T[j]); pv.Add(v);
                last = s;
                if (s >= L - 0.5) break;
            }
            g.ProfS = ps.ToArray(); g.ProfT = pt.ToArray(); g.ProfV = pv.ToArray();
        }

        static double Interp(double[] xs, double[] ys, double x)
        {
            int n = xs.Length;
            if (n == 0) return double.NaN;
            if (x <= xs[0]) return ys[0];
            if (x >= xs[n - 1]) return ys[n - 1];
            int i = Array.BinarySearch(xs, x);
            if (i >= 0) return ys[i];
            i = ~i;
            double a = xs[i - 1], b = xs[i];
            return b > a ? ys[i - 1] + (ys[i] - ys[i - 1]) * (x - a) / (b - a) : ys[i];
        }

        public static bool HasProfile(GymkhanaData g) { return g != null && g.ProfS != null && g.ProfS.Length >= 2; }

        /// <summary>基準の走行が距離 s [m] にいた時刻 (GpsSample.T)。</summary>
        public static double TimeAtS(GymkhanaData g, double s) { return Interp(g.ProfS, g.ProfT, s); }

        /// <summary>基準の走行の距離 s [m] での速度 [km/h]。</summary>
        public static double SpeedAtS(GymkhanaData g, double s) { return Interp(g.ProfS, g.ProfV, s); }

        /// <summary>基準の走行が時刻 t (GpsSample.T) にいた距離 [m]。</summary>
        public static double SAtTime(GymkhanaData g, double t) { return Interp(g.ProfT, g.ProfS, t); }

        /// <summary>
        /// 基準の走行の速度の極小点 (サイドターン・スラローム・低速コーナー)。sMin〜sMax の範囲で、
        /// 前後 30 m の中でいちばん遅く、前後 60 m の最高速より 10 km/h 以上遅い所。
        /// </summary>
        public static List<SpeedMin> SpeedMinima(GymkhanaData g, double sMin, double sMax)
        {
            List<SpeedMin> list = new List<SpeedMin>();
            if (!HasProfile(g)) return list;
            const double step = 2;
            int n = (int)(g.ProfS[g.ProfS.Length - 1] / step) + 1;
            double[] v = new double[n];
            for (int i = 0; i < n; i++) v[i] = SpeedAtS(g, i * step);
            double[] sm = new double[n];
            for (int i = 0; i < n; i++)
            {
                double sum = 0; int c = 0;
                for (int k = Math.Max(0, i - 2); k <= Math.Min(n - 1, i + 2); k++) { sum += v[k]; c++; }
                sm[i] = sum / c;
            }
            int w = (int)(30 / step), w2 = (int)(60 / step);
            for (int i = 0; i < n; i++)
            {
                double s = i * step;
                if (s < sMin || s > sMax) continue;
                bool isMin = true;
                for (int k = Math.Max(0, i - w); k <= Math.Min(n - 1, i + w) && isMin; k++)
                    if (sm[k] < sm[i] || (sm[k] == sm[i] && k < i)) isMin = false;
                if (!isMin) continue;
                double lmax = 0, rmax = 0;
                for (int k = Math.Max(0, i - w2); k < i; k++) lmax = Math.Max(lmax, sm[k]);
                for (int k = i + 1; k <= Math.Min(n - 1, i + w2); k++) rmax = Math.Max(rmax, sm[k]);
                if (lmax - sm[i] < 10 || rmax - sm[i] < 10) continue;
                list.Add(new SpeedMin { S = s, Kmh = sm[i] });
            }
            return list;
        }

        public static string Summary(CourseDefinition def)
        {
            Gate st = def.Gates.FirstOrDefault(x => x.Name == CourseDefinition.StartGateName);
            Gate fi = def.Gates.FirstOrDefault(x => x.Name == CourseDefinition.FinishGateName);
            double len = st != null && fi != null ? fi.S - st.S : def.LengthM;
            return string.Format(Inv, "ジムカーナ / スタート → フィニッシュ {0:F0} m / セクター {1} 本 / 使った走行 {2} 本 (検出 {3} 本) / タイムの目安 中央値 {4:F1} 秒 / ほかの走行との差 平均 {5:F1} m",
                len, Math.Max(0, def.Gates.Count - 2), def.Stats.LapsUsed, def.Stats.LapsTotal, def.Stats.MedianLapSec, def.Stats.ScatterStdevM);
        }

        // ほかの走行の点と中心線の距離の平均 (基準の走行とどれくらい重なっているかの目安)。
        // 同じ所を何度も通るので、走行の中の距離の前後 (±60 m) だけで探す
        static double Scatter(GymkhanaData g, IEnumerable<GymRun> runs, List<Pt> line, double[] cum)
        {
            double sum = 0; int cnt = 0;
            foreach (GymRun r in runs)
            {
                double d = 0;
                for (int j = r.MoveIdx + 1; j <= r.FinishIdx; j++)
                {
                    d += Dist(g, j - 1, j);
                    double off;
                    ProjectS(line, cum, g.X[j], g.Y[j], d - 60, d + 60, out off);
                    sum += off; cnt++;
                }
            }
            return cnt > 0 ? sum / cnt : 0;
        }

        // ================= 幾何 =================

        static double Dist(GymkhanaData g, int a, int b)
        {
            double dx = g.X[b] - g.X[a], dy = g.Y[b] - g.Y[a];
            return Math.Sqrt(dx * dx + dy * dy);
        }

        static double[] Cum(List<Pt> p)
        {
            double[] c = new double[p.Count];
            for (int i = 1; i < p.Count; i++) c[i] = c[i - 1] + Math.Sqrt(Math.Pow(p[i].X - p[i - 1].X, 2) + Math.Pow(p[i].Y - p[i - 1].Y, 2));
            return c;
        }

        static List<Pt> ResampleOpen(List<Pt> p, double step)
        {
            double[] c = Cum(p);
            double L = c[c.Length - 1];
            List<Pt> r = new List<Pt>();
            int k = 0;
            for (double s = 0; s <= L; s += step)
            {
                while (k < p.Count - 2 && c[k + 1] < s) k++;
                double seg = c[k + 1] - c[k];
                double f = seg > 0 ? (s - c[k]) / seg : 0;
                r.Add(new Pt(p[k].X + (p[k + 1].X - p[k].X) * f, p[k].Y + (p[k + 1].Y - p[k].Y) * f));
            }
            if (r.Count < 2) throw new InvalidOperationException("基準の走行が短すぎます");
            return r;
        }

        // 前後 w 点の平均。端は使える点だけで平均する (端を動かさないよう、始点と終点はそのまま)
        static List<Pt> Smooth(List<Pt> c, int w)
        {
            List<Pt> r = new List<Pt>(c.Count);
            for (int i = 0; i < c.Count; i++)
            {
                if (i == 0 || i == c.Count - 1) { r.Add(c[i]); continue; }
                int a = Math.Max(0, i - w), b = Math.Min(c.Count - 1, i + w);
                double sx = 0, sy = 0;
                for (int j = a; j <= b; j++) { sx += c[j].X; sy += c[j].Y; }
                r.Add(new Pt(sx / (b - a + 1), sy / (b - a + 1)));
            }
            return r;
        }

        static double ProjectS(List<Pt> line, double[] cum, double px, double py, double sMin, double sMax)
        {
            double off;
            return ProjectS(line, cum, px, py, sMin, sMax, out off);
        }

        /// <summary>中心線のうち始点からの距離が [sMin, sMax] の部分で、点にいちばん近い位置の距離を返す。</summary>
        static double ProjectS(List<Pt> line, double[] cum, double px, double py, double sMin, double sMax, out double off)
        {
            double best = double.MaxValue, bestS = Math.Max(0, sMin);
            for (int i = 0; i + 1 < line.Count; i++)
            {
                if (cum[i + 1] < sMin || cum[i] > sMax) continue;
                double ax = line[i].X, ay = line[i].Y, dx = line[i + 1].X - ax, dy = line[i + 1].Y - ay;
                double l2 = dx * dx + dy * dy;
                double u = l2 > 0 ? ((px - ax) * dx + (py - ay) * dy) / l2 : 0;
                u = Math.Max(0, Math.Min(1, u));
                double ex = ax + dx * u - px, ey = ay + dy * u - py;
                double d2 = ex * ex + ey * ey;
                if (d2 < best) { best = d2; bestS = cum[i] + (cum[i + 1] - cum[i]) * u; }
            }
            off = best == double.MaxValue ? 0 : Math.Sqrt(best);
            return bestS;
        }

        static double Median(IEnumerable<double> values)
        {
            List<double> v = values.OrderBy(x => x).ToList();
            if (v.Count == 0) return 0;
            return v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2;
        }
    }
}
