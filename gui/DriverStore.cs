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
    //  ドライバー名の保存・復元
    //  セッションの時間範囲 (ログの通算秒) とドライバー名を、ログファイルごとに保存する。
    //  保存先は、ログファイルのそばの「ログ名.drivers.json」。書き込めなければ
    //  %LOCALAPPDATA%\WintecTK2Converter\drivers\ に退避する。
    //  復元は時間範囲の重なりで対応づけるので、停止の判定秒数を変えてセッションの分かれ方が
    //  変わっても、名前を引き継げる。
    // =====================================================================
    public static class DriverStore
    {
        public sealed class Entry
        {
            [JsonPropertyName("startT")] public double StartT { get; set; }
            [JsonPropertyName("endT")] public double EndT { get; set; }
            [JsonPropertyName("driver")] public string Driver { get; set; }
        }

        sealed class FileData
        {
            [JsonPropertyName("format")] public string Format { get; set; } = "WintecTK2Drivers";
            [JsonPropertyName("version")] public int Version { get; set; } = 1;
            [JsonPropertyName("log")] public string Log { get; set; }
            [JsonPropertyName("entries")] public List<Entry> Entries { get; set; } = new List<Entry>();
        }

        static readonly JsonSerializerOptions Opt = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        static string SidecarPath(string log)
        {
            return log + ".drivers.json";
        }

        static string FallbackPath(string log)
        {
            string key = BitConverter.ToString(SHA1.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(log).ToLowerInvariant()))).Replace("-", "").Substring(0, 16);
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WintecTK2Converter", "drivers");
            return Path.Combine(dir, Path.GetFileName(log) + "_" + key + ".json");
        }

        static List<Entry> ReadFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                FileData d = JsonSerializer.Deserialize<FileData>(File.ReadAllText(path, Encoding.UTF8), Opt);
                return d != null && d.Format == "WintecTK2Drivers" && d.Entries != null ? d.Entries : null;
            }
            catch (Exception)
            {
                return null;   // 壊れたファイルは無視する (名前が復元されないだけ)
            }
        }

        public static List<Entry> Load(string log)
        {
            return ReadFile(SidecarPath(log)) ?? ReadFile(FallbackPath(log)) ?? new List<Entry>();
        }

        /// <summary>解析結果のセッションに、保存されているドライバー名を割り当てる (名前が空のセッションだけ)。復元した数を返す。</summary>
        public static int Apply(LapAnalysis res)
        {
            int n = 0;
            foreach (IGrouping<string, SessionRecord> g in res.Sessions.Where(s => !string.IsNullOrEmpty(s.FilePath)).GroupBy(s => s.FilePath))
            {
                List<Entry> entries = Load(g.Key);
                if (entries.Count == 0) continue;
                foreach (SessionRecord s in g)
                {
                    if (!string.IsNullOrEmpty(s.Driver)) continue;
                    Entry best = null; double bestScore = 0;
                    foreach (Entry e in entries)
                    {
                        double overlap = Math.Max(0, Math.Min(s.EndT, e.EndT) - Math.Max(s.StartT, e.StartT));
                        double score = overlap + (s.StartT >= e.StartT - 1 && s.StartT <= e.EndT + 1 ? 0.001 : 0);   // 時間が 0 のセッションは開始位置が範囲内かで判定
                        if (score > bestScore) { bestScore = score; best = e; }
                    }
                    if (best != null && !string.IsNullOrEmpty(best.Driver)) { s.Driver = best.Driver; n++; }
                }
            }
            return n;
        }

        /// <summary>書き込む内容 (ログ 1 つ分)。画面側で素早く作り、書き込みは別スレッドで行う。</summary>
        public sealed class Snapshot
        {
            public string LogPath;
            public int Count;          // 名前のあるセッション数 (0 なら保存ファイルを削除)
            public string Json;
        }

        /// <summary>現在のドライバー名から、保存内容を作る (ファイルには触らない。画面のスレッドで呼んでよい)。</summary>
        public static List<Snapshot> MakeSnapshots(LapAnalysis res)
        {
            List<Snapshot> list = new List<Snapshot>();
            foreach (IGrouping<string, SessionRecord> g in res.Sessions.Where(s => !string.IsNullOrEmpty(s.FilePath)).GroupBy(s => s.FilePath))
            {
                FileData d = new FileData { Log = Path.GetFileName(g.Key) };
                foreach (SessionRecord s in g)
                    if (!string.IsNullOrWhiteSpace(s.Driver))
                        d.Entries.Add(new Entry { StartT = Math.Round(s.StartT, 3), EndT = Math.Round(s.EndT, 3), Driver = s.Driver.Trim() });
                list.Add(new Snapshot { LogPath = g.Key, Count = d.Entries.Count, Json = d.Entries.Count == 0 ? null : JsonSerializer.Serialize(d, Opt) });
            }
            return list;
        }

        /// <summary>現在のドライバー名を保存する (ファイル書き込みを伴うので、画面のスレッドでは呼ばないこと)。</summary>
        public static List<string> Save(LapAnalysis res)
        {
            return Write(MakeSnapshots(res));
        }

        static readonly object WriteLock = new object();

        /// <summary>保存内容をファイルに書く。名前がひとつもなければ保存ファイルを削除する。保存した場所の一覧を返す。</summary>
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
                string json = sn.Json;
                try
                {
                    File.WriteAllText(side, json, new UTF8Encoding(false));
                    TryDelete(fall);
                    saved.Add(side);
                }
                catch (Exception)
                {
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(fall));
                        File.WriteAllText(fall, json, new UTF8Encoding(false));
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
    }
}
