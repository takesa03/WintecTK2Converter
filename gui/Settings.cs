using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Tk2Gui
{
    /// <summary>
    /// 画面の設定の保存・復元 (ウィンドウの位置と大きさ、各画面の入力値・チェック・選択、ファイル選択で最後に開いたフォルダ)。
    /// 保存先は exe と同じフォルダの WintecTK2Converter.settings.json (tk2nmea.ini と同じ場所)。
    /// exe のフォルダに書き込めないとき (Program Files に置いたときなど) は %LOCALAPPDATA%\WintecTK2Converter\ に保存する。
    /// 各画面を閉じるときに保存し、次に開いたときに戻す。
    /// </summary>
    public static class Settings
    {
        public sealed class Bounds
        {
            [JsonPropertyName("left")] public double Left { get; set; }
            [JsonPropertyName("top")] public double Top { get; set; }
            [JsonPropertyName("width")] public double Width { get; set; }
            [JsonPropertyName("height")] public double Height { get; set; }
            [JsonPropertyName("maximized")] public bool Maximized { get; set; }
        }

        sealed class Data
        {
            [JsonPropertyName("format")] public string Format { get; set; } = "WintecTK2Settings";
            [JsonPropertyName("version")] public int Version { get; set; } = 1;
            [JsonPropertyName("windows")] public Dictionary<string, Bounds> Windows { get; set; } = new Dictionary<string, Bounds>();
            [JsonPropertyName("values")] public Dictionary<string, string> Values { get; set; } = new Dictionary<string, string>();
        }

        static readonly JsonSerializerOptions Opt = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        const string FileName = "WintecTK2Converter.settings.json";
        /// <summary>exe と同じフォルダの設定ファイル</summary>
        public static readonly string FilePath = Path.Combine(AppContext.BaseDirectory, FileName);
        /// <summary>exe のフォルダに書き込めないときの保存先</summary>
        static readonly string FallbackPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WintecTK2Converter", FileName);

        static Data Read(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                Data d = JsonSerializer.Deserialize<Data>(File.ReadAllText(path, Encoding.UTF8), Opt);
                if (d == null || d.Format != "WintecTK2Settings") return null;
                if (d.Windows == null) d.Windows = new Dictionary<string, Bounds>();
                if (d.Values == null) d.Values = new Dictionary<string, string>();
                return d;
            }
            catch (Exception) { return null; }   // 壊れた設定ファイルは無視する (既定の状態で起動するだけ)
        }

        static Data _data;

        static Data D
        {
            get
            {
                if (_data != null) return _data;
                _data = Read(FilePath) ?? Read(FallbackPath) ?? new Data();
                return _data;
            }
        }

        public static void Save()
        {
            string json = JsonSerializer.Serialize(D, Opt);
            try
            {
                File.WriteAllText(FilePath, json, new UTF8Encoding(false));
                return;
            }
            catch (Exception) { }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FallbackPath));
                File.WriteAllText(FallbackPath, json, new UTF8Encoding(false));
            }
            catch (Exception) { }   // 保存できなくても動作は続ける
        }

        // ---------------- 値 ----------------

        public static string Get(string key) { string v; return D.Values.TryGetValue(key, out v) ? v : null; }
        public static void Set(string key, string value) { if (value == null) D.Values.Remove(key); else D.Values[key] = value; }

        // ---------------- ファイル選択で最後に開いたフォルダ ----------------

        /// <summary>最後に開いたフォルダを、ファイル選択の最初のフォルダにする (まだ決まっていないときだけ)</summary>
        public static void InitialDir(Microsoft.Win32.CommonItemDialog dlg, string key)
        {
            if (!string.IsNullOrEmpty(dlg.InitialDirectory)) return;
            string dir = Get("dir." + key);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) dlg.InitialDirectory = dir;
        }

        /// <summary>選んだファイル (またはフォルダ) のフォルダを覚える</summary>
        public static void RememberDir(string key, string path, bool isFolder = false)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return;
                string dir = isFolder ? path : Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Set("dir." + key, dir);
            }
            catch (Exception) { }
        }

        // ---------------- ウィンドウ ----------------

        /// <summary>
        /// ウィンドウの位置・大きさを戻し、閉じるときに保存する。あわせて、ほかの設定の保存 (save) も閉じるときに呼ぶ。
        /// InitializeComponent() の直後に呼ぶ
        /// </summary>
        public static void Track(Window w, string key, Action save = null)
        {
            Bounds b;
            if (D.Windows.TryGetValue(key, out b) && b.Width >= 200 && b.Height >= 150 && OnScreen(b))
            {
                w.WindowStartupLocation = WindowStartupLocation.Manual;
                w.Left = b.Left; w.Top = b.Top; w.Width = b.Width; w.Height = b.Height;
                if (b.Maximized) w.WindowState = WindowState.Maximized;
            }
            w.Closing += (s, e) =>
            {
                // 子のウィンドウ (コース定義・ラップ解析・比較再生) を先に閉じて、それぞれの設定を保存させる
                // (親を閉じると子も閉じるが、そのままだとアプリが先に終わり、子の設定が保存されないことがある)
                foreach (Window child in new List<Window>(w.OwnedWindows.Cast<Window>()))
                {
                    try { child.Close(); } catch (Exception) { }
                }
                try
                {
                    Rect r = w.WindowState == WindowState.Normal ? new Rect(w.Left, w.Top, w.ActualWidth, w.ActualHeight) : w.RestoreBounds;
                    if (!r.IsEmpty && r.Width > 0 && r.Height > 0)
                        D.Windows[key] = new Bounds { Left = r.Left, Top = r.Top, Width = r.Width, Height = r.Height, Maximized = w.WindowState == WindowState.Maximized };
                    if (save != null) save();
                }
                catch (Exception) { }
                Save();
            };
        }

        // 前回の位置が、今の画面 (複数モニターを含む全体) の中に十分入っているか (モニターを外したときなどに、画面の外へ出ないように)
        static bool OnScreen(Bounds b)
        {
            Rect screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                                   SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            Rect title = new Rect(b.Left, b.Top, b.Width, 40);   // タイトルバーが見えていれば、つかんで動かせる
            Rect hit = Rect.Intersect(screen, title);
            return !hit.IsEmpty && hit.Width >= 120 && hit.Height >= 20;
        }

        // ---------------- 入力欄・チェック・選択 (戻す値と、閉じるときに覚える値を 1 行で書くための補助) ----------------

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public sealed class Binder
        {
            readonly string _prefix;
            readonly List<Action> _savers = new List<Action>();
            public Binder(string prefix) { _prefix = prefix + "."; }

            /// <summary>閉じるときに呼ぶ (覚える)</summary>
            public void SaveAll() { foreach (Action a in _savers) a(); }

            public void Text(TextBox tb, string key)
            {
                string v = Get(_prefix + key);
                if (v != null) tb.Text = v;
                _savers.Add(() => Set(_prefix + key, tb.Text));
            }

            public void Check(ToggleButton cb, string key)
            {
                string v = Get(_prefix + key);
                if (v != null) cb.IsChecked = v == "1";
                _savers.Add(() => Set(_prefix + key, cb.IsChecked == true ? "1" : "0"));
            }

            public void Combo(Selector c, string key)
            {
                string v = Get(_prefix + key);
                int i;
                if (v != null && int.TryParse(v, NumberStyles.Integer, Inv, out i) && i >= 0 && i < c.Items.Count) c.SelectedIndex = i;
                _savers.Add(() => { if (c.SelectedIndex >= 0) Set(_prefix + key, c.SelectedIndex.ToString(Inv)); });
            }

            public void Expander(System.Windows.Controls.Expander x, string key)
            {
                string v = Get(_prefix + key);
                if (v != null) x.IsExpanded = v == "1";
                _savers.Add(() => Set(_prefix + key, x.IsExpanded ? "1" : "0"));
            }

            /// <summary>そのほかの値 (文字列で読み書きする)</summary>
            public void Custom(string key, Func<string> get, Action<string> set)
            {
                string v = Get(_prefix + key);
                if (v != null) { try { set(v); } catch (Exception) { } }
                _savers.Add(() => { string s = get(); if (s != null) Set(_prefix + key, s); });
            }
        }
    }
}
