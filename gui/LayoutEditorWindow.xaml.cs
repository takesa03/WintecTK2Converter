using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Microsoft.Win32;

namespace Tk2Gui
{
    /// <summary>レイアウトエディタに渡すサブ動画 (無ければ null を渡す)</summary>
    public sealed class SubPreview
    {
        public BitmapSource Frame;              // 今のコマ (トリミング前の全体。無ければ枠だけ出す)
        public double[] Crop;                   // [左, 上, 右, 下] (割合)
        public Action<double[]> CropChanged;    // トリミングを変えたとき (呼び出し側で log.json に保存)
        public int SourceWidth, SourceHeight;   // 元の動画の画素数 (トリミングを画素で入れるため。0 なら Frame の大きさ)
    }

    // =====================================================================
    //  表示のレイアウトエディタ (動画に重ねる速度計・地図・ラップなどの ON/OFF と位置)。設計は docs/subvideo_design.md の 6 章
    //  プレビューは高さ 1080 (幅は映像の縦横比) で描くので、画面の 1 画素 = ini の 1。
    //  変えるたびに呼び出し元へレイアウトの写し (Clone) を渡し、呼び出し元のプレビューにもすぐ出す。
    //  保存は ini の書き換え (OverlayLayout.Save。コメント・並びはそのまま)。既定の overlay_default.ini には上書きしない
    // =====================================================================
    public partial class LayoutEditorWindow : Window
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        sealed class Elem
        {
            public string Name, Label, Note;
            public string[] Keys;
            public bool NoPosition;
            public CheckBox Check;
            public ListBoxItem Item;
        }

        static readonly Dictionary<string, string> KeyLabels = new Dictionary<string, string>
        {
            { "x", "x (横)" }, { "y", "y (縦)" }, { "scale", "倍率" }, { "size", "文字の大きさ" }, { "width", "幅" }, { "height", "高さ" },
            { "laps", "周回の数" }, { "opacity", "不透明度" }, { "items", "項目" }, { "align", "寄せ方" }, { "border", "枠線の太さ" },
            { "background", "背景" }, { "session", "セッション番号" }, { "panel", "背景の色" }, { "panelopacity", "背景の濃さ" },
        };

        static readonly string[] Anchors = { "topleft", "top", "topright", "bottomleft", "bottom", "bottomright" };
        static readonly string[] AnchorLabels = { "左上", "上 (中央)", "右上", "左下", "下 (中央)", "右下" };

        readonly List<Elem> _elems = new List<Elem>
        {
            new Elem { Name = "speedometer", Label = "速度計", Keys = new[] { "x", "y", "scale" } },
            new Elem { Name = "gmeter", Label = "G メーター", Keys = new[] { "x", "y", "scale" } },
            new Elem { Name = "tacho", Label = "回転計", Keys = new[] { "x", "y", "scale", "opacity" }, Note = "音声から回転数を解析したときだけ出ます。" },
            new Elem { Name = "map", Label = "コース図", Keys = new[] { "x", "y", "width", "height", "background", "panel", "panelopacity" } },
            new Elem { Name = "course", Label = "コース名", Keys = new[] { "x", "y", "size", "background", "panel", "panelopacity" }, Note = "コース定義の表示名を出します (ラップ解析の画面の「表示名」)。空なら出ません。" },
            new Elem { Name = "event", Label = "イベント名", Keys = new[] { "x", "y", "size", "background", "panel", "panelopacity" }, Note = "ログごとのイベント名を出します (ラップ解析の画面の「イベント名」)。空なら出ません。" },
            new Elem { Name = "machine", Label = "マシン名", Keys = new[] { "x", "y", "size", "background", "panel", "panelopacity" }, Note = "ログごとのマシン名を出します (ラップ解析の画面の「マシン名」)。空なら出ません。" },
            new Elem { Name = "session", Label = "ドライバー・心拍", Keys = new[] { "x", "y", "size", "background", "panel", "panelopacity" }, Note = "ドライバー名と心拍だけを出します。セッション番号はラップタイムの先頭の行に出ます。" },
            new Elem { Name = "heart", Label = "心拍", Keys = new[] { "size" }, NoPosition = true, Note = "位置はセッション・ドライバーの右に付いて動きます。FIT を合わせたセッションだけに出ます。" },
            new Elem { Name = "laptime", Label = "ラップタイム", Keys = new[] { "x", "y", "size", "laps", "session", "background", "panel", "panelopacity" } },
            new Elem { Name = "sector", Label = "セクター", Keys = new[] { "x", "y", "size", "laps", "background", "panel", "panelopacity" } },
            new Elem { Name = "graph", Label = "速度のグラフ", Keys = new[] { "x", "y", "width", "height" }, Note = "出し入れは書き出し・時刻合わせ・動画で比較の 3 画面で共通です。" },
            new Elem { Name = "info", Label = "情報 (日時・緯度経度など)", Keys = new[] { "x", "y", "size", "items" } },
            new Elem { Name = "subvideo", Label = "サブ動画", Keys = new[] { "x", "y", "width", "height", "align", "border", "opacity" }, Note = "トリミングした絵を、縦横比を変えずに枠に収めます。" },
        };

