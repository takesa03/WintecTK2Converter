using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Tk2Gui
{
    public partial class App : Application
    {
        /// <summary>ウィンドウのタイトルに付けるバージョン (例: v1.1.0.12)</summary>
        public static string VersionText
        {
            get
            {
                System.Version v = Assembly.GetExecutingAssembly().GetName().Version;
                return v == null ? "" : "v" + v;
            }
        }

        /// <summary>
        /// 最初の画面を開く (App.xaml に StartupUri を書かない)。
        /// 引数なし = ランチャー (変換ツール / 解析ツール)、ファイル・フォルダ = 変換ツール (exe へのドラッグ＆ドロップ)、
        /// -course ファイル... = コース定義、-lap コース定義 ログ... = ラップ解析、-compare [フォルダ] = ライブラリから比較
        /// </summary>
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            string first = e.Args.Length > 0 ? e.Args[0] : null;
            string[] rest = e.Args.Skip(1).ToArray();
            if (Is(first, "-compare"))
                new LibraryWindow(rest.Length > 0 ? rest[0] : null).Show();
            else if (Is(first, "-course"))
                new CourseWindow(rest).Show();
            else if (Is(first, "-lap"))
                new LapWindow(null, null, rest).Show();
            else if (e.Args.Any(a => !a.StartsWith("-")))
                new MainWindow().Show();
            else
                new LauncherWindow().Show();
        }

        static bool Is(string a, string opt) { return a != null && string.Equals(a, opt, System.StringComparison.OrdinalIgnoreCase); }

        static App()
        {
            // リスト (ListBox・DataGrid など) の上でホイールを回すと、リストがそれ以上動けなくても
            // ホイールを食べてしまい、外側のパネルがスクロールしない。全ウィンドウ共通で外側へ渡す。
            EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewMouseWheelEvent,
                new MouseWheelEventHandler(PassWheelOutward));
        }

        static void PassWheelOutward(object sender, MouseWheelEventArgs e)
        {
            if (e.Handled || e.Delta == 0) return;
            DependencyObject d = e.OriginalSource as DependencyObject;
            ScrollViewer inner = null;
            while (d != null)
            {
                ScrollViewer sv = d as ScrollViewer;
                if (sv != null)
                {
                    if (inner == null)
                    {
                        // 一番内側がリストや入力欄のスクロールでなければ、何もしない (地図などのホイール操作を邪魔しない)
                        if (!(sv.TemplatedParent is ItemsControl) && !(sv.TemplatedParent is TextBoxBase)) return;
                        if (CanScroll(sv, e.Delta)) return;   // リスト自身が動けるならそのまま
                        inner = sv;
                    }
                    else if (CanScroll(sv, e.Delta))
                    {
                        int n = 3 * System.Math.Max(1, System.Math.Abs(e.Delta) / 120);
                        for (int i = 0; i < n; i++) { if (e.Delta > 0) sv.LineUp(); else sv.LineDown(); }
                        e.Handled = true;
                        return;
                    }
                }
                d = (d is Visual || d is System.Windows.Media.Media3D.Visual3D)
                    ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
            }
        }

        static bool CanScroll(ScrollViewer sv, int delta)
        {
            if (sv.ScrollableHeight <= 0) return false;
            return delta > 0 ? sv.VerticalOffset > 0 : sv.VerticalOffset < sv.ScrollableHeight;
        }
    }
}
