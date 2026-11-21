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
            byte[] bytes = File.ReadAllBytes(path);
            bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            return FromText(new UTF8Encoding(false).GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0)), path, bom);
        }

        /// <summary>
        /// 今の中身 (保存していない変更も含む) の写し。描画のスレッドに渡すときはこちらを渡す
        /// (エディタで変えている最中のものを別スレッドで読むと壊れるため)
        /// </summary>
        public OverlayLayout Clone()
        {
            OverlayLayout c = FromText(ToText(), Path, _bom);
            return c;
        }

        static OverlayLayout FromText(string text, string path, bool bom)
        {
            OverlayLayout l = new OverlayLayout { Path = path };
            l._bom = bom;
            l._nl = text.Contains("\r\n") || !text.Contains("\n") ? "\r\n" : "\n";
            l._endsNl = text.EndsWith("\n", StringComparison.Ordinal);
            l._lines = SplitLines(text, l._endsNl);
            OverlaySection cur = null;
            foreach (string raw in l._lines)
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
            l.FillDefaults();
            return l;
        }

        // 後から足した要素の既定値 (前の版で保存したレイアウトに節やキーが無くても、既定の位置に出す)。
        // 読み込んだ値として持つだけで、エディタで変えたキーのほかはファイルに書かない
        static readonly Dictionary<string, string[]> Defaults = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            // コース名: コース図の左上 (既定の [map] は x = 20, y = 50 で、この文字の高さぶん下)
            { "course", new[] { "show", "1", "anchor", "topleft", "x", "20", "y", "20", "size", "24", "color", "#FFFFFF",
                                "background", "0", "panel", "#1F000000", "panelopacity", "0.12", "panelpad", "8" } },
            // イベント名: 上の中央
            { "event", new[] { "show", "1", "anchor", "top", "x", "0", "y", "15", "size", "30", "color", "#FFFFFF",
                               "background", "0", "panel", "#1F000000", "panelopacity", "0.12", "panelpad", "8" } },
        };

        void FillDefaults()
        {
            foreach (KeyValuePair<string, string[]> d in Defaults)
            {
                OverlaySection s;
                if (!Sections.TryGetValue(d.Key, out s)) { s = new OverlaySection(d.Key); Sections[d.Key] = s; }
                for (int i = 0; i + 1 < d.Value.Length; i += 2)
                    if (!s.Values.ContainsKey(d.Value[i])) s.Values[d.Value[i]] = d.Value[i + 1];
            }
        }

        // ---------------- 書き込み (レイアウトエディタ用) ----------------
        //  読んだ行をそのまま覚えておき、保存では Set で変えた値の所だけを書き換える。
        //  行の並び・コメント・知らないキーはそのまま。無いキーは節の最後のキーの次に、無い節はファイルの最後に足す

        List<string> _lines = new List<string>();
        bool _bom = true, _endsNl = true;
        string _nl = "\r\n";
        readonly List<KeyValuePair<string, string>> _dirty = new List<KeyValuePair<string, string>>();   // (節, キー)

        static List<string> SplitLines(string text, bool endsNl)
        {
            List<string> ls = new List<string>(text.Replace("\r\n", "\n").Split('\n'));
            if (endsNl && ls.Count > 0) ls.RemoveAt(ls.Count - 1);
            return ls;
        }

        /// <summary>変えてまだ保存していない値があるか</summary>
        public bool Dirty { get { return _dirty.Count > 0; } }

        /// <summary>節 section のキー key を value にする (null でキーを消す)。保存は Save</summary>
        public void Set(string section, string key, string value)
        {
            OverlaySection s;
            if (!Sections.TryGetValue(section, out s)) { s = new OverlaySection(section); Sections[section] = s; }
            string old;
            bool had = s.Values.TryGetValue(key, out old);
            if (value == null) { if (!had) return; s.Values.Remove(key); }
            else { if (had && old == value) return; s.Values[key] = value; }
            if (!_dirty.Exists(d => d.Key.Equals(section, StringComparison.OrdinalIgnoreCase) && d.Value.Equals(key, StringComparison.OrdinalIgnoreCase)))
                _dirty.Add(new KeyValuePair<string, string>(section, key));
        }

        public void Set(string section, string key, double value) { Set(section, key, value.ToString("0.###", CultureInfo.InvariantCulture)); }

        /// <summary>1 行を読む。節の行なら section、キーの行なら key と値の範囲 [valStart, valEnd)</summary>
        static void ParseLine(string raw, out string section, out string key, out int valStart, out int valEnd)
        {
            section = null; key = null; valStart = valEnd = -1;
            string t = raw.Trim();
            if (t.Length == 0 || t.StartsWith("#", StringComparison.Ordinal) || t.StartsWith(";", StringComparison.Ordinal)) return;
            int sc = raw.IndexOf(';');
            string body = sc >= 0 ? raw.Substring(0, sc) : raw;
            string bt = body.Trim();
            if (bt.StartsWith("[", StringComparison.Ordinal) && bt.EndsWith("]", StringComparison.Ordinal)) { section = bt.Substring(1, bt.Length - 2).Trim(); return; }
            int eq = body.IndexOf('=');
            if (eq <= 0) return;
            key = body.Substring(0, eq).Trim();
            if (key.Length == 0) { key = null; return; }
            int a = eq + 1;
            while (a < body.Length && (body[a] == ' ' || body[a] == '\t')) a++;
            int b = body.Length;
            while (b > a && (body[b - 1] == ' ' || body[b - 1] == '\t')) b--;
            valStart = a; valEnd = b;
        }

        /// <summary>変えた値を書き込んだ ini の中身</summary>
        public string ToText()
        {
            List<string> lines = new List<string>(_lines);
            foreach (KeyValuePair<string, string> d in _dirty)
            {
                OverlaySection s;
                string val = Sections.TryGetValue(d.Key, out s) && s.Values.ContainsKey(d.Value) ? s.Values[d.Value] : null;
                // その節の範囲とキーの行を探す (同じ節・キーが何度もあれば、読み込みで勝つ最後のもの)
                string cur = null;
                int secLine = -1, lastKeyLine = -1, keyLine = -1;
                for (int i = 0; i < lines.Count; i++)
                {
                    string sec, key; int va, vb;
                    ParseLine(lines[i], out sec, out key, out va, out vb);
                    if (sec != null) { cur = sec; if (sec.Equals(d.Key, StringComparison.OrdinalIgnoreCase)) { secLine = i; lastKeyLine = i; } continue; }
                    if (key == null || cur == null || !cur.Equals(d.Key, StringComparison.OrdinalIgnoreCase)) continue;
                    lastKeyLine = i;
                    if (key.Equals(d.Value, StringComparison.OrdinalIgnoreCase)) keyLine = i;
                }
                if (keyLine >= 0)
                {
                    if (val == null) { lines.RemoveAt(keyLine); continue; }
                    string raw = lines[keyLine];
                    string sec2, key2; int va2, vb2;
                    ParseLine(raw, out sec2, out key2, out va2, out vb2);
                    string head = raw.Substring(0, va2), tail = raw.Substring(vb2);
                    int oldLen = vb2 - va2;
                    // 注釈の前の空白を、注釈の列がなるべく変わらないように詰める・広げる
                    int sp = 0;
                    while (sp < tail.Length && (tail[sp] == ' ' || tail[sp] == '\t')) sp++;
                    if (sp < tail.Length && tail[sp] == ';')
                        tail = new string(' ', Math.Max(1, sp + oldLen - val.Length)) + tail.Substring(sp);
                    lines[keyLine] = head + val + tail;
                }
                else if (val != null)
                {
                    if (secLine < 0)
                    {
                        if (lines.Count > 0 && lines[lines.Count - 1].Trim().Length > 0) lines.Add("");
                        lines.Add("[" + d.Key + "]");
                        lines.Add(d.Value + " = " + val);
                    }
                    else lines.Insert(lastKeyLine + 1, d.Value + " = " + val);
                }
            }
            return string.Join(_nl, lines) + (_endsNl ? _nl : "");
        }

        /// <summary>保存 (path を省くと読み込んだファイル)。一時ファイルに書いてから置き換える。保存した後は、その中身を基準にする</summary>
        public void Save(string path = null)
        {
            if (string.IsNullOrEmpty(path)) path = Path;
            string text = ToText();
            byte[] body = new UTF8Encoding(false).GetBytes(text);
            byte[] all = body;
            if (_bom) { all = new byte[body.Length + 3]; all[0] = 0xEF; all[1] = 0xBB; all[2] = 0xBF; Buffer.BlockCopy(body, 0, all, 3, body.Length); }
            string tmp = path + ".tmp";
            File.WriteAllBytes(tmp, all);
            if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
            Path = path;
            _lines = SplitLines(text, _endsNl);
            _dirty.Clear();
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
