using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Tk2Nmea;

namespace Tk2Gui
{
    // =====================================================================
    //  ライブラリから比較 (docs/data_library_design.md 7.2)
    //  -compare フォルダ で起動すると、この画面だけが開く (段階 5 で別の exe にする)。
    //  コース定義 (条件) を選ぶと、その条件の周回を全部のログから並べる。2 本 (A / B) を選んで、動画か地図で比べる
    // =====================================================================
    public partial class LibraryWindow : Window
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public sealed class Row
        {
            public LibLap L;
            public string Mark { get { return L.IsBest ? "★" : L.IsSessionBest ? "※" : ""; } }
            public string Date { get { return L.Local.ToString("yyyy-MM-dd HH:mm", Inv); } }
            public string Folder { get { return L.LogFolder; } }
            public int Session { get { return L.SessionNo; } }
            public string LapNo { get { return (L.Lap.Type == "Run" ? "H" : "") + L.Lap.No + (L.Lap.In ? " IN" : ""); } }
            public string Driver { get { return L.Driver; } }
            public string Time { get { return LapAnalyzer.FormatTime(L.Time); } }
            public string Sectors { get { return L.Lap.Sectors == null ? "" : string.Join("  ", L.Lap.Sectors.Select(s => s.HasValue ? s.Value.ToString("0.000", Inv) : "-")); } }
            public string Max { get { return L.Lap.MaxKmh > 0 ? L.Lap.MaxKmh.ToString("0.0", Inv) : ""; } }
            public string Videos { get { return L.Videos > 0 ? "🎥" + (L.Videos > 1 ? L.Videos.ToString(Inv) : "") : ""; } }
            public string Heart { get { return L.Heart ? "♥" : ""; } }
            public string Note { get { return (L.Lap.MissCourse ? "ミスコースの疑い  " : "") + (L.Lap.Memo ?? "") + (string.IsNullOrEmpty(L.Lap.Note) ? "" : "  " + L.Lap.Note); } }
        }

        List<LibCourse> _courses = new List<LibCourse>();
        List<LibLap> _laps = new List<LibLap>();
        readonly LibLap[] _slot = new LibLap[2];
        readonly Dictionary<string, List<GpsSample>> _gps = new Dictionary<string, List<GpsSample>>(StringComparer.OrdinalIgnoreCase);
        bool _busy;
        int _loadSeq;

        public LibraryWindow(string root)
        {
            InitializeComponent();
            Theme.ApplyDark(this);
            Title += "  " + App.VersionText;
            Settings.Binder st = new Settings.Binder("Library");
            st.Text(DriverBox, "Driver");
            st.Text(DateBox, "Date");
            st.Check(VideoOnlyCheck, "VideoOnly");
            st.Combo(SortCombo, "Sort");
            Settings.Track(this, "LibraryWindow", () =>
            {
                st.SaveAll();
                Settings.Set("Library.Root", DirBox.Text.Trim());
                LibCourse c = CourseCombo.SelectedItem is ComboBoxItem ? (LibCourse)((ComboBoxItem)CourseCombo.SelectedItem).Tag : null;
                if (c != null) Settings.Set("Library.Course", c.Def.Id);
            });
            if (string.IsNullOrEmpty(root)) root = Settings.Get("Library.Root") ?? Settings.Get("Pack.LibraryRoot");
            DirBox.Text = root ?? "";
            Loaded += async (s, e) => await LoadRoot();
        }

        void Log(string s) { LogBox.AppendText((LogBox.Text.Length > 0 ? Environment.NewLine : "") + s); LogBox.ScrollToEnd(); }

        // ---------------- 読み込み ----------------

