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
    //  保存先は、ログの付属情報 (ログ名.log.json の drivers。LogFile)。
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

        public static List<Entry> Load(string log)
        {
            return LogFile.Load(log).Drivers ?? new List<Entry>();
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
            public List<Entry> Entries;   // 名前のあるセッション (空なら保存から外す)
        }

        /// <summary>現在のドライバー名から、保存内容を作る (ファイルには触らない。画面のスレッドで呼んでよい)。</summary>
        public static List<Snapshot> MakeSnapshots(LapAnalysis res)
        {
            List<Snapshot> list = new List<Snapshot>();
            foreach (IGrouping<string, SessionRecord> g in res.Sessions.Where(s => !string.IsNullOrEmpty(s.FilePath)).GroupBy(s => s.FilePath))
            {
                List<Entry> entries = new List<Entry>();
                foreach (SessionRecord s in g)
                    if (!string.IsNullOrWhiteSpace(s.Driver))
                        entries.Add(new Entry { StartT = Math.Round(s.StartT, 3), EndT = Math.Round(s.EndT, 3), Driver = s.Driver.Trim() });
                list.Add(new Snapshot { LogPath = g.Key, Entries = entries });
            }
            return list;
        }

        /// <summary>
        /// 保存内容をログの付属情報 (ログ名.log.json) に書く。保存したファイルの一覧を返す。書けなかったものは errors に理由を足す
        /// (ファイルを書くので、画面のスレッドでは呼ばないこと)
        /// </summary>
        public static List<string> Write(List<Snapshot> snapshots, List<string> errors)
        {
            List<string> saved = new List<string>();
            foreach (Snapshot sn in snapshots)
            {
                try
                {
                    LogFile.Update(sn.LogPath, d => d.Drivers = sn.Entries.Count == 0 ? null : sn.Entries);
                    if (sn.Entries.Count > 0) saved.Add(LogFile.PathOf(sn.LogPath));
                }
                catch (Exception ex) { if (errors != null) errors.Add(Path.GetFileName(sn.LogPath) + ": " + ex.Message); }
            }
            return saved;
        }
    }
}
