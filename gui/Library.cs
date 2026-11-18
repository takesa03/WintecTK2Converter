using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Tk2Nmea;

namespace Tk2Gui
{
    // =====================================================================
    //  ライブラリ (docs/data_library_design.md 3・7 章)
    //  最上位 (またはコースフォルダ) から *.course.json と *.log.json を探し、
    //  コース定義 (条件) ごとに、各ログの laps.json から周回の一覧を作る。
    //  laps.json が無い・古い (指紋が合わない) ログは、ログとコース定義から解析し直して書き直す。
    //  比べるときは、そのログを解析し直して本物の SessionRecord / LapRecord を作る (表示のベスト・履歴はそのセッションのもの)。
    // =====================================================================

    public sealed class LibCourse
    {
        public string Path;                 // コース定義のファイル
        public string Dir;                  // コースフォルダ
        public CourseDefinition Def;
        public List<string> Logs = new List<string>();   // このコースフォルダの下のログ
        public string Label { get { return Def.Name + "  [" + System.IO.Path.GetFileNameWithoutExtension(System.IO.Path.GetFileNameWithoutExtension(Path)) + "]" + (Def.IsGymkhana ? "  ジムカーナ" : ""); } }
    }

    public sealed class LibLap
    {
        public LibCourse Course;
        public string Log;                  // ログのファイル
        public string LogFolder;            // ログフォルダの名前 (表示用)
        public int SessionNo;               // そのログの中で何番目のセッションか (1〜)
        public LapsSession Session;
        public LapsLap Lap;
        public string Driver;
        public int Videos;                  // この周回が映っている動画の数 (表示を焼き込んだものは除く)
        public bool Heart;                  // 心拍の FIT が合わせてあるセッション
        public bool IsBest, IsSessionBest;  // このコース定義での全ログ通しのベスト / セッションのベスト
        public double? Time { get { return Lap.LapTime; } }
        public DateTime Local { get { return new DateTime(2000, 1, 1).AddSeconds(Lap.StartT).AddHours(9); } }
        public string Key { get { return Log + "|" + Lap.StartT.ToString("F3", CultureInfo.InvariantCulture); } }
        public string Short
        {
            get
            {
                string k = Lap.Type == "Run" ? "H" + Lap.No : "L" + Lap.No;
                return Local.ToString("MM/dd", CultureInfo.InvariantCulture) + " S" + SessionNo + " " + k + (Driver.Length > 0 ? " " + Driver : "") + "  " + LapAnalyzer.FormatTime(Time);
            }
        }
    }

    public static class Library
    {
        /// <summary>フォルダの下 (深さ 4 まで) のコース定義を探す。それぞれのコースフォルダの下のログ (*.log.json) も集める</summary>
        public static List<LibCourse> Scan(string root, List<string> notes)
        {
            List<LibCourse> o = new List<LibCourse>();
            foreach (string f in Find(root, "*.course.json", 4))
            {
                try
                {
                    CourseDefinition d = CourseDefinition.Load(f);
                    if (string.IsNullOrEmpty(d.Id)) continue;
                    LibCourse c = new LibCourse { Path = f, Dir = System.IO.Path.GetDirectoryName(f), Def = d };
                    foreach (string lj in Find(c.Dir, "*" + LogFile.Suffix, 2))
                    {
                        string log = lj.Substring(0, lj.Length - LogFile.Suffix.Length);
                        if (File.Exists(log)) c.Logs.Add(log);
                    }
                    o.Add(c);
                }
                catch (Exception ex) { notes.Add("コース定義を読めません: " + f + " (" + ex.Message + ")"); }
            }
            // 同じ id の定義が 2 つあれば (コピーなど) 1 つにまとめる
            return o.GroupBy(c => c.Def.Id).Select(g =>
            {
                LibCourse first = g.First();
                foreach (LibCourse c in g.Skip(1)) foreach (string l in c.Logs) if (!first.Logs.Contains(l, StringComparer.OrdinalIgnoreCase)) first.Logs.Add(l);
                return first;
            }).OrderBy(c => c.Def.Name).ToList();
        }

        static IEnumerable<string> Find(string dir, string pattern, int depth)
        {
            List<string> o = new List<string>();
            try
            {
                o.AddRange(Directory.GetFiles(dir, pattern));
                if (depth > 0)
                    foreach (string d in Directory.GetDirectories(dir))
                    {
                        string n = System.IO.Path.GetFileName(d);
                        if (n.Equals("videos", StringComparison.OrdinalIgnoreCase) || n.Equals(HeartStore.Folder, StringComparison.OrdinalIgnoreCase)) continue;
                        o.AddRange(Find(d, pattern, depth - 1));
                    }
            }
            catch (Exception) { }
            return o;
        }

        /// <summary>ログの解析の条件 (log.json に無ければ画面の既定と同じ値)</summary>
        public static LapParams ParamsOf(string log)
        {
            LogData d = LogFile.Load(log);
            LapParams p = new LapParams { PitStopSec = 30, MinCrossSpeedKmh = 8, SlowLapRatio = 1.25 };
            if (d.Params != null) { p.PitStopSec = d.Params.PitStopSec; p.MinCrossSpeedKmh = d.Params.MinCrossSpeedKmh; p.SlowLapRatio = d.Params.SlowLapRatio; }
            p.Edits = new Dictionary<string, SessionEdits> { { SessionEdits.Key(log), SessionEditStore.Load(log) } };
            return p;
        }

