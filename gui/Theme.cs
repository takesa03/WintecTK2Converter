using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Tk2Gui
{
    /// <summary>
    /// ダークテーマの色 (コードから使うもの) と、タイトルバーを暗くする処理。
    /// 色は App.xaml のカラーパレット (TimelapseChecker / SwitchBotMeter と同じ) に合わせてある。
    /// </summary>
    public static class Theme
    {
        static SolidColorBrush B(byte r, byte g, byte b) { SolidColorBrush x = new SolidColorBrush(Color.FromRgb(r, g, b)); x.Freeze(); return x; }

        public static readonly Brush Background = B(0x1E, 0x1E, 0x1E);
        public static readonly Brush Panel = B(0x25, 0x25, 0x26);
        public static readonly Brush Section = B(0x2D, 0x2D, 0x30);
        public static readonly Brush Border = B(0x3F, 0x3F, 0x46);
        public static readonly Brush Text = B(0xDC, 0xDC, 0xDC);
        public static readonly Brush TextSecondary = B(0x9D, 0x9D, 0x9D);
        public static readonly Brush Ok = B(0x89, 0xD1, 0x85);          // 状態の文字 (成功・情報)
        public static readonly Brush Error = B(0xF4, 0x87, 0x71);       // 状態の文字 (失敗)・無効な周回

        // 周回表の行の背景 (暗い背景の上で、種類が分かる程度の色)
        public static readonly Brush RowHeader = B(0x26, 0x3A, 0x55);   // セッションの区切り行
        public static readonly Brush RowInvalid = B(0x4A, 0x24, 0x28);  // 無効 (×)
        public static readonly Brush RowOut = B(0x30, 0x30, 0x33);      // OUT
        public static readonly Brush RowBest = B(0x1E, 0x47, 0x2B);     // 全体ベストの周回
        public static readonly Brush RowIn = B(0x4A, 0x3D, 0x1C);       // IN
        public static readonly Brush Mark = B(0xFF, 0x5C, 0x5C);        // ★ ※ の付いた値 (赤の太字)

        // プレビュー・再生の地図の背景
        public static readonly Brush MapBackground = B(0x1E, 0x22, 0x28);

        const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;   // Windows 10 の古い版

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int pvAttribute, int cbAttribute);

        /// <summary>
        /// ウィンドウをダークにする: 背景・文字色 (Window 向けの共通スタイルは、MainWindow などの派生したウィンドウには効かないため、ここで設定する) と、
        /// タイトルバー (ウィンドウのハンドルができたときに設定する)。各ウィンドウの InitializeComponent() の直後に呼ぶ
        /// </summary>
        public static void ApplyDark(Window w)
        {
            w.SetResourceReference(Window.BackgroundProperty, "BackgroundBrush");
            w.SetResourceReference(Window.ForegroundProperty, "TextPrimaryBrush");
            w.SourceInitialized += (s, e) =>
            {
                try
                {
                    IntPtr hwnd = new WindowInteropHelper(w).Handle;
                    int on = 1;
                    if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int)) != 0)
                        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref on, sizeof(int));
                }
                catch (Exception) { /* 古い Windows では明るいタイトルバーのまま */ }
            };
        }
    }
}
