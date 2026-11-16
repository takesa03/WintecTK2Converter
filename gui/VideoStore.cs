using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tk2Gui
{
    // =====================================================================
    //  走行動画とログの時刻の対応を、ログごとに保存する (ログ名.videos.json)
    //  1 本の動画につき「動画の 0 秒がログの何秒か (t0)」を持つ。
    //  動画の t 秒 = ログの t0 + t 秒。切り出した動画も、切り出した所の t0 で登録する
    // =====================================================================
    public static class VideoStore
    {
        public sealed class Entry
        {
            [JsonPropertyName("path")] public string Path { get; set; }
            [JsonPropertyName("t0")] public double T0 { get; set; }               // 動画の 0 秒に当たるログの通算秒
            [JsonPropertyName("duration")] public double Duration { get; set; }   // 動画の長さ [s]
            [JsonPropertyName("clipOf")] public string ClipOf { get; set; }       // 切り出した動画なら元の動画
            [JsonPropertyName("label")] public string Label { get; set; }         // 切り出した範囲 (S6-L3 など)
            [JsonPropertyName("overlay")] public bool Overlay { get; set; }       // 表示を焼き込んだ切り出し (比較の画面では使わない)

            [JsonIgnore] public double EndT { get { return T0 + Duration; } }
        }

        sealed class FileData
        {
            [JsonPropertyName("format")] public string Format { get; set; } = "WintecTK2Videos";
            [JsonPropertyName("version")] public int Version { get; set; } = 1;
            [JsonPropertyName("log")] public string Log { get; set; }
            [JsonPropertyName("videos")] public List<Entry> Videos { get; set; } = new List<Entry>();
        }

        static readonly JsonSerializerOptions Opt = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        public static string SidecarPath(string log) { return log + ".videos.json"; }

        public static List<Entry> Load(string log)
        {
            try
            {
                string p = SidecarPath(log);
                if (!File.Exists(p)) return new List<Entry>();
                FileData d = JsonSerializer.Deserialize<FileData>(File.ReadAllText(p, Encoding.UTF8), Opt);
                return d != null && d.Format == "WintecTK2Videos" && d.Videos != null ? d.Videos.Where(v => !string.IsNullOrEmpty(v.Path)).ToList() : new List<Entry>();
            }
            catch (Exception)
            {
                return new List<Entry>();
            }
        }

        public static void Save(string log, List<Entry> list)
        {
            FileData d = new FileData { Log = System.IO.Path.GetFileName(log), Videos = list };
            File.WriteAllText(SidecarPath(log), JsonSerializer.Serialize(d, Opt), new UTF8Encoding(false));
        }

        /// <summary>動画を登録 (同じパスがあれば置き換える)</summary>
        public static void Put(string log, Entry e)
        {
            List<Entry> list = Load(log);
            list.RemoveAll(v => string.Equals(v.Path, e.Path, StringComparison.OrdinalIgnoreCase));
            list.Add(e);
            Save(log, list);
        }

        public static Entry Get(string log, string video)
        {
            return Load(log).FirstOrDefault(v => string.Equals(v.Path, video, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 範囲 [t0, t1] を含む動画。あるファイルのうち、範囲を全部含むもの → 一部を含むもの の順で、短い (切り出した) ものを優先
        /// </summary>
        public static Entry Find(string log, double t0, double t1)
        {
            // 表示を焼き込んだ動画は除く (比較の画面で表示を重ねると二重になる)
            List<Entry> list = Load(log).Where(v => !v.Overlay && File.Exists(v.Path) && v.T0 < t1 && v.EndT > t0).ToList();
            Entry full = list.Where(v => v.T0 <= t0 + 0.05 && v.EndT >= t1 - 0.05).OrderBy(v => v.Duration).FirstOrDefault();
            if (full != null) return full;
            return list.OrderByDescending(v => Math.Min(v.EndT, t1) - Math.Max(v.T0, t0)).FirstOrDefault();
        }
    }
}