        readonly OverlayLayout _lay;
        readonly OverlayData _data;
        readonly double _logT, _fps;
        readonly SubPreview _sub;
        readonly Action<OverlayLayout> _changed;
        OverlayRenderer _renderer;
        Dictionary<string, Rect> _bounds = new Dictionary<string, Rect>();
        Elem _sel;
        bool _fill;

        /// <summary>保存したとき (保存先のパス)</summary>
        public event Action<string> Saved;

        public LayoutEditorWindow(string layoutPath, OverlayData data, double logT, double fps, BitmapSource background, SubPreview sub, Action<OverlayLayout> changed)
        {
            InitializeComponent();
            Theme.ApplyDark(this);
            Title = "表示のレイアウト  " + App.VersionText;
            _lay = OverlayLayout.Load(layoutPath);
            _data = data; _logT = logT; _fps = fps > 0 ? fps : 30; _sub = sub; _changed = changed;
            if (background != null)
            {
                BackImage.Source = background;
                Stage.Width = Math.Round(1080.0 * background.PixelWidth / Math.Max(1, background.PixelHeight));
            }
            for (int i = 0; i < Anchors.Length; i++) AnchorCombo.Items.Add(new ComboBoxItem { Content = AnchorLabels[i], Tag = Anchors[i] });
            BuildList();
            Settings.Track(this, "LayoutEditorWindow", null);
            PreviewKeyDown += OnKey;
            Closing += OnClosing;
            Loaded += (s, e) => { Render(true); Select(_elems[0]); };
            UpdatePath();
        }

        void Status(string s) { StatusText.Text = s; }

        void UpdatePath()
        {
            PathText.Text = _lay.Path + (IsDefault(_lay.Path) ? "   (既定のレイアウト。保存すると別の名前で保存します)" : "");
            UndoButton.IsEnabled = _undo.Count > 0;
        }

        static bool IsDefault(string p)
        {
            try { return string.Equals(System.IO.Path.GetFullPath(p), System.IO.Path.GetFullPath(OverlayLayout.DefaultPath), StringComparison.OrdinalIgnoreCase); }
            catch (Exception) { return false; }
        }

        // ---------------- 要素の一覧 ----------------

        bool IsShown(Elem e)
        {
            if (e.Name == "graph") return OverlayPrefs.Graph;
            // 回転計・心拍は ini に節が無くても出す (OverlayRenderer と同じ)
            if ((e.Name == "tacho" || e.Name == "heart") && !_lay.Sections.ContainsKey(e.Name)) return true;
            return _lay.Shown(e.Name);
        }

