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
    //  ジムカーナの走行ごとの手入力 (メモ・ドライバー・印) の保存・復元
    //  ログファイルのそばの「ログ名.runs.json」。書き込めなければ
    //  %LOCALAPPDATA%\WintecTK2Converter\runs\ に退避する (DriverStore と同じ考え方)。
    //  走行はスタートの通過時刻 (ログの通算秒) で対応づける。コース定義のスタートラインを
    //  少し動かしても引き継げるように、±RunMatchSec 秒以内で一番近い走行に割り当てる。
    // =====================================================================
    public static class RunStore
    {
        const double RunMatchSec = 5;

        public sealed class Entry
        {
            [JsonPropertyName("startT")] public double StartT { get; set; }
            [JsonPropertyName("driver"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string Driver { get; set; }
            [JsonPropertyName("memo"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string Memo { get; set; }
            [JsonPropertyName("mark"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string Mark { get; set; }
        }

        sealed class FileData
        {
            [JsonPropertyName("format")] public string Format { get; set; } = "WintecTK2Runs";
            [JsonPropertyName("version")] public int Version { get; set; } = 1;
            [JsonPropertyName("log")] public string Log { get; set; }
            [JsonPropertyName("runs")] public List<Entry> Runs { get; set; } = new List<Entry>();
        }

        static readonly JsonSerializerOptions Opt = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        static string SidecarPath(string log) { return log + ".runs.json"; }

        static string FallbackPath(string log)
        {
            string key = BitConverter.ToString(SHA1.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(log).ToLowerInvariant()))).Replace("-", "").Substring(0, 16);
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WintecTK2Converter", "runs");
            return Path.Combine(dir, Path.GetFileName(log) + "_" + key + ".json");
        }

        static List<Entry> ReadFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                FileData d = JsonSerializer.Deserialize<FileData>(File.ReadAllText(path, Encoding.UTF8), Opt);
                return d != null && d.Format == "WintecTK2Runs" && d.Runs != null ? d.Runs : null;
            }
            catch (Exception)
            {
                return null;   // 壊れたファイルは無視する (メモが復元されないだけ)
            }
        }

        public static List<Entry> Load(string log)
        {
            return ReadFile(SidecarPath(log)) ?? ReadFile(FallbackPath(log)) ?? new List<Entry>();
        }

        static bool HasInput(LapRecord l)
        {
            return !string.IsNullOrWhiteSpace(l.Memo) || !string.IsNullOrWhiteSpace(l.RunDriver) || l.Mark != null;
        }

        /// <summary>解析結果の走行に、保存してあるメモ・ドライバー・印を割り当てる (印を反映するため、この後で LapAnalyzer.RecomputeGymkhana を呼ぶこと)。復元した走行の数を返す。</summary>
        public static int Apply(LapAnalysis res)
        {
            if (res == null || !res.IsGymkhana) return 0;
            int n = 0;
            foreach (SessionRecord s in res.Sessions)
            {
                if (string.IsNullOrEmpty(s.FilePath)) continue;
                List<Entry> entries = Load(s.FilePath);
                foreach (Entry e in entries)
                {
                    LapRecord best = s.Laps.Where(l => l.IsGymRun && Math.Abs(l.StartT - e.StartT) <= RunMatchSec)
                        .OrderBy(l => Math.Abs(l.StartT - e.StartT)).FirstOrDefault();
                    if (best == null || HasInput(best)) continue;
                    best.Memo = e.Memo ?? "";
                    best.RunDriver = e.Driver ?? "";
                    best.Mark = e.Mark == LapRecord.MarkValid || e.Mark == LapRecord.MarkMiss ? e.Mark : null;
                    n++;
                }
            }
            return n;
        }

        /// <summary>書き込む内容 (ログ 1 つ分)。画面側で作り、書き込みは別スレッドで行う。</summary>
        public sealed class Snapshot
        {
            public string LogPath;
            public int Count;          // 入力のある走行の数 (0 なら保存ファイルを削除)
            public string Json;
        }

        public static List<Snapshot> MakeSnapshots(LapAnalysis res)
        {
            List<Snapshot> list = new List<Snapshot>();
            if (res == null || !res.IsGymkhana) return list;
            foreach (SessionRecord s in res.Sessions)
            {
                if (string.IsNullOrEmpty(s.FilePath)) continue;
                FileData d = new FileData { Log = Path.GetFileName(s.FilePath) };
                foreach (LapRecord l in s.Laps)
                    if (l.IsGymRun && HasInput(l))
                        d.Runs.Add(new Entry
                        {
                            StartT = Math.Round(l.StartT, 3),
                            Driver = string.IsNullOrWhiteSpace(l.RunDriver) ? null : l.RunDriver.Trim(),
                            Memo = string.IsNullOrWhiteSpace(l.Memo) ? null : l.Memo.Trim(),
                            Mark = l.Mark
                        });
                list.Add(new Snapshot { LogPath = s.FilePath, Count = d.Runs.Count, Json = d.Runs.Count == 0 ? null : JsonSerializer.Serialize(d, Opt) });
            }
            return list;
        }

        static readonly object WriteLock = new object();

        /// <summary>保存内容をファイルに書く。入力がひとつもなければ保存ファイルを削除する。保存した場所の一覧を返す。</summary>
        public static List<string> Write(List<Snapshot> snapshots)
        {
            List<string> saved = new List<string>();
            lock (WriteLock)
            foreach (Snapshot sn in snapshots)
            {
                string side = SidecarPath(sn.LogPath), fall = FallbackPath(sn.LogPath);
                if (sn.Count == 0)
                {
                    TryDelete(side); TryDelete(fall);
                    continue;
                }
                try
                {
                    File.WriteAllText(side, sn.Json, new UTF8Encoding(false));
                    TryDelete(fall);
                    saved.Add(side);
                }
                catch (Exception)
                {
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(fall));
                        File.WriteAllText(fall, sn.Json, new UTF8Encoding(false));
                        saved.Add(fall);
                    }
                    catch (Exception) { /* 保存できなくても解析は続けられる */ }
                }
            }
            return saved;
        }

        static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { }
        }

        // ---- メモの定型 (ボタンで入れる) ----

        /// <summary>メモに「パイロンタッチ ×N」を 1 回足す (すでにあれば N を増やす)。</summary>
        public static string AddPylon(string memo)
        {
            memo = memo ?? "";
            System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(memo, @"パイロンタッチ\s*[×xX]\s*(\d+)");
            if (m.Success)
                return memo.Substring(0, m.Groups[1].Index) + (int.Parse(m.Groups[1].Value) + 1) + memo.Substring(m.Groups[1].Index + m.Groups[1].Length);
            return AddItem(memo, "パイロンタッチ ×1");
        }

        /// <summary>メモに項目を足す (同じ言葉がもうあれば足さない)。区切りは「、」。</summary>
        public static string AddItem(string memo, string item)
        {
            memo = (memo ?? "").Trim();
            if (memo.Contains(item)) return memo;
            return memo.Length == 0 ? item : memo + "、" + item;
        }
    }
}
