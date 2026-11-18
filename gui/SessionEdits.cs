using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tk2Gui
{
    /// <summary>
    /// 手動で行ったセッションの編集 (ログ 1 つ分)。時刻はログの通算秒で、ライン通過の時刻 (周回の開始時刻) を指す。
    /// ライン通過の時刻は設定 (停止とみなす秒数など) を変えても動かないので、設定を変えても編集は有効なまま。
    /// </summary>
    public sealed class SessionEdits
    {
        [JsonPropertyName("format")] public string Format { get; set; } = "WintecTK2SessionEdits";
        [JsonPropertyName("version")] public int Version { get; set; } = 1;
        [JsonPropertyName("log")] public string Log { get; set; }
        /// <summary>この時刻から始まる周回の前で、セッションを分ける</summary>
        [JsonPropertyName("splits")] public List<double> Splits { get; set; } = new List<double>();
        /// <summary>この時刻から始まるセッション (自動の停止判定で分かれたもの) を、前のセッションと結合する</summary>
        [JsonPropertyName("merges")] public List<double> Merges { get; set; } = new List<double>();

        public const double Tol = 0.01;
        public bool IsEmpty { get { return Splits.Count == 0 && Merges.Count == 0; } }
        public static string Key(string path) { return Path.GetFullPath(path).ToLowerInvariant(); }
        public static bool Near(double a, double b) { return Math.Abs(a - b) < Tol; }
        public bool HasSplitAt(double t) { return Splits.Any(s => Near(s, t)); }
        public bool HasMergeAt(double t) { return Merges.Any(s => Near(s, t)); }
    }

    /// <summary>セッション編集の保存・読み込み。保存先はログの付属情報 (ログ名.log.json の sessionEdits)</summary>
    public static class SessionEditStore
    {
        public static SessionEdits Load(string log)
        {
            SessionEditsData d = LogFile.Load(log).SessionEdits;
            return new SessionEdits
            {
                Log = Path.GetFileName(log),
                Splits = d != null && d.Splits != null ? new List<double>(d.Splits) : new List<double>(),
                Merges = d != null && d.Merges != null ? new List<double>(d.Merges) : new List<double>()
            };
        }

        /// <summary>保存する (ファイルを書くので、画面のスレッドでは呼ばない)。書けなければ例外</summary>
        public static void Save(string log, SessionEdits ed)
        {
            LogFile.Update(log, d => d.SessionEdits = ed.IsEmpty ? null
                : new SessionEditsData { Splits = new List<double>(ed.Splits), Merges = new List<double>(ed.Merges) });
        }
    }
}
