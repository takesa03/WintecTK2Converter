using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Tk2Nmea;

namespace Tk2Gui
{
    public partial class MainWindow : Window
    {
        readonly ObservableCollection<FileItem> _items = new ObservableCollection<FileItem>();
        bool _busy;
        string _lastOutDir;

        public MainWindow()
        {
            InitializeComponent();
            Title += "  " + App.VersionText;
            FileGrid.ItemsSource = _items;
            _items.CollectionChanged += (s, e) => UpdateState();
            ApplyIniToUi();
            UpdateState();

            // exe へのドラッグ＆ドロップや「送る」、コマンドラインで渡されたファイル・フォルダを最初から並べる
            // 先頭が -course なら、残りのファイルでコース定義の作成画面を開く
            string[] raw = Environment.GetCommandLineArgs().Skip(1).ToArray();
            if (raw.Length > 0 && string.Equals(raw[0], "-course", StringComparison.OrdinalIgnoreCase))
            {
                string[] courseFiles = raw.Skip(1).ToArray();
                Loaded += (s, e) => { CourseWindow w = new CourseWindow(courseFiles); w.Owner = this; w.Show(); };
            }
            else if (raw.Length > 0 && string.Equals(raw[0], "-lap", StringComparison.OrdinalIgnoreCase))
            {
                // -lap コース定義.course.json ログ... : ラップ解析の画面を開く
                string[] lapFiles = raw.Skip(1).ToArray();
                Loaded += (s, e) => { LapWindow w = new LapWindow(null, null, lapFiles); w.Owner = this; w.Show(); };
            }
            else
            {
                string[] args = raw.Where(a => !a.StartsWith("-")).ToArray();
                if (args.Length > 0) Loaded += (s, e) => AddPaths(args);
            }
        }

        // ================= 初期値 (tk2nmea.ini) =================

        // ini の内容を画面の初期値にする。読めなければ既定値のまま
        void ApplyIniToUi()
        {
            Options o = new Options();
            try
            {
                Program.LoadIni(o);
            }
            catch (Exception ex)
            {
                o = new Options();
                Log("tk2nmea.ini を読めませんでした (既定値で起動します): " + ex.Message);
            }
            KmlCombo.SelectedIndex = o.NoKml ? 2 : (o.PlainKml ? 1 : 0);
            LocalDateCheck.IsChecked = o.LocalDate;
            AltCombo.SelectedIndex = o.AltGps ? 0 : 1;
            TourCheck.IsChecked = o.Tour;
            if (o.Weeks >= 0) WeeksBox.Text = o.Weeks.ToString(CultureInfo.InvariantCulture);
            if (!string.IsNullOrEmpty(o.OutDir))
            {
                OutCustomRadio.IsChecked = true;
                OutDirBox.Text = o.OutDir;
            }
        }

        // 画面の設定を反映した Options を作る (ini → 画面の値の順で適用)
        Options BuildOptions()
        {
            Options o = new Options();
            Program.LoadIni(o);
            o.NoPause = true; // 時刻の問い合わせはしない (カットは一覧の入力欄で指定)

            o.NoKml = KmlCombo.SelectedIndex == 2;
            o.PlainKml = KmlCombo.SelectedIndex == 1;
            o.LocalDate = LocalDateCheck.IsChecked == true;
            o.AltGps = AltCombo.SelectedIndex == 0;
            o.Tour = TourCheck.IsChecked == true;

            string w = WeeksBox.Text.Trim();
            if (w.Length == 0) o.Weeks = -1;
            else
            {
                int weeks;
                if (!int.TryParse(w, NumberStyles.Integer, CultureInfo.InvariantCulture, out weeks) || weeks < 0)
                    throw new ArgumentException("補正する週数は 0 以上の整数か空欄にしてください");
                o.Weeks = weeks;
            }

            if (OutCustomRadio.IsChecked == true)
            {
                string d = OutDirBox.Text.Trim();
                if (d.Length == 0) throw new ArgumentException("出力先フォルダを指定してください");
                o.OutDir = d;
            }
            else
            {
                o.OutDir = null;
            }
            return o;
        }

        // ================= ファイルの追加・削除 =================

        void Window_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = (!_busy && e.Data.GetDataPresent(DataFormats.FileDrop)) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        void Window_Drop(object sender, DragEventArgs e)
        {
            if (_busy || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            string[] paths = (string[])e.Data.GetData(DataFormats.FileDrop);
            AddPaths(paths);
        }

        void AddFiles_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog dlg = new OpenFileDialog();
            dlg.Title = "Wintec TK2 ファイルを選択";
            dlg.Filter = "Wintec TK2 (*.tk2)|*.tk2|すべてのファイル (*.*)|*.*";
            dlg.Multiselect = true;
            if (dlg.ShowDialog(this) == true) AddPaths(dlg.FileNames);
        }

        void AddFolder_Click(object sender, RoutedEventArgs e)
        {
            OpenFolderDialog dlg = new OpenFolderDialog();
            dlg.Title = ".tk2 ファイルのあるフォルダを選択";
            if (dlg.ShowDialog(this) == true) AddPaths(new[] { dlg.FolderName });
        }

        void Remove_Click(object sender, RoutedEventArgs e)
        {
            foreach (FileItem it in FileGrid.SelectedItems.Cast<FileItem>().ToList()) _items.Remove(it);
        }

        void Clear_Click(object sender, RoutedEventArgs e)
        {
            _items.Clear();
        }

        // ラップ・セクタータイムの解析画面を開く (一覧にある .tk2 を走行ログの初期値にする)
        void Lap_Click(object sender, RoutedEventArgs e)
        {
            LapWindow w = new LapWindow(null, null, _items.Where(i => i.Track != null).Select(i => i.Path));
            w.Owner = this;
            w.Show();
        }

