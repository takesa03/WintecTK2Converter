using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Tk2Gui
{
    // =====================================================================
    //  動画で比較: 2 つの周回・走行の動画を並べて同時に再生する
    //  時計は「周回の開始 (ラインの通過) からの秒 τ」。両方とも τ = 0 でラインを通る。
    //  それぞれの動画の時刻 = 周回の開始 (ログの秒) + τ − 動画の t0
    //  映像は ffmpeg で読み、この画面の時計に合わせて表示する (コマ送り可)。
    //  音声は両方 / A / B / なし を切り替え (1 × のときだけ)。地図 (比較再生の画面) も一緒に動かせる
    // =====================================================================
    public partial class VideoCompareWindow : Window
    {
        public sealed class Lane
        {
            public SessionRecord Sess;
            public LapRecord Lap;
            public List<GpsSample> Gps;
            public VideoStore.Entry Video;
            public Color Color;

            internal string Name;
            internal VideoInfo Vi;
            internal FrameReader Reader;
            internal OverlayData Data;
            internal VideoPane Pane;
            internal FrameworkElement Cell;
            internal double Shown = double.NaN;     // 表示中のコマの τ
            internal DateTime LastSeek;
            internal AudioClip Audio;               // 読み込んだ音声 (48 kHz ステレオ)
            internal double Offset;                 // 揃える位置: 周回の開始から何秒の所を τ = 0 にするか (セクターの開始で揃えるとき)
            internal double Base { get { return Lap.StartT + Offset; } }    // τ = 0 のログの通算秒
            internal double VideoT(double tau) { return Base + tau - Video.T0; }
        }

        /// <summary>音声の切れ端。T0 = 先頭が周回の開始から何秒か</summary>
        internal sealed class AudioClip
        {
            public short[] Pcm;
            public double T0;
        }

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        const double Before = 3, After = 3;
        readonly CourseDefinition _def;
        readonly List<Lane> _lanes;
        readonly PlayClock _clock = new PlayClock();
        double _tmin, _tmax;                // τ の範囲 (揃える位置の前後。周回の全体が入る)
        readonly string[] _sectorNames;
        int _audioVersion;
        double _tau;
        bool _sliderBusy;
        string _ffmpeg;
        WaveOutPlayer _audio;
        volatile int _audioMode;            // 0 = 両方, 1 = A, 2 = B, 3 = なし, 4 = 左右に分ける (A = L, B = R)
        double _audioCursor = double.NaN;   // 音声の再生位置 (τ。音声のスレッドだけが使う)
        ReplayWindow _map;
        readonly CancellationTokenSource _cancel = new CancellationTokenSource();
        OverlayLayout _layout;

        public VideoCompareWindow(CourseDefinition def, string[] sectorNames, List<Lane> lanes)
        {
            InitializeComponent();
            Theme.ApplyDark(this);
            _def = def; _lanes = lanes; _sectorNames = sectorNames ?? new string[0];
            UpdateRange();
            _ffmpeg = FfVideo.Ffmpeg();
            BuildAlignItems();

            OverlayLayout layout = null;
            try { layout = VideoKit.LoadLayout(); }
            catch (Exception ex) { Status("表示のレイアウトを読めません: " + ex.Message); }
            _layout = layout;

            string[] ab = { "A", "B" };
            for (int i = 0; i < lanes.Count; i++)
            {
                Lane l = lanes[i];
                string drv = LapAnalyzer.DriverOf(l.Sess, l.Lap);
                l.Name = ab[i] + "  " + VideoKit.Label(l.Sess, l.Lap) + (drv.Length > 0 ? "  " + drv : "") + "   " + LapAnalyzer.FormatTime(l.Lap.LapTime);
                l.Pane = new VideoPane();
                TextBlock cap = new TextBlock { Text = l.Name, FontWeight = FontWeights.Bold, FontSize = 14, Foreground = new SolidColorBrush(l.Color), Margin = new Thickness(2, 0, 0, 4) };
                DockPanel dp = new DockPanel { Margin = new Thickness(3) };
                DockPanel.SetDock(cap, Dock.Top);
                dp.Children.Add(cap); dp.Children.Add(l.Pane);
                l.Cell = dp;
                PaneGrid.Children.Add(dp);
                if (layout != null)
                {
                    try
                    {
                        l.Data = new OverlayData(def, l.Sess, sectorNames, l.Gps, VideoKit.Options(layout), Before, After, l.Lap);
                    }
                    catch (Exception ex) { Status("表示を重ねる準備ができません: " + ex.Message); }
                }
            }
            string kind = def.IsGymkhana ? "走行" : "周回";
            Title = "動画で比較 - " + string.Join("  /  ", lanes.Select(l => VideoKit.Label(l.Sess, l.Lap))) + "  " + App.VersionText;

            Settings.Binder st = new Settings.Binder("VideoCompare");
            st.Combo(SpeedCombo, "Speed");
            st.Combo(AudioCombo, "Audio");
            st.Combo(LayoutCombo, "Layout");
            st.Check(OverlayCheck, "Overlay");
            OverlayPrefs.BindGraph(GraphCheck, () => { foreach (Lane l in _lanes) l.Pane.RefreshOverlay(); });   // 速度のグラフ (書き出し・時刻合わせと共通)
            st.Check(LoopCheck, "Loop");
            Settings.Track(this, "VideoCompareWindow", st.SaveAll);
            ApplyLayout();
            Speed_Changed(null, null);
            Audio_Changed(null, null);

            PreviewKeyDown += OnKey;
            Loaded += OnLoaded;
            Closed += (s, e) =>
            {
                CompositionTarget.Rendering -= OnRender;
                _cancel.Cancel();
                if (_audio != null) _audio.Dispose();
                foreach (Lane l in _lanes) { l.Pane.StopOverlay(); if (l.Reader != null) l.Reader.Dispose(); }
                if (_map != null) { try { _map.Close(); } catch (Exception) { } }
            };
            Status(string.Format(Inv, "両方とも、{0}の開始 (ラインの通過) を 0 秒にして同時に再生します。「揃える位置」でセクターの開始に揃えることもできます。前後に {1} 秒の余白。スペース = 再生・一時停止、← → = コマ送り、1 / 2 / 3 / 4 / 0 = 音声 A / B / 両方 / 左右 / なし。", kind, Before));
        }

        void Status(string s) { StatusText.Text = s; }

        async void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_ffmpeg == null)
            {
                foreach (Lane l in _lanes) l.Pane.Message = "ffmpeg が見つかりません。「動画素材を書き出す...」の画面で ffmpeg.exe の場所を指定してください";
                return;
            }
            string ff = _ffmpeg;
            foreach (Lane l in _lanes)
            {
                Lane ln = l;
                try
                {
                    ln.Vi = await Task.Run(() => FfVideo.Probe(ff, ln.Video.Path));
                    ln.Reader = new FrameReader(ff, ln.Video.Path, ln.Vi, 960);
                    if (ln.Data != null)
                    {
                        OverlayLayout lay = _layout; OverlayData d = ln.Data; double fps = ln.Vi.Fps;
                        ln.Pane.StartOverlay((w, h) => new OverlayRenderer(lay, d, w, h), (r, t) => d.At(t, fps, r.TrailFrames), (double)ln.Vi.Width / ln.Vi.Height);
                    }
                }
                catch (Exception ex) { ln.Pane.Message = ex.Message; }
            }
            CompositionTarget.Rendering += OnRender;
            SeekAll(0);
            OpenMap();

            await LoadAudioAsync();
        }

        // 音声 (それぞれの範囲を読み込んでおく)。揃える位置を変えたら読み直す
        async Task LoadAudioAsync()
        {
            if (!_lanes.Any(l => l.Vi != null && l.Vi.HasAudio)) return;
            int ver = ++_audioVersion;
            string ff = _ffmpeg;
            Status("音声を読み込み中...  " + StatusText.Text);
            double tmin = _tmin, dur = _tmax - _tmin;
            CancellationToken ct = _cancel.Token;
            try
            {
                foreach (Lane l in _lanes)
                {
                    if (l.Vi == null || !l.Vi.HasAudio) continue;
                    Lane ln = l;
                    double from = ln.VideoT(tmin), t0 = ln.Offset + tmin;
                    short[] pcm = await Task.Run(() => FfVideo.ReadAudio(ff, ln.Video.Path, from, dur, ct));
                    if (ver != _audioVersion) return;
                    ln.Audio = new AudioClip { Pcm = pcm, T0 = t0 };
                }
                if (!ct.IsCancellationRequested && _audio == null) _audio = new WaveOutPlayer(FfVideo.AudioRate, FillAudio);
                Status(StatusText.Text.Replace("音声を読み込み中...  ", ""));
            }
            catch (Exception ex) { if (ver == _audioVersion) Status("音声を出せません: " + ex.Message); }
        }

        // ---------------- 並べ方 ----------------

        void ApplyLayout()
        {
            PaneGrid.RowDefinitions.Clear(); PaneGrid.ColumnDefinitions.Clear();
            bool vertical = LayoutCombo.SelectedIndex == 1;
            for (int i = 0; i < _lanes.Count; i++)
            {
                if (vertical) PaneGrid.RowDefinitions.Add(new RowDefinition());
                else PaneGrid.ColumnDefinitions.Add(new ColumnDefinition());
                Grid.SetRow(_lanes[i].Cell, vertical ? i : 0);
                Grid.SetColumn(_lanes[i].Cell, vertical ? 0 : i);
            }
        }

        void Layout_Changed(object sender, SelectionChangedEventArgs e) { if (_lanes != null) ApplyLayout(); }

        // ---------------- 再生 ----------------

        void SeekAll(double tau)
        {
            _tau = Math.Max(_tmin, Math.Min(_tmax, tau));
            _clock.Set(_tau);
            foreach (Lane l in _lanes)
            {
                if (l.Reader == null) continue;
                l.Shown = double.NaN;
                double vt = l.VideoT(_tau);
                if (vt >= 0 && vt < l.Vi.Duration) { l.Reader.Seek(vt); l.LastSeek = DateTime.Now; }
            }
            UpdateInfo();
        }

        void OnRender(object sender, EventArgs e)
        {
            if (_clock.Playing)
            {
                _tau = _clock.Now;
                if (_tau >= _tmax)
                {
                    if (LoopCheck.IsChecked == true) { SeekAll(-Before); _clock.Play(); }
                    else { _clock.Pause(); _tau = _tmax; _clock.Set(_tau); PlayButton.Content = "▶ 再生"; }
                }
            }
            bool changed = false;
            foreach (Lane l in _lanes)
            {
                if (l.Reader == null) continue;
                double vt = l.VideoT(_tau);
                if (vt < 0 || vt >= l.Vi.Duration)
                {
                    if (l.Pane.Message == null || l.Pane.Message.Length == 0) { l.Pane.ClearFrame(); l.Pane.Message = "(動画の範囲外)"; }
                    continue;
                }
                if (l.Pane.Message == "(動画の範囲外)") l.Pane.Message = null;
                bool needSeek;
                FrameReader.Frame f = l.Reader.Get(vt, out needSeek);
                if (needSeek && (DateTime.Now - l.LastSeek).TotalSeconds > 0.3) { l.Reader.Seek(vt); l.LastSeek = DateTime.Now; }
                if (f != null)
                {
                    l.Pane.ShowFrame(l.Reader, f);
                    l.Shown = f.T + l.Video.T0 - l.Base;
                    l.Reader.Recycle(f);
                    changed = true;
                }
            }
            if (changed || _clock.Playing) UpdateInfo();
        }

        void UpdateInfo()
        {
            TimeText.Text = VideoKit.Clock(_tau) + " / " + VideoKit.Clock(_tmax - After) + (AlignIndex > 0 ? "  (" + AlignName + " から)" : "");
            _sliderBusy = true; TimeSlider.Value = Math.Max(_tmin, Math.Min(_tmax, _tau)); _sliderBusy = false;
            foreach (Lane l in _lanes)
            {
                double tau = double.IsNaN(l.Shown) ? _tau : l.Shown;
                l.Pane.RequestOverlay(l.Data != null && OverlayCheck.IsChecked == true ? l.Base + tau : double.NaN);
            }
        }

        double Fps { get { Lane l = _lanes.FirstOrDefault(x => x.Vi != null); return l != null ? l.Vi.Fps : 30; } }

        void TogglePlay()
        {
            if (_clock.Playing) { _clock.Pause(); _tau = _clock.Now; PlayButton.Content = "▶ 再生"; }
            else
            {
                if (_tau >= _tmax - 0.05) SeekAll(-Before);
                _clock.Set(_tau); _clock.Play(); PlayButton.Content = "⏸ 一時停止";
            }
        }

        void Play_Click(object sender, RoutedEventArgs e) { TogglePlay(); }
        void Restart_Click(object sender, RoutedEventArgs e) { SeekAll(-Before); }

        // ---------------- 揃える位置 (コントロールライン / セクターの開始) ----------------

        int AlignIndex { get { return Math.Max(0, AlignCombo.SelectedIndex); } }
        string AlignName { get { ComboBoxItem it = AlignCombo.SelectedItem as ComboBoxItem; return it != null ? (string)it.Content : ""; } }

        // 0 = コントロールライン (スタート)、k = セクター k+1 の開始 (k 本目のラインの通過)。両方の周回にそのセクターのタイムがあるものだけ選べる
        void BuildAlignItems()
        {
            AlignCombo.Items.Clear();
            AlignCombo.Items.Add(new ComboBoxItem { Content = _def.IsGymkhana ? "スタート" : "コントロールライン" });
            for (int k = 1; k < _sectorNames.Length; k++)
            {
                bool ok = _lanes.All(l => l.Lap.Sector != null && l.Lap.Sector.Length >= k && Enumerable.Range(0, k).All(j => l.Lap.Sector[j].HasValue));
                AlignCombo.Items.Add(new ComboBoxItem { Content = _sectorNames[k] + " の開始", IsEnabled = ok });
            }
            AlignCombo.SelectedIndex = 0;
            AlignCombo.SelectionChanged += Align_Changed;      // 画面ができる前に呼ばれないよう、選んでから付ける
        }

        // 各周回で、揃える位置が周回の開始から何秒か
        double OffsetOf(Lane l, int k)
        {
            double s = 0;
            for (int j = 0; j < k; j++) s += l.Lap.Sector[j].Value;
            return s;
        }

        void UpdateRange()
        {
            _tmin = -Before - _lanes.Max(l => l.Offset);
            _tmax = _lanes.Max(l => l.Lap.LapTime.Value - l.Offset) + After;
            _sliderBusy = true; TimeSlider.Minimum = _tmin; TimeSlider.Maximum = _tmax; _sliderBusy = false;
        }

        async void Align_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_lanes == null || AlignCombo.SelectedItem == null) return;
            int k = AlignIndex;
            bool playing = _clock.Playing;
            if (playing) { _clock.Pause(); PlayButton.Content = "▶ 再生"; }
            foreach (Lane l in _lanes) l.Offset = k == 0 ? 0 : OffsetOf(l, k);
            UpdateRange();
            _audioCursor = double.NaN;
            if (_map != null) _map.SetShifts(_lanes.Select(l => l.Offset).ToArray());
            SeekAll(-Before);
            if (IsLoaded) await LoadAudioAsync();
        }

        void Step(string tag)
        {
            if (_clock.Playing) { _clock.Pause(); _tau = _clock.Now; PlayButton.Content = "▶ 再生"; }
            if (tag == "f") { _tau = Math.Min(_tmax, _tau + 1.0 / Fps); _clock.Set(_tau); UpdateInfo(); }      // 読み出しの続きのコマ
            else if (tag == "-f") SeekAll(_tau - 1.0 / Fps);
            else
            {
                double d = double.Parse(tag, Inv);
                if (d > 0) { _tau = Math.Min(_tmax, _tau + d); _clock.Set(_tau); UpdateInfo(); }
                else SeekAll(_tau + d);
            }
        }

        void Step_Click(object sender, RoutedEventArgs e) { Step((string)((Button)sender).Tag); }

        void Slider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_sliderBusy) return;
            if (_clock.Playing) { _clock.Pause(); PlayButton.Content = "▶ 再生"; }
            SeekAll(TimeSlider.Value);
        }

        void Speed_Changed(object sender, SelectionChangedEventArgs e)
        {
            ComboBoxItem it = SpeedCombo.SelectedItem as ComboBoxItem;
            double v;
            if (it != null && double.TryParse(it.Tag as string, NumberStyles.Float, Inv, out v)) _clock.SetSpeed(v);
        }

        void Audio_Changed(object sender, SelectionChangedEventArgs e) { _audioMode = Math.Max(0, AudioCombo.SelectedIndex); }

        void Overlay_Changed(object sender, RoutedEventArgs e) { if (IsLoaded) UpdateInfo(); }

        void OnKey(object sender, KeyEventArgs e)
        {
            if (e.OriginalSource is TextBox) return;
            switch (e.Key)
            {
                case Key.Space: TogglePlay(); break;
                case Key.Right: Step("f"); break;
                case Key.Left: Step("-f"); break;
                case Key.D1: case Key.NumPad1: AudioCombo.SelectedIndex = 1; break;
                case Key.D2: case Key.NumPad2: AudioCombo.SelectedIndex = 2; break;
                case Key.D3: case Key.NumPad3: AudioCombo.SelectedIndex = 0; break;
                case Key.D0: case Key.NumPad0: AudioCombo.SelectedIndex = 3; break;
                case Key.D4: case Key.NumPad4: AudioCombo.SelectedIndex = 4; break;
                default: return;
            }
            e.Handled = true;
        }

        // ---------------- 音声 (別スレッドから呼ばれる) ----------------

        void FillAudio(short[] buf, int frames)
        {
            int mode = _audioMode;
            if (!_clock.Playing || Math.Abs(_clock.Speed - 1) > 1e-6 || mode == 3)
            {
                Array.Clear(buf, 0, frames * 2);
                _audioCursor = double.NaN;
                return;
            }
            double target = _clock.Now + (_audio != null ? _audio.Latency * 0.875 : 0.14);
            if (double.IsNaN(_audioCursor) || Math.Abs(_audioCursor - target) > 0.06) _audioCursor = target;
            // それぞれの音声の位置: 音声の先頭は周回の開始から T0 秒、今の位置は周回の開始から (τ + Offset) 秒
            AudioClip ca = _lanes.Count > 0 ? _lanes[0].Audio : null, cb = _lanes.Count > 1 ? _lanes[1].Audio : null;
            short[] a = ca != null ? ca.Pcm : null, b = cb != null ? cb.Pcm : null;
            if (mode == 1) b = null;
            if (mode == 2) a = null;
            double ka = a != null && b != null ? 0.6 : 1.0;
            int ia = a != null ? (int)Math.Round((_audioCursor + _lanes[0].Offset - ca.T0) * FfVideo.AudioRate) * 2 : 0;
            int ib = b != null ? (int)Math.Round((_audioCursor + _lanes[1].Offset - cb.T0) * FfVideo.AudioRate) * 2 : 0;
            if (mode == 4)
            {
                // 左右に分ける: A (左・上の映像) をモノラルにして L、B (右・下の映像) をモノラルにして R
                for (int f = 0; f < frames; f++)
                {
                    int pa = ia + f * 2, pb = ib + f * 2;
                    bool inA = a != null && pa >= 0 && pa + 1 < a.Length, inB = b != null && pb >= 0 && pb + 1 < b.Length;
                    buf[f * 2] = inA ? (short)((a[pa] + a[pa + 1]) / 2) : (short)0;
                    buf[f * 2 + 1] = inB ? (short)((b[pb] + b[pb + 1]) / 2) : (short)0;
                }
                _audioCursor += (double)frames / FfVideo.AudioRate;
                return;
            }
            for (int k = 0; k < frames * 2; k++)
            {
                int pa = ia + k, pb = ib + k;
                double v = 0;
                if (a != null && pa >= 0 && pa < a.Length) v += a[pa] * ka;
                if (b != null && pb >= 0 && pb < b.Length) v += b[pb] * ka;
                buf[k] = (short)Math.Max(-32768, Math.Min(32767, v));
            }
            _audioCursor += (double)frames / FfVideo.AudioRate;
        }

        // ---------------- 地図 ----------------

        void OpenMap()
        {
            if (_map != null) { _map.Activate(); return; }
            try
            {
                List<ReplayLap> laps = new List<ReplayLap>();
                string[] ab = { "A", "B" };
                for (int i = 0; i < _lanes.Count; i++)
                {
                    Lane l = _lanes[i];
                    string drv = LapAnalyzer.DriverOf(l.Sess, l.Lap);
                    string label = ab[i] + " " + VideoKit.Label(l.Sess, l.Lap) + (drv.Length > 0 ? " " + drv : "");
                    laps.Add(ReplayLap.Build(label, label, l.Lap.StartT, l.Lap.LapTime.Value, l.Gps, _def.Lat0, _def.Lon0, l.Color));
                }
                _map = new ReplayWindow(_def, laps);
                _map.AttachClock(() => _tau);
                _map.SetShifts(_lanes.Select(l => l.Offset).ToArray());
                _map.Closed += (s, e) => _map = null;
                _map.Show();
            }
            catch (Exception ex) { Status("地図を開けません: " + ex.Message); }
        }

        void Map_Click(object sender, RoutedEventArgs e) { OpenMap(); }

        // ---------------- ウィンドウの配置 ----------------

        /// <summary>呼び出し元 (ラップ・セクタータイムの画面)。「並べて配置」で左下に置く</summary>
        public Window Origin { get; set; }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public int dwFlags; }
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool GetMonitorInfo(IntPtr hMon, ref MONITORINFO mi);

        /// <summary>この画面があるモニターの作業領域 (タスクバーを除く。WPF の単位)</summary>
        Rect WorkArea()
        {
            IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            MONITORINFO mi = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(MONITORINFO)) };
            if (hwnd == IntPtr.Zero || !GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref mi)) return SystemParameters.WorkArea;
            Point a = new Point(mi.rcWork.Left, mi.rcWork.Top), b = new Point(mi.rcWork.Right, mi.rcWork.Bottom);
            PresentationSource src = PresentationSource.FromVisual(this);
            if (src != null && src.CompositionTarget != null)
            {
                Matrix m = src.CompositionTarget.TransformFromDevice;
                a = m.Transform(a); b = m.Transform(b);
            }
            return new Rect(a, b);
        }

        static void Place(Window w, double x, double y, double width, double height)
        {
            if (w == null) return;
            if (w.WindowState != WindowState.Normal) w.WindowState = WindowState.Normal;
            w.Left = x; w.Top = y;
            w.Width = Math.Max(width, w.MinWidth);
            w.Height = Math.Max(height, w.MinHeight);
        }

        /// <summary>動画を上半分に大きく、左下にラップ・セクタータイム、右下に地図を並べる</summary>
        void Arrange_Click(object sender, RoutedEventArgs e)
        {
            OpenMap();
            Rect r = WorkArea();
            double topH = Math.Round(r.Height * 0.63);
            double botH = r.Height - topH;
            double leftW = Math.Round(r.Width * 0.6);
            Place(this, r.Left, r.Top, r.Width, topH);
            Place(Origin, r.Left, r.Top + topH, leftW, botH);
            Place(_map, r.Left + leftW, r.Top + topH, r.Width - leftW, botH);
            if (Origin != null) Origin.Activate();
            if (_map != null) _map.Activate();
            Activate();
        }
    }
}
