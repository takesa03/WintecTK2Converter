using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Media;

namespace Tk2Gui
{
    // =====================================================================
    //  動画素材のレイアウト (*.overlay.ini)。VSD のスキンに相当する。
    //  座標・大きさは 1920x1080 を基準にした画素数。ほかの解像度では高さの比で拡大縮小する。
    //  注釈は ';' が行のどこでも、'#' は行頭のみ (#RRGGBB の色指定のため)。
    // =====================================================================

    public sealed class OverlaySection
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        public readonly string Name;
        public readonly Dictionary<string, string> Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public OverlaySection(string name) { Name = name; }

        public string Str(string key, string def = "")
        {
            string v;
            return Values.TryGetValue(key, out v) && v.Length > 0 ? v : def;
        }
        public double Num(string key, double def)
        {
            double d;
            return double.TryParse(Str(key), NumberStyles.Float, Inv, out d) ? d : def;
        }
        public int Int(string key, int def) { return (int)Math.Round(Num(key, def)); }
        public bool Bool(string key, bool def)
        {
            string v = Str(key).ToLowerInvariant();
            if (v == "1" || v == "true" || v == "yes" || v == "on") return true;
            if (v == "0" || v == "false" || v == "no" || v == "off") return false;
            return def;
        }
        public Color Col(string key, string def) { return OverlayLayout.ParseColor(Str(key, def), OverlayLayout.ParseColor(def, Colors.White)); }
    }

    public sealed class OverlayLayout
    {
        public string Path;                 // 読み込んだ ini (相対パスの基準)
        public readonly Dictionary<string, OverlaySection> Sections = new Dictionary<string, OverlaySection>(StringComparer.OrdinalIgnoreCase);

        public OverlaySection this[string name]
        {
            get
            {
                OverlaySection s;
                if (!Sections.TryGetValue(name, out s)) { s = new OverlaySection(name); s.Values["show"] = "0"; }
                return s;
            }
        }

        public bool Shown(string name)
        {
            OverlaySection s;
            return Sections.TryGetValue(name, out s) && s.Bool("show", true);
        }

        /// <summary>ini のパスを基準にした絶対パス (空なら空)</summary>
        public string Resolve(string p)
        {
            if (string.IsNullOrWhiteSpace(p)) return "";
            p = Environment.ExpandEnvironmentVariables(p.Trim().Trim('"'));
            if (System.IO.Path.IsPathRooted(p)) return p;
            string dir = string.IsNullOrEmpty(Path) ? AppContext.BaseDirectory : System.IO.Path.GetDirectoryName(Path);
            return System.IO.Path.GetFullPath(System.IO.Path.Combine(dir, p));
        }

        public static OverlayLayout Load(string path)
        {
            OverlayLayout l = new OverlayLayout { Path = path };
            OverlaySection cur = null;
            foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                string line = raw;
                int sc = line.IndexOf(';');
                if (sc >= 0) line = line.Substring(0, sc);
                line = line.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
                {
                    string name = line.Substring(1, line.Length - 2).Trim();
                    if (!l.Sections.TryGetValue(name, out cur)) { cur = new OverlaySection(name); l.Sections[name] = cur; }
                    continue;
                }
                int eq = line.IndexOf('=');
                if (eq <= 0 || cur == null) continue;
                cur.Values[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }
            return l;
        }

        /// <summary>#RRGGBB / #AARRGGBB</summary>
        public static Color ParseColor(string s, Color def)
        {
            if (string.IsNullOrWhiteSpace(s)) return def;
            s = s.Trim().TrimStart('#');
            uint v;
            if (!uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v)) return def;
            if (s.Length == 6) return Color.FromArgb(255, (byte)(v >> 16), (byte)(v >> 8), (byte)v);
            if (s.Length == 8) return Color.FromArgb((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
            return def;
        }

        /// <summary>既定のレイアウト (exe と同じフォルダの overlay_default.ini)</summary>
        public static string DefaultPath { get { return System.IO.Path.Combine(AppContext.BaseDirectory, "overlay_default.ini"); } }
    }
}
