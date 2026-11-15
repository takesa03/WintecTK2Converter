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
            internal short[] Pcm;                   // τ = Tmin からの音声 (48 kHz ステレオ)
            internal double VideoT(double tau) { return Lap.StartT + tau - Video.T0; }
        }

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        const double Before = 3, After = 3;
        readonly CourseDefinition _def;
        readonly List<Lane> _lanes;
        readonly PlayClock _clock = new PlayClock();
        readonly double _tmin, _tmax;
        double _tau;
        bool _sliderBusy;
        string _ffmpeg;
        WaveOutPlayer _audio;
        volatile int _audioMode;            // 0 = 両方, 1 = A, 2 = B, 3 = なし
        double _audioCursor = double.NaN;   // 音声の再生位置 (τ。音声のスレッドだけが使う)
        ReplayWindow _map;
        readonly CancellationTokenSource _cancel = new CancellationTokenSource();
        OverlayLayout _layout;

        public VideoCompareWindow(CourseDefinition def, string[] sectorNames, List<Lane> lanes)
        {
            InitializeComponent();
            Theme.ApplyDark(this);
            _def = def; _lanes = lanes;
            _tmin = -Before;
            _tmax = lanes.Max(l => l.Lap.LapTime.Value) + After;
            _sliderBusy = true; TimeSlider.Minimum = _tmin; TimeSlider.Maximum = _tmax; _sliderBusy = false;
            _ffmpeg = FfVideo.Ffmpeg();

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
            Status(string.Format(Inv, "両方とも、{0}の開始 (ラインの通過) を 0 秒にして同時に再生します。前後に {1} 秒の余白。スペース = 再生・一時停止、← → = コマ送り、1 / 2 / 3 / 0 = 音声 A / B / 両方 / なし。", kind, Before));
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

            // 音声 (それぞれの範囲を読み込んでおく)
            if (_lanes.Any(l => l.Vi != null && l.Vi.HasAudio))
            {
                Status("音声を読み込み中...  " + StatusText.Text);
                double dur = _tmax - _tmin;
                CancellationToken ct = _cancel.Token;
                try
                {
                    foreach (Lane l in _lanes)
                    {
                        if (l.Vi == null || !l.Vi.HasAudio) continue;
                        Lane ln = l;
                        ln.Pcm = await Task.Run(() => FfVideo.ReadAudio(ff, ln.Video.Path, ln.VideoT(_tmin), dur, ct));
                    }
                    if (!ct.IsCancellationRequested) _audio = new WaveOutPlayer(FfVideo.AudioRate, FillAudio);
                    Status(StatusText.Text.Replace("音声を読み込み中...  ", ""));
                }
                catch (Exception ex) { Status("音声を出せません: " + ex.Message); }
            }
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
                    if (LoopCheck.IsChecked == true) { SeekAll(_tmin); _clock.Play(); }
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
                    l.Shown = f.T + l.Video.T0 - l.Lap.StartT;
                    l.Reader.Recycle(f);
                    changed = true;
                }
            }
            if (changed || _clock.Playing) UpdateInfo();
        }

        void UpdateInfo()
        {
            TimeText.Text = VideoKit.Clock(_tau) + " / " + VideoKit.Clock(_tmax - After);
            _sliderBusy = true; TimeSlider.Value = Math.Max(_tmin, Math.Min(_tmax, _tau)); _sliderBusy = false;
            foreach (Lane l in _lanes)
            {
                double tau = double.IsNaN(l.Shown) ? _tau : l.Shown;
                l.Pane.RequestOverlay(l.Data != null && OverlayCheck.IsChecked == true ? l.Lap.StartT + tau : double.NaN);
            }
        }

        double Fps { get { Lane l = _lanes.FirstOrDefault(x => x.Vi != null); return l != null ? l.Vi.Fps : 30; } }

        void TogglePlay()
        {
            if (_clock.Playing) { _clock.Pause(); _tau = _clock.Now; PlayButton.Content = "▶ 再生"; }
            else
            {
                if (_tau >= _tmax - 0.05) SeekAll(_tmin);
                _clock.Set(_tau); _clock.Play(); PlayButton.Content = "⏸ 一時停止";
            }
        }

        void Play_Click(object sender, RoutedEventArgs e) { TogglePlay(); }
        void Restart_Click(object sender, RoutedEventArgs e) { SeekAll(_tmin); }

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
            short[] a = _lanes.Count > 0 ? _lanes[0].Pcm : null, b = _lanes.Count > 1 ? _lanes[1].Pcm : null;
            if (mode == 1) b = null;
            if (mode == 2) a = null;
            double ka = a != null && b != null ? 0.6 : 1.0;
            int i0 = (int)Math.Round((_audioCursor - _tmin) * FfVideo.AudioRate) * 2;
            for (int k = 0; k < frames * 2; k++)
            {
                int i = i0 + k;
                double v = 0;
                if (a != null && i >= 0 && i < a.Length) v += a[i] * ka;
                if (b != null && i >= 0 && i < b.Length) v += b[i] * ka;
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
                _map.Owner = this;
                _map.AttachClock(() => _tau);
                _map.Closed += (s, e) => _map = null;
                _map.Show();
            }
            catch (Exception ex) { Status("地図を開けません: " + ex.Message); }
        }

        void Map_Click(object sender, RoutedEventArgs e) { OpenMap(); }
    }
}
