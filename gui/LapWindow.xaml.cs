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
    }

    public partial class LapWindow : Window
    {
        CourseDefinition _def;
        LapAnalysis _res;
        bool _busy;
        Task _saveChain = Task.CompletedTask;                     // ドライバー名の書き込みを順番に行うための鎖
        readonly HashSet<int> _collapsed = new HashSet<int>();   // 畳んでいるセッションの番号 (全セッション表示のとき)
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        const double JstOffsetHours = 9;

        public LapWindow(CourseDefinition def, string courseLabel, IEnumerable<string> logs)
        {
            InitializeComponent();
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
            try
            {
                LapAnalysis res = await Task.Run(() =>
                {
                    List<List<GpsSample>> files = paths.Select(GpsLoader.Load).ToList();
                    LapAnalysis r = LapAnalyzer.Analyze(def, files, paths, prm);
                    restored = DriverStore.Apply(r);        // 保存してあるドライバー名を復元 (ファイルを読むので裏で行う)
                    return r;
                });
                _res = res;
                foreach (string l in res.Log) Log(l);
                if (restored > 0) Log(string.Format("ドライバー名を復元: {0} セッション", restored));
                ShowResult();
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

        // ================= 結果表示 =================

        static string Clock(double t)
        {
            return new DateTime(2000, 1, 1).AddSeconds(t + JstOffsetHours * 3600).ToString("HH:mm:ss", Inv);
        }

        void ShowResult()
        {
            // 周回表の区間の列を作り直す
            LapGrid.Columns.Clear();
            LapGrid.Columns.Add(new DataGridTextColumn { Header = "Session", Binding = new System.Windows.Data.Binding("Session"), Width = 56 });
            LapGrid.Columns.Add(new DataGridTextColumn { Header = "Lap", Binding = new System.Windows.Data.Binding("Lap"), Width = 118 });
            LapGrid.Columns.Add(new DataGridTextColumn { Header = "開始", Binding = new System.Windows.Data.Binding("Start"), Width = 70 });
            for (int k = 0; k < _res.SectorNames.Length; k++)
                LapGrid.Columns.Add(new DataGridTextColumn
                {
                    Header = _res.SectorNames[k],
                    Binding = new System.Windows.Data.Binding("Sectors[" + k.ToString(Inv) + "]"),
                    Width = 72
                });
            LapGrid.Columns.Add(new DataGridTextColumn { Header = "ラップ", Binding = new System.Windows.Data.Binding("LapTime"), Width = 90 });
            LapGrid.Columns.Add(new DataGridTextColumn { Header = "最高 km/h", Binding = new System.Windows.Data.Binding("MaxKmh"), Width = 80 });
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
                    StartKind = first == null ? "" : first.Type == LapType.Out ? (first.StartGate == 0 ? "OUT (停止から発進)" : "OUT (Sector" + first.StartGate + " から計測)") : "走行中から"
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

        LapRow ToRow(LapRecord l)
        {
            LapRow r = new LapRow
            {
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
                    if (!_collapsed.Contains(s.No)) list.AddRange(s.Laps.Select(ToRow));
                }
            }
            else
            {
                SessionRow sel = SessionGrid.SelectedItem as SessionRow;
                if (sel != null) list.AddRange(sel.Rec.Laps.Select(ToRow));
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
            return string.Format("{0}{1} ～ {2}  {5} 周  ベスト {3}  最高速 {4}", s.Driver.Length > 0 ? "ドライバー: " + s.Driver + "   " : "", Clock(s.StartT), Clock(s.EndT),
                s.BestLap.HasValue ? LapAnalyzer.FormatTime(s.BestLap) : "-", s.BestMaxKmh.HasValue ? s.BestMaxKmh.Value.ToString("0.0", Inv) + " km/h" : "-", s.FullLaps);
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
