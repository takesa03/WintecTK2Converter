using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Tk2Gui
{
    // =====================================================================
    //  動画で比較: 最大表示
    //  選んでいる映像を、重ねる表示ごと、この画面があるモニターいっぱいに出す (枠なし・手前)。
    //  操作はキーボード (OnKey)。Esc で戻る。2 つのときは Tab で表示する方を切り替える (もう一方も裏で同じ時刻に進めておく)。
    //  出している映像は、モニターの幅で読み直し、表示も大きく描く (戻したら元の幅)。
    //  最大表示の間は、この画面の位置・大きさとして保存しない
    // =====================================================================
    public partial class VideoCompareWindow
    {
        const int NormalReaderWidth = 960;      // 普段の映像を読む幅 (2 つ並べるので軽く)
        const int NormalOverlayWidth = 1280;    // 普段の表示を描く幅

        int _sel;               // 選んでいる映像 (2 つのとき、クリックで選ぶ)
        int _full = -1;         // 最大表示している映像 (-1 = 最大表示でない)

        // 最大表示に入る前の状態 (戻すときに使う)
        WindowStyle _fsStyle;
        ResizeMode _fsResize;
        WindowState _fsState;
        Rect _fsBounds;
        bool _fsTopmost, _fsKeep;
        Thickness _fsMargin;
        DispatcherTimer _hintTimer;

        void SelectLane(int i)
        {
            if (i < 0 || i >= _lanes.Count) return;
            _sel = i;
            for (int k = 0; k < _lanes.Count; k++)
            {
                Lane l = _lanes[k];
                bool on = _lanes.Count > 1 && k == i && _full < 0;
                l.Frame.BorderBrush = on ? new SolidColorBrush(l.Color) : Brushes.Transparent;
            }
        }

        // 映像のクリック = 選ぶ、ダブルクリック = 最大表示 (最大表示の間は戻る)
        void Pane_MouseDown(int i, MouseButtonEventArgs e)
        {
            SelectLane(i);
            PaneGrid.Focus();       // キー操作をこの画面で受ける (ComboBox などに取られないように)
            if (e.ClickCount == 2)
            {
                if (_full >= 0) ExitFull(); else EnterFull(i);
                e.Handled = true;
            }
        }

        void Full_Click(object sender, RoutedEventArgs e) { EnterFull(_sel); }

        void EnterFull(int i)
        {
            if (i < 0 || i >= _lanes.Count) return;
            bool first = _full < 0;
            if (first)
            {
                _fsStyle = WindowStyle; _fsResize = ResizeMode; _fsState = WindowState; _fsTopmost = Topmost; _fsMargin = RootGrid.Margin;
                _fsBounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
                _fsKeep = Settings.KeepsBounds(this);
                Settings.KeepBounds(this, false);       // 最大表示の位置・大きさは保存しない
            }
            int prev = _full;
            _full = i;
            if (prev >= 0 && prev != i) SetQuality(_lanes[prev], false);
            if (first)
            {
                Controls.Visibility = Visibility.Collapsed;
                TimeRow.Visibility = Visibility.Collapsed;
                StatusText.Visibility = Visibility.Collapsed;
                RootGrid.Margin = new Thickness(0);
                foreach (Lane l in _lanes)
                {
                    l.Cap.Visibility = Visibility.Collapsed;
                    l.Frame.Margin = new Thickness(0); l.Frame.Padding = new Thickness(0); l.Frame.BorderThickness = new Thickness(0);
                }
                Rect m = MonitorRect(false);
                if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;
                WindowStyle = WindowStyle.None;
                ResizeMode = ResizeMode.NoResize;
                Left = m.Left; Top = m.Top; Width = m.Width; Height = m.Height;
                Topmost = true;
            }
            SelectLane(i);
            ApplyFullLayout();
            SetQuality(_lanes[i], true);
            Activate();
            PaneGrid.Focus();
            if (first) ShowHint(FullKeysText(), 5);
            else ShowHint(_lanes[i].Name, 2);
        }

        void ExitFull()
        {
            if (_full < 0) return;
            SetQuality(_lanes[_full], false);
            _full = -1;
            Topmost = _fsTopmost;
            WindowStyle = _fsStyle;
            ResizeMode = _fsResize;
            Left = _fsBounds.Left; Top = _fsBounds.Top; Width = _fsBounds.Width; Height = _fsBounds.Height;
            if (_fsState == WindowState.Maximized) WindowState = WindowState.Maximized;
            Controls.Visibility = Visibility.Visible;
            TimeRow.Visibility = Visibility.Visible;
            StatusText.Visibility = Visibility.Visible;
            RootGrid.Margin = _fsMargin;
            foreach (Lane l in _lanes)
            {
                l.Cap.Visibility = Visibility.Visible;
                l.Frame.Margin = new Thickness(2); l.Frame.Padding = new Thickness(1); l.Frame.BorderThickness = new Thickness(2);
            }
            Settings.KeepBounds(this, _fsKeep);
            HideHint();
            ApplyLayout();
            SelectLane(_sel);
            PaneGrid.Focus();
        }

        /// <summary>最大表示: 出す映像だけを 1 マスに (ほかは隠すが、裏で同じ時刻に進めておく)</summary>
        void ApplyFullLayout()
        {
            PaneGrid.RowDefinitions.Clear(); PaneGrid.ColumnDefinitions.Clear();
            for (int k = 0; k < _lanes.Count; k++)
            {
                Grid.SetRow(_lanes[k].Cell, 0);
                Grid.SetColumn(_lanes[k].Cell, 0);
                _lanes[k].Cell.Visibility = k == _full ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        /// <summary>映像を読む幅と表示を描く幅を、最大表示用 (モニターの幅) / 普段に切り替える</summary>
        void SetQuality(Lane l, bool big)
        {
            int px = (int)Math.Round(MonitorRect(true).Width);
            int rw = big ? Math.Max(NormalReaderWidth, Math.Min(3840, px)) : NormalReaderWidth;
            int ow = big ? Math.Max(NormalOverlayWidth, Math.Min(2560, px)) : NormalOverlayWidth;
            l.Pane.SetOverlayWidth(ow);
            if (l.Vi == null || l.Reader == null || _ffmpeg == null || l.ReaderWidth == rw) return;
            FrameReader old = l.Reader;
            try
            {
                l.Reader = new FrameReader(_ffmpeg, l.Video.Path, l.Vi, rw);
                l.ReaderWidth = rw;
            }
            catch (Exception ex) { l.Reader = old; Status("映像を読み直せません: " + ex.Message); return; }
            old.Dispose();
            l.Shown = double.NaN;
            double vt = l.VideoT(_tau);
            if (vt >= 0 && vt < l.Vi.Duration) { l.Reader.Seek(vt); l.LastSeek = DateTime.Now; }
        }

        /// <summary>この画面があるモニターの全体 (タスクバーも含む)。device = true なら画素、false なら WPF の単位</summary>
        Rect MonitorRect(bool device)
        {
            IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            MONITORINFO mi = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(MONITORINFO)) };
            if (hwnd == IntPtr.Zero || !GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref mi))
                return new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
            Point a = new Point(mi.rcMonitor.Left, mi.rcMonitor.Top), b = new Point(mi.rcMonitor.Right, mi.rcMonitor.Bottom);
            if (!device)
            {
                PresentationSource src = PresentationSource.FromVisual(this);
                if (src != null && src.CompositionTarget != null)
                {
                    Matrix m = src.CompositionTarget.TransformFromDevice;
                    a = m.Transform(a); b = m.Transform(b);
                }
            }
            return new Rect(a, b);
        }

        // ---------------- キー操作の補助 ----------------

        void ChangeSpeed(int d)
        {
            int i = Math.Max(0, Math.Min(SpeedCombo.Items.Count - 1, SpeedCombo.SelectedIndex + d));
            SpeedCombo.SelectedIndex = i;
            ComboBoxItem it = SpeedCombo.SelectedItem as ComboBoxItem;
            if (it != null) ShowHint("速度 " + it.Content, 1.5);
        }

        void ShowAudioHint()
        {
            ComboBoxItem it = AudioCombo.SelectedItem as ComboBoxItem;
            if (it != null) ShowHint("音声: " + it.Content, 1.5);
        }

        string FullKeysText()
        {
            string s = "Esc = 戻る    スペース = 再生・一時停止    ← → = コマ    Shift + ← → = 1 秒    Home = 最初から    ↑ ↓ = 速度";
            if (_lanes.Count > 1) s += "\nTab = A / B の切り替え    1 / 2 / 3 / 4 / 0 = 音声 A / B / 両方 / 左右 / なし";
            s += (_lanes.Count > 1 ? "    " : "\n") + "L = 繰り返す    O = 表示を重ねる";
            return s;
        }

        /// <summary>最大表示の間だけ、画面の下に短い案内を出す (seconds 秒で消す)</summary>
        void ShowHint(string text, double seconds = 1.5)
        {
            if (_full < 0) return;
            FullHintText.Text = text;
            FullHint.Visibility = Visibility.Visible;
            if (_hintTimer == null)
            {
                _hintTimer = new DispatcherTimer();
                _hintTimer.Tick += (s, e) => HideHint();
            }
            _hintTimer.Stop();
            _hintTimer.Interval = TimeSpan.FromSeconds(seconds);
            _hintTimer.Start();
        }

        void HideHint()
        {
            if (_hintTimer != null) _hintTimer.Stop();
            FullHint.Visibility = Visibility.Collapsed;
        }
    }
}
