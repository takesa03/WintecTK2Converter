using System;
using System.Windows;

namespace Tk2Gui
{
    /// <summary>
    /// 起動したときの最初の画面。「変換ツール」(メイン画面) と「解析ツール」(コース定義・ラップ解析・ライブラリから比較) の 2 択。
    /// 選んだ画面を開いている間はこの画面を隠し、その画面を閉じたらここへ戻る
    /// </summary>
    public partial class LauncherWindow : Window
    {
        public LauncherWindow()
        {
            InitializeComponent();
            Theme.ApplyDark(this);
            Title += "  " + App.VersionText;
            VersionLabel.Text = App.VersionText;
            Settings.Binder st = new Settings.Binder("Launcher");
            st.Custom("Page", () => AnalysisPanel.Visibility == Visibility.Visible ? "analysis" : "top",
                      v => ShowAnalysis(v == "analysis"));
            Settings.Track(this, "LauncherWindow", st.SaveAll);
            SizeToContent = SizeToContent.WidthAndHeight;   // 位置だけ戻し、大きさは中身に合わせる (ページで高さが変わる)
        }

        void ShowAnalysis(bool on)
        {
            TopPanel.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
            AnalysisPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>開いた画面の数。全部閉じたらこの画面を出し直す</summary>
        int _open;

        /// <summary>ツールの画面を開き、この画面は隠す (閉じずに残すのでアプリは終わらない)。閉じたら戻ってくる</summary>
        void Open(Func<Window> make)
        {
            Window w;
            try { w = make(); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "開けません", MessageBoxButton.OK, MessageBoxImage.Error); return; }
            _open++;
            w.Closed += (s, e) =>
            {
                if (--_open > 0) return;
                Show();
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                Activate();
            };
            w.Show();
            Hide();
        }

        void ConvertTool_Click(object sender, RoutedEventArgs e) { Open(() => new MainWindow()); }

        void AnalysisTool_Click(object sender, RoutedEventArgs e) { ShowAnalysis(true); }
        void Back_Click(object sender, RoutedEventArgs e) { ShowAnalysis(false); }

        void Course_Click(object sender, RoutedEventArgs e) { Open(() => new CourseWindow(new string[0])); }
        void Lap_Click(object sender, RoutedEventArgs e) { Open(() => new LapWindow(null, null, new string[0])); }
        void Library_Click(object sender, RoutedEventArgs e) { Open(() => new LibraryWindow(null)); }
    }
}
