using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace Tk2Gui
{
    // =====================================================================
    //  データをまとめる画面 (処理は LibraryPacker)
    // =====================================================================
    public partial class PackWindow : Window
    {
        readonly CourseDefinition _def;
        readonly string _coursePath;
        readonly LapAnalysis _res;
        readonly List<string> _logs;
        PackPlan _plan;
        CancellationTokenSource _cancel;
        bool _running;
        List<string> _result;     // まとめた先 (コース定義・ログ)

        public PackWindow(CourseDefinition def, string coursePath, LapAnalysis res, List<string> logs)
        {
            InitializeComponent();
            Theme.ApplyDark(this);
            Title += "  " + App.VersionText;
            _def = def; _coursePath = coursePath; _res = res; _logs = logs;
            // 既定のコースフォルダ: 前回の最上位フォルダ\コース名。前回が無ければコース定義のフォルダ\コース名
            string root = Settings.Get("Pack.LibraryRoot");
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) root = Path.GetDirectoryName(Path.GetFullPath(coursePath));
            // 今のコース定義が既にコースフォルダにあれば (まとめた先で開き直した等)、そのフォルダ
            string here = Path.GetDirectoryName(Path.GetFullPath(coursePath));
            DirBox.Text = Settings.Get("Pack.LastCourseId") == def.Id && !string.IsNullOrEmpty(Settings.Get("Pack.LastCourseDir"))
                ? Settings.Get("Pack.LastCourseDir") : Path.Combine(root, LibraryPacker.SafeName(def.Name));
            if (string.Equals(here, DirBox.Text, StringComparison.OrdinalIgnoreCase)) DirBox.Text = here;
            Settings.Track(this, "PackWindow", () => { });
            Loaded += (s, e) => Replan();
            Closing += (s, e) => { if (_running) { e.Cancel = true; StatusText.Text = "まとめている間は閉じられません。先に「中止」を押してください。"; } };
        }

        PackVideoMode Mode { get { return MoveRadio.IsChecked == true ? PackVideoMode.Move : RefRadio.IsChecked == true ? PackVideoMode.Reference : PackVideoMode.Copy; } }

        void Replan()
        {
            if (_running || !IsLoaded) return;
            _plan = null;
            RunButton.IsEnabled = false;
            string dir = DirBox.Text.Trim();
            if (dir.Length == 0 || !Path.IsPathRooted(dir)) { PlanBox.Text = "コースフォルダを選んでください。"; return; }
            try
            {
                _plan = LibraryPacker.Plan(dir, _def, _coursePath, _res, _logs, Mode);
                List<string> lines = LibraryPacker.Describe(_plan);
                bool full = _plan.FreeBytes >= 0 && _plan.CopyBytes > _plan.FreeBytes;
                if (full) lines.Add("空き容量が足りないので、まとめられません。");
                PlanBox.Text = string.Join(Environment.NewLine, lines);
                RunButton.IsEnabled = !full;
                StatusText.Text = "内容を確かめて「まとめる」を押してください。";
            }
            catch (Exception ex) { PlanBox.Text = "まとめられません: " + ex.Message; }
        }

        void Browse_Click(object sender, RoutedEventArgs e)
        {
            Microsoft.Win32.OpenFolderDialog dlg = new Microsoft.Win32.OpenFolderDialog();
            dlg.Title = "コースフォルダを選ぶ (無ければ作ってから選んでください。ライブラリの最上位を選ぶと、その中にコース名のフォルダを作ります)";
            string cur = DirBox.Text.Trim();
            string start = Directory.Exists(cur) ? cur : Path.GetDirectoryName(cur);
            if (!string.IsNullOrEmpty(start) && Directory.Exists(start)) dlg.InitialDirectory = start;
            if (dlg.ShowDialog(this) != true) return;
            string d = dlg.FolderName;
            // 選んだフォルダにコース定義が無く、中にも無ければ、ライブラリの最上位とみなしてコース名のフォルダを足す
            bool isCourse = Directory.GetFiles(d, "*.course.json").Length > 0;
            if (!isCourse && !string.Equals(Path.GetFileName(d), LibraryPacker.SafeName(_def.Name), StringComparison.OrdinalIgnoreCase)
                && MessageBox.Show(this, "このフォルダの中に「" + LibraryPacker.SafeName(_def.Name) + "」フォルダを作って、そこへまとめますか？\n(「いいえ」= 選んだフォルダをそのままコースフォルダにする)",
                                   "コースフォルダ", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                d = Path.Combine(d, LibraryPacker.SafeName(_def.Name));
            DirBox.Text = d;
            Replan();
        }

        void Dir_Changed(object sender, RoutedEventArgs e) { Replan(); }
        void Dir_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) Replan(); }
        void Mode_Changed(object sender, RoutedEventArgs e) { Replan(); }

        async void Run_Click(object sender, RoutedEventArgs e)
        {
            Replan();
            PackPlan plan = _plan;
            if (plan == null) return;
            if (plan.Mode == PackVideoMode.Move && plan.Logs.Any(l => l.Items.Any(i => i.IsVideo))
                && MessageBox.Show(this, "動画を移動します (元の場所から無くなります)。よろしいですか？", "動画の移動", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            _running = true; _cancel = new CancellationTokenSource();
            CancellationToken ct = _cancel.Token;
            RunButton.IsEnabled = false; CancelButton.IsEnabled = true; CloseButton.IsEnabled = false; OpenButton.IsEnabled = false;
            DirBox.IsEnabled = CopyRadio.IsEnabled = MoveRadio.IsEnabled = RefRadio.IsEnabled = false;
            Action<double, string> prog = (p, s) => Dispatcher.BeginInvoke(new Action(() => { Progress.Value = p; StatusText.Text = s; }));
            try
            {
                List<string> report = await Task.Run(() => LibraryPacker.Run(plan, prog, ct));
                PlanBox.Text = string.Join(Environment.NewLine, report.Concat(new[] { "", "まとめました: " + plan.CourseDir, "元のファイルは残してあります (不要なら後で手で消してください)。" }));
                StatusText.Text = report.Any(r => r.StartsWith("要確認", StringComparison.Ordinal)) ? "まとめましたが、数が合わない所があります (上の「要確認」)。" : "まとめました。";
                Settings.Set("Pack.LibraryRoot", Path.GetDirectoryName(plan.CourseDir));
                Settings.Set("Pack.LastCourseDir", plan.CourseDir);
                Settings.Set("Pack.LastCourseId", _def.Id);
                _result = new List<string> { plan.CourseDst };
                _result.AddRange(plan.Logs.Select(l => l.Dst));
                OpenButton.IsEnabled = true;
            }
            catch (OperationCanceledException) { StatusText.Text = "中止しました。途中まで作ったファイルは消しました。"; Progress.Value = 0; }
            catch (Exception ex)
            {
                StatusText.Text = "まとめられませんでした。途中まで作ったファイルは消しました: " + ex.Message; Progress.Value = 0;
                MessageBox.Show(this, ex.Message, "まとめられませんでした", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _running = false;
                CancelButton.IsEnabled = false; CloseButton.IsEnabled = true;
                DirBox.IsEnabled = CopyRadio.IsEnabled = MoveRadio.IsEnabled = RefRadio.IsEnabled = true;
                RunButton.IsEnabled = _result == null;
            }
        }

        void Cancel_Click(object sender, RoutedEventArgs e) { if (_cancel != null) _cancel.Cancel(); }

        void Open_Click(object sender, RoutedEventArgs e)
        {
            if (_result == null) return;
            LapWindow w = new LapWindow(null, null, _result);
            w.Owner = Owner;
            w.Show();
            Close();
        }

        void Close_Click(object sender, RoutedEventArgs e) { Close(); }
    }
}
