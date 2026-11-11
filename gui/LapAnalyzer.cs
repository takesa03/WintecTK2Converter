using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Tk2Gui
{
    // =====================================================================
    //  ラップ・セクタータイムの計算
    //  コース定義 (コントロールライン + セクターライン) と走行ログから、各ラインの通過時刻を求め、
    //  セクタータイム・ラップタイムを計算する。
    //
    //  区間の名前: ライン (コントロール → Sector1 → Sector2 → ... → コントロール) の順に
    //    S1 = コントロール → Sector1, S2 = Sector1 → Sector2, ..., 最後 = 最後のセクター → コントロール
    //  (旧 GpsLapChecker と同じ。最後の区間はコントロールラインで終わる)
    //
    //  ・アウトラップ: セッションの最初の通過がコントロールラインでなければ、そのラインから計測を始める。
    //    先頭側の区間タイムはなく、ラップタイムも出さない (OUT)
    //  ・遅い周回: 周回または区間のタイムが通常より大きく遅い周回 (スピン等)。備考に理由を出し、ベストの対象から外す
    //  ・ピットイン: コントロールライン通過後、PitStopSec 秒以上どのラインも通らなければ、
    //    ピットで停止したとみなして新しいセッションにする (ドライバー交代など)。直前の周回は IN ラップ
    //  ・欠測・ラインの飛び越し・逆走などで順序が崩れた周回は「無効」にして理由を表示する
    // =====================================================================

    public enum LapType
    {
        Out,          // アウトラップ (途中のラインから計測開始)
        Normal,       // コントロールライン → コントロールラインの 1 周
        Partial,      // 途中から始まった周回 (欠測・飛びの後)
        Incomplete    // ログの終わりで途切れた周回
    }

    public sealed class LapParams
    {
        public double PitStopSec = 60;           // これ以上止まっていたら (記録の途切れも含む) ピットインとして、セッションを分ける
        public double MinCrossSpeedKmh = 8;      // これ未満の速度での通過は無視 (停止中の GPS のふらつき)
        public double MaxGapSec = 3;             // この秒数を超えて途切れた点の間は、通過を判定しない
        public double DebounceSec = 8;           // 同じラインの通過が続けて検出されたら、この秒数内は無視
        public double SlowLapRatio = 1.25;       // セッションの周回タイム中央値のこの倍を超える周回を「遅い周回」とする (スピン・コースアウトなど)
        public double SlowSectorRatio = 1.5;     // どれかの区間タイムが、セッション内のその区間の中央値のこの倍を超える周回も「遅い周回」とする
        public double StopSpeedKmh = 6;          // 区間の平均速度がこれ未満の間を「停止」とみなす (ロガーの記録が途切れている間も含む)
        public Dictionary<string, SessionEdits> Edits;   // 手動のセッション編集 (キー = SessionEdits.Key(ログのパス))
        public double MaxSectorSec = 120;        // 停止がないのに、この秒数を超えて次のラインを通らなければ「欠測」として周回を無効にする
    }

    public sealed class LapRecord
    {
        public int Session;
        public int LapNo;                         // Normal の周回番号 (1〜)。OUT は 0
        public LapType Type;
        public bool IsIn;                         // ピットインにつながる周回 (IN ラップ)
        public bool ContainsStop;                 // 手動で結合した停止を含む周回 (タイムに停止の時間が入るので、ベストの対象外)
        public bool Valid = true;                 // false = 順序が崩れて無効
        public string Note = "";
        public double StartT, EndT;               // 計測の開始・終了時刻 (ログの通算秒)
        public int StartGate;                     // 計測を始めたライン (0 = コントロール)
        public double?[] Sector;                  // 区間タイム [s]
        public double[] SectorMaxKmh, SectorMinKmh;
        public double? LapTime;                   // 1 周 (Normal のみ)
        public double? PartialTime;               // OUT など、途中から終わりまでの時間
        public double MaxKmh;
        public bool IsBestLap;                    // 全セッション通しての最速ラップ
        public bool IsSessionBestLap;
        public bool[] IsBestSector;               // 全セッション通しての区間ベスト
        public bool[] IsSessionBestSector;        // そのセッション内の区間ベスト
        public bool IsBestMax;                    // 全セッション通しての最高速ベスト (周回内の最高速度)
        public bool IsSessionBestMax;             // そのセッション内の最高速ベスト
        public string FileName;
        public string Label
        {
            get
            {
                if (!Valid) return "×";
                if (Type == LapType.Out) return "OUT";
                if (Type == LapType.Partial) return "途中";
                if (Type == LapType.Incomplete) return "未完";
                return IsIn ? LapNo + " (IN)" : LapNo.ToString(CultureInfo.InvariantCulture);
            }
        }
    }

    public sealed class SessionRecord
    {
        public int No;
        public string Driver = "";
        public string FileName;
        public string FilePath;                   // フルパス (ドライバー名の保存先を決めるため)
        public string EditNote = "";              // 手動編集の注記 (手動分割・結合済みなど)
        public double StartT, EndT;               // 最初・最後のライン通過時刻
        public List<LapRecord> Laps = new List<LapRecord>();
        public double? BestLap;
        public int BestLapNo;
        public double?[] BestSector;
        public double? BestMaxKmh;                // セッション内の最高速
        public int FullLaps { get { return Laps.Count(l => l.Type == LapType.Normal && l.Valid); } }
    }

    public sealed class LapAnalysis
    {
        public List<SessionRecord> Sessions = new List<SessionRecord>();
        public string[] SectorNames;              // "S1", "S2", ...
        public List<string> Log = new List<string>();
        public double? BestLap;
        public double?[] BestSector;
        public double? BestMaxKmh;                // 全体の最高速
    }

    public static class LapAnalyzer
    {
        struct Crossing
        {
            public double T;          // 通過時刻 (通算秒)
            public int Gate;          // 0 = コントロール, 1.. = コントロールから進行方向に数えたセクターライン
            public double SpeedKmh;
        }

        sealed class Seg
        {
            public double[] T, X, Y, V, VR, RS;   // VR: 最高速・通過速度の表示用。ロガーの記録した速度があればそれ (位置が 1 点飛んでも異常値にならない)、無ければ V
            // V: その点に着いたときの区間の速度 [km/h] (欠測をまたぐ場合は NaN)
        }

        public static string FormatTime(double? sec)
        {
            if (!sec.HasValue) return "";
            double s = sec.Value;
            int m = (int)(s / 60);
            return string.Format(CultureInfo.InvariantCulture, "{0}:{1:00.000}", m, s - m * 60);
        }

        public static LapAnalysis Analyze(CourseDefinition def, IList<List<GpsSample>> files, IList<string> names, LapParams prm)
        {
            if (def.Gates.Count < 2) throw new InvalidOperationException("コース定義にラインが足りません (コントロールラインとセクターライン 1 本以上が必要です)");
            LapAnalysis res = new LapAnalysis();

            // ラインを「コントロールから進行方向」の順に並べる
            Gate ctrl = def.Gates.FirstOrDefault(g => g.Name == "ControlLine") ?? def.Gates[0];
            List<Gate> ordered = new List<Gate> { ctrl };
            ordered.AddRange(def.Gates.Where(g => !ReferenceEquals(g, ctrl))
                .OrderBy(g => (((g.S - ctrl.S) % def.LengthM) + def.LengthM) % def.LengthM));
            int M = ordered.Count;                      // 区間の数 = ラインの数
            res.SectorNames = Enumerable.Range(1, M).Select(i => "S" + i).ToArray();
            res.BestSector = new double?[M];

            double lat0 = def.Lat0, lon0 = def.Lon0;
            double ky = 111132.0, kx = 111320.0 * Math.Cos(lat0 * Math.PI / 180);

            // ライン (局所平面) と進行方向
            double[] ax = new double[M], ay = new double[M], bx = new double[M], by = new double[M], tx = new double[M], ty = new double[M];
            for (int k = 0; k < M; k++)
            {
                Gate g = ordered[k];
                ax[k] = (g.A.Lon - lon0) * kx; ay[k] = (g.A.Lat - lat0) * ky;
                bx[k] = (g.B.Lon - lon0) * kx; by[k] = (g.B.Lat - lat0) * ky;
                LatLon p; double hd;
                def.PointAt(g.S, out p, out hd);
                tx[k] = Math.Sin(hd * Math.PI / 180); ty[k] = Math.Cos(hd * Math.PI / 180);
            }

            int sessionNo = 0;
            for (int fi = 0; fi < files.Count; fi++)
            {
                string fname = fi < names.Count ? Path.GetFileName(names[fi]) : "";
                Seg sg = ToSeg(files[fi], lat0, lon0, kx, ky, prm);
                List<Crossing> cs = FindCrossings(sg, M, ax, ay, bx, by, tx, ty, prm);
                res.Log.Add(string.Format("{0}: ライン通過 {1} 回", fname, cs.Count));
                RunFile(res, cs, sg, M, prm, fname, fi < names.Count ? names[fi] : null, ref sessionNo);
            }

            Finish(res, M, prm);
            return res;
        }

        static Seg ToSeg(List<GpsSample> list, double lat0, double lon0, double kx, double ky, LapParams prm)
        {
            int n = list.Count;
            Seg s = new Seg { T = new double[n], X = new double[n], Y = new double[n], V = new double[n], VR = new double[n], RS = new double[n] };
            for (int i = 0; i < n; i++)
            {
                s.T[i] = list[i].T; s.X[i] = (list[i].Lon - lon0) * kx; s.Y[i] = (list[i].Lat - lat0) * ky;
            }
            s.V[0] = double.NaN;
            for (int i = 1; i < n; i++)
            {
                double dt = s.T[i] - s.T[i - 1];
                s.V[i] = dt > 0 && dt <= prm.MaxGapSec
                    ? Math.Sqrt(Math.Pow(s.X[i] - s.X[i - 1], 2) + Math.Pow(s.Y[i] - s.Y[i - 1], 2)) / dt * 3.6
                    : double.NaN;
            }
            s.VR[0] = double.NaN;
            for (int i = 0; i < n; i++) s.RS[i] = list[i].SpeedKmh;
            for (int i = 1; i < n; i++)
                s.VR[i] = double.IsNaN(s.V[i]) || double.IsNaN(list[i].SpeedKmh) ? s.V[i] : list[i].SpeedKmh;
            return s;
        }

        // 各点の間の線分とラインの交点を求め、距離の比で通過時刻を補間する
        static List<Crossing> FindCrossings(Seg s, int M, double[] ax, double[] ay, double[] bx, double[] by, double[] tx, double[] ty, LapParams prm)
        {
            List<Crossing> list = new List<Crossing>();
            double[] last = new double[M];
            for (int k = 0; k < M; k++) last[k] = double.NegativeInfinity;
            for (int i = 1; i < s.T.Length; i++)
            {
                double dt = s.T[i] - s.T[i - 1];
                if (dt <= 0 || dt > prm.MaxGapSec) continue;
                double v = s.VR[i];
                if (double.IsNaN(v) || v < prm.MinCrossSpeedKmh) continue;
                double px = s.X[i - 1], py = s.Y[i - 1], dx = s.X[i] - px, dy = s.Y[i] - py;
                for (int k = 0; k < M; k++)
                {
                    if (dx * tx[k] + dy * ty[k] <= 0) continue;           // 進行方向と逆の通過は無視
                    double ex = bx[k] - ax[k], ey = by[k] - ay[k];
                    double den = dx * ey - dy * ex;
                    if (Math.Abs(den) < 1e-9) continue;
                    double t = ((ax[k] - px) * ey - (ay[k] - py) * ex) / den;     // 点の間の位置 (0..1)
                    double u = ((ax[k] - px) * dy - (ay[k] - py) * dx) / den;     // ライン上の位置 (0..1)
                    if (t < 0 || t > 1 || u < 0 || u > 1) continue;
                    double tc = s.T[i - 1] + t * dt;
                    if (tc - last[k] < prm.DebounceSec) continue;
                    last[k] = tc;
                    list.Add(new Crossing { T = tc, Gate = k, SpeedKmh = v });
                }
            }
            return list.OrderBy(c => c.T).ToList();
        }

        static LapRecord NewLap(SessionRecord sess, Crossing c, int M, string fname, bool outLap)
        {
            LapRecord l = new LapRecord
            {
                Session = sess.No, StartT = c.T, EndT = c.T, StartGate = c.Gate, FileName = fname,
                Sector = new double?[M], SectorMaxKmh = new double[M], SectorMinKmh = new double[M], IsBestSector = new bool[M], IsSessionBestSector = new bool[M],
                Type = outLap ? LapType.Out : LapType.Normal
            };
            if (outLap)
                l.Note = c.Gate == 0 ? "アウトラップ (停止からの発進)" : "アウトラップ (Sector" + c.Gate + " から計測)";
            sess.Laps.Add(l);
            return l;
        }

        // 区間 (a → b) の最高・最低速度
        static void SectorSpeeds(Seg s, double ta, double tb, double va, double vb, out double max, out double min)
        {
            max = Math.Max(va, vb); min = Math.Min(va, vb);
            for (int i = 1; i < s.T.Length; i++)
            {
                if (s.T[i] <= ta) continue;
                if (s.T[i] >= tb) break;
                double v = s.VR[i];
                if (double.IsNaN(v)) continue;
                if (v > max) max = v;
                if (v < min) min = v;
            }
        }

        // 連続した停止 (平均速度が StopSpeedKmh 未満) のうち、PitStopSec 以上続いたものの [開始, 終了] 時刻。
        // ロガーは止まっている間は記録しないので、記録の途切れも停止として扱う
        static List<double[]> FindStops(Seg s, LapParams prm)
        {
            List<double[]> stops = new List<double[]>();
            double runStart = double.NaN, runEnd = double.NaN;
            for (int i = 1; i < s.T.Length; i++)
            {
                double dt = s.T[i] - s.T[i - 1];
                double v = dt > 0 ? Math.Sqrt(Math.Pow(s.X[i] - s.X[i - 1], 2) + Math.Pow(s.Y[i] - s.Y[i - 1], 2)) / dt * 3.6 : 0;
                // 記録の間隔が短いログ (5Hz など) は、停止中の位置のふらつきで速度が大きく出るので、ロガーが記録した速度があればそれを使う
                if (dt > 0 && dt <= prm.MaxGapSec && !double.IsNaN(s.RS[i])) v = s.RS[i];
                if (v < prm.StopSpeedKmh)
                {
                    if (double.IsNaN(runStart)) runStart = s.T[i - 1];
                    runEnd = s.T[i];
                }
                else
                {
                    if (!double.IsNaN(runStart) && runEnd - runStart >= prm.PitStopSec) stops.Add(new[] { runStart, runEnd });
                    runStart = double.NaN;
                }
            }
            if (!double.IsNaN(runStart) && runEnd - runStart >= prm.PitStopSec) stops.Add(new[] { runStart, runEnd });
            return stops;
        }

        static void RunFile(LapAnalysis res, List<Crossing> cs, Seg sg, int M, LapParams prm, string fname, string fpath, ref int sessionNo)
        {
            List<double[]> stops = FindStops(sg, prm);
            SessionRecord sess = null;
            LapRecord lap = null;
            bool havePrev = false;
            Crossing prev = new Crossing();
            double logStart = sg.T[0], logEnd = sg.T[sg.T.Length - 1];
            int lapNo = 0;
            bool manualStart = false;
            bool fromStop = stops.Any(st => st[0] <= logStart + 2);   // ログの最初から止まっている = 停止からの発進
            // コントロールラインから次のラインまでの、普段の所要時間 (中央値)
            List<double> firstSec = new List<double>();
            for (int k = 1; k < cs.Count; k++)
                if (cs[k - 1].Gate == 0 && cs[k].Gate == 1 % M) firstSec.Add(cs[k].T - cs[k - 1].T);
            double pitWait = Math.Max(prm.PitStopSec, firstSec.Count >= 3 ? 2 * Median(firstSec) : 0);
            SessionEdits ed = null;
            if (prm.Edits != null && fpath != null) prm.Edits.TryGetValue(SessionEdits.Key(fpath), out ed);

            foreach (Crossing c in cs)
            {
                bool mergeSkip = false, splitAfter = false;
                if (havePrev)
                {
                    // 2 つの通過の間に長い停止があった = ピットイン (ドライバー交代など)
                    bool stopBetween = stops.Any(st => st[0] >= prev.T - 2 && st[0] <= c.T);
                    // コントロールライン通過後、なかなか次のラインを通らない = ピットイン (ピットレーンを低速で走っている間など)。
                    // 待つ時間は「停止とみなす秒数」と「普段の所要時間の 2 倍」の大きいほう (鈴鹿のように 1 区間が長いコースで、毎周これに当たらないように)
                    bool autoSplit = stopBetween || (prev.Gate == 0 && c.T - prev.T > pitWait);
                    // 手動の編集: 結合 (自動の分割を取りやめる) と、分割 (この通過から新しいセッションにする)
                    bool merged = autoSplit && ed != null && ed.HasMergeAt(c.T);
                    bool manualSplit = !autoSplit && ed != null && ed.Splits.Any(sp => sp > prev.T + 0.0005 && sp <= c.T + 0.005);
                    if (merged) mergeSkip = true;
                    // コントロールラインでの手動分割は、この通過で前の周回 (IN ラップ) を最後まで計測してから、ここから新しいセッションにする
                    splitAfter = manualSplit && c.Gate == 0;
                    if ((autoSplit && !merged) || (manualSplit && !splitAfter))
                    {
                        if (lap != null && lap.Sector.All(v => !v.HasValue)) sess.Laps.Remove(lap);
                        else if (lap != null && prev.Gate != 0)
                        {
                            // コントロールラインに着く前に止まった: この周回は IN (途中で停止) として残す
                            lap.Type = LapType.Incomplete; lap.IsIn = true;
                            lap.Note = (lap.Note.Length > 0 ? lap.Note + " / " : "") + "ピットイン (コントロールライン手前で停止)";
                        }
                        CloseSession(sess, prev.T, autoSplit && prev.Gate == 0);
                        sess = null; lap = null; havePrev = false; fromStop = autoSplit;   // 手動の分割は、止まっていないので「停止からの発進」ではない
                        manualStart = manualSplit;
                    }
                }
                if (sess == null)
                {
                    sess = new SessionRecord { No = ++sessionNo, FileName = fname, FilePath = fpath == null ? null : Path.GetFullPath(fpath), StartT = c.T };
                    if (manualStart) { sess.EditNote = "手動分割"; manualStart = false; }
                    res.Sessions.Add(sess);
                    lapNo = 0;
                }

                if (!havePrev)
                {
                    // セッション最初の通過。停止からの発進、またはコントロール以外のライン (Sector1 など) からの開始は
                    // アウトライン扱い。それ以外 (走行中のログ開始でコントロールを通過) は 1 周目の開始
                    lap = NewLap(sess, c, M, fname, fromStop || c.Gate != 0);
                    fromStop = false;
                }
                else
                {
                    double dt = c.T - prev.T;
                    int fwd = (c.Gate - prev.Gate + M) % M;              // 次のラインまでいくつ先か (1 が普通)
                    bool skipsOnly = fwd >= 1 && prev.Gate + fwd <= M;   // コントロールを飛ばさず、セクターラインだけを飛ばした
                    if (mergeSkip)
                    {
                        // 手動で結合した: 停止はなかったものとして、前のラインの通過から計測を続ける (停止の時間もその区間に入る)。
                        // この周回は、停止の時間を含むのでベストの対象にしない
                        lap.ContainsStop = true;
                        lap.Note = (lap.Note.Length > 0 ? lap.Note + " / " : "") + "結合: 停止を含む周回 (ベスト対象外)";
                        if (!sess.EditNote.Contains("結合")) sess.EditNote = (sess.EditNote.Length > 0 ? sess.EditNote + " / " : "") + "結合済み";
                    }
                    if (dt > prm.MaxSectorSec && !mergeSkip)
                    {
                        // 停止もないのに長く途切れた (欠測): 周回を無効にして、ここから計測し直す
                        lap.Valid = false;
                        lap.Note = string.Format("{0} 秒間のデータ欠測", dt.ToString("0"));
                        lap.EndT = prev.T;
                        if (lap.Sector.All(v => !v.HasValue)) sess.Laps.Remove(lap);
                        lap = NewLap(sess, c, M, fname, false);
                        if (c.Gate != 0) lap.Type = LapType.Partial;
                    }
                    else if (skipsOnly)
                    {
                        int first = prev.Gate;                           // 終わった区間の番号 (最初)
                        int last = c.Gate == 0 ? M - 1 : c.Gate - 1;     // 終わった区間の番号 (最後)
                        double mx, mn;
                        SectorSpeeds(sg, prev.T, c.T, prev.SpeedKmh, c.SpeedKmh, out mx, out mn);
                        if (fwd == 1)
                        {
                            lap.Sector[last] = dt;
                            lap.SectorMaxKmh[last] = mx; lap.SectorMinKmh[last] = mn;
                        }
                        else
                        {
                            // セクターラインの通過を検出できなかった: その間の区間タイムは出さない (周回は有効)
                            string miss = string.Join(", ", Enumerable.Range(first + 1, fwd - 1).Select(g => "Sector" + g));
                            lap.Note = (lap.Note.Length > 0 ? lap.Note + " / " : "") + miss + " の通過を検出できず (区間タイムなし)";
                        }
                        lap.MaxKmh = Math.Max(lap.MaxKmh, mx);
                        lap.EndT = c.T;
                        if (c.Gate == 0)
                        {
                            // 1 周の終わり (コントロールライン)
                            if (lap.Type == LapType.Normal && lap.Valid)
                            {
                                lap.LapTime = c.T - lap.StartT;
                                lap.LapNo = ++lapNo;
                            }
                            else lap.PartialTime = c.T - lap.StartT;
                            lap = NewLap(sess, c, M, fname, false);
                        }
                    }
                    else
                    {
                        // 順序が崩れた (逆走・コース外・コントロールの検出漏れなど): この周回は無効にして、ここから計測し直す
                        lap.Valid = false;
                        lap.Note = string.Format("{0} の次に {1} を通過 (コントロールライン検出漏れ・逆走など)", GateName(prev.Gate, M), GateName(c.Gate, M));
                        lap.EndT = prev.T;
                        if (lap.Sector.All(v => !v.HasValue)) sess.Laps.Remove(lap);
                        lap = NewLap(sess, c, M, fname, false);
                        if (c.Gate != 0) lap.Type = LapType.Partial;
                    }
                }
                prev = c; havePrev = true;

                if (splitAfter)
                {
                    // この通過で始めた周回は新しいセッションのものにする。いまのセッションは、この通過で終える (最後の周回は IN)
                    if (lap != null) sess.Laps.Remove(lap);
                    CloseSession(sess, c.T, true);
                    sess = new SessionRecord { No = ++sessionNo, FileName = fname, FilePath = fpath == null ? null : Path.GetFullPath(fpath), StartT = c.T, EditNote = "手動分割" };
                    res.Sessions.Add(sess);
                    lapNo = 0;
                    lap = NewLap(sess, c, M, fname, false);
                }
            }

            if (sess != null)
            {
                // ログの終わり: コントロール通過後に長く止まっていれば IN。続きのない周回は「未完」
                bool inLap = havePrev && prev.Gate == 0 && stops.Any(st => st[0] >= prev.T - 2);
                if (lap != null && lap.Sector.All(v => !v.HasValue)) sess.Laps.Remove(lap);
                else if (lap != null && lap.Valid && lap.Type == LapType.Normal && !lap.LapTime.HasValue) lap.Type = LapType.Incomplete;
                else if (lap != null && lap.Type == LapType.Out && !lap.PartialTime.HasValue) lap.Type = LapType.Incomplete;
                CloseSession(sess, havePrev ? prev.T : sess.StartT, inLap);
            }
        }
        static string GateName(int gate, int M)
        {
            return gate == 0 ? "コントロールライン" : "Sector" + gate;
        }

        static void CloseSession(SessionRecord sess, double endT, bool pitIn)
        {
            sess.EndT = endT;
            if (pitIn)
            {
                LapRecord lastLap = sess.Laps.LastOrDefault(l => l.Type == LapType.Normal && l.LapTime.HasValue);
                if (lastLap != null && lastLap.EndT >= endT - 0.001) lastLap.IsIn = true;
            }
        }

        // ---- ベスト・注記 ----
        static void Finish(LapAnalysis res, int M, LapParams prm)
        {
            // ライン通過がほとんどなかったセッション (停止の前後に 1 回通過しただけなど) は除き、番号を振り直す
            res.Sessions.RemoveAll(s => s.Laps.Count == 0);
            for (int i = 0; i < res.Sessions.Count; i++)
            {
                res.Sessions[i].No = i + 1;
                foreach (LapRecord l in res.Sessions[i].Laps) l.Session = i + 1;
            }
            foreach (SessionRecord s in res.Sessions)
            {
                // 遅い周回の注記 (スピン・停止・コースアウトなど)。
                // セッション内の周回タイムの中央値より大きく遅い周回、または、どれかの区間がその区間の中央値より大きく遅い周回
                List<LapRecord> full = s.Laps.Where(l => l.Valid && l.Type == LapType.Normal && l.LapTime.HasValue).ToList();
                if (full.Count >= 3)
                {
                    double med = Median(full.Select(l => l.LapTime.Value));
                    double[] secMed = new double[M];
                    for (int k = 0; k < M; k++)
                    {
                        List<double> v = full.Where(l => l.Sector[k].HasValue).Select(l => l.Sector[k].Value).ToList();
                        secMed[k] = v.Count >= 3 ? Median(v) : 0;
                    }
                    foreach (LapRecord l in full)
                    {
                        List<string> why = new List<string>();
                        if (l.LapTime.Value > med * prm.SlowLapRatio)
                            why.Add(string.Format(CultureInfo.InvariantCulture, "周回が中央値の {0:0.00} 倍", l.LapTime.Value / med));
                        for (int k = 0; k < M; k++)
                            if (secMed[k] > 0 && l.Sector[k].HasValue && l.Sector[k].Value > secMed[k] * prm.SlowSectorRatio)
                                why.Add(string.Format(CultureInfo.InvariantCulture, "{0} が通常の {1:0.0} 倍", res.SectorNames[k], l.Sector[k].Value / secMed[k]));
                        if (why.Count > 0)
                            l.Note = (l.Note.Length > 0 ? l.Note + " / " : "") + "遅い周回 (" + string.Join(", ", why) + ")";
                    }
                }
                // セッション内のベスト (遅い周回は対象外)
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

                // セッション内の区間ベストのマーキング
                foreach (LapRecord l in s.Laps)
                {
                    if (!CountsForBest(l)) continue;
                    for (int k = 0; k < M; k++)
                        if (l.Sector[k].HasValue && s.BestSector[k].HasValue && Math.Abs(l.Sector[k].Value - s.BestSector[k].Value) < 1e-9) l.IsSessionBestSector[k] = true;
                }

                // セッション内の最高速ベスト (有効な 1 周の中の最高速度。遅い周回・IN ラップも含む。実際に出た速度なので)
                foreach (LapRecord l in s.Laps)
                    if (CountsForMaxSpeed(l) && (!s.BestMaxKmh.HasValue || l.MaxKmh > s.BestMaxKmh.Value)) s.BestMaxKmh = l.MaxKmh;
                foreach (LapRecord l in s.Laps)
                    if (CountsForMaxSpeed(l) && s.BestMaxKmh.HasValue && Math.Abs(l.MaxKmh - s.BestMaxKmh.Value) < 1e-9) l.IsSessionBestMax = true;
            }

            // 全セッション通しのベスト
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
                    if (CountsForMaxSpeed(l) && res.BestMaxKmh.HasValue && Math.Abs(l.MaxKmh - res.BestMaxKmh.Value) < 1e-9) l.IsBestMax = true;
                    if (!CountsForBest(l)) continue;
                    if (res.BestLap.HasValue && Math.Abs(l.LapTime.Value - res.BestLap.Value) < 1e-9) l.IsBestLap = true;
                    for (int k = 0; k < M; k++)
                        if (l.Sector[k].HasValue && res.BestSector[k].HasValue && Math.Abs(l.Sector[k].Value - res.BestSector[k].Value) < 1e-9) l.IsBestSector[k] = true;
                }
        }

        static double Median(IEnumerable<double> values)
        {
            List<double> v = values.OrderBy(x => x).ToList();
            int n = v.Count;
            return n == 0 ? 0 : (n % 2 == 1 ? v[n / 2] : (v[n / 2 - 1] + v[n / 2]) / 2);
        }

        // ベストの対象: 有効な 1 周 (OUT・途中・未完・遅い周回は対象外)
        static bool CountsForBest(LapRecord l)
        {
            return l.Valid && l.Type == LapType.Normal && l.LapTime.HasValue && !l.Note.Contains("遅い") && !l.ContainsStop;
        }

        // 最高速ベストの対象: 有効な 1 周 (速度は実際に出た値なので、遅い周回・IN ラップも含む)
        static bool CountsForMaxSpeed(LapRecord l)
        {
            return l.Valid && l.Type == LapType.Normal && l.LapTime.HasValue && l.MaxKmh > 0;
        }

        // ---- CSV ----
        public static void WriteCsv(LapAnalysis res, string path, double jstOffsetHours)
        {
            DateTime epoch = new DateTime(2000, 1, 1);
            StringBuilder sb = new StringBuilder();
            sb.Append("Session,Driver,File,Lap,Type,Valid,StartTime");
            foreach (string n in res.SectorNames) sb.Append(',').Append(n);
            sb.Append(",LapSec,LapTime");
            foreach (string n in res.SectorNames) sb.Append(',').Append(n).Append("MaxKmh");
            sb.Append(",MaxKmh,BestLap,BestSectors,BestMaxKmh,Note\r\n");
            foreach (SessionRecord s in res.Sessions)
            {
                foreach (LapRecord l in s.Laps)
                {
                    string type = l.Type == LapType.Out ? "OUT" : l.Type == LapType.Normal ? (l.IsIn ? "IN" : "LAP") : l.Type == LapType.Partial ? "PARTIAL" : "INCOMPLETE";
                    sb.Append(s.No).Append(',').Append(Csv(s.Driver)).Append(',').Append(Csv(l.FileName)).Append(',')
                      .Append(l.Type == LapType.Normal && l.Valid ? l.LapNo.ToString(CultureInfo.InvariantCulture) : "").Append(',')
                      .Append(type).Append(',').Append(l.Valid ? "1" : "0").Append(',')
                      .Append(epoch.AddSeconds(l.StartT + jstOffsetHours * 3600).ToString("HH:mm:ss", CultureInfo.InvariantCulture));
                    for (int k = 0; k < res.SectorNames.Length; k++) sb.Append(',').Append(Sec(l.Sector[k]));
                    sb.Append(',').Append(Sec(l.LapTime ?? l.PartialTime)).Append(',').Append(l.LapTime.HasValue ? FormatTime(l.LapTime) : "");
                    for (int k = 0; k < res.SectorNames.Length; k++)
                        sb.Append(',').Append(l.Sector[k].HasValue ? l.SectorMaxKmh[k].ToString("0.0", CultureInfo.InvariantCulture) : "");
                    sb.Append(',').Append(l.MaxKmh > 0 ? l.MaxKmh.ToString("0.0", CultureInfo.InvariantCulture) : "")
                      .Append(',').Append(l.IsBestLap ? "*" : l.IsSessionBestLap ? "s" : "");
                    // 区間ベストのマーク: S2* = 全体ベスト, S3s = セッション内ベスト
                    List<string> bs = new List<string>();
                    for (int k = 0; k < res.SectorNames.Length; k++)
                        if (l.IsBestSector[k]) bs.Add(res.SectorNames[k] + "*");
                        else if (l.IsSessionBestSector[k]) bs.Add(res.SectorNames[k] + "s");
                    sb.Append(',').Append(string.Join(" ", bs))
                      .Append(',').Append(l.IsBestMax ? "*" : l.IsSessionBestMax ? "s" : "")
                      .Append(',').Append(Csv(l.Note)).Append("\r\n");
                }
            }
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        }

        static string Sec(double? v)
        {
            return v.HasValue ? v.Value.ToString("0.000", CultureInfo.InvariantCulture) : "";
        }

        static string Csv(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }
    }
}
