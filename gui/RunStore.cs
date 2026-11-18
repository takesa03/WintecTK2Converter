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
    //  ログの付属情報 (ログ名.log.json の runs。LogFile)。
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

        public static List<Entry> Load(string log)
        {
            return LogFile.Load(log).Runs ?? new List<Entry>();
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
            public List<Entry> Runs;   // 入力のある走行 (空なら保存から外す)
        }

        public static List<Snapshot> MakeSnapshots(LapAnalysis res)
        {
            List<Snapshot> list = new List<Snapshot>();
            if (res == null || !res.IsGymkhana) return list;
            foreach (IGrouping<string, SessionRecord> g in res.Sessions.Where(s => !string.IsNullOrEmpty(s.FilePath)).GroupBy(s => s.FilePath, StringComparer.OrdinalIgnoreCase))
            {
                List<Entry> runs = new List<Entry>();
                foreach (SessionRecord s in g)
                foreach (LapRecord l in s.Laps)
                    if (l.IsGymRun && HasInput(l))
                        runs.Add(new Entry
                        {
                            StartT = Math.Round(l.StartT, 3),
                            Driver = string.IsNullOrWhiteSpace(l.RunDriver) ? null : l.RunDriver.Trim(),
                            Memo = string.IsNullOrWhiteSpace(l.Memo) ? null : l.Memo.Trim(),
                            Mark = l.Mark
                        });
                list.Add(new Snapshot { LogPath = g.Key, Runs = runs });
            }
            return list;
        }

        /// <summary>保存内容をログの付属情報 (ログ名.log.json) に書く。保存したファイルの一覧を返す。書けなかったものは errors に理由を足す</summary>
        public static List<string> Write(List<Snapshot> snapshots, List<string> errors)
        {
            List<string> saved = new List<string>();
            foreach (Snapshot sn in snapshots)
            {
                try
                {
                    LogFile.Update(sn.LogPath, d => d.Runs = sn.Runs.Count == 0 ? null : sn.Runs);
                    if (sn.Runs.Count > 0) saved.Add(LogFile.PathOf(sn.LogPath));
                }
                catch (Exception ex) { if (errors != null) errors.Add(Path.GetFileName(sn.LogPath) + ": " + ex.Message); }
            }
            return saved;
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
