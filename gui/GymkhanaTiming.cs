using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Tk2Gui
{
    // =====================================================================
    //  ジムカーナのタイム計算 (docs/gymkhana_design.md の 4 章)
    //  ライン (Start → Sector1 → ... → Finish) を順番どおりに通ったときだけ数える。
    //  同じ所を何度も・逆向きにも通るコースなので、次のように絞り込む:
    //   ・次に通るはずのラインだけを待つ (先のラインを先に通ったら、間を飛ばした = ミスコースの疑い)
    //   ・ラインごとの向き (HeadingDeg) と同じ向きの通過だけ
    //   ・スタートからの走行距離が、そのラインの位置 (distanceM) ±30% (最低 ±50 m) の中にあるときだけ
    //   ・スタートは止まった状態からの発進 (直前に停止がある通過) だけ
    //  1 つのログ = 1 セッション、走行 = 1 本目, 2 本目, ... (LapRecord.LapNo)
    // =====================================================================
    public static partial class LapAnalyzer
    {
        const double GymWindowRatio = 0.30;       // 直前に通ったラインからの走行距離の、ライン間の距離からのずれの許容 (比)
        const double GymWindowMinM = 30;          // 同 (最低 [m])
        const double GymTimeoutRatio = 3;         // 基準のタイムのこの倍を過ぎてもフィニッシュしなければ DNF
        const double GymCrossKmh = 3;             // これ未満の速度での通過は無視 (停止中の位置のふらつき)
        const double GymStopKmh = 3;              // これ未満を停止とみなす
        const double GymStopSec = 2;              // 停止がこの秒数以上続いたら「停止を含む」
        const double GymLaunchLookSec = 15;       // スタートの通過の、この秒数前までに停止があれば「止まった状態からの発進」

        struct GymCross
        {
            public double T;        // 通過時刻 (通算秒)
            public int Gate;        // 0 = Start, 1.. = Sector, 最後 = Finish
            public double D;        // ログの始めからの走行距離 [m]
            public double SpeedKmh;
        }

        sealed class GymRunState
        {
            public LapRecord Lap;
            public double D0;
            public int Next = 1;
            public int LastGate;            // 最後に通ったライン
            public double LastD;            // そのときの走行距離 (ログの始めから)
            public double?[] GateT;
            public List<int> Skipped = new List<int>();
        }

        static LapAnalysis AnalyzeGymkhana(CourseDefinition def, IList<List<GpsSample>> files, IList<string> names, LapParams prm)
        {
            if (def.Gates.Count < 2) throw new InvalidOperationException("コース定義にラインが足りません (スタートとフィニッシュが必要です)");
            LapAnalysis res = new LapAnalysis { IsGymkhana = true };

            List<Gate> gates = def.Gates.OrderBy(g => g.S).ToList();
            int G = gates.Count, M = G - 1;            // 区間の数 = ライン - 1
            res.SectorNames = Enumerable.Range(1, M).Select(i => "S" + i).ToArray();
            res.BestSector = new double?[M];
            double[] dist = gates.Select(g => g.S - gates[0].S).ToArray();
            double refSec = def.Stats != null && def.Stats.MedianLapSec > 0 ? def.Stats.MedianLapSec : Math.Max(30, dist[G - 1] / 10);
            double timeout = refSec * GymTimeoutRatio;

            double lat0 = def.Lat0, lon0 = def.Lon0;
            double ky = 111132.0, kx = 111320.0 * Math.Cos(lat0 * Math.PI / 180);
            double[] ax = new double[G], ay = new double[G], bx = new double[G], by = new double[G], tx = new double[G], ty = new double[G];
            for (int k = 0; k < G; k++)
            {
                Gate g = gates[k];
                ax[k] = (g.A.Lon - lon0) * kx; ay[k] = (g.A.Lat - lat0) * ky;
                bx[k] = (g.B.Lon - lon0) * kx; by[k] = (g.B.Lat - lat0) * ky;
                double hd;
                if (g.HeadingDeg.HasValue) hd = g.HeadingDeg.Value;
                else { LatLon p; def.PointAt(g.S, out p, out hd); }
                tx[k] = Math.Sin(hd * Math.PI / 180); ty[k] = Math.Cos(hd * Math.PI / 180);
            }
            res.Log.Add(string.Format(CultureInfo.InvariantCulture, "ジムカーナ: ライン {0} 本 (スタート → フィニッシュ {1:0} m)、DNF の判定 {2:0} 秒 (基準 {3:0.0} 秒の {4} 倍)",
                G, dist[G - 1], timeout, refSec, GymTimeoutRatio));

            for (int fi = 0; fi < files.Count; fi++)
            {
                string fname = fi < names.Count ? Path.GetFileName(names[fi]) : "";
                Seg sg = ToSeg(files[fi], lat0, lon0, kx, ky, prm);
                double[] cum = GymDistance(sg, prm);
                List<GymCross> cs = GymCrossings(sg, cum, G, ax, ay, bx, by, tx, ty, prm);
                res.Log.Add(string.Format("{0}: ライン通過 {1} 回 (向きの合うもの)", fname, cs.Count));

                SessionRecord sess = new SessionRecord { No = res.Sessions.Count + 1, FileName = fname, FilePath = fi < names.Count ? names[fi] : null };
                GymRunState cur = null;
                for (int i = 0; i < cs.Count; i++)
                {
                    GymCross c = cs[i];
                    if (cur != null && c.T - cur.Lap.StartT > timeout)
                    {
                        GymClose(sess, cur, sg, gates, "フィニッシュせず (" + timeout.ToString("0", CultureInfo.InvariantCulture) + " 秒経過)");
                        cur = null;
                    }
                    if (c.Gate == 0)
                    {
                        if (!GymStandingStart(sg, c.T)) continue;
                        if (cur != null)
                        {
                            // 前の走行がフィニッシュしないまま、次のスタート (止まった状態からの発進) が来た
                            if (c.T - cur.Lap.StartT < 10) continue;     // スタート直後の重複
                            GymClose(sess, cur, sg, gates, "フィニッシュせずに次のスタート");
                        }
                        cur = GymStart(sess, c, M, fname);
                        continue;
                    }
                    if (cur == null || c.Gate < cur.Next || !GymInWindow(cur, c, dist)) continue;
                    // 同じラインを窓の中で何度か通っていたら (コースが交差するところなど)、
                    // 次のラインを通るまでの中で、予想の走行距離に一番近い通過を使う
                    int best = i;
                    for (int j = i + 1; j < cs.Count && cs[j].T - cur.Lap.StartT <= timeout; j++)
                    {
                        GymCross d = cs[j];
                        if (d.Gate == 0 && GymStandingStart(sg, d.T)) break;
                        if (d.Gate > c.Gate && GymInWindow(cur, d, dist)) break;
                        if (d.Gate == c.Gate && GymInWindow(cur, d, dist) && GymMiss(cur, d, dist) < GymMiss(cur, cs[best], dist)) best = j;
                    }
                    i = best; c = cs[best];
                    for (int k = cur.Next; k < c.Gate; k++) cur.Skipped.Add(k);
                    cur.GateT[c.Gate] = c.T;
                    cur.Next = c.Gate + 1;
                    cur.LastGate = c.Gate; cur.LastD = c.D;
                    if (c.Gate == G - 1)
                    {
                        GymClose(sess, cur, sg, gates, null);
                        cur = null;
                    }
                }
                if (cur != null) GymClose(sess, cur, sg, gates, "ログの終わりまでにフィニッシュせず");

                if (sess.Laps.Count > 0)
                {
                    sess.StartT = sess.Laps[0].StartT;
                    sess.EndT = sess.Laps[sess.Laps.Count - 1].EndT;
                    res.Sessions.Add(sess);
                }
                res.Log.Add(string.Format("{0}: 走行 {1} 本 (完走 {2} 本)", fname, sess.Laps.Count, sess.Laps.Count(l => l.LapTime.HasValue)));
            }

            RecomputeGymkhana(res);
            return res;
        }

        // 直前に通ったラインからの走行距離が、ライン間の距離 (コース定義の distanceM の差) とどれだけずれているか [m]
        static double GymMiss(GymRunState st, GymCross c, double[] dist)
        {
            return Math.Abs((c.D - st.LastD) - (dist[c.Gate] - dist[st.LastGate]));
        }

        static bool GymInWindow(GymRunState st, GymCross c, double[] dist)
        {
            double span = dist[c.Gate] - dist[st.LastGate];
            return GymMiss(st, c, dist) <= Math.Max(GymWindowMinM, span * GymWindowRatio);
        }

        // ログの始めからの走行距離。止まっている間の位置のふらつきを足さないように、動いている区間だけ足す
        static double[] GymDistance(Seg s, LapParams prm)
        {
            int n = s.T.Length;
            double[] cum = new double[n];
            for (int i = 1; i < n; i++)
            {
                double dt = s.T[i] - s.T[i - 1];
                double d = Math.Sqrt(Math.Pow(s.X[i] - s.X[i - 1], 2) + Math.Pow(s.Y[i] - s.Y[i - 1], 2));
                double v = s.VR[i];
                bool moving = dt > 0 && dt <= prm.MaxGapSec && !double.IsNaN(v) && v >= GymStopKmh;
                cum[i] = cum[i - 1] + (moving ? d : 0);
            }
            return cum;
        }

        static List<GymCross> GymCrossings(Seg s, double[] cum, int G, double[] ax, double[] ay, double[] bx, double[] by, double[] tx, double[] ty, LapParams prm)
        {
            List<GymCross> list = new List<GymCross>();
            for (int i = 1; i < s.T.Length; i++)
            {
                double dt = s.T[i] - s.T[i - 1];
                if (dt <= 0 || dt > prm.MaxGapSec) continue;
                double v = s.VR[i];
                if (double.IsNaN(v) || v < GymCrossKmh) continue;
                double px = s.X[i - 1], py = s.Y[i - 1], dx = s.X[i] - px, dy = s.Y[i] - py;
                for (int k = 0; k < G; k++)
                {
                    if (dx * tx[k] + dy * ty[k] <= 0) continue;           // ラインの向きと逆の通過は無視
                    double ex = bx[k] - ax[k], ey = by[k] - ay[k];
                    double den = dx * ey - dy * ex;
                    if (Math.Abs(den) < 1e-9) continue;
                    double t = ((ax[k] - px) * ey - (ay[k] - py) * ex) / den;
                    double u = ((ax[k] - px) * dy - (ay[k] - py) * dx) / den;
                    if (t < 0 || t > 1 || u < 0 || u > 1) continue;
                    list.Add(new GymCross { T = s.T[i - 1] + t * dt, Gate = k, D = cum[i - 1] + t * (cum[i] - cum[i - 1]), SpeedKmh = v });
                }
            }
            return list.OrderBy(c => c.T).ThenBy(c => c.Gate).ToList();
        }

        // スタートの通過の直前 (GymLaunchLookSec 秒以内) に停止 (または記録の途切れ = Wintec は止まると記録しない) があるか
        static bool GymStandingStart(Seg s, double t)
        {
            int i = Array.BinarySearch(s.T, t);
            if (i < 0) i = ~i;
            for (int j = Math.Min(i, s.T.Length - 1); j >= 1 && s.T[j] >= t - GymLaunchLookSec; j--)
            {
                if (s.T[j] - s.T[j - 1] > 2) return true;
                double v = s.VR[j];
                if (!double.IsNaN(v) && v < GymStopKmh) return true;
            }
            return false;
        }

        static GymRunState GymStart(SessionRecord sess, GymCross c, int M, string fname)
        {
            LapRecord l = new LapRecord
            {
                Session = sess.No, LapNo = sess.Laps.Count + 1, Type = LapType.Normal, IsGymRun = true,
                StartT = c.T, EndT = c.T, StartGate = 0, FileName = fname,
                Sector = new double?[M], SectorMaxKmh = new double[M], SectorMinKmh = new double[M], IsBestSector = new bool[M], IsSessionBestSector = new bool[M]
            };
            sess.Laps.Add(l);
            GymRunState st = new GymRunState { Lap = l, D0 = c.D, LastGate = 0, LastD = c.D, GateT = new double?[M + 1] };
            st.GateT[0] = c.T;
            return st;
        }

        static void GymClose(SessionRecord sess, GymRunState st, Seg sg, List<Gate> gates, string dnfReason)
        {
            LapRecord l = st.Lap;
            int G = gates.Count, M = G - 1;
            List<string> notes = new List<string>();
            int last = 0;
            for (int k = 0; k < G; k++) if (st.GateT[k].HasValue) last = k;
            l.EndT = st.GateT[last].Value;
            if (dnfReason != null)
            {
                l.Dnf = true;
                notes.Add("DNF: " + dnfReason + (last > 0
                    ? string.Format(CultureInfo.InvariantCulture, " (最後に通ったライン: {0}、{1:0.0} 秒)", gates[last].Name, l.EndT - l.StartT)
                    : " (スタートの後、どのラインも通らず)"));
            }
            else l.LapTime = l.EndT - l.StartT;
            if (st.Skipped.Count > 0)
            {
                l.AutoMissCourse = true;
                notes.Add("ミスコースの疑い (" + string.Join(", ", st.Skipped.Select(k => gates[k].Name)) + " を通過せず)");
            }
            for (int k = 1; k < G; k++)
            {
                if (!st.GateT[k].HasValue || !st.GateT[k - 1].HasValue) continue;
                double ta = st.GateT[k - 1].Value, tb = st.GateT[k].Value;
                l.Sector[k - 1] = tb - ta;
                double mx, mn;
                SectorSpeeds(sg, ta, tb, GymSpeedAt(sg, ta), GymSpeedAt(sg, tb), out mx, out mn);
                l.SectorMaxKmh[k - 1] = mx; l.SectorMinKmh[k - 1] = mn;
            }
            // 最高速と停止 (スピンなど)
            double stopFrom = double.NaN, longest = 0;
            for (int i = 1; i < sg.T.Length; i++)
            {
                if (sg.T[i] <= l.StartT) continue;
                if (sg.T[i - 1] >= l.EndT) break;
                double v = sg.VR[i];
                if (!double.IsNaN(v) && v > l.MaxKmh) l.MaxKmh = v;
                bool stopped = sg.T[i] - sg.T[i - 1] > 2 || (!double.IsNaN(v) && v < GymStopKmh);
                if (stopped) { if (double.IsNaN(stopFrom)) stopFrom = sg.T[i - 1]; longest = Math.Max(longest, sg.T[i] - stopFrom); }
                else stopFrom = double.NaN;
            }
            if (longest >= GymStopSec)
            {
                l.HasStop = true;
                notes.Add(string.Format(CultureInfo.InvariantCulture, "停止を含む ({0:0.0} 秒)", longest));
            }
            l.AutoNote = string.Join(" / ", notes);
        }

        static double GymSpeedAt(Seg s, double t)
        {
            int i = Array.BinarySearch(s.T, t);
            if (i < 0) i = ~i;
            if (i >= s.T.Length) i = s.T.Length - 1;
            double v = s.VR[i];
            return double.IsNaN(v) ? 0 : v;
        }

        /// <summary>
        /// ジムカーナ: 手動の印 (有効 / ミスコース扱い) を反映して、備考・ベスト・ベストとの差・理論ベストを計算し直す。
        /// 解析の後と、画面で印を変えたときに呼ぶ。
        /// </summary>
        public static void RecomputeGymkhana(LapAnalysis res)
        {
            if (res == null || !res.IsGymkhana) return;
            int M = res.SectorNames.Length;
            res.BestLap = null; res.BestMaxKmh = null; res.TheoreticalBest = null; res.BestSector = new double?[M];
            foreach (SessionRecord s in res.Sessions)
            {
                s.BestLap = null; s.BestLapNo = 0; s.BestMaxKmh = null; s.BestSector = new double?[M];
                foreach (LapRecord l in s.Laps)
                {
                    l.MissCourse = l.Mark == LapRecord.MarkMiss || (l.AutoMissCourse && l.Mark != LapRecord.MarkValid);
                    string extra = l.Mark == LapRecord.MarkValid && l.AutoMissCourse ? "手動で有効 (ベストの対象)"
                        : l.Mark == LapRecord.MarkMiss ? "手動でミスコース扱い (ベストの対象外)" : "";
                    l.Note = extra.Length == 0 ? (l.AutoNote ?? "") : string.IsNullOrEmpty(l.AutoNote) ? extra : l.AutoNote + " / " + extra;
                    l.Gap = null;
                    l.IsBestLap = l.IsSessionBestLap = l.IsBestMax = l.IsSessionBestMax = false;
                    for (int k = 0; k < M; k++) { l.IsBestSector[k] = false; l.IsSessionBestSector[k] = false; }
                }
            }
            GymFinish(res, M);
        }

        // ベスト・ベストとの差・理論ベスト
        static void GymFinish(LapAnalysis res, int M)
        {
            foreach (SessionRecord s in res.Sessions)
            {
                s.BestSector = new double?[M];
                LapRecord best = null;
                foreach (LapRecord l in s.Laps)
                {
                    if (!CountsForBest(l)) continue;
                    if (best == null || l.LapTime.Value < best.LapTime.Value) best = l;
                    for (int k = 0; k < M; k++)
                        if (l.Sector[k].HasValue && (!s.BestSector[k].HasValue || l.Sector[k].Value < s.BestSector[k].Value)) s.BestSector[k] = l.Sector[k];
                }
                if (best != null) { s.BestLap = best.LapTime; s.BestLapNo = best.LapNo; best.IsSessionBestLap = true; }
                foreach (LapRecord l in s.Laps)
                {
                    if (!CountsForBest(l)) continue;
                    for (int k = 0; k < M; k++)
                        if (l.Sector[k].HasValue && s.BestSector[k].HasValue && Math.Abs(l.Sector[k].Value - s.BestSector[k].Value) < 1e-9) l.IsSessionBestSector[k] = true;
                }
                foreach (LapRecord l in s.Laps)
                    if (CountsForMaxSpeed(l) && (!s.BestMaxKmh.HasValue || l.MaxKmh > s.BestMaxKmh.Value)) s.BestMaxKmh = l.MaxKmh;
                foreach (LapRecord l in s.Laps)
                    if (CountsForMaxSpeed(l) && s.BestMaxKmh.HasValue && Math.Abs(l.MaxKmh - s.BestMaxKmh.Value) < 1e-9) l.IsSessionBestMax = true;
            }

            // ※ (IsSessionBest*) は、ジムカーナでは「ドライバー別のベスト」にする (複数人・複数ログの比較用)。
            // ドライバー名が無い走行は、ログ (セッション) ごと。セッション一覧のベスト (s.BestLap など) はログごとのまま
            foreach (IGrouping<string, KeyValuePair<SessionRecord, LapRecord>> grp in res.Sessions
                .SelectMany(s => s.Laps.Select(l => new KeyValuePair<SessionRecord, LapRecord>(s, l)))
                .GroupBy(x => { string d = DriverOf(x.Key, x.Value); return d.Length > 0 ? "d:" + d : "s:" + x.Key.No; }))
            {
                List<LapRecord> ls = grp.Select(x => x.Value).ToList();
                foreach (LapRecord l in ls)
                {
                    l.IsSessionBestLap = l.IsSessionBestMax = false;
                    for (int k = 0; k < M; k++) l.IsSessionBestSector[k] = false;
                }
                List<LapRecord> cb = ls.Where(CountsForBest).ToList();
                if (cb.Count > 0)
                {
                    double b = cb.Min(l => l.LapTime.Value);
                    foreach (LapRecord l in cb) if (Math.Abs(l.LapTime.Value - b) < 1e-9) l.IsSessionBestLap = true;
                }
                for (int k = 0; k < M; k++)
                {
                    List<LapRecord> wk = cb.Where(l => l.Sector[k].HasValue).ToList();
                    if (wk.Count == 0) continue;
                    double b = wk.Min(l => l.Sector[k].Value);
                    foreach (LapRecord l in wk) if (Math.Abs(l.Sector[k].Value - b) < 1e-9) l.IsSessionBestSector[k] = true;
                }
                List<LapRecord> mx = ls.Where(CountsForMaxSpeed).ToList();
                if (mx.Count > 0)
                {
                    double b = mx.Max(l => l.MaxKmh);
                    foreach (LapRecord l in mx) if (Math.Abs(l.MaxKmh - b) < 1e-9) l.IsSessionBestMax = true;
                }
            }

            foreach (SessionRecord s in res.Sessions)
            {
                if (s.BestLap.HasValue && (!res.BestLap.HasValue || s.BestLap.Value < res.BestLap.Value)) res.BestLap = s.BestLap;
                for (int k = 0; k < M; k++)
                    if (s.BestSector[k].HasValue && (!res.BestSector[k].HasValue || s.BestSector[k].Value < res.BestSector[k].Value)) res.BestSector[k] = s.BestSector[k];
                if (s.BestMaxKmh.HasValue && (!res.BestMaxKmh.HasValue || s.BestMaxKmh.Value > res.BestMaxKmh.Value)) res.BestMaxKmh = s.BestMaxKmh;
            }
            foreach (SessionRecord s in res.Sessions)
                foreach (LapRecord l in s.Laps)
                {
                    if (l.LapTime.HasValue && res.BestLap.HasValue) l.Gap = l.LapTime.Value - res.BestLap.Value;
                    if (CountsForMaxSpeed(l) && res.BestMaxKmh.HasValue && Math.Abs(l.MaxKmh - res.BestMaxKmh.Value) < 1e-9) l.IsBestMax = true;
                    if (!CountsForBest(l)) continue;
                    if (res.BestLap.HasValue && Math.Abs(l.LapTime.Value - res.BestLap.Value) < 1e-9) l.IsBestLap = true;
                    for (int k = 0; k < M; k++)
                        if (l.Sector[k].HasValue && res.BestSector[k].HasValue && Math.Abs(l.Sector[k].Value - res.BestSector[k].Value) < 1e-9) l.IsBestSector[k] = true;
                }
            // 理論ベスト (区間ベストの合計)。区間が 1 つ (セクターなし) のときはベストと同じなので出さない
            if (M >= 2 && res.BestSector.All(b => b.HasValue)) res.TheoreticalBest = res.BestSector.Sum(b => b.Value);
        }
    }
}
