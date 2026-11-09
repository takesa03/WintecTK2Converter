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

    /// <summary>セッション編集の保存・読み込み。保存先はログのそばの「ログ名.sessions.json」、書けなければ %LOCALAPPDATA% に退避。</summary>
    public static class SessionEditStore
    {
        static readonly JsonSerializerOptions Opt = new JsonSerializerOptions { WriteIndented = true };
        static readonly object WriteLock = new object();

        static string Sidecar(string log) { return log + ".sessions.json"; }

        static string Fallback(string log)
        {
            string key = BitConverter.ToString(SHA1.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(log).ToLowerInvariant()))).Replace("-", "").Substring(0, 16);
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WintecTK2Converter", "sessions");
            return Path.Combine(dir, Path.GetFileName(log) + "_" + key + ".json");
        }

        static SessionEdits Read(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                SessionEdits d = JsonSerializer.Deserialize<SessionEdits>(File.ReadAllText(path, Encoding.UTF8), Opt);
                return d != null && d.Format == "WintecTK2SessionEdits" ? d : null;
            }
            catch (Exception) { return null; }   // 壊れたファイルは無視する (編集がなくなるだけ)
        }

        public static SessionEdits Load(string log)
        {
            SessionEdits d = Read(Sidecar(log)) ?? Read(Fallback(log)) ?? new SessionEdits();
            if (d.Splits == null) d.Splits = new List<double>();
            if (d.Merges == null) d.Merges = new List<double>();
            return d;
        }

        /// <summary>保存する (ファイルを書くので、画面のスレッドでは呼ばない)。編集が空なら保存ファイルを消す。</summary>
        public static void Save(string log, SessionEdits ed)
        {
            lock (WriteLock)
            {
                string side = Sidecar(log), fall = Fallback(log);
                if (ed.IsEmpty) { TryDelete(side); TryDelete(fall); return; }
                ed.Log = Path.GetFileName(log);
                string json = JsonSerializer.Serialize(ed, Opt);
                try { File.WriteAllText(side, json, new UTF8Encoding(false)); TryDelete(fall); }
                catch (Exception)
                {
                    try { Directory.CreateDirectory(Path.GetDirectoryName(fall)); File.WriteAllText(fall, json, new UTF8Encoding(false)); }
                    catch (Exception) { }
                }
            }
        }

        static void TryDelete(string p) { try { if (File.Exists(p)) File.Delete(p); } catch (Exception) { } }
    }
}