        async Task LoadRoot()
        {
            string root = DirBox.Text.Trim();
            CourseCombo.Items.Clear(); _laps.Clear(); LapGrid.ItemsSource = null;
            if (root.Length == 0 || !Directory.Exists(root)) { Log("ライブラリのフォルダを開いてください (「データをまとめる...」でまとめたフォルダ、またはその上のフォルダ)。"); return; }
            List<string> notes = new List<string>();
            _courses = await Task.Run(() => Library.Scan(root, notes));
            foreach (string n in notes) Log(n);
            if (_courses.Count == 0) { Log("コース定義 (*.course.json) が見つかりません: " + root); return; }
            string want = Settings.Get("Library.Course");
            int sel = 0;
            for (int i = 0; i < _courses.Count; i++)
            {
                CourseCombo.Items.Add(new ComboBoxItem { Content = _courses[i].Label + "  (ログ " + _courses[i].Logs.Count + ")", Tag = _courses[i] });
                if (_courses[i].Def.Id == want) sel = i;
            }
            CourseCombo.SelectedIndex = sel;     // → Course_Changed で周回を読む
        }

        LibCourse Course { get { ComboBoxItem it = CourseCombo.SelectedItem as ComboBoxItem; return it == null ? null : (LibCourse)it.Tag; } }

        async void Course_Changed(object sender, SelectionChangedEventArgs e)
        {
            LibCourse c = Course;
            if (c == null) return;
            int seq = ++_loadSeq;
            _busy = true; UpdateButtons();
            List<string> notes = new List<string>();
            Log("読み込み中: " + c.Label + " (" + c.Dir + ")");
            try
            {
                List<LibLap> laps = await Task.Run(() => Library.Laps(c, notes,
                    (k, n, name) => Dispatcher.BeginInvoke(new Action(() => Progress.Value = n == 0 ? 1 : k / (double)n))));
                if (seq != _loadSeq) return;
                foreach (string n in notes) Log(n);
                _laps = laps;
                // 条件が変わったら、選んであった周回は外す (別の条件の周回どうしは比べない)
                for (int i = 0; i < 2; i++) if (_slot[i] != null && _slot[i].Course.Def.Id != c.Def.Id) _slot[i] = null;
                ShowSlots();
                Refresh();
                Log(string.Format(Inv, "{0} 周 ({1} ログ)", laps.Count, c.Logs.Count));
            }
            catch (Exception ex) { Log("読めません: " + ex.Message); }
            finally { if (seq == _loadSeq) { _busy = false; UpdateButtons(); } }
        }

        void Refresh()
        {
            string drv = DriverBox.Text.Trim(), date = DateBox.Text.Trim();
            IEnumerable<LibLap> q = _laps;
            if (drv.Length > 0) q = q.Where(l => l.Driver.IndexOf(drv, StringComparison.OrdinalIgnoreCase) >= 0);
            if (date.Length > 0) q = q.Where(l => l.Local.ToString("yyyy-MM-dd", Inv).Contains(date) || l.Local.ToString("MM/dd", Inv).Contains(date));
            if (VideoOnlyCheck.IsChecked == true) q = q.Where(l => l.Videos > 0);
            q = SortCombo.SelectedIndex == 1 ? q.OrderBy(l => l.Lap.StartT) : q.OrderBy(l => l.Time ?? double.MaxValue).ThenBy(l => l.Lap.StartT);
            LapGrid.ItemsSource = q.Select(l => new Row { L = l }).ToList();
        }

        void Filter_Changed(object sender, RoutedEventArgs e) { if (IsLoaded) Refresh(); }

        void LapGrid_LoadingRow(object sender, DataGridRowEventArgs e)
        {
            Row r = e.Row.Item as Row;
            if (r == null) return;
            e.Row.Foreground = Theme.Text;
            e.Row.FontWeight = FontWeights.Normal;
            e.Row.Background = null;
            if (r.L.IsBest) { e.Row.Background = Theme.RowBest; e.Row.FontWeight = FontWeights.Bold; }
            for (int i = 0; i < 2; i++) if (_slot[i] != null && _slot[i].Key == r.L.Key) e.Row.FontWeight = FontWeights.Bold;
        }

