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
