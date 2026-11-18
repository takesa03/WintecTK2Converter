using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tk2Gui
{
    // =====================================================================
    //  ラップ解析の結果の保存 (ログ名.laps.json。キャッシュ)
    //  ログ 1 つにつき 1 ファイル。中にコース定義 (id) ごとの結果を持つ。
    //  比較アプリの一覧は、GPS を読まずにこれだけで作れるようにする (docs/data_library_design.md 5.2)。
    //  指紋 (コース定義のライン・解析の条件と手動の編集・ログの大きさと更新日時) が合わなければ古い結果。
    //  正はログと log.json とコース定義なので、壊れていても消えても作り直せる。
    // =====================================================================

    public sealed class LapsFile
    {
        public const string FormatName = "WintecTK2Laps";
        [JsonPropertyName("format")] public string Format { get; set; } = FormatName;
        [JsonPropertyName("version")] public int Version { get; set; } = 1;
        [JsonPropertyName("log")] public string Log { get; set; }
        [JsonPropertyName("results")] public List<LapsResult> Results { get; set; } = new List<LapsResult>();
        [JsonExtensionData] public Dictionary<string, JsonElement> Extra { get; set; }
    }

    public sealed class LapsResult
    {
        [JsonPropertyName("course")] public string Course { get; set; }               // コース定義の id
        [JsonPropertyName("courseName")] public string CourseName { get; set; }
        [JsonPropertyName("courseFile")] public string CourseFile { get; set; }       // コース定義のファイル (laps.json からの相対。確認用)
        [JsonPropertyName("courseHash")] public string CourseHash { get; set; }
        [JsonPropertyName("paramsHash")] public string ParamsHash { get; set; }
        [JsonPropertyName("logStamp")] public string LogStamp { get; set; }           // ログの大きさと更新日時
        [JsonPropertyName("analyzed")] public string Analyzed { get; set; }
        [JsonPropertyName("gymkhana"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool Gymkhana { get; set; }
        [JsonPropertyName("sectorNames")] public string[] SectorNames { get; set; }
        [JsonPropertyName("sessions")] public List<LapsSession> Sessions { get; set; } = new List<LapsSession>();
    }

    public sealed class LapsSession
    {
        [JsonPropertyName("startT")] public double StartT { get; set; }
        [JsonPropertyName("endT")] public double EndT { get; set; }
        [JsonPropertyName("driver")] public string Driver { get; set; }
        [JsonPropertyName("bestLap")] public double? BestLap { get; set; }
        [JsonPropertyName("laps")] public List<LapsLap> Laps { get; set; } = new List<LapsLap>();
    }

    public sealed class LapsLap
    {
        [JsonPropertyName("startT")] public double StartT { get; set; }
        [JsonPropertyName("endT")] public double EndT { get; set; }
        [JsonPropertyName("no")] public int No { get; set; }
        [JsonPropertyName("type")] public string Type { get; set; }
        [JsonPropertyName("valid")] public bool Valid { get; set; }
        [JsonPropertyName("in"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool In { get; set; }
        [JsonPropertyName("lapTime")] public double? LapTime { get; set; }
        [JsonPropertyName("partialTime"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? PartialTime { get; set; }
        [JsonPropertyName("sectors")] public double?[] Sectors { get; set; }
        [JsonPropertyName("maxKmh")] public double MaxKmh { get; set; }
        [JsonPropertyName("note"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string Note { get; set; }
        // ジムカーナの走行
        [JsonPropertyName("dnf"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool Dnf { get; set; }
        [JsonPropertyName("missCourse"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool MissCourse { get; set; }
        [JsonPropertyName("driver"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string Driver { get; set; }
        [JsonPropertyName("memo"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string Memo { get; set; }
    }

    public static class LapsStore
    {
        public const string Suffix = ".laps.json";
        static readonly object Lock = new object();
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static readonly JsonSerializerOptions Opt = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            NumberHandling = JsonNumberHandling.AllowReadingFromString
        };

        public static string PathOf(string log) { return log + Suffix; }

        /// <summary>読む。無い・壊れていれば null</summary>
        public static LapsFile Load(string log)
        {
            try
            {
                string p = PathOf(log);
                if (!File.Exists(p)) return null;
                LapsFile f = JsonSerializer.Deserialize<LapsFile>(File.ReadAllText(p, Encoding.UTF8), Opt);
                return f != null && f.Format == LapsFile.FormatName && f.Results != null ? f : null;
            }
            catch (Exception) { return null; }
        }

        /// <summary>ログの大きさと更新日時 (ログを差し替えたら古い結果と分かる)</summary>
        public static string LogStamp(string log)
        {
            FileInfo fi = new FileInfo(log);
            return fi.Exists ? fi.Length.ToString(Inv) + "@" + fi.LastWriteTimeUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", Inv) : "";
        }

        /// <summary>解析の条件と手動のセッション編集 (log.json に保存してあるもの) の hash。ジムカーナは条件を使わない</summary>
        public static string ParamsHash(string log, bool gymkhana)
        {
            LogData d = LogFile.Load(log);
            StringBuilder sb = new StringBuilder();
            if (gymkhana) sb.Append("gymkhana");
            else if (d.Params != null)
                sb.Append(d.Params.PitStopSec.ToString("R", Inv)).Append(',').Append(d.Params.MinCrossSpeedKmh.ToString("R", Inv)).Append(',').Append(d.Params.SlowLapRatio.ToString("R", Inv));
            if (d.SessionEdits != null)
            {
                if (d.SessionEdits.Splits != null) sb.Append("|s:").Append(string.Join(",", d.SessionEdits.Splits.OrderBy(x => x).Select(x => x.ToString("R", Inv))));
                if (d.SessionEdits.Merges != null) sb.Append("|m:").Append(string.Join(",", d.SessionEdits.Merges.OrderBy(x => x).Select(x => x.ToString("R", Inv))));
            }
            using (SHA256 sha = SHA256.Create())
                return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString())).Take(8).Select(b => b.ToString("x2")));
        }

        /// <summary>保存してある結果が、今のコース定義・条件・ログのままか</summary>
        public static bool IsCurrent(LapsResult r, CourseDefinition def, string log)
        {
            return r != null && r.Course == def.Id && r.CourseHash == def.Fingerprint()
                && r.ParamsHash == ParamsHash(log, def.IsGymkhana) && r.LogStamp == LogStamp(log);
        }

        /// <summary>
        /// 解析の結果を、ログごとの laps.json に保存する (同じコース定義の id の結果は置き換える)。
        /// あわせて log.json の courses にコース定義を足す。前の結果とラインが違えば notes に知らせる
        /// </summary>
        public static void Save(CourseDefinition def, string coursePath, LapAnalysis res, IEnumerable<string> logs, List<string> notes)
        {
            if (def == null || res == null || string.IsNullOrEmpty(def.Id)) return;
            string hash = def.Fingerprint();
            foreach (string log in logs)
            {
                string full = Path.GetFullPath(log);
                try
                {
                    lock (Lock)
                    {
                        LapsFile f = Load(log) ?? new LapsFile();
                        LapsResult old = f.Results.FirstOrDefault(r => r.Course == def.Id);
                        if (old != null && old.CourseHash != hash)
                            notes.Add("コース定義のラインが前に解析したときと違うので、解析し直した結果に置き換えました (" + Path.GetFileName(log) + ")");
                        LapsResult nr = new LapsResult
                        {
                            Course = def.Id, CourseName = def.Name, CourseHash = hash,
                            CourseFile = string.IsNullOrEmpty(coursePath) ? null : RelPath.ToRel(LogFile.DirOf(log), coursePath),
                            ParamsHash = ParamsHash(log, def.IsGymkhana), LogStamp = LogStamp(log),
                            Analyzed = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz", Inv),
                            Gymkhana = res.IsGymkhana, SectorNames = res.SectorNames,
                        };
                        foreach (SessionRecord s in res.Sessions.Where(s => s.FilePath != null && string.Equals(Path.GetFullPath(s.FilePath), full, StringComparison.OrdinalIgnoreCase)))
                            nr.Sessions.Add(ToDto(s));
                        f.Results.RemoveAll(r => r.Course == def.Id);
                        f.Results.Add(nr);
                        f.Log = Path.GetFileName(log);
                        LogFile.WriteAtomic(PathOf(log), JsonSerializer.Serialize(f, Opt));
                    }
                    if (!string.IsNullOrEmpty(coursePath))
                    {
                        string cp = Path.GetFullPath(coursePath);
                        LogFile.Update(log, d =>
                        {
                            if (d.Courses == null) d.Courses = new List<string>();
                            if (!d.Courses.Any(c => string.Equals(Path.GetFullPath(c), cp, StringComparison.OrdinalIgnoreCase))) d.Courses.Add(cp);
                        });
                    }
                }
                catch (Exception ex) { notes.Add("解析の結果を保存できません (" + Path.GetFileName(log) + "): " + ex.Message); }
            }
        }

        static LapsSession ToDto(SessionRecord s)
        {
            LapsSession o = new LapsSession { StartT = s.StartT, EndT = s.EndT, Driver = s.Driver ?? "", BestLap = s.BestLap };
            foreach (LapRecord l in s.Laps)
                o.Laps.Add(new LapsLap
                {
                    StartT = l.StartT, EndT = l.EndT, No = l.LapNo, Type = l.IsGymRun ? "Run" : l.Type.ToString(), Valid = l.Valid, In = l.IsIn,
                    LapTime = l.LapTime, PartialTime = l.PartialTime, Sectors = l.Sector, MaxKmh = Math.Round(l.MaxKmh, 2),
                    Note = string.IsNullOrEmpty(l.Note) ? null : l.Note,
                    Dnf = l.Dnf, MissCourse = l.MissCourse,
                    Driver = l.IsGymRun && !string.IsNullOrEmpty(l.RunDriver) ? l.RunDriver : null,
                    Memo = string.IsNullOrEmpty(l.Memo) ? null : l.Memo,
                });
            return o;
        }
    }
}