        async void Browse_Click(object sender, RoutedEventArgs e)
        {
            Microsoft.Win32.OpenFolderDialog dlg = new Microsoft.Win32.OpenFolderDialog { Title = "ライブラリ (またはコースフォルダ) を開く" };
            if (Directory.Exists(DirBox.Text.Trim())) dlg.InitialDirectory = DirBox.Text.Trim();
            if (dlg.ShowDialog(this) != true) return;
            DirBox.Text = dlg.FolderName;
            await LoadRoot();
        }

        async void Reload_Click(object sender, RoutedEventArgs e) { await LoadRoot(); }
        async void Dir_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) await LoadRoot(); }

        // ---------------- A / B ----------------

        void LapGrid_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            Row r = LapGrid.SelectedItem as Row;
            if (r == null) return;
            int i = _slot[0] == null ? 0 : _slot[1] == null ? 1 : 1;
            Put(i, r.L);
        }

        void Put_Click(object sender, RoutedEventArgs e)
        {
            Row r = LapGrid.SelectedItem as Row;
            if (r == null) { Log("一覧で周回を選んでください。"); return; }
            Put(int.Parse((string)((Button)sender).Tag, Inv), r.L);
        }

        void Put(int i, LibLap l)
        {
            if (_slot[1 - i] != null && _slot[1 - i].Key == l.Key) { Log("同じ周回は A と B の両方に入れられません。"); return; }
            _slot[i] = l;
            ShowSlots();
            LapGrid.Items.Refresh();
        }

        void Clear_Click(object sender, RoutedEventArgs e)
        {
            _slot[int.Parse((string)((Button)sender).Tag, Inv)] = null;
            ShowSlots();
            LapGrid.Items.Refresh();
        }

        void Swap_Click(object sender, RoutedEventArgs e)
        {
            LibLap t = _slot[0]; _slot[0] = _slot[1]; _slot[1] = t;
            ShowSlots();
        }

        void ShowSlots()
        {
            TextBlock[] txt = { AText, BText };
            ComboBox[] vid = { AVideo, BVideo };
            for (int i = 0; i < 2; i++)
            {
                LibLap l = _slot[i];
                vid[i].Items.Clear();
                if (l == null) { txt[i].Text = "(空き)"; continue; }
                txt[i].Text = l.Short + "\n" + l.LogFolder + (l.Heart ? "  ♥" : "");
                foreach (VideoStore.Entry v in Library.VideosFor(l))
                    vid[i].Items.Add(new ComboBoxItem { Content = Path.GetFileName(v.Path) + (string.IsNullOrEmpty(v.ClipOf) ? "" : "  (切り出し)"), Tag = v, ToolTip = v.Path });
                if (vid[i].Items.Count == 0) vid[i].Items.Add(new ComboBoxItem { Content = "(動画なし)", Tag = null });
                vid[i].SelectedIndex = 0;
            }
            UpdateButtons();
        }

        VideoStore.Entry VideoOf(int i)
        {
            ComboBoxItem it = (i == 0 ? AVideo : BVideo).SelectedItem as ComboBoxItem;
            return it == null ? null : it.Tag as VideoStore.Entry;
        }

        void UpdateButtons()
        {
            bool two = _slot[0] != null && _slot[1] != null && !_busy;
            MapButton.IsEnabled = two;
            VideoButton.IsEnabled = two;
        }

        // ---------------- 比べる ----------------

        List<GpsSample> Gps(string log)
        {
            lock (_gps)
            {
                List<GpsSample> g;
                if (!_gps.TryGetValue(log, out g)) { g = GpsLoader.Load(log); _gps[log] = g; }
                return g;
            }
        }

        /// <summary>A・B を、ログを解析し直して本物の周回にする (失敗したら null)</summary>
        async Task<List<VideoCompareWindow.Lane>> MakeLanes(bool needVideo)
        {
            LibLap[] s = { _slot[0], _slot[1] };
            VideoStore.Entry[] v = { VideoOf(0), VideoOf(1) };
            if (needVideo && (v[0] == null || v[1] == null))
            {
                MessageBox.Show(this, "動画の無い周回があります (" + string.Join(", ", Enumerable.Range(0, 2).Where(i => v[i] == null).Select(i => i == 0 ? "A" : "B")) + ")。\n「地図で比較」なら動画なしで比べられます。",
                                "動画がありません", MessageBoxButton.OK, MessageBoxImage.Information);
                return null;
            }
            List<string> notes = new List<string>();
            List<VideoCompareWindow.Lane> lanes = await Task.Run(() =>
            {
                List<VideoCompareWindow.Lane> o = new List<VideoCompareWindow.Lane>();
                for (int i = 0; i < 2; i++)
                {
                    List<GpsSample> g = Gps(s[i].Log);
                    SessionRecord sess; LapRecord lap; string[] names;
                    if (!Library.Resolve(s[i], g, out sess, out lap, out names, notes))
                    {
                        notes.Add((i == 0 ? "A" : "B") + " の周回が、解析し直すと見つかりません (ログか条件が変わった)。「読み直す」を押してください。");
                        return null;
                    }
                    o.Add(new VideoCompareWindow.Lane { Sess = sess, Lap = lap, Gps = g, Video = v[i], Color = ReplayWindow.Palette[i] });
                }
                return o;
            });
            foreach (string n in notes) Log(n);
            return lanes;
        }

        async void VideoCompare_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            _busy = true; UpdateButtons();
            try
            {
                List<VideoCompareWindow.Lane> lanes = await MakeLanes(true);
                if (lanes == null) return;
                LibCourse c = _slot[0].Course;
                VideoCompareWindow w = new VideoCompareWindow(c.Def, c.Def.Gates.Count > 0 ? SectorNames(lanes) : null, lanes);
                w.Origin = this;
                w.Show();
            }
            catch (Exception ex) { Log("比べられません: " + ex.Message); MessageBox.Show(this, ex.Message, "比べられません", MessageBoxButton.OK, MessageBoxImage.Error); }
            finally { _busy = false; UpdateButtons(); }
        }

        string[] SectorNames(List<VideoCompareWindow.Lane> lanes)
        {
            int n = lanes[0].Lap.Sector == null ? 0 : lanes[0].Lap.Sector.Length;
            return Enumerable.Range(1, n).Select(i => "S" + i).ToArray();
        }

        async void MapCompare_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            _busy = true; UpdateButtons();
            try
            {
                List<VideoCompareWindow.Lane> lanes = await MakeLanes(false);
                if (lanes == null) return;
                CourseDefinition def = _slot[0].Course.Def;
                List<ReplayLap> laps = new List<ReplayLap>();
                for (int i = 0; i < 2; i++)
                {
                    VideoCompareWindow.Lane ln = lanes[i];
                    string drv = LapAnalyzer.DriverOf(ln.Sess, ln.Lap);
                    string day = new DateTime(2000, 1, 1).AddSeconds(ln.Lap.StartT).AddHours(9).ToString("MM/dd", Inv);
                    string k = ln.Lap.IsGymRun ? "H" + ln.Lap.LapNo : "L" + ln.Lap.LapNo;
                    double lt = ln.Lap.LapTime ?? ln.Lap.PartialTime ?? (ln.Lap.EndT - ln.Lap.StartT);
                    laps.Add(ReplayLap.Build(day + " Session " + ln.Sess.No + " / " + k + (drv.Length > 0 ? "  (" + drv + ")" : ""),
                                             day + " S" + ln.Sess.No + "-" + k + (drv.Length > 0 ? " " + drv : ""),
                                             ln.Lap.StartT, lt, ln.Gps, def.Lat0, def.Lon0, ReplayWindow.Palette[i]));
                }
                ReplayWindow w = new ReplayWindow(def, laps);
                w.Show();
            }
            catch (Exception ex) { Log("比べられません: " + ex.Message); }
            finally { _busy = false; UpdateButtons(); }
        }
    }
}