        /// <summary>1 つのログを、そのログの条件で解析する (ドライバー名・走行のメモも戻す)。laps.json も書き直す</summary>
        public static LapAnalysis Analyze(LibCourse c, string log, List<GpsSample> gps, List<string> notes)
        {
            LapAnalysis r = LapAnalyzer.Analyze(c.Def, new List<List<GpsSample>> { gps }, new List<string> { log }, ParamsOf(log));
            DriverStore.Apply(r);
            if (r.IsGymkhana) { RunStore.Apply(r); LapAnalyzer.RecomputeGymkhana(r); }
            LapsStore.SaveTo(c.Def, c.Path, r, log, log, notes);
            notes.AddRange(LogFile.TakeNotes(log));
            return r;
        }

        /// <summary>
        /// コース定義の周回の一覧。laps.json が無い・古いログは解析し直す (progress: 何本目のログか)。
        /// 一覧に出すのは、タイムのある周回 (サーキット = 有効な Normal、ジムカーナ = DNF でない走行)
        /// </summary>
        public static List<LibLap> Laps(LibCourse c, List<string> notes, Action<int, int, string> progress)
        {
            List<LibLap> o = new List<LibLap>();
            for (int k = 0; k < c.Logs.Count; k++)
            {
                string log = c.Logs[k];
                progress(k, c.Logs.Count, System.IO.Path.GetFileName(log));
                LapsResult res = null;
                try
                {
                    LapsFile f = LapsStore.Load(log);
                    res = f == null ? null : f.Results.FirstOrDefault(x => x.Course == c.Def.Id);
                    if (!LapsStore.IsCurrent(res, c.Def, log))
                    {
                        notes.Add((res == null ? "解析の結果が無いので解析します: " : "解析の結果が古い (コース定義・条件・ログが変わった) ので解析し直します: ") + System.IO.Path.GetFileName(log));
                        Analyze(c, log, GpsLoader.Load(log), notes);
                        f = LapsStore.Load(log);
                        res = f == null ? null : f.Results.FirstOrDefault(x => x.Course == c.Def.Id);
                    }
                }
                catch (Exception ex) { notes.Add("解析できません: " + System.IO.Path.GetFileName(log) + " (" + ex.Message + ")"); continue; }
                if (res == null) continue;
                List<VideoStore.Entry> vids = VideoStore.Load(log).Where(v => !v.Overlay && File.Exists(v.Path)).ToList();
                List<HeartStore.Entry> hearts = LogFile.Load(log).Heart ?? new List<HeartStore.Entry>();
                string folder = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(log));
                for (int si = 0; si < res.Sessions.Count; si++)
                {
                    LapsSession s = res.Sessions[si];
                    bool heart = hearts.Any(h => Math.Min(h.EndT, s.EndT) - Math.Max(h.StartT, s.StartT) >= 0.5 * Math.Min(h.EndT - h.StartT, s.EndT - s.StartT));
                    foreach (LapsLap l in s.Laps)
                    {
                        bool timed = l.Type == "Run" ? !l.Dnf && l.LapTime.HasValue : l.Type == "Normal" && l.Valid && l.LapTime.HasValue;
                        if (!timed) continue;
                        o.Add(new LibLap
                        {
                            Course = c, Log = log, LogFolder = folder, SessionNo = si + 1, Session = s, Lap = l,
                            Driver = !string.IsNullOrEmpty(l.Driver) ? l.Driver : (s.Driver ?? ""),
                            Videos = vids.Count(v => v.T0 <= l.StartT && v.EndT >= l.EndT),
                            Heart = heart,
                        });
                    }
                }
            }
            progress(c.Logs.Count, c.Logs.Count, "");
            // ベスト: ミスコースは対象外
            List<LibLap> ok = o.Where(x => !x.Lap.MissCourse).ToList();
            if (ok.Count > 0) { double b = ok.Min(x => x.Time.Value); foreach (LibLap x in ok) x.IsBest = Math.Abs(x.Time.Value - b) < 1e-6; }
            foreach (var g in ok.GroupBy(x => x.Session))
            {
                double b = g.Min(x => x.Time.Value);
                foreach (LibLap x in g) x.IsSessionBest = Math.Abs(x.Time.Value - b) < 1e-6;
            }
            return o;
        }

        /// <summary>この周回が映っている動画の候補 (既定 = VideoStore.Find が選ぶものを先頭に)。表示を焼き込んだものは除く</summary>
        public static List<VideoStore.Entry> VideosFor(LibLap l)
        {
            List<VideoStore.Entry> all = VideoStore.Load(l.Log).Where(v => !v.Overlay && File.Exists(v.Path) && v.T0 < l.Lap.EndT + 5 && v.EndT > l.Lap.StartT - 5).ToList();
            VideoStore.Entry def = VideoStore.Find(l.Log, l.Lap.StartT - 5, l.Lap.EndT + 5);
            return all.OrderBy(v => v == def || (def != null && v.Path == def.Path) ? 0 : 1)
                      .ThenBy(v => v.T0 <= l.Lap.StartT && v.EndT >= l.Lap.EndT ? 0 : 1).ThenBy(v => v.Duration).ToList();
        }

        /// <summary>
        /// 比べるための本物の周回: ログを解析し直して、開始時刻の同じ周回を探す (解析の結果は lap.json と同じ条件なので同じ周回になる)
        /// </summary>
        public static bool Resolve(LibLap l, List<GpsSample> gps, out SessionRecord sess, out LapRecord lap, out string[] sectorNames, List<string> notes)
        {
            LapAnalysis r = Analyze(l.Course, l.Log, gps, notes);
            sectorNames = r.SectorNames;
            foreach (SessionRecord s in r.Sessions)
                foreach (LapRecord x in s.Laps)
                    if (Math.Abs(x.StartT - l.Lap.StartT) < 0.01) { sess = s; lap = x; return true; }
            sess = null; lap = null;
            return false;
        }
    }
}