        void BuildList()
        {
            foreach (Elem e in _elems)
            {
                Elem el = e;
                CheckBox cb = new CheckBox { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
                if (e.Name == "graph") OverlayPrefs.BindGraph(cb, () => { if (IsLoaded) Render(false); });
                else
                {
                    cb.IsChecked = IsShown(e);
                    RoutedEventHandler h = (s, a) =>
                    {
                        if (_fill || (cb.IsChecked == true) == IsShown(el)) return;
                        Begin();
                        Change(el.Name, "show", cb.IsChecked == true ? "1" : "0");
                        Commit();
                        Render(false);
                        Status(el.Label + (cb.IsChecked == true ? " を出します。" : " を消します。"));
                    };
                    cb.Checked += h; cb.Unchecked += h;
                }
                StackPanel sp = new StackPanel { Orientation = Orientation.Horizontal };
                sp.Children.Add(cb);
                sp.Children.Add(new TextBlock { Text = e.Label, VerticalAlignment = VerticalAlignment.Center });
                e.Check = cb;
                e.Item = new ListBoxItem { Content = sp, Tag = e, Padding = new Thickness(4, 3, 4, 3) };
                ElementList.Items.Add(e.Item);
            }
        }

        void Element_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_fill) return;
            ListBoxItem it = ElementList.SelectedItem as ListBoxItem;
            if (it != null) Select((Elem)it.Tag);
        }

        void Select(Elem e)
        {
            _sel = e;
            _fill = true;
            ElementList.SelectedItem = e.Item;
            _fill = false;
            FillProps();
            UpdateSel();
        }

        // ---------------- 項目 ----------------

        void FillProps()
        {
            Elem e = _sel;
            _fill = true;
            SelTitle.Text = e.Label + "  [" + e.Name + "]";
            UpdateNote();
            AnchorRow.Visibility = e.NoPosition ? Visibility.Collapsed : Visibility.Visible;
            string an = Anchor(e);
            AnchorCombo.SelectedIndex = Math.Max(0, Array.IndexOf(Anchors, an));
            PropPanel.Children.Clear();
            OverlaySection s = _lay[e.Name];
            foreach (string k in e.Keys)
            {
                Grid g = new Grid { Margin = new Thickness(0, 0, 0, 4) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.Children.Add(new TextBlock { Text = KeyLabels.ContainsKey(k) ? KeyLabels[k] : k, VerticalAlignment = VerticalAlignment.Center });
                if (k == "background" || k == "session")      // ON/OFF はチェックで
                {
                    string kk = k;
                    bool defOn = k == "session";     // 既定: セッション番号は出す、背景は出さない
                    Func<string, bool> isOn = v => v.Length == 0 ? defOn : v == "1";
                    CheckBox bc = new CheckBox { Tag = k, Content = k == "session" ? "先頭の行に出す (ジムカーナは出ません)" : "薄い背景を敷く", VerticalAlignment = VerticalAlignment.Center, IsChecked = isOn(CurrentValue(e, k)) };
                    System.Windows.Automation.AutomationProperties.SetAutomationId(bc, "Prop_" + k);
                    Grid.SetColumn(bc, 1);
                    RoutedEventHandler bh = (o, a) =>
                    {
                        if (_fill || _sel == null) return;
                        bool on = bc.IsChecked == true;
                        if (isOn(CurrentValue(_sel, kk)) == on) return;
                        Begin();
                        Change(_sel.Name, kk, on ? "1" : "0");
                        Commit();
                        Render(true);
                        Status(_sel.Label + " の" + (kk == "session" ? "セッション番号" : "背景") + "を " + (on ? "出す" : "出さない") + "ようにしました。");
                    };
                    bc.Checked += bh; bc.Unchecked += bh;
                    g.Children.Add(bc);
                    PropPanel.Children.Add(g);
                    continue;
                }
                TextBox tb = new TextBox { Tag = k, FontFamily = new FontFamily("Consolas"), Text = CurrentValue(e, k) };
                System.Windows.Automation.AutomationProperties.SetAutomationId(tb, "Prop_" + k);
                tb.ToolTip = k == "panelopacity" ? "0〜1 (0.12 = グラフの背景と同じ)。ホイールで増減。空にすると背景の色の先頭 2 桁の濃さ" : k == "panel" ? "#AARRGGBB (先頭 2 桁が濃さ。空にすると既定の #1F000000 = グラフの背景と同じ濃さ)" : k == "items" ? "DateTime, Elapsed, Lat, Lng, Alt, Dist, Speed, G をカンマで区切って" : k == "align" ? "center / left / right / top / bottom (\"top left\" のように組み合わせ可)" : "Enter で反映。数値はホイールで増減 (空にすると既定の値)";
                Grid.SetColumn(tb, 1);
                tb.KeyDown += (o, a) => { if (a.Key == Key.Enter) { ApplyProp(tb); a.Handled = true; } };
                tb.LostFocus += (o, a) => ApplyProp(tb);
                tb.MouseWheel += (o, a) => { WheelProp(tb, a.Delta > 0 ? 1 : -1); a.Handled = true; };
                g.Children.Add(tb);
                PropPanel.Children.Add(g);
            }
            CropGroup.Visibility = e.Name == "subvideo" ? Visibility.Visible : Visibility.Collapsed;
            FillCrop();
            _fill = false;
        }

        /// <summary>選んだ要素の説明 (below / above の指定があればそのことも)</summary>
        void UpdateNote()
        {
            Elem e = _sel;
            if (e == null) return;
            string note = e.Note ?? "";
            if (e.Name == "laptime" && _lay[e.Name].Str("below", "").Length > 0) note += (note.Length > 0 ? "\n" : "") + "いまは「" + _lay[e.Name].Str("below") + " の下に続ける」指定です。動かすと、この指定を外して位置を数値にします。";
            if (e.Name == "graph" && !_lay[e.Name].Str("above", "sector").Equals("none", StringComparison.OrdinalIgnoreCase))
                note += (note.Length > 0 ? "\n" : "") + "いまは「セクターの上に置く」指定です (ジムカーナを除く)。動かすと、この指定を外して位置と大きさを数値にします。";
            if (e.Name == "sector" && _data.Def.IsGymkhana && _lay[e.Name].Bool("follow", true))
                note += (note.Length > 0 ? "\n" : "") + "いまは「ラップタイムの下に付ける」指定です (ジムカーナ)。動かすと、この指定を外して位置を数値にします。";
            SelNote.Text = note;
            SelNote.Visibility = note.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>画面に出す値 (ini に無いキーは、サブ動画なら既定の値、ほかは空 = 既定)</summary>
        string CurrentValue(Elem e, string k)
        {
            OverlaySection s = _lay[e.Name];
            string v = s.Str(k, "");
            if (v.Length > 0 || e.Name != "subvideo") return v;
            SubVideoLayout d = new SubVideoLayout();
            switch (k)
            {
                case "x": return d.X.ToString(Inv);
                case "y": return d.Y.ToString(Inv);
                case "width": return d.Width.ToString(Inv);
                case "height": return d.Height.ToString(Inv);
                case "align": return d.Align;
                case "border": return d.Border.ToString(Inv);
                case "opacity": return d.Opacity.ToString(Inv);
            }
            return v;
        }

        string Anchor(Elem e)
        {
            string a = _lay[e.Name].Str("anchor", "").ToLowerInvariant();
            if (a.Length > 0) return a;
            if (e.Name == "subvideo") return new SubVideoLayout().Anchor;
            if (e.Name == "tacho" && !_lay.Sections.ContainsKey("tacho")) return "bottomleft";
            return "topleft";
        }

        /// <summary>入力欄の値を今のレイアウトに合わせる (all = 入力中の欄も)</summary>
        void RefreshPropValues(bool all = false)
        {
            if (_sel == null) return;
            bool f = _fill;
            _fill = true;
            foreach (Grid g in PropPanel.Children.OfType<Grid>())
            {
                TextBox tb = g.Children.OfType<TextBox>().FirstOrDefault();
                if (tb != null && (all || !tb.IsKeyboardFocused)) tb.Text = CurrentValue(_sel, (string)tb.Tag);
                CheckBox cb = g.Children.OfType<CheckBox>().FirstOrDefault();
                if (cb != null)
                {
                    string ck = (string)cb.Tag, cv = CurrentValue(_sel, ck);
                    cb.IsChecked = cv.Length == 0 ? ck == "session" : cv == "1";
                }
            }
            _fill = f;
        }

        void ApplyProp(TextBox tb)
        {
            if (_fill || _sel == null) return;
            string k = (string)tb.Tag, v = tb.Text.Trim();
            if (v == CurrentValue(_sel, k)) return;
            if (v.Length > 0 && k == "panel" && !System.Text.RegularExpressions.Regex.IsMatch(v, "^#([0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})$"))
            { Status("色は #RRGGBB か #AARRGGBB で入れてください: " + v); tb.Text = CurrentValue(_sel, k); return; }
            if (v.Length > 0 && k != "items" && k != "align" && k != "panel")
            {
                double d;
                if (!double.TryParse(v, NumberStyles.Float, Inv, out d)) { Status("数値を入れてください: " + v); tb.Text = CurrentValue(_sel, k); return; }
            }
            Begin();
            if ((k == "x" || k == "y")) Unchain(_sel);
            Change(_sel.Name, k, v.Length > 0 ? v : null);
            Commit();
            Render(true);
            RefreshPropValues(true);
            Status(_sel.Label + " の " + (KeyLabels.ContainsKey(k) ? KeyLabels[k] : k) + " を " + (v.Length > 0 ? v : "既定") + " にしました。");
        }

        void WheelProp(TextBox tb, int dir)
        {
            string k = (string)tb.Tag;
            if (k == "items" || k == "align" || k == "panel") return;
            double step = k == "panelopacity" ? 0.02 : k == "scale" || k == "opacity" ? 0.05 : k == "width" || k == "height" ? 10 : 1;
            double v;
            if (!double.TryParse(tb.Text.Trim(), NumberStyles.Float, Inv, out v)) v = k == "scale" || k == "opacity" ? 1 : k == "panelopacity" ? 0.12 : 0;
            v = Math.Round((v + dir * step) / step) * step;
            if (k == "panelopacity") v = Math.Min(1, v);
            if (k == "opacity") v = Math.Max(0.05, Math.Min(1, v));
            if (k == "scale") v = Math.Max(0.05, v);
            if (k != "x" && k != "y") v = Math.Max(0, v);
            tb.Text = v.ToString("0.###", Inv);
            ApplyProp(tb);
        }

        void Anchor_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_fill || _sel == null || _sel.NoPosition) return;
            string a = (string)((ComboBoxItem)AnchorCombo.SelectedItem).Tag;
            if (a == Anchor(_sel)) return;
            Rect before = RectOf(_sel.Name);
            Begin();
            Unchain(_sel);
            Change(_sel.Name, "anchor", a);
            KeepPlace(_sel, before);
            Commit();
            RefreshPropValues();
            Status(_sel.Label + " の基準の角を「" + AnchorLabels[Array.IndexOf(Anchors, a)] + "」にしました (見た目の位置は保ちました)。");
        }

        // ---------------- 位置 ----------------

        /// <summary>below (ラップタイム)・above (グラフ) の指定を外し、見た目の位置のまま数値の位置にする</summary>
        void Unchain(Elem e)
        {
            bool chained = e.Name == "laptime" && _lay[e.Name].Str("below", "").Length > 0
                        || e.Name == "graph" && !_lay[e.Name].Str("above", "sector").Equals("none", StringComparison.OrdinalIgnoreCase)
                        || e.Name == "sector" && _data.Def.IsGymkhana && _lay[e.Name].Bool("follow", true);
            if (!chained) return;
            Rect before = RectOf(e.Name);
            if (e.Name == "laptime") Change(e.Name, "below", null);
            else if (e.Name == "sector") Change(e.Name, "follow", "0");
            else
            {
                // セクターの上に置いていたときの大きさ (幅はセクターに合わせ、高さはラップタイムに重ならないよう縮めていた) を数値にして保つ
                Rect g = _renderer != null ? _renderer.GraphRect : Rect.Empty;
                Change(e.Name, "above", "none");
                if (!g.IsEmpty)
                {
                    Change(e.Name, "width", Math.Round(g.Width).ToString(Inv));
                    Change(e.Name, "height", Math.Round(g.Height).ToString(Inv));
                }
            }
            KeepPlace(e, before);
            UpdateNote();
            RefreshPropValues();
        }

        /// <summary>描き直して、要素の枠が before と同じ所に来るように x・y を直す</summary>
        void KeepPlace(Elem e, Rect before)
        {
            Render(false);
            Rect after = RectOf(e.Name);
            if (before.IsEmpty || after.IsEmpty) return;
            Move(e, before.X - after.X, before.Y - after.Y);
            Render(false);
        }

        /// <summary>見た目で (dx, dy) 動かす (1080 基準の画素)</summary>
        void Move(Elem e, double dx, double dy)
        {
            if (e.NoPosition) return;
            OverlaySection s = _lay[e.Name];
            string a = Anchor(e);
            SubVideoLayout d = new SubVideoLayout();
            double x = s.Num("x", e.Name == "subvideo" ? d.X : 0), y = s.Num("y", e.Name == "subvideo" ? d.Y : 0);
            x += a.Contains("right") ? -dx : dx;
            y += a.Contains("bottom") ? -dy : dy;
            Change(e.Name, "x", Math.Round(x).ToString(Inv));
            Change(e.Name, "y", Math.Round(y).ToString(Inv));
        }

        Rect RectOf(string name)
        {
            if (name == "subvideo")
            {
                SubVideoLayout sv = SubVideoLayout.From(_lay);
                return sv.Show ? sv.Box(Stage.Width, Stage.Height) : Rect.Empty;
            }
            if (name == "heart") name = "session";
            Rect r;
            return _bounds.TryGetValue(name, out r) ? r : Rect.Empty;
        }

        // ---------------- マウス・キー ----------------

        Point _dragStart, _dragLast;
        bool _dragging, _dragMoved;

        void Stage_Down(object sender, MouseButtonEventArgs e)
        {
            Point p = e.GetPosition(Stage);
            Elem hit = null; double best = double.MaxValue;
            foreach (Elem el in _elems)
            {
                if (el.NoPosition || !IsShown(el)) continue;
                Rect r = RectOf(el.Name);
                if (r.IsEmpty || !r.Contains(p)) continue;
                double area = r.Width * r.Height;
                if (area < best) { best = area; hit = el; }
            }
            if (hit == null) return;
            Select(hit);
            _dragStart = _dragLast = p;
            _dragging = true;
            _dragMoved = false;
            Begin();
            Stage.CaptureMouse();
            e.Handled = true;
        }

        void Stage_Move(object sender, MouseEventArgs e)
        {
            if (!_dragging || _sel == null) return;
            Point p = e.GetPosition(Stage);
            if (!_dragMoved)
            {
                // 少し動かしてから (クリックの手ぶれで指定を外したり動かしたりしない)
                if (Math.Abs(p.X - _dragStart.X) < 4 && Math.Abs(p.Y - _dragStart.Y) < 4) return;
                _dragMoved = true;
                Unchain(_sel);
            }
            double dx = Math.Round(p.X - _dragLast.X), dy = Math.Round(p.Y - _dragLast.Y);
            if (dx == 0 && dy == 0) return;
            Move(_sel, dx, dy);
            _dragLast = new Point(_dragLast.X + dx, _dragLast.Y + dy);
            Render(false);
            RefreshPropValues();
        }

        void Stage_Up(object sender, MouseButtonEventArgs e)
        {
            if (!_dragging) return;
            _dragging = false;
            Stage.ReleaseMouseCapture();
            Commit();
            if (_sel != null && _dragMoved && (_dragLast - _dragStart).Length > 0)
                Status(string.Format(Inv, "{0} を動かしました (x = {1}, y = {2})。", _sel.Label, _lay[_sel.Name].Str("x"), _lay[_sel.Name].Str("y")));
        }

        void OnKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Z && (Keyboard.Modifiers & ModifierKeys.Control) != 0) { Undo(); e.Handled = true; return; }
            if (e.OriginalSource is TextBox || e.OriginalSource is ComboBox || _sel == null || _sel.NoPosition) return;
            double step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 10 : 1, dx = 0, dy = 0;
            if (e.Key == Key.Left) dx = -step; else if (e.Key == Key.Right) dx = step;
            else if (e.Key == Key.Up) dy = -step; else if (e.Key == Key.Down) dy = step;
            else return;
            Begin();
            Unchain(_sel);
            Move(_sel, dx, dy);
            Commit();
            Render(false);
            RefreshPropValues();
            e.Handled = true;
        }

        // ---------------- 元に戻す ----------------

        readonly Stack<List<Tuple<string, string, string>>> _undo = new Stack<List<Tuple<string, string, string>>>();
        List<Tuple<string, string, string>> _group;

        void Begin() { if (_group == null) _group = new List<Tuple<string, string, string>>(); }

        /// <summary>値を変える (元の値を元に戻す用に覚える)</summary>
        void Change(string sec, string key, string value)
        {
            Begin();
            if (!_group.Any(t => t.Item1 == sec && t.Item2 == key))
            {
                OverlaySection s;
                string old = _lay.Sections.TryGetValue(sec, out s) && s.Values.ContainsKey(key) ? s.Values[key] : null;
                _group.Add(Tuple.Create(sec, key, old));
            }
            _lay.Set(sec, key, value);
        }

        void Commit()
        {
            List<Tuple<string, string, string>> g = _group;
            _group = null;
            if (g == null || g.Count == 0) return;
            // 実際に値が変わったものだけ残す
            g = g.Where(t => { OverlaySection s; string now = _lay.Sections.TryGetValue(t.Item1, out s) && s.Values.ContainsKey(t.Item2) ? s.Values[t.Item2] : null; return now != t.Item3; }).ToList();
            if (g.Count == 0) return;
            _undo.Push(g);
            UpdatePath();
            Notify();
        }

        // ---------------- 既定に戻す ----------------

        void ResetOne_Click(object sender, RoutedEventArgs e) { if (_sel != null) Reset(new[] { _sel.Name }, _sel.Label); }

        void ResetAll_Click(object sender, RoutedEventArgs e) { Reset(null, "すべての要素"); }

        /// <summary>節 (null = すべて) を既定のレイアウトの値にする。1 回の操作として元に戻せる</summary>
        void Reset(string[] names, string what)
        {
            OverlayLayout def;
            try { def = OverlayLayout.Load(OverlayLayout.DefaultPath); }
            catch (Exception ex) { Status("既定のレイアウトを読めません: " + ex.Message); return; }
            IEnumerable<string> secs = names != null ? (IEnumerable<string>)names : _lay.Sections.Keys.Concat(def.Sections.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            Begin();
            foreach (string sec in secs)
            {
                OverlaySection cur, d;
                _lay.Sections.TryGetValue(sec, out cur);
                def.Sections.TryGetValue(sec, out d);
                IEnumerable<string> keys = (cur != null ? cur.Values.Keys : Enumerable.Empty<string>())
                    .Concat(d != null ? d.Values.Keys : Enumerable.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                foreach (string k in keys)
                {
                    string v = d != null && d.Values.ContainsKey(k) ? d.Values[k] : null;
                    // メーター画像は別のフォルダへ保存したとき絶対パスにしているので、指すファイルが同じなら残す
                    if (k.Equals("image", StringComparison.OrdinalIgnoreCase) && v != null && cur != null && cur.Values.ContainsKey(k)
                        && string.Equals(_lay.Resolve(cur.Values[k]), def.Resolve(v), StringComparison.OrdinalIgnoreCase)) continue;
                    Change(sec, k, v);
                }
            }
            int n = _group == null ? 0 : _group.Count;
            Commit();
            foreach (Elem el in _elems) if (el.Name != "graph") { _fill = true; el.Check.IsChecked = IsShown(el); _fill = false; }
            Render(true);
            if (_sel != null) FillProps();
            Status(n > 0 ? what + " を既定に戻しました (元に戻すで取り消せます。ファイルは保存するまで変わりません)。" : what + " は既定のままです。");
        }

        void Undo_Click(object sender, RoutedEventArgs e) { Undo(); }

        void Undo()
        {
            if (_dragging || _undo.Count == 0) return;
            List<Tuple<string, string, string>> g = _undo.Pop();
            for (int i = g.Count - 1; i >= 0; i--) _lay.Set(g[i].Item1, g[i].Item2, g[i].Item3);
            foreach (Elem el in _elems) if (el.Name != "graph") { _fill = true; el.Check.IsChecked = IsShown(el); _fill = false; }
            Render(true);
            if (_sel != null) FillProps();
            UpdatePath();
            Notify();
            Status("元に戻しました。");
        }

        void Notify()
        {
            if (_changed == null) return;
            try { _changed(_lay.Clone()); } catch (Exception ex) { Status("呼び出し元の画面に反映できません: " + ex.Message); }
        }

        // ---------------- 描画 ----------------

        void Render(bool newRenderer)
        {
            int w = (int)Stage.Width, h = (int)Stage.Height;
            try
            {
                if (_data != null)
                {
                    // 文字の形・メーター画像は作るときに読むので、項目を変えたときは作り直す。ドラッグ中は使い回す
                    if (newRenderer || _renderer == null || _renderer.W != w) _renderer = new OverlayRenderer(_lay, _data, w, h) { CollectBounds = true };
                    OverlayState st = _data.At(_logT, _fps, _renderer.TrailFrames);
                    OverlayImage.Source = _renderer.RenderBitmap(st);
                    _bounds = new Dictionary<string, Rect>(_renderer.Bounds, StringComparer.OrdinalIgnoreCase);
                }
            }
            catch (Exception ex) { Status("表示を描けません: " + ex.Message); }
            PlaceSub(w, h);
            UpdateSel();
        }

        void PlaceSub(double W, double H)
        {
            SubVideoLayout sv = SubVideoLayout.From(_lay);
            SubPlaceholder.Children.Clear();
            SubLayer.Visibility = Visibility.Collapsed;
            if (!sv.Show) return;
            double[] crop = _sub != null ? _sub.Crop : null;
            if (_sub != null && _sub.Frame != null)
            {
                BitmapSource f = _sub.Frame;
                double sw = f.PixelWidth, sh = f.PixelHeight;
                Rect src = SubVideoLayout.Source(sw, sh, crop), dst = sv.Dest(W, H, sw, sh, crop);
                double k = dst.Width / src.Width;
                SubImage.Source = f;
                SubImage.Width = sw * k; SubImage.Height = sh * k;
                Canvas.SetLeft(SubImage, dst.X - src.X * k); Canvas.SetTop(SubImage, dst.Y - src.Y * k);
                SubLayer.Width = W; SubLayer.Height = H;
                SubLayer.Clip = new RectangleGeometry(dst);
                SubLayer.Opacity = sv.Opacity;
                SubLayer.Visibility = Visibility.Visible;
                if (sv.Border > 0)
                {
                    double bw = sv.Border * H / 1080.0;
                    Rectangle b = new Rectangle { Width = dst.Width + 2 * bw, Height = dst.Height + 2 * bw, Stroke = new SolidColorBrush(sv.BorderColor), StrokeThickness = bw };
                    Canvas.SetLeft(b, dst.X - bw); Canvas.SetTop(b, dst.Y - bw);
                    SubPlaceholder.Children.Add(b);
                }
            }
            else
            {
                // サブ動画のコマが無いときは、枠だけ (16:9 の絵として収めた所)
                Rect box = sv.Box(W, H), dst = sv.Dest(W, H, 1920, 1080, crop);
                Rectangle r = new Rectangle { Width = dst.Width, Height = dst.Height, Fill = new SolidColorBrush(Color.FromArgb(0x60, 0x40, 0x80, 0xC0)), Stroke = new SolidColorBrush(sv.BorderColor), StrokeThickness = Math.Max(1, sv.Border) };
                Canvas.SetLeft(r, dst.X); Canvas.SetTop(r, dst.Y);
                SubPlaceholder.Children.Add(r);
                TextBlock t = new TextBlock { Text = "サブ動画", Foreground = Brushes.White, FontSize = 28 };
                Canvas.SetLeft(t, dst.X + 12); Canvas.SetTop(t, dst.Y + 8);
                SubPlaceholder.Children.Add(t);
            }
        }

        void UpdateSel()
        {
            Rect r = _sel == null ? Rect.Empty : RectOf(_sel.Name);
            if (r.IsEmpty || _sel == null || !IsShown(_sel)) { SelRect.Visibility = Visibility.Collapsed; return; }
            r.Inflate(4, 4);
            Canvas.SetLeft(SelRect, r.X); Canvas.SetTop(SelRect, r.Y);
            SelRect.Width = r.Width; SelRect.Height = r.Height;
            SelRect.Visibility = Visibility.Visible;
        }

        // ---------------- サブ動画のトリミング ----------------

        TextBox[] CropBoxes { get { return new[] { CropL, CropT, CropR, CropB }; } }

        /// <summary>元の動画の画素数 (横, 縦)。わからなければ 0</summary>
        void SrcSize(out int sw, out int sh)
        {
            sw = 0; sh = 0;
            if (_sub == null) return;
            sw = _sub.SourceWidth; sh = _sub.SourceHeight;
            if ((sw <= 0 || sh <= 0) && _sub.Frame != null) { sw = _sub.Frame.PixelWidth; sh = _sub.Frame.PixelHeight; }
        }

        /// <summary>トリミングの欄に今の値を画素で入れる</summary>
        void FillCrop()
        {
            int sw, sh; SrcSize(out sw, out sh);
            bool ok = _sub != null && sw > 0 && sh > 0;
            CropBoxPanel.IsEnabled = ok;
            TextBox[] bs = CropBoxes;
            if (!ok)
            {
                foreach (TextBox b in bs) b.Text = "";
                CropInfo.Text = _sub == null
                    ? "サブ動画が読み込まれていないので、トリミングはできません。走行動画の時刻合わせ画面でサブ動画を登録してから、そこ (または動画で比較) の「表示の編集...」で開いてください。"
                    : "サブ動画の大きさがわからないので、トリミングはできません。";
                return;
            }
            double[] c = _sub.Crop ?? new double[4];
            int[] dim = { sw, sh, sw, sh };
            int[] px = new int[4];
            for (int i = 0; i < 4; i++) { px[i] = (int)Math.Round((i < c.Length ? c[i] : 0) * dim[i]); bs[i].Text = px[i].ToString(Inv); }
            CropInfo.Text = string.Format(Inv, "元の動画 {0} x {1} → 切り落とした後 {2} x {3}", sw, sh, sw - px[0] - px[2], sh - px[1] - px[3]);
        }

        void Crop_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { ApplyCrop(); e.Handled = true; } }
        void Crop_LostFocus(object sender, RoutedEventArgs e) { ApplyCrop(); }

        void Crop_Wheel(object sender, MouseWheelEventArgs e)
        {
            TextBox b = (TextBox)sender;
            double v;
            if (!double.TryParse(b.Text.Trim(), NumberStyles.Float, Inv, out v)) v = 0;
            int step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 10 : 2;
            b.Text = Math.Max(0, Math.Round(v) + (e.Delta > 0 ? step : -step)).ToString(Inv);
            ApplyCrop();
            e.Handled = true;
        }

        void ApplyCrop()
        {
            if (_fill || _sub == null) return;
            int sw, sh; SrcSize(out sw, out sh);
            if (sw <= 0 || sh <= 0) return;
            double[] old = _sub.Crop ?? new double[4], c = new double[4];
            int[] dim = { sw, sh, sw, sh };
            int[] px = new int[4];
            TextBox[] bs = CropBoxes;
            for (int i = 0; i < 4; i++)
            {
                double v;
                if (!double.TryParse(bs[i].Text.Trim(), NumberStyles.Float, Inv, out v)) v = (i < old.Length ? old[i] : 0) * dim[i];
                px[i] = (int)Math.Max(0, Math.Round(v));
            }
            // 残りが元の 10 % より小さくならないように、右・下を減らす
            int minW = Math.Max(2, sw / 10), minH = Math.Max(2, sh / 10);
            px[0] = Math.Min(px[0], sw - minW); px[2] = Math.Min(px[2], sw - minW - px[0]);
            px[1] = Math.Min(px[1], sh - minH); px[3] = Math.Min(px[3], sh - minH - px[1]);
            for (int i = 0; i < 4; i++) c[i] = (double)px[i] / dim[i];
            bool same = Enumerable.Range(0, 4).All(i => Math.Abs(c[i] - (i < old.Length ? old[i] : 0)) < 1e-9);
            if (!same)
            {
                _sub.Crop = c;
                if (_sub.CropChanged != null) _sub.CropChanged(c);
            }
            _fill = true;
            try { FillCrop(); } finally { _fill = false; }
            if (same) return;
            Render(false);
            Status(string.Format(Inv, "サブ動画のトリミングを 左 {0} 上 {1} 右 {2} 下 {3} 画素にしました (log.json に保存)。", px[0], px[1], px[2], px[3]));
        }

        // ---------------- 保存 ----------------

        void Save_Click(object sender, RoutedEventArgs e) { Save(false); }
        void SaveAs_Click(object sender, RoutedEventArgs e) { Save(true); }

        /// <summary>保存できたら true</summary>
        bool Save(bool ask)
        {
            string path = _lay.Path;
            if (ask || IsDefault(path) || string.IsNullOrEmpty(path))
            {
                SaveFileDialog d = new SaveFileDialog { Filter = "レイアウト (*.ini)|*.ini", Title = "レイアウトを保存", FileName = IsDefault(path) ? "overlay_user.ini" : System.IO.Path.GetFileName(path) };
                d.InitialDirectory = IsDefault(path) ? AppContext.BaseDirectory : System.IO.Path.GetDirectoryName(path);
                if (d.ShowDialog(this) != true) return false;
                path = d.FileName;
                if (IsDefault(path))
                {
                    MessageBox.Show(this, "既定のレイアウト (overlay_default.ini) には上書きしません (発行し直すと消えるため)。別の名前にしてください。", "保存", MessageBoxButton.OK, MessageBoxImage.Information);
                    return false;
                }
            }
            try
            {
                // 別のフォルダへ保存するときは、相対パスのメーター画像を絶対パスにする (ini のある所が基準なので)
                string oldDir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(_lay.Path ?? path));
                string newDir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
                if (!string.Equals(oldDir, newDir, StringComparison.OrdinalIgnoreCase))
                {
                    string img = _lay["speedometer"].Str("image", "");
                    if (img.Length > 0 && !System.IO.Path.IsPathRooted(Environment.ExpandEnvironmentVariables(img.Trim('"'))))
                    {
                        string abs = _lay.Resolve(img);
                        if (File.Exists(abs)) _lay.Set("speedometer", "image", abs);
                    }
                }
                _lay.Save(path);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "保存できません", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
            Settings.Set("Overlay.Layout", path);
            try { Settings.Save(); } catch (Exception) { }
            UpdatePath();
            Notify();       // 呼び出し元に保存先 (Path) の変わった写しを渡す (次に開くときにこのファイルを開くため)
            Status("保存しました: " + path + "  (時刻合わせ・動画で比較・書き出しの画面は、このレイアウトを使います)");
            Action<string> h = Saved;
            if (h != null) h(path);
            return true;
        }

        void Close_Click(object sender, RoutedEventArgs e) { Close(); }

        void OnClosing(object sender, CancelEventArgs e)
        {
            if (!_lay.Dirty) return;
            MessageBoxResult r = MessageBox.Show(this, "レイアウトの変更を保存しますか？", "表示のレイアウト", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.Cancel) { e.Cancel = true; return; }
            if (r == MessageBoxResult.Yes) { if (!Save(false)) e.Cancel = true; return; }
            // 保存しない: 呼び出し元を、保存してある中身に戻す
            try { if (_changed != null && File.Exists(_lay.Path)) _changed(OverlayLayout.Load(_lay.Path)); } catch (Exception) { }
        }
    }
}
