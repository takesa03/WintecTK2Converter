using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tk2Gui
{
    // =====================================================================
    //  ログ 1 つ分の付属情報: ログのファイル名 + ".log.json" (例: 20261107.nmea.log.json)
    //  解析の条件・セッションの手動編集・ドライバー名・ジムカーナの走行のメモ・動画の時刻・心拍の FIT・車の設定を
    //  1 つのファイルにまとめる。音声から求めた回転数の列は大きいので別のファイル (ログ名.rpm.json、EngineStore)。
    //  結び付けはすべてログの時刻 (通算秒) で行い、セッション・周回の番号は使わない (ログの組み合わせや条件で変わるため)。
    //  パスは、このファイルのあるフォルダからの相対パスで保存する (別のドライブのときだけ絶対パス)。
    //  フォルダごと移しても使える。設計は docs/data_library_design.md
    //  前の版の付属ファイル (.drivers / .sessions / .runs / .videos / .heart / .engine.json と %LOCALAPPDATA% の退避先) は、
    //  このファイルが無いときに取り込む。前の版のファイルは消さない
    // =====================================================================

    /// <summary>ラップ解析の条件 (サーキット)。ログごとに保存して、同じ結果を作り直せるようにする</summary>
    public sealed class LogParams
    {
        [JsonPropertyName("pitStopSec")] public double PitStopSec { get; set; }
        [JsonPropertyName("minCrossSpeedKmh")] public double MinCrossSpeedKmh { get; set; }
        [JsonPropertyName("slowLapRatio")] public double SlowLapRatio { get; set; }

        public bool SameAs(LogParams o)
        {
            return o != null && Math.Abs(PitStopSec - o.PitStopSec) < 1e-9 && Math.Abs(MinCrossSpeedKmh - o.MinCrossSpeedKmh) < 1e-9
                && Math.Abs(SlowLapRatio - o.SlowLapRatio) < 1e-9;
        }
    }

    /// <summary>セッションの手動の分割・結合 (時刻はログの通算秒。SessionEdits を参照)</summary>
    public sealed class SessionEditsData
    {
        [JsonPropertyName("splits")] public List<double> Splits { get; set; } = new List<double>();
        [JsonPropertyName("merges")] public List<double> Merges { get; set; } = new List<double>();
    }

    public sealed class LogData
    {
        public const string FormatName = "WintecTK2Log";
        public const int CurrentVersion = 1;

        [JsonPropertyName("format")] public string Format { get; set; } = FormatName;
        [JsonPropertyName("version")] public int Version { get; set; } = CurrentVersion;
        [JsonPropertyName("log")] public string Log { get; set; }                                  // ログのファイル名 (確認用)
        [JsonPropertyName("imported")] public List<string> Imported { get; set; }                  // 取り込んだ前の版の付属ファイル
        [JsonPropertyName("courses")] public List<string> Courses { get; set; }                    // このログを解析したコース定義 (相対パス)
        [JsonPropertyName("params")] public LogParams Params { get; set; }                         // ラップ解析の条件 (最後に解析したとき)
        [JsonPropertyName("sessionEdits")] public SessionEditsData SessionEdits { get; set; }
        [JsonPropertyName("drivers")] public List<DriverStore.Entry> Drivers { get; set; }
        [JsonPropertyName("runs")] public List<RunStore.Entry> Runs { get; set; }                  // ジムカーナの走行のメモ・ドライバー・印
        [JsonPropertyName("videos")] public List<VideoStore.Entry> Videos { get; set; }            // 走行動画の t0 (path・clipOf は相対)
        [JsonPropertyName("heart")] public List<HeartStore.Entry> Heart { get; set; }              // 心拍の FIT (fit は相対。FIT は heart\ に置く)
        [JsonPropertyName("engine")] public EngineStore.FileData Engine { get; set; }              // 車の設定と回転数の係数 (区間の列は rpm.json)

        /// <summary>この版が知らない項目 (新しい版で足したもの) も、書き戻すときに消さない</summary>
        [JsonExtensionData] public Dictionary<string, JsonElement> Extra { get; set; }
    }

    public static class LogFile
    {
        public const string Suffix = ".log.json";
        static readonly object Lock = new object();
        static readonly Dictionary<string, List<string>> Notes = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        internal static readonly JsonSerializerOptions Opt = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public static string PathOf(string log) { return log + Suffix; }

        public static string DirOf(string log) { return Path.GetDirectoryName(Path.GetFullPath(log)); }

        /// <summary>読む (無ければ前の版の付属ファイルから取り込む)。読めなければ空のデータを返し、理由は TakeNotes で渡す</summary>
        public static LogData Load(string log)
        {
            if (string.IsNullOrEmpty(log)) return new LogData();
            lock (Lock)
            {
                try { return LoadCore(log); }
                catch (Exception ex)
                {
                    AddNote(log, "付属情報を読めません (" + Path.GetFileName(PathOf(log)) + "): " + ex.Message);
                    return new LogData { Log = Path.GetFileName(log) };
                }
            }
        }

        /// <summary>写しを作る (パスはそのまま)</summary>
        public static LogData Clone(LogData d)
        {
            return JsonSerializer.Deserialize<LogData>(JsonSerializer.Serialize(d, Opt), Opt);
        }

        /// <summary>丸ごと書く (データをまとめるときに、まとめ先のログへ)。パスは絶対で渡す。区間の列 (rpm.json) は書かない</summary>
        public static void Save(string log, LogData d)
        {
            lock (Lock) Write(log, d);
        }

        /// <summary>読んで、変えて、書く。ファイルが壊れていれば例外 (上書きして消さない)。書けなければ例外</summary>
        public static void Update(string log, Action<LogData> change)
        {
            lock (Lock)
            {
                LogData d = LoadCore(log);
                change(d);
                Write(log, d);
            }
        }

        /// <summary>取り込み・読み込みの知らせ (画面のログに出す)。出したものは消える</summary>
        public static List<string> TakeNotes(string log)
        {
            lock (Lock)
            {
                List<string> l;
                if (log == null || !Notes.TryGetValue(log, out l)) return new List<string>();
                Notes.Remove(log);
                return l.Distinct().ToList();
            }
        }

        internal static void AddNote(string log, string s)
        {
            lock (Lock)
            {
                List<string> l;
                if (!Notes.TryGetValue(log, out l)) { l = new List<string>(); Notes[log] = l; }
                l.Add(s);
            }
        }

        static LogData LoadCore(string log)
        {
            string p = PathOf(log);
            if (File.Exists(p))
            {
                LogData d = JsonSerializer.Deserialize<LogData>(File.ReadAllText(p, Encoding.UTF8), Opt);
                if (d == null || d.Format != LogData.FormatName) throw new InvalidDataException("形式が違います");
                if (d.Version > LogData.CurrentVersion) throw new InvalidDataException("新しい版のツールで作られています (version " + d.Version + ")");
                ToAbsolute(d, DirOf(log));
                return d;
            }
            LogData n = LegacyFiles.Import(log);
            n.Log = Path.GetFileName(log);
            if (n.Imported != null && n.Imported.Count > 0)
            {
                try
                {
                    Write(log, n);
                    AddNote(log, Path.GetFileName(log) + ": 前の版の付属ファイルを " + Path.GetFileName(p) + " に取り込みました (" + string.Join(", ", n.Imported) + "。元のファイルは残してあります)");
                }
                catch (Exception ex) { AddNote(log, Path.GetFileName(log) + ": 前の版の付属ファイルを取り込みましたが、保存できません: " + ex.Message); }
            }
            return n;
        }

        static void Write(string log, LogData d)
        {
            string dir = DirOf(log);
            d.Format = LogData.FormatName; d.Version = LogData.CurrentVersion; d.Log = Path.GetFileName(log);
            List<EngineStore.Segment> segs = d.Engine != null ? d.Engine.Segments : null;
            string json;
            ToRelative(d, dir);
            if (d.Engine != null) d.Engine.Segments = null;      // 区間の列は rpm.json
            try { json = JsonSerializer.Serialize(d, Opt); }
            finally
            {
                ToAbsolute(d, dir);
                if (d.Engine != null) d.Engine.Segments = segs ?? new List<EngineStore.Segment>();
            }
            WriteAtomic(PathOf(log), json);
        }

        /// <summary>一時ファイルに書いてから置き換える (途中で止まっても元のファイルが壊れない)</summary>
        public static void WriteAtomic(string path, string text)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, text, new UTF8Encoding(false));
            File.Move(tmp, path, true);
        }

        static void ToRelative(LogData d, string dir)
        {
            if (d.Courses != null) d.Courses = d.Courses.Select(c => RelPath.ToRel(dir, c)).ToList();
            if (d.Videos != null) foreach (VideoStore.Entry v in d.Videos) { v.Path = RelPath.ToRel(dir, v.Path); v.ClipOf = RelPath.ToRel(dir, v.ClipOf); }
            if (d.Heart != null) foreach (HeartStore.Entry h in d.Heart) h.Fit = RelPath.ToRel(dir, h.Fit);
        }

        static void ToAbsolute(LogData d, string dir)
        {
            if (d.Courses != null) d.Courses = d.Courses.Select(c => RelPath.ToAbs(dir, c)).ToList();
            if (d.Videos != null) foreach (VideoStore.Entry v in d.Videos) { v.Path = RelPath.ToAbs(dir, v.Path); v.ClipOf = RelPath.ToAbs(dir, v.ClipOf); }
            if (d.Heart != null) foreach (HeartStore.Entry h in d.Heart) h.Fit = RelPath.ToAbs(dir, h.Fit);
        }
    }

    /// <summary>保存するパスの相対・絶対の変換</summary>
    public static class RelPath
    {
        /// <summary>baseDir からの相対パス。ドライブ (UNC の共有) が違えば絶対パスのまま</summary>
        public static string ToRel(string baseDir, string path)
        {
            if (string.IsNullOrEmpty(path) || !Path.IsPathRooted(path)) return path;
            string full = Path.GetFullPath(path);
            if (!string.Equals(Path.GetPathRoot(full), Path.GetPathRoot(baseDir), StringComparison.OrdinalIgnoreCase)) return full;
            return Path.GetRelativePath(baseDir, full);
        }

        public static string ToAbs(string baseDir, string path)
        {
            if (string.IsNullOrEmpty(path) || Path.IsPathRooted(path)) return path;
            return Path.GetFullPath(Path.Combine(baseDir, path));
        }
    }

    // =====================================================================
    //  前の版の付属ファイルの取り込み (読むだけ。消さない)
    // =====================================================================
    static class LegacyFiles
    {
        static readonly JsonSerializerOptions Opt = new JsonSerializerOptions { PropertyNameCaseInsensitive = false };

        sealed class Drivers
        {
            [JsonPropertyName("format")] public string Format { get; set; }
            [JsonPropertyName("entries")] public List<DriverStore.Entry> Entries { get; set; }
        }

        sealed class Runs
        {
            [JsonPropertyName("format")] public string Format { get; set; }
            [JsonPropertyName("runs")] public List<RunStore.Entry> Items { get; set; }
        }

        sealed class Sessions
        {
            [JsonPropertyName("format")] public string Format { get; set; }
            [JsonPropertyName("splits")] public List<double> Splits { get; set; }
            [JsonPropertyName("merges")] public List<double> Merges { get; set; }
        }

        sealed class Videos
        {
            [JsonPropertyName("format")] public string Format { get; set; }
            [JsonPropertyName("videos")] public List<VideoStore.Entry> Items { get; set; }
        }

        sealed class HeartEntry
        {
            [JsonPropertyName("fit")] public string Fit { get; set; }
            [JsonPropertyName("device")] public string Device { get; set; }
            [JsonPropertyName("offset")] public double Offset { get; set; }
            [JsonPropertyName("auto")] public double? AutoOffset { get; set; }
            [JsonPropertyName("candidates")] public List<HeartStore.Candidate> Candidates { get; set; }
            [JsonPropertyName("t")] public double[] T { get; set; }
        }

        sealed class Heart
        {
            [JsonPropertyName("format")] public string Format { get; set; }
            [JsonPropertyName("entries")] public List<HeartEntry> Entries { get; set; }
        }

        /// <summary>%LOCALAPPDATA%\WintecTK2Converter\(sub)\ に退避した前の版のファイル (ログのフルパスの hash で探す)</summary>
        static string Fallback(string log, string sub)
        {
            string key = BitConverter.ToString(SHA1.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(log).ToLowerInvariant()))).Replace("-", "").Substring(0, 16);
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WintecTK2Converter", sub);
            return Path.Combine(dir, Path.GetFileName(log) + "_" + key + ".json");
        }

        static T Read<T>(string path, string format, Func<T, string> formatOf, List<string> got) where T : class
        {
            try
            {
                if (!File.Exists(path)) return null;
                T d = JsonSerializer.Deserialize<T>(File.ReadAllText(path, Encoding.UTF8), Opt);
                if (d == null || formatOf(d) != format) return null;
                got.Add(path);
                return d;
            }
            catch (Exception) { return null; }   // 壊れたファイルは取り込まない (前の版と同じ扱い)
        }

        public static LogData Import(string log)
        {
            LogData d = new LogData();
            List<string> got = new List<string>();
            string dir = LogFile.DirOf(log);

            Drivers dr = Read<Drivers>(log + ".drivers.json", "WintecTK2Drivers", x => x.Format, got)
                      ?? Read<Drivers>(Fallback(log, "drivers"), "WintecTK2Drivers", x => x.Format, got);
            if (dr != null && dr.Entries != null && dr.Entries.Count > 0) d.Drivers = dr.Entries;

            Runs rn = Read<Runs>(log + ".runs.json", "WintecTK2Runs", x => x.Format, got)
                   ?? Read<Runs>(Fallback(log, "runs"), "WintecTK2Runs", x => x.Format, got);
            if (rn != null && rn.Items != null && rn.Items.Count > 0) d.Runs = rn.Items;

            Sessions se = Read<Sessions>(log + ".sessions.json", "WintecTK2SessionEdits", x => x.Format, got)
                       ?? Read<Sessions>(Fallback(log, "sessions"), "WintecTK2SessionEdits", x => x.Format, got);
            if (se != null && ((se.Splits != null && se.Splits.Count > 0) || (se.Merges != null && se.Merges.Count > 0)))
                d.SessionEdits = new SessionEditsData { Splits = se.Splits ?? new List<double>(), Merges = se.Merges ?? new List<double>() };

            Videos vi = Read<Videos>(log + ".videos.json", "WintecTK2Videos", x => x.Format, got);
            if (vi != null && vi.Items != null)
            {
                List<VideoStore.Entry> vs = vi.Items.Where(v => !string.IsNullOrEmpty(v.Path)).ToList();
                foreach (VideoStore.Entry v in vs) { v.Path = RelPath.ToAbs(dir, v.Path); v.ClipOf = RelPath.ToAbs(dir, v.ClipOf); }
                if (vs.Count > 0) d.Videos = vs;
            }

            // 心拍: 前の版はセッションの番号で結び付けていた (ログの組み合わせで番号が変わる) ので、
            // FIT のうちカートに乗っていた所の範囲 (ログの時刻) で結び付け直す。合わせたときと同じ「短い方の半分以上が重なる」で、そのセッションに付く。
            // FIT は記録を写さず毎回読むので、ログの横の heart\ にコピーする。FIT が見つからなければ取り込めない
            Heart he = Read<Heart>(log + ".heart.json", "WintecTK2Heart", x => x.Format, got);
            if (he != null && he.Entries != null)
            {
                foreach (HeartEntry e in he.Entries)
                {
                    if (e.T == null || e.T.Length == 0) continue;
                    if (string.IsNullOrEmpty(e.Fit) || !File.Exists(e.Fit))
                    {
                        LogFile.AddNote(log, Path.GetFileName(log) + ": FIT が見つからないので、心拍を取り込めません: " + e.Fit);
                        continue;
                    }
                    string fit;
                    double s0, s1;
                    try
                    {
                        if (!HeartStore.DrivingRange(e.Fit, e.Offset, out s0, out s1)) { s0 = e.T[0] + e.Offset; s1 = e.T[e.T.Length - 1] + e.Offset; }
                        fit = HeartStore.CopyIn(log, e.Fit);
                    }
                    catch (Exception ex) { LogFile.AddNote(log, Path.GetFileName(log) + ": FIT を heart\\ にコピーできません: " + ex.Message); continue; }
                    if (d.Heart == null) d.Heart = new List<HeartStore.Entry>();
                    d.Heart.Add(new HeartStore.Entry
                    {
                        Fit = fit, Device = e.Device, Offset = e.Offset, AutoOffset = e.AutoOffset, Candidates = e.Candidates,
                        StartT = s0, EndT = s1
                    });
                }
            }

            // 回転数: 車の設定は log.json、区間の列は rpm.json へ
            EngineStore.FileData en = Read<EngineStore.FileData>(log + ".engine.json", "WintecTK2Engine", x => x.Format, got);
            if (en != null)
            {
                List<EngineStore.Segment> segs = en.Segments ?? new List<EngineStore.Segment>();
                foreach (EngineStore.Segment s in segs) s.Video = RelPath.ToAbs(dir, s.Video);
                if (segs.Count > 0)
                {
                    try { EngineStore.WriteSegments(log, segs); }
                    catch (Exception ex) { LogFile.AddNote(log, Path.GetFileName(log) + ": 回転数の解析結果を保存できません: " + ex.Message); }
                }
                en.Segments = new List<EngineStore.Segment>();
                d.Engine = en;
            }

            if (got.Count > 0)
                d.Imported = got.Select(p => string.Equals(Path.GetDirectoryName(Path.GetFullPath(p)), dir, StringComparison.OrdinalIgnoreCase) ? Path.GetFileName(p) : p).ToList();
            return d;
        }
    }
}