        // 一覧にある .tk2 を走行ログの初期値にして、コース定義の作成画面を開く
        void Course_Click(object sender, RoutedEventArgs e)
        {
            CourseWindow w = new CourseWindow(_items.Where(i => i.Track != null).Select(i => i.Path));
            w.Owner = this;
            w.Show();
        }

        // ファイルまたはフォルダ (直下の *.tk2) を一覧に追加し、バックグラウンドで読み込む
        async void AddPaths(IEnumerable<string> paths)
        {
            List<string> files = Program.Collect(paths.ToList());
            List<FileItem> added = new List<FileItem>();
            foreach (string f in files)
            {
                string full = Path.GetFullPath(f);
                if (_items.Any(i => string.Equals(i.Path, full, StringComparison.OrdinalIgnoreCase))) continue;
                FileItem item = new FileItem(full);
                _items.Add(item);
                added.Add(item);
            }
            if (added.Count == 0) return;
            StatusText.Text = "読み込み中...";

            Options o;
            try { o = BuildOptions(); }
            catch (Exception) { o = new Options(); } // 週数の入力が不正でも、読み込みは自動補正で進める

            foreach (FileItem item in added)
            {
                FileItem it = item;
                try
                {
                    await Task.Run(() => it.Load(o));
                }
                catch (Exception ex)
                {
                    it.Range = "(読み込めません)";
                    it.Status = "NG";
                    it.Summary = ex.Message;
                    Log("NG  " + it.Name + " : " + ex.Message);
                }
            }
            ShowSummary();
            UpdateState();
        }

        void FileGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ShowSummary();
        }

        void ShowSummary()
        {
            FileItem it = FileGrid.SelectedItem as FileItem;
            if (it == null && _items.Count == 1) it = _items[0];
            SummaryBox.Text = it == null ? "" : (it.Summary ?? "");
        }

        // ================= 変換 =================

        async void Convert_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            Options opt;
            try
            {
                opt = BuildOptions();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "設定を確認してください", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            List<FileItem> targets = _items.Where(i => i.Track != null).ToList();
            if (targets.Count == 0) return;

            _busy = true;
            UpdateState();
            // 書き込み中に一覧の入力が変わらないよう、カット時刻は先に取り出す
            List<KeyValuePair<FileItem, string[]>> jobs = targets
                .Select(t => new KeyValuePair<FileItem, string[]>(t, new[] { t.Start.Trim(), t.End.Trim() })).ToList();

            int ok = 0, ng = 0;
            string lastDir = null;
            foreach (KeyValuePair<FileItem, string[]> job in jobs)
            {
                FileItem it = job.Key;
                string start = job.Value[0], end = job.Value[1];
                it.Status = "変換中...";
                StatusText.Text = string.Format("変換中 ({0}/{1}) {2}", ok + ng + 1, jobs.Count, it.Name);
                try
                {
                    Options o = opt;
                    o.Start = start.Length == 0 ? null : start;
                    o.End = end.Length == 0 ? null : end;
                    string path = it.Path;
                    string info = null;
                    List<string> outs = await Task.Run(() => Program.ConvertFile(path, o, out info));
                    it.Status = "完了";
                    Log("OK  " + it.Name);
                    foreach (string p in outs) Log(" -> " + p);
                    Log("    " + info);
                    if (outs.Count > 0) lastDir = Path.GetDirectoryName(outs[0]);
                    ok++;
                }
                catch (Exception ex)
                {
                    it.Status = "失敗";
                    Log("NG  " + it.Name + " : " + ex.Message);
                    ng++;
                }
            }

            _busy = false;
            _lastOutDir = lastDir;
            Log(string.Format("完了: 成功 {0} 件, 失敗 {1} 件", ok, ng));
            Log("");
            UpdateState();
            StatusText.Text = string.Format("完了: 成功 {0} 件, 失敗 {1} 件", ok, ng);
            StatusText.Foreground = ng > 0 ? System.Windows.Media.Brushes.Firebrick : System.Windows.Media.Brushes.DarkGreen;
        }

        void OpenOut_Click(object sender, RoutedEventArgs e)
        {
            if (_lastOutDir != null && Directory.Exists(_lastOutDir))
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + _lastOutDir + "\"") { UseShellExecute = true });
        }

        void BrowseOut_Click(object sender, RoutedEventArgs e)
        {
            OpenFolderDialog dlg = new OpenFolderDialog();
            dlg.Title = "出力先フォルダを選択";
            if (Directory.Exists(OutDirBox.Text)) dlg.InitialDirectory = OutDirBox.Text;
            if (dlg.ShowDialog(this) == true) OutDirBox.Text = dlg.FolderName;
        }

        // ================= 画面状態 =================

        void UpdateState()
        {
            EmptyHint.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            bool ready = !_busy && _items.Any(i => i.Track != null);
            ConvertButton.IsEnabled = ready;
            AddFilesButton.IsEnabled = !_busy;
            AddFolderButton.IsEnabled = !_busy;
            RemoveButton.IsEnabled = !_busy;
            ClearButton.IsEnabled = !_busy;
            FileGrid.IsReadOnly = _busy;
            OpenOutButton.IsEnabled = !_busy && _lastOutDir != null;
            if (!_busy)
            {
                if (_items.Count == 0) StatusText.Text = "ファイルを追加してください";
                else if (ready && !StatusText.Text.StartsWith("完了")) StatusText.Text = "「変換する」を押してください";
                StatusText.Foreground = System.Windows.Media.Brushes.DarkGreen;
            }
        }

        void Log(string line)
        {
            LogBox.AppendText(line + Environment.NewLine);
            LogBox.ScrollToEnd();
        }
    }
}
