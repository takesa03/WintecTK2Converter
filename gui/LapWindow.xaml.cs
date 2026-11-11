using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace Tk2Gui
{
    /// <summary>セッション一覧の 1 行 (ドライバー名は入力できる)。</summary>
    public sealed class SessionRow
    {
        public SessionRecord Rec;
        public string Title { get { return "Session " + Rec.No; } }
        public string Driver { get { return Rec.Driver; } set { Rec.Driver = value ?? ""; } }
        public string Range { get; set; }
        public int LapCount { get { return Rec.FullLaps; } }
        public string Best { get; set; }
        public string BestMax { get; set; }
        public string StartKind { get; set; }
    }

    /// <summary>周回表の 1 行。</summary>
    public sealed class LapRow : INotifyPropertyChanged
    {
        public string Session { get; set; }
        public string Lap { get; set; }
        public string Start { get; set; }
        public string[] Sectors { get; set; }
        public string LapTime { get; set; }
        public string MaxKmh { get; set; }
        string _note;
        // 備考が変わったときは、その行の表示だけを更新する (表全体を作り直さない)
        public string Note
        {
            get { return _note; }
            set { _note = value; PropertyChangedEventHandler h = PropertyChanged; if (h != null) h(this, new PropertyChangedEventArgs("Note")); }
        }
        public event PropertyChangedEventHandler PropertyChanged;
        public bool IsHeader, IsOut, IsInvalid, IsBest, IsIn, IsSessionBest;
        public int SessionNo;                  // 区切り行のとき、そのセッションの番号 (クリックで畳む/開く)
        public SessionRecord Sess;             // この行が属するセッション (右クリックの分割・結合に使う)
        public LapRecord Rec;                  // 周回行のとき、その周回
    }

    /// <summary>周回表のセルの太さ。★・※の付いた値 (ラップ・区間・最高速) を太字にする。それ以外は行の太さのまま (区切り行・全体ベストの周回は行ごと太字)。</summary>
    public sealed class MarkWeightConverter : System.Windows.Data.IValueConverter
    {
        /// <summary>そのセルの値に ★ か ※ が付いているか (key = LapTime / MaxKmh / S0, S1, ...)</summary>
        public static bool IsMarked(LapRow r, string key)
        {
            if (r == null) return false;
            string text = null;
            if (key == "LapTime") text = r.LapTime;
            else if (key == "MaxKmh") text = r.MaxKmh;
            else if (key != null && key.StartsWith("S") && r.Sectors != null)
            {
                int k;
                if (int.TryParse(key.Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out k) && k >= 0 && k < r.Sectors.Length) text = r.Sectors[k];
            }
            return text != null && (text.Contains("★") || text.Contains("※"));
        }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            LapRow r = value as LapRow;
            if (r == null) return FontWeights.Normal;
            return IsMarked(r, parameter as string) || r.IsHeader || (r.IsBest && !r.IsInvalid) ? FontWeights.Bold : FontWeights.Normal;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) { throw new NotSupportedException(); }
    }

    /// <summary>周回表のセルの文字色。★・※の付いた値を赤にする。それ以外は行の文字色 (無効な周回は赤茶、それ以外は黒)。</summary>
    public sealed class MarkBrushConverter : System.Windows.Data.IValueConverter
    {
        static readonly Brush Red = Frozen(new SolidColorBrush(Color.FromRgb(0xE0, 0x00, 0x00)));
        static Brush Frozen(SolidColorBrush b) { b.Freeze(); return b; }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            LapRow r = value as LapRow;
            if (r == null) return Brushes.Black;
            if (MarkWeightConverter.IsMarked(r, parameter as string)) return Red;
            return r.IsInvalid ? Brushes.Firebrick : Brushes.Black;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) { throw new NotSupportedException(); }
    }

    public partial class LapWindow : Window
    {
        CourseDefinition _def;
        LapAnalysis _res;
        bool _busy;
        bool _readOnly;                                            // CSV を表示しているとき (ログがないので、分割・結合はできない)
        Task _saveChain = Task.CompletedTask;                     // ドライバー名の書き込みを順番に行うための鎖
        readonly HashSet<int> _collapsed = new HashSet<int>();   // 畳んでいるセッションの番号 (全セッション表示のとき)
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        const double JstOffsetHours = 9;

        public LapWindow(CourseDefinition def, string courseLabel, IEnumerable<string> logs)
        {
            InitializeComponent();
            Title += "  " + App.VersionText;
            if (def != null) SetCourse(def, courseLabel);
            if (logs != null)
                foreach (string f in logs) AddFile(f);
            UpdateButtons();
        }

        // ================= 入力 =================

        void SetCourse(CourseDefinition def, string label)
        {
            _def = def;
            CourseText.Text = string.Format(Inv, "{0}\n周長 {1:F0} m / ライン {2} 本{3}", def.Name, def.LengthM, def.Gates.Count,
                string.IsNullOrEmpty(label) ? "" : "\n" + label);
            UpdateButtons();
        }

        void UpdateButtons()
        {
            AnalyzeButton.IsEnabled = !_busy && _def != null && FileList.Items.Count > 0;
            CsvButton.IsEnabled = !_busy && _res != null && _res.Sessions.Count > 0;
        }

        void OpenCourse_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog dlg = new OpenFileDialog();
            dlg.Title = "コース定義を開く";
            dlg.Filter = "コース定義 (*.course.json)|*.course.json|すべてのファイル (*.*)|*.*";
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                SetCourse(CourseDefinition.Load(dlg.FileName), dlg.FileName);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "開けませんでした", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // 保存したラップ CSV を読み込んで表示する (読み取り専用: ログがないので、分割・結合や再解析はできない)
        void OpenCsv_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            OpenFileDialog dlg = new OpenFileDialog();
            dlg.Title = "ラップ CSV を開く";
            dlg.Filter = "ラップ CSV (*.csv)|*.csv|すべてのファイル (*.*)|*.*";
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                LapAnalysis res = LapCsv.Read(dlg.FileName);
                _res = res;
                _readOnly = true;
                Log("CSV を表示: " + dlg.FileName);
                ShowResult();
                InfoText.Text += "   [CSV 表示中・読み取り専用: " + System.IO.Path.GetFileName(dlg.FileName) + "]";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "CSV を開けませんでした", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        void AddFile(string path)
        {
            string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".json" && path.EndsWith(".course.json", StringComparison.OrdinalIgnoreCase))
            {
                try { SetCourse(CourseDefinition.Load(path), path); } catch (Exception ex) { Log("コース定義を開けません: " + ex.Message); }
                return;
            }
            if (ext != ".tk2" && ext != ".nmea" && ext != ".txt" && ext != ".log") return;
            if (!FileList.Items.Cast<string>().Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)))
                FileList.Items.Add(path);
            UpdateButtons();
        }

        void Window_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        void Window_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            foreach (string p in (string[])e.Data.GetData(DataFormats.FileDrop))
            {
                if (Directory.Exists(p)) foreach (string f in Directory.GetFiles(p)) AddFile(f);
                else AddFile(p);
            }
        }

        void AddFile_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog dlg = new OpenFileDialog();
            dlg.Title = "走行ログを選択";
            dlg.Filter = "走行ログ (*.tk2;*.nmea)|*.tk2;*.nmea|すべてのファイル (*.*)|*.*";
            dlg.Multiselect = true;
            if (dlg.ShowDialog(this) == true)
                foreach (string f in dlg.FileNames) AddFile(f);
        }

        void RemoveFile_Click(object sender, RoutedEventArgs e)
        {
            foreach (object o in FileList.SelectedItems.Cast<object>().ToList()) FileList.Items.Remove(o);
            UpdateButtons();
        }

        // ================= 解析 =================

        async void Analyze_Click(object sender, RoutedEventArgs e)
        {
            await RunAnalysisAsync(null);
        }

        async Task RunAnalysisAsync(double? focusT)
        {
            if (_busy || _def == null) return;
            LapParams prm = new LapParams();
            try
            {
                prm.PitStopSec = ParseD(PitStopBox.Text, "停止とみなす秒数", 5, 3600);
                prm.MinCrossSpeedKmh = ParseD(MinSpeedBox.Text, "通過の最低速度", 0, 100);
                prm.SlowLapRatio = ParseD(SlowLapBox.Text, "遅い周回の倍率", 1.0, 10);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "設定を確認してください", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            List<string> paths = FileList.Items.Cast<string>().ToList();
            CourseDefinition def = _def;
            int restored = 0;
            _busy = true; UpdateButtons();
            InfoText.Foreground = Brushes.DarkGreen;
            InfoText.Text = "解析中...";
            Log("--- 解析開始: " + string.Join(", ", paths.Select(System.IO.Path.GetFileName)));
            System.Diagnostics.Stopwatch swAll = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                LapAnalysis res = await Task.Run(() =>
                {
                    List<List<GpsSample>> files = paths.Select(GpsLoader.Load).ToList();
                    foreach (string p in paths)       // 手動のセッション編集を、保存ファイルから読む (まだ読んでいないログだけ)
                    {
                        string key = SessionEdits.Key(p);
                        if (!_edits.ContainsKey(key)) _edits[key] = SessionEditStore.Load(p);
                    }
                    prm.Edits = _edits.ToDictionary(kv => kv.Key, kv => kv.Value);
                    LapAnalysis r = LapAnalyzer.Analyze(def, files, paths, prm);
                    restored = DriverStore.Apply(r);        // 保存してあるドライバー名を復元 (ファイルを読むので裏で行う)
                    return r;
                });
                long msCalc = swAll.ElapsedMilliseconds;
                _res = res;
                _readOnly = false;
                foreach (string l in res.Log) Log(l);
                if (restored > 0) Log(string.Format("ドライバー名を復元: {0} セッション", restored));
                ShowResult();
                Log(string.Format("所要時間: 解析 {0} ms / 表示 {1} ms", msCalc, swAll.ElapsedMilliseconds - msCalc));
                if (focusT.HasValue) ScrollToTime(focusT.Value);
            }
            catch (Exception ex)
            {
                _res = null;
                Log("失敗: " + ex.Message);
                InfoText.Foreground = Brushes.Firebrick;
                InfoText.Text = "失敗: " + ex.Message;
            }
            finally { _busy = false; UpdateButtons(); }
        }

        static double ParseD(string s, string name, double min, double max)
        {
            double v;
            if (!double.TryParse(s.Trim(), NumberStyles.Float, Inv, out v) || v < min || v > max)
                throw new ArgumentException(string.Format("{0} は {1}～{2} の数値にしてください", name, min, max));
            return v;
        }



        // ================= 周回の比較再生 =================

        readonly Dictionary<string, List<GpsSample>> _gps = new Dictionary<string, List<GpsSample>>(StringComparer.OrdinalIgnoreCase);   // 読み込んだログ (再生のたびに読み直さない)

        // 周回表で選んだ周回 (複数可) を、コースの上で同時に走らせて比較する
        async void Replay_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            if (_readOnly)
            {
                MessageBox.Show(this, "CSV を表示しているときは、ログがないので再生できません。ログとコース定義から「解析する」を押してください。", "再生できません", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (_res == null || _def == null) return;
            List<LapRow> rows = LapGrid.SelectedItems.Cast<LapRow>().Where(r => r.Rec != null && r.Sess != null && !string.IsNullOrEmpty(r.Sess.FilePath)).ToList();
            List<LapRow> ok = rows.Where(r => r.Rec.Type == LapType.Normal && r.Rec.Valid && r.Rec.LapTime.HasValue).ToList();
            if (ok.Count == 0)
            {
                MessageBox.Show(this, "再生する周回を、周回表で選んでください (Ctrl や Shift を押しながらクリックで複数選択)。\nOUT・未完・無効 (×) の周回は、コントロールラインから始まらないので再生できません。", "周回を選んでください", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            int max = ReplayWindow.Palette.Length;
            if (ok.Count > max) { Log(string.Format("再生は最大 {0} 周までです。選んだ順の先頭 {0} 周を再生します", max)); ok = ok.Take(max).ToList(); }
            if (rows.Count > ok.Count && rows.Count <= max) Log(string.Format("OUT・未完・無効の周回 {0} 周は再生しません", rows.Count - ok.Count));

            CourseDefinition def = _def;
            _busy = true; UpdateButtons();
            InfoText.Text = "再生の準備中...";
            try
            {
                List<ReplayLap> laps = await Task.Run(() =>
                {
                    List<ReplayLap> list = new List<ReplayLap>();
                    int ci = 0;
                    foreach (LapRow r in ok)
                    {
                        string path = r.Sess.FilePath;
                        List<GpsSample> g;
                        lock (_gps)
                        {
                            if (!_gps.TryGetValue(path, out g)) { g = GpsLoader.Load(path); _gps[path] = g; }
                        }
                        string drv = r.Sess.Driver ?? "";
                        string name = "Session " + r.Sess.No + " / Lap " + r.Rec.LapNo + (drv.Length > 0 ? "  (" + drv + ")" : "");
                        string label = "S" + r.Sess.No + "-L" + r.Rec.LapNo + (drv.Length > 0 ? " " + drv : "");
                        list.Add(ReplayLap.Build(name, label, r.Rec.StartT, r.Rec.LapTime.Value, g, def.Lat0, def.Lon0, ReplayWindow.Palette[ci++]));
                    }
                    return list;
                });
                ReplayWindow w = new ReplayWindow(def, laps);
                w.Owner = this;
                w.Show();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "再生できませんでした", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _busy = false; UpdateButtons();
                if (_res != null) ShowInfoOnly();
            }
        }

        // 解析結果の要約行を、元の表示に戻す
        void ShowInfoOnly()
        {
            string best = (_res.BestLap.HasValue ? "全体のベスト " + LapAnalyzer.FormatTime(_res.BestLap) : "ベストラップなし")
                + (_res.BestMaxKmh.HasValue ? " / 最高速 " + _res.BestMaxKmh.Value.ToString("0.0", Inv) + " km/h" : "");
            InfoText.Foreground = Brushes.DarkGreen;
            InfoText.Text = string.Format(Inv, "{0} セッション / {1} 周 / {2}", _res.Sessions.Count, _res.Sessions.Sum(s => s.FullLaps), best);
            if (_readOnly) InfoText.Text += "   [CSV 表示中・読み取り専用]";
        }

        // ================= セッションの編集 (分割・結合) =================

        // 手動のセッション編集 (ログごと)。キー = SessionEdits.Key(パス)。解析のときに保存ファイルから読む
        readonly Dictionary<string, SessionEdits> _edits = new Dictionary<string, SessionEdits>();

        // 右クリックした行を選択する (メニューはその行が対象)
        void LapGrid_PreviewRightDown(object sender, MouseButtonEventArgs e)
        {
            DependencyObject d = e.OriginalSource as DependencyObject;
            while (d != null && !(d is DataGridRow)) d = VisualTreeHelper.GetParent(d);
            DataGridRow row = d as DataGridRow;
            if (row != null && !row.IsSelected) LapGrid.SelectedItem = row.Item;   // すでに選んだ行の上なら、複数選択をそのまま残す
        }

        // 結合できるか: 同じログの直前のセッションがあるときだけ
        SessionRecord PreviousSessionInSameLog(SessionRecord s)
        {
            if (_res == null || s == null || string.IsNullOrEmpty(s.FilePath)) return null;
            int i = _res.Sessions.IndexOf(s);
            if (i <= 0) return null;
            SessionRecord p = _res.Sessions[i - 1];
            return string.Equals(p.FilePath, s.FilePath, StringComparison.OrdinalIgnoreCase) ? p : null;
        }

        void LapMenu_Opened(object sender, RoutedEventArgs e)
        {
            LapRow r = LapGrid.SelectedItem as LapRow;
            SessionRecord s = r == null ? null : r.Sess;
            bool canSplit = r != null && !r.IsHeader && r.Rec != null && s != null && s.Laps.IndexOf(r.Rec) > 0 && !string.IsNullOrEmpty(s.FilePath);
            SessionRecord prev = PreviousSessionInSameLog(s);
            if (_readOnly) { canSplit = false; prev = null; }
            SplitItem.IsEnabled = canSplit;
            SplitItem.Header = canSplit ? "この周回 (" + r.Lap + ") の前でセッションを分割" : "この周回の前でセッションを分割 (周回の行を右クリック)";
            MergeItem.IsEnabled = prev != null;
            MergeItem.Header = prev != null ? "Session " + s.No + " を Session " + prev.No + " と結合" : "前のセッションと結合 (対象の行を右クリック)";
            ResetEditsItem.IsEnabled = !_readOnly && s != null && _edits.ContainsKey(SessionEdits.Key(s.FilePath ?? "")) && !_edits[SessionEdits.Key(s.FilePath)].IsEmpty;
        }

        void Split_Click(object sender, RoutedEventArgs e)
        {
            LapRow r = LapGrid.SelectedItem as LapRow;
            if (r == null || r.Rec == null || r.Sess == null || string.IsNullOrEmpty(r.Sess.FilePath)) return;
            SessionEdits ed = EditsOf(r.Sess.FilePath);
            double t = Math.Round(r.Rec.StartT, 3);
            ed.Merges.RemoveAll(m => SessionEdits.Near(m, t));      // 結合してあったところを分割したら、結合の指定は外す
            if (!ed.HasSplitAt(t)) ed.Splits.Add(t);
            ApplyEdit(r.Sess.FilePath, ed, t);
        }

        void Merge_Click(object sender, RoutedEventArgs e)
        {
            LapRow r = LapGrid.SelectedItem as LapRow;
            SessionRecord s = r == null ? null : r.Sess;
            SessionRecord prev = PreviousSessionInSameLog(s);
            if (prev == null) return;
            SessionEdits ed = EditsOf(s.FilePath);
            double t = Math.Round(s.StartT, 3);
            // 手動で分割したところなら、その分割を取り消す。自動 (停止の判定) で分かれたところなら、結合を指定する
            if (ed.Splits.RemoveAll(x => SessionEdits.Near(x, t)) == 0 && !ed.HasMergeAt(t)) ed.Merges.Add(t);
            ApplyEdit(s.FilePath, ed, prev.StartT);
        }

        void ResetEdits_Click(object sender, RoutedEventArgs e)
        {
            LapRow r = LapGrid.SelectedItem as LapRow;
            SessionRecord s = r == null ? null : r.Sess;
            if (s == null || string.IsNullOrEmpty(s.FilePath)) return;
            if (MessageBox.Show(this, System.IO.Path.GetFileName(s.FilePath) + " のセッションの分割・結合の編集を、すべて取り消しますか?", "編集を取り消す",
                    MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            SessionEdits ed = EditsOf(s.FilePath);
            ed.Splits.Clear(); ed.Merges.Clear();
            ApplyEdit(s.FilePath, ed, s.StartT);
        }

        SessionEdits EditsOf(string path)
        {
            string key = SessionEdits.Key(path);
            SessionEdits ed;
            if (!_edits.TryGetValue(key, out ed)) { ed = new SessionEdits(); _edits[key] = ed; }
            return ed;
        }

        // 編集を保存して (裏で順番に)、再解析する。再解析のあと、focusT のあたりの行を見える位置にする
        void ApplyEdit(string path, SessionEdits ed, double focusT)
        {
            if (_busy) return;
            SessionEdits copy = new SessionEdits { Splits = new List<double>(ed.Splits), Merges = new List<double>(ed.Merges) };
            _saveChain = _saveChain.ContinueWith(_ => SessionEditStore.Save(path, copy));
            Log(string.Format("セッションを編集: {0} (分割 {1} 件 / 結合 {2} 件)", System.IO.Path.GetFileName(path), ed.Splits.Count, ed.Merges.Count));
            var _ignored = RunAnalysisAsync(focusT);
        }

        void ScrollToTime(double t)
        {
            List<LapRow> list = LapGrid.ItemsSource as List<LapRow>;
            if (list == null) return;
            LapRow target = list.FirstOrDefault(x => x.Rec != null && Math.Abs(x.Rec.StartT - t) < 0.5)
                            ?? list.FirstOrDefault(x => x.IsHeader && x.Sess != null && Math.Abs(x.Sess.StartT - t) < 0.5);
            if (target != null) { LapGrid.SelectedItem = target; LapGrid.ScrollIntoView(target); }
        }

        // ================= 結果表示 =================

        static string Clock(double t)
        {
            return new DateTime(2000, 1, 1).AddSeconds(t + JstOffsetHours * 3600).ToString("HH:mm:ss", Inv);
        }

        // 周回表の列を作る。★・※の付いた値は太字にする (key = 太さの判定に使う項目)
        static DataGridTextColumn MarkCol(string header, string path, string key, double width)
        {
            Style st = new Style(typeof(TextBlock));
            st.Setters.Add(new Setter(TextBlock.FontWeightProperty,
                new System.Windows.Data.Binding(".") { Converter = new MarkWeightConverter(), ConverterParameter = key }));
            st.Setters.Add(new Setter(TextBlock.ForegroundProperty,
                new System.Windows.Data.Binding(".") { Converter = new MarkBrushConverter(), ConverterParameter = key }));
            // 選択中の行は青い背景なので、赤は読みにくい: 白にする
            DataTrigger sel = new DataTrigger
            {
                Binding = new System.Windows.Data.Binding("IsSelected") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(DataGridCell), 1) },
                Value = true
            };
            sel.Setters.Add(new Setter(TextBlock.ForegroundProperty, Brushes.White));
            st.Triggers.Add(sel);
            return new DataGridTextColumn { Header = header, Binding = new System.Windows.Data.Binding(path), Width = width, ElementStyle = st };
        }

        void ShowResult()
        {
            // 周回表の区間の列を作り直す
            LapGrid.Columns.Clear();
            LapGrid.Columns.Add(new DataGridTextColumn { Header = "Session", Binding = new System.Windows.Data.Binding("Session"), Width = 56 });
            LapGrid.Columns.Add(new DataGridTextColumn { Header = "Lap", Binding = new System.Windows.Data.Binding("Lap"), Width = 118 });
            LapGrid.Columns.Add(new DataGridTextColumn { Header = "開始", Binding = new System.Windows.Data.Binding("Start"), Width = 70 });
            for (int k = 0; k < _res.SectorNames.Length; k++)
                LapGrid.Columns.Add(MarkCol(_res.SectorNames[k], "Sectors[" + k.ToString(Inv) + "]", "S" + k.ToString(Inv), 72));
            LapGrid.Columns.Add(MarkCol("ラップ", "LapTime", "LapTime", 90));
            LapGrid.Columns.Add(MarkCol("最高 km/h", "MaxKmh", "MaxKmh", 80));
            LapGrid.Columns.Add(new DataGridTextColumn { Header = "備考", Binding = new System.Windows.Data.Binding("Note"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });

            List<SessionRow> rows = new List<SessionRow>();
            foreach (SessionRecord s in _res.Sessions)
            {
                LapRecord first = s.Laps.FirstOrDefault();
                rows.Add(new SessionRow
                {
                    Rec = s,
                    Range = Clock(s.StartT) + " ～ " + Clock(s.EndT),
                    Best = s.BestLap.HasValue ? LapAnalyzer.FormatTime(s.BestLap) : "-",
                    BestMax = s.BestMaxKmh.HasValue ? s.BestMaxKmh.Value.ToString("0.0", Inv) + " km/h" : "-",
                    StartKind = (first == null ? "" : first.Type == LapType.Out ? (first.StartGate == 0 ? "OUT (停止から発進)" : "OUT (Sector" + first.StartGate + " から計測)") : "走行中から")
                        + (s.EditNote.Length > 0 ? "  [" + s.EditNote + "]" : "")
                });
            }
            _collapsed.Clear();
            SessionGrid.ItemsSource = rows;
            SessionExp.Header = string.Format("セッション一覧 ({0} 件)", rows.Count);
            if (rows.Count > 0) SessionGrid.SelectedIndex = 0;

            string best = (_res.BestLap.HasValue ? "全体のベスト " + LapAnalyzer.FormatTime(_res.BestLap) : "ベストラップなし")
                + (_res.BestMaxKmh.HasValue ? " / 最高速 " + _res.BestMaxKmh.Value.ToString("0.0", Inv) + " km/h" : "");
            InfoText.Foreground = Brushes.DarkGreen;
            InfoText.Text = string.Format(Inv, "{0} セッション / {1} 周 / {2}", _res.Sessions.Count, _res.Sessions.Sum(s => s.FullLaps), best);
            RefreshLaps();
            UpdateButtons();
        }

        // ドライバー名を編集したら (確定した後で) 保存する。区切り行の表示にも反映する
        void SessionGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            if (e.EditAction != DataGridEditAction.Commit) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_res == null) return;
                // 保存内容は画面側で作り (速い)、ファイルへの書き込みは裏で順番に行う (保存先が遅くても画面が固まらないように)
                List<DriverStore.Snapshot> snaps = DriverStore.MakeSnapshots(_res);
                _saveChain = _saveChain.ContinueWith(_ =>
                {
                    System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
                    List<string> saved = DriverStore.Write(snaps);
                    long ms = sw.ElapsedMilliseconds;
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (saved.Count > 0) Log("ドライバー名を保存: " + string.Join(", ", saved.Select(System.IO.Path.GetFileName))
                            + (ms >= 500 ? string.Format("  (書き込みに {0:0.0} 秒かかりました: {1})", ms / 1000.0, System.IO.Path.GetDirectoryName(saved[0])) : ""));
                    }));
                });
                UpdateHeaderNotes();    // 区切り行のドライバー名だけを書き換える (周回表は作り直さない)
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        // 周回表を畳んだら、余った高さはセッション一覧が使えるように行の高さを Auto にする (開いているときは残り全部)
        void LapExp_Changed(object sender, RoutedEventArgs e)
        {
            if (LapsRow == null) return;
            LapsRow.Height = LapExp.IsExpanded ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        }

        void SessionGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // 全セッション表示のときの周回表は、選んだセッションに関係なく同じ内容なので作り直さない
            // (行を選ぶたびに表全体を作り直すと、画面の部品を監視するソフトがあると特に重くなる)
            if (AllCheck.IsChecked == true) return;
            RefreshLaps();
        }

        void AllCheck_Changed(object sender, RoutedEventArgs e)
        {
            RefreshLaps();
        }

        LapRow ToRow(LapRecord l, SessionRecord sess)
        {
            LapRow r = new LapRow
            {
                Sess = sess, Rec = l,
                Session = l.Session.ToString(Inv),
                Lap = l.Label,
                Start = Clock(l.StartT),
                LapTime = LapAnalyzer.FormatTime(l.LapTime ?? l.PartialTime),
                MaxKmh = l.MaxKmh > 0 ? l.MaxKmh.ToString("0.0", Inv) : "",
                Note = l.Note,
                IsOut = l.Type == LapType.Out, IsInvalid = !l.Valid, IsBest = l.IsBestLap, IsIn = l.IsIn, IsSessionBest = l.IsSessionBestLap,
                Sectors = new string[_res.SectorNames.Length]
            };
            if (l.IsBestMax) r.MaxKmh += " ★";
            else if (l.IsSessionBestMax) r.MaxKmh += " ※";
            if (l.IsBestLap) r.LapTime += " ★";
            else if (l.IsSessionBestLap) r.LapTime += " ※";
            for (int k = 0; k < r.Sectors.Length; k++)
                r.Sectors[k] = l.Sector[k].HasValue ? l.Sector[k].Value.ToString("0.00", Inv) + (l.IsBestSector[k] ? " ★" : l.IsSessionBestSector[k] ? " ※" : "") : "-";
            return r;
        }

        void RefreshLaps()
        {
            if (_res == null) { LapGrid.ItemsSource = null; return; }
            List<LapRow> list = new List<LapRow>();
            if (AllCheck.IsChecked == true)
            {
                foreach (SessionRecord s in _res.Sessions)
                {
                    list.Add(Header(s));
                    if (!_collapsed.Contains(s.No)) list.AddRange(s.Laps.Select(l => ToRow(l, s)));
                }
            }
            else
            {
                SessionRow sel = SessionGrid.SelectedItem as SessionRow;
                if (sel != null) list.AddRange(sel.Rec.Laps.Select(l => ToRow(l, sel.Rec)));
            }
            LapGrid.ItemsSource = list;
        }

        LapRow Header(SessionRecord s)
        {
            return new LapRow
            {
                IsHeader = true,
                Session = "",
                SessionNo = s.No,
                Sess = s,
                Lap = (_collapsed.Contains(s.No) ? "▶ " : "▼ ") + "Session " + s.No,
                Start = "",
                Sectors = new string[_res.SectorNames.Length],
                LapTime = "",
                MaxKmh = "",
                Note = HeaderNote(s)
            };
        }

        string HeaderNote(SessionRecord s)
        {
            return string.Format("{0}{1} ～ {2}  {5} 周  ベスト {3}  最高速 {4}{6}", s.Driver.Length > 0 ? "ドライバー: " + s.Driver + "   " : "", Clock(s.StartT), Clock(s.EndT),
                s.BestLap.HasValue ? LapAnalyzer.FormatTime(s.BestLap) : "-", s.BestMaxKmh.HasValue ? s.BestMaxKmh.Value.ToString("0.0", Inv) + " km/h" : "-", s.FullLaps,
                s.EditNote.Length > 0 ? "  [" + s.EditNote + "]" : "");
        }

        // 区切り行の備考 (ドライバー名を含む) だけを、現在の名前で書き換える
        void UpdateHeaderNotes()
        {
            List<LapRow> list = LapGrid.ItemsSource as List<LapRow>;
            if (list == null || _res == null) return;
            foreach (LapRow r in list)
            {
                if (!r.IsHeader) continue;
                SessionRecord s = _res.Sessions.FirstOrDefault(x => x.No == r.SessionNo);
                if (s != null) r.Note = HeaderNote(s);
            }
        }

        void LapGrid_LoadingRow(object sender, DataGridRowEventArgs e)
        {
            LapRow r = e.Row.Item as LapRow;
            if (r == null) return;
            e.Row.FontWeight = FontWeights.Normal;
            e.Row.Cursor = r.IsHeader ? Cursors.Hand : null;
            e.Row.ToolTip = string.IsNullOrEmpty(r.Note) ? null : r.Note;     // 備考が列の幅に収まらないときは、マウスを乗せると全文が出る
            e.Row.Foreground = Brushes.Black;
            if (r.IsHeader) { e.Row.Background = new SolidColorBrush(Color.FromRgb(0xC9, 0xD6, 0xEA)); e.Row.FontWeight = FontWeights.Bold; }
            else if (r.IsInvalid) { e.Row.Background = new SolidColorBrush(Color.FromRgb(0xFB, 0xE3, 0xE3)); e.Row.Foreground = Brushes.Firebrick; }
            else if (r.IsOut) e.Row.Background = new SolidColorBrush(Color.FromRgb(0xEE, 0xEE, 0xEE));
            else if (r.IsBest) { e.Row.Background = new SolidColorBrush(Color.FromRgb(0xD9, 0xF2, 0xDC)); e.Row.FontWeight = FontWeights.Bold; }
            else if (r.IsIn) e.Row.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xF3, 0xD6));
            else e.Row.Background = Brushes.White;
        }

        // 左の設定パネルを隠す/出す (結果の表を広く使うため)
        void TogglePanel_Click(object sender, RoutedEventArgs e)
        {
            bool hide = LeftPanel.Visibility == Visibility.Visible;
            LeftPanel.Visibility = hide ? Visibility.Collapsed : Visibility.Visible;
            PanelButton.Content = hide ? "▶ 設定を表示" : "◀ 設定を隠す";
        }

        // ---- 全セッション表示のときの、セッションごとの畳み/展開 ----

        // 区切り行をクリックしたら、そのセッションの周回行を畳む/開く
        void LapGrid_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            DependencyObject d = e.OriginalSource as DependencyObject;
            while (d != null && !(d is DataGridRow)) d = VisualTreeHelper.GetParent(d);
            DataGridRow row = d as DataGridRow;
            LapRow r = row == null ? null : row.Item as LapRow;
            if (r == null || !r.IsHeader) return;
            if (!_collapsed.Remove(r.SessionNo)) _collapsed.Add(r.SessionNo);
            RefreshLaps();
            // 畳む/開くで一覧が組み直されるので、クリックした区切り行が見える位置に戻す
            LapRow again = (LapGrid.ItemsSource as List<LapRow>).FirstOrDefault(x => x.IsHeader && x.SessionNo == r.SessionNo);
            if (again != null) LapGrid.ScrollIntoView(again);
        }

        void CollapseAll_Click(object sender, RoutedEventArgs e)
        {
            if (_res == null) return;
            foreach (SessionRecord s in _res.Sessions) _collapsed.Add(s.No);
            if (AllCheck.IsChecked != true) AllCheck.IsChecked = true; else RefreshLaps();
        }

        void ExpandAll_Click(object sender, RoutedEventArgs e)
        {
            _collapsed.Clear();
            RefreshLaps();
        }

        // ================= 保存 =================

        void SaveCsv_Click(object sender, RoutedEventArgs e)
        {
            if (_res == null) return;
            SaveFileDialog dlg = new SaveFileDialog();
            dlg.Title = "ラップタイムを CSV に保存";
            dlg.Filter = "CSV (*.csv)|*.csv";
            string first = FileList.Items.Count > 0 ? (string)FileList.Items[0] : null;
            dlg.FileName = (first != null ? System.IO.Path.GetFileNameWithoutExtension(first) : "laps") + "_laps.csv";
            if (first != null) dlg.InitialDirectory = System.IO.Path.GetDirectoryName(first);
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                LapAnalyzer.WriteCsv(_res, dlg.FileName, JstOffsetHours);
                Log("保存: " + dlg.FileName);
                InfoText.Foreground = Brushes.DarkGreen;
                InfoText.Text = "保存しました: " + System.IO.Path.GetFileName(dlg.FileName);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "保存できませんでした", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        string _lastLog = "";

        void Log(string line)
        {
            LogBox.AppendText(line + Environment.NewLine);
            LogBox.ScrollToEnd();
            if (!string.IsNullOrWhiteSpace(line)) _lastLog = line;
            UpdateLogHeader();
        }

        // ログを畳んでいる間は、見出しに最新の 1 行を出す
        void UpdateLogHeader()
        {
            if (LogHeader == null) return;
            LogHeader.Text = LogExp.IsExpanded || _lastLog.Length == 0 ? "ログ" : "ログ:  " + _lastLog;
        }

        void LogExp_Changed(object sender, RoutedEventArgs e)
        {
            UpdateLogHeader();
        }
    }
}
