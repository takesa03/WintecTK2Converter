using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace Tk2Gui
{
    // =====================================================================
    //  走行動画の時刻合わせ・切り出し
    //  動画をコマ送りして、ある周回の開始 (ラインの通過) のコマで「開始」を押すと、
    //  動画の 0 秒がログの何秒か (t0) が決まり、ログ名.videos.json に保存する。
    //  選んだ周回を、前後の余白付きで MP4 に切り出せる (切り出した動画も t0 付きで登録)
    // =====================================================================
    public partial class VideoSyncWindow : Window
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        readonly CourseDefinition _def;
        readonly SessionRecord _sess;
        readonly List<GpsSample> _gps;
        readonly string _log;
        readonly List<LapRecord> _laps;
        readonly VideoPane _pane = new VideoPane();
        readonly PlayClock _clock = new PlayClock();
        readonly string _ffmpeg;

        string _video;
        VideoInfo _vi;
        FrameReader _reader;
        double _t0 = double.NaN;               // 動画の 0 秒 = ログの t0 秒
        double _t;                              // 動画の時刻 [s]
        double _shownT = double.NaN;
        bool _sliderBusy;
        OverlayData _data;
        OverlayLayout _layout;
        CancellationTokenSource _cancel;
        DateTime _lastSeek;

        public VideoSyncWindow(CourseDefinition def, SessionRecord sess, string[] sectorNames, List<GpsSample> gps)
        {
            InitializeComponent();
            Theme.ApplyDark(this);
            _def = def; _sess = sess; _gps = gps; _log = sess.FilePath;
            PaneHost.Children.Add(_pane);
            _ffmpeg = FfVideo.Ffmpeg();
            Title = "走行動画の時刻合わせ・切り出し - Session " + sess.No + (sess.Driver.Length > 0 ? " (" + sess.Driver + ")" : "") + "  " + App.VersionText;

            _laps = sess.Laps.OrderBy(l => l.StartT).ToList();
            if (def.IsGymkhana)
            {
                LapHeader.Text = "走行 (Ctrl / Shift で複数選択)";
                SyncButton.Content = "このコマが選んだ走行のスタート";
                SyncHelp.Text = SyncHelp.Text.Replace("周回の開始 (コントロールラインを通った瞬間)", "走行のスタート (スタートラインを通った瞬間)").Replace("周回", "走行");
                ClipGroup.Header = "選んだ走行を切り出す";
            }
            foreach (LapRecord r in _laps)
            {
                double? tm = r.LapTime ?? r.PartialTime;
                LapList.Items.Add(new ListBoxItem
                {
                    Content = string.Format(Inv, "{0,-8} {1,9}  {2}", VideoKit.Label(sess, r), tm.HasValue ? LapAnalyzer.FormatTime(tm) : "", VideoKit.Local(r.StartT).Substring(0, 8)),
                    Tag = r
                });
            }

            Settings.Binder st = new Settings.Binder("VideoSync");
            st.Text(BeforeBox, "Before");
            st.Text(AfterBox, "After");
            st.Check(AudioCheck, "Audio");
            st.Check(OverlayCheck, "Overlay");
            Settings.Track(this, "VideoSyncWindow", st.SaveAll);

            try
            {
                _layout = VideoKit.LoadLayout();
                _data = new OverlayData(def, sess, sectorNames, gps, VideoKit.Options(_layout), 600, 600);
            }
            catch (Exception ex) { Status("表示を重ねる準備ができません: " + ex.Message); }

            if (_ffmpeg == null)
            {
                _pane.Message = "ffmpeg が見つかりません。「動画素材を書き出す...」の画面で ffmpeg.exe の場所を指定してください (winget install Gyan.FFmpeg で入れられます)";
            }
            else
            {
                _pane.Message = "「開く...」で走行動画を選んでください";
                FillVideos(null);
            }

            PreviewKeyDown += OnKey;
            Loaded += (s, e) => CompositionTarget.Rendering += OnRender;
            Closed += (s, e) => { CompositionTarget.Rendering -= OnRender; if (_cancel != null) _cancel.Cancel(); _pane.StopOverlay(); if (_reader != null) _reader.Dispose(); };
        }

        void Status(string s) { StatusText.Text = s; }

        // ---------------- 動画の選択 ----------------

        bool _fill;
        void FillVideos(string select, bool open = true)
        {
            _fill = true;
            VideoCombo.Items.Clear();
            // このセッションに重なる動画を先に
            foreach (VideoStore.Entry v in VideoStore.Load(_log).OrderByDescending(v => v.T0 < _sess.EndT + 60 && v.EndT > _sess.StartT - 60).ThenBy(v => v.T0))
            {
                string txt = Path.GetFileName(v.Path) + "   (" + VideoKit.Local(v.T0).Substring(0, 8) + " 〜 " + VideoKit.Local(v.EndT).Substring(0, 8) + ")" + (File.Exists(v.Path) ? "" : "  ※ ファイルがありません");
                VideoCombo.Items.Add(new ComboBoxItem { Content = txt, Tag = v.Path });
            }
            _fill = false;
            int idx = -1;
            for (int i = 0; i < VideoCombo.Items.Count; i++)
                if (string.Equals((string)((ComboBoxItem)VideoCombo.Items[i]).Tag, select, StringComparison.OrdinalIgnoreCase)) idx = i;
            if (idx < 0 && select == null && VideoCombo.Items.Count > 0) idx = 0;
            _fill = !open;
            if (idx >= 0) VideoCombo.SelectedIndex = idx;
            _fill = false;
        }

        void Browse_Click(object sender, RoutedEventArgs e)
        {
            if (_ffmpeg == null) return;
            OpenFileDialog d = new OpenFileDialog { Filter = "動画 (*.mp4;*.mov;*.mkv;*.avi;*.mts;*.m4v;*.webm)|*.mp4;*.mov;*.mkv;*.avi;*.mts;*.m4v;*.webm|すべて (*.*)|*.*", Title = "走行動画を開く" };
            Settings.InitialDir(d, "VideoDir");
            if (d.ShowDialog(this) != true) return;
            Settings.RememberDir("VideoDir", d.FileName);
            string p = d.FileName;
            if (VideoStore.Get(_log, p) == null)
            {
                // まだ時刻合わせしていない: 仮に、動画の先頭 = このセッションの最初の周回の 30 秒前 とする
                VideoInfo vi;
                try { vi = FfVideo.Probe(_ffmpeg, p); }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "動画を開けません", MessageBoxButton.OK, MessageBoxImage.Error); return; }
                double guess = (_laps.Count > 0 ? _laps[0].StartT : _sess.StartT) - 30;
                VideoStore.Put(_log, new VideoStore.Entry { Path = p, T0 = guess, Duration = vi.Duration });
                Status("まだ時刻合わせをしていない動画です。仮に「動画の先頭 = 最初の周回の 30 秒前」としました。周回を選んで、開始のコマで「このコマが選んだ周回の開始」を押してください。");
            }
            FillVideos(p);
        }

        void Video_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_fill) return;
            ComboBoxItem it = VideoCombo.SelectedItem as ComboBoxItem;
            if (it == null) return;
            OpenVideo((string)it.Tag);
        }

        void OpenVideo(string path)
        {
            _clock.Pause(); PlayButton.Content = "▶ 再生";
            if (_reader != null) { _reader.Dispose(); _reader = null; }
            _pane.ClearFrame();
            VideoStore.Entry en = VideoStore.Get(_log, path);
            if (en == null) return;
            if (!File.Exists(path)) { _pane.Message = "動画のファイルがありません:\n" + path; return; }
            try
            {
                _vi = FfVideo.Probe(_ffmpeg, path);
            }
            catch (Exception ex) { _pane.Message = ex.Message; return; }
            _video = path; _t0 = en.T0;
            if (Math.Abs(en.Duration - _vi.Duration) > 0.01) { en.Duration = _vi.Duration; VideoStore.Put(_log, en); }
            _pane.Message = null;
            _reader = new FrameReader(_ffmpeg, path, _vi, 1280);
            if (_data != null)
            {
                OverlayLayout lay = _layout; OverlayData d = _data; double fps = _vi.Fps;
                _pane.StartOverlay((w, h) => new OverlayRenderer(lay, d, w, h), (r, t) => d.At(t, fps, r.TrailFrames), (double)_vi.Width / _vi.Height);
            }
            _sliderBusy = true; TimeSlider.Maximum = _vi.Duration; _sliderBusy = false;
            // はじめは、最初の周回の開始の所
            double t = _laps.Count > 0 ? _laps[0].StartT - _t0 : 0;
            SeekTo(Math.Max(0, Math.Min(_vi.Duration - 0.1, t)));
            UpdateSyncText();
        }

        // ---------------- 再生 ----------------

        void SeekTo(double t)
        {
            if (_reader == null) return;
            t = Math.Max(0, Math.Min(_vi.Duration - 0.5 / _vi.Fps, t));
            _t = t; _clock.Set(t);
            _shownT = double.NaN;
            _reader.Seek(t);
            _lastSeek = DateTime.Now;
            UpdateInfo();
        }

        void OnRender(object sender, EventArgs e)
        {
            if (_reader == null) return;
            if (_clock.Playing)
            {
                _t = _clock.Now;
                if (_t >= _vi.Duration) { _t = _vi.Duration; _clock.Pause(); _clock.Set(_t); PlayButton.Content = "▶ 再生"; }
            }
            bool needSeek;
            FrameReader.Frame f = _reader.Get(_t, out needSeek);
            if (needSeek && (DateTime.Now - _lastSeek).TotalSeconds > 0.3) { _reader.Seek(_t); _lastSeek = DateTime.Now; }
            if (f != null)
            {
                _pane.ShowFrame(_reader, f);
                _shownT = f.T;
                _reader.Recycle(f);
                UpdateInfo();
            }
            else if (_clock.Playing) UpdateInfo();
        }

        void UpdateInfo()
        {
            double vt = double.IsNaN(_shownT) ? _t : _shownT;
            double lt = _t0 + vt;
            string lap = "";
            LapRecord cur = _laps.LastOrDefault(r => r.StartT <= lt);
            if (cur != null && lt <= cur.EndT + 0.001) lap = "  " + VideoKit.Label(_sess, cur) + " " + VideoKit.Clock(lt - cur.StartT);
            TimeText.Text = string.Format(Inv, "動画 {0} / {1}    ログ {2}{3}", VideoKit.Clock(vt), VideoKit.Clock(_vi != null ? _vi.Duration : 0), VideoKit.Local(lt), lap);
            _sliderBusy = true; TimeSlider.Value = Math.Min(TimeSlider.Maximum, vt); _sliderBusy = false;
            _pane.RequestOverlay(_data != null && OverlayCheck.IsChecked == true && !double.IsNaN(_t0) ? lt : double.NaN);
        }

        void Play_Click(object sender, RoutedEventArgs e) { TogglePlay(); }

        void TogglePlay()
        {
            if (_reader == null) return;
            if (_clock.Playing) { _clock.Pause(); _t = _clock.Now; PlayButton.Content = "▶ 再生"; }
            else
            {
                if (_t >= _vi.Duration - 0.1) SeekTo(0);
                _clock.Set(_t); _clock.Play(); PlayButton.Content = "⏸ 一時停止";
            }
        }

        void Step(string tag)
        {
            if (_reader == null) return;
            bool playing = _clock.Playing;
            if (playing) { _clock.Pause(); _t = _clock.Now; PlayButton.Content = "▶ 再生"; }
            double cur = double.IsNaN(_shownT) ? _t : _shownT;
            if (tag == "f") { _t = cur + 1.0 / _vi.Fps; _clock.Set(_t); }             // 次のコマは読み出しの続き
            else if (tag == "-f") SeekTo(cur - 1.0 / _vi.Fps);
            else
            {
                double d = double.Parse(tag, Inv);
                if (d > 0 && d <= 1) { _t = cur + d; _clock.Set(_t); }
                else SeekTo(cur + d);
            }
        }

        void Step_Click(object sender, RoutedEventArgs e) { Step((string)((Button)sender).Tag); }

        void OnKey(object sender, KeyEventArgs e)
        {
            if (e.OriginalSource is TextBox) return;
            if (e.Key == Key.Space) { TogglePlay(); e.Handled = true; }
            else if (e.Key == Key.Right) { Step("f"); e.Handled = true; }
            else if (e.Key == Key.Left) { Step("-f"); e.Handled = true; }
        }

        void Slider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_sliderBusy || _reader == null) return;
            if (_clock.Playing) { _clock.Pause(); PlayButton.Content = "▶ 再生"; }
            SeekTo(TimeSlider.Value);
        }

        void LapList_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            LapRecord r = SelectedLaps().FirstOrDefault();
            if (r == null || _reader == null) return;
            if (_clock.Playing) { _clock.Pause(); PlayButton.Content = "▶ 再生"; }
            SeekTo(r.StartT - _t0);
        }

        List<LapRecord> SelectedLaps()
        {
            return LapList.SelectedItems.Cast<ListBoxItem>().Select(i => (LapRecord)i.Tag).OrderBy(r => r.StartT).ToList();
        }

        // ---------------- 時刻合わせ ----------------

        void SetT0(double t0, string why)
        {
            _t0 = t0;
            VideoStore.Entry en = VideoStore.Get(_log, _video);
            if (en != null) { en.T0 = t0; VideoStore.Put(_log, en); }
            UpdateSyncText();
            UpdateInfo();
            Status(why);
        }

        void UpdateSyncText()
        {
            if (_video == null || double.IsNaN(_t0)) { SyncText.Text = ""; return; }
            SyncText.Text = "動画の先頭 = ログ " + VideoKit.Local(_t0) + "\n動画の終わり = ログ " + VideoKit.Local(_t0 + (_vi != null ? _vi.Duration : 0));
        }

        void Sync_Click(object sender, RoutedEventArgs e)
        {
            if (_reader == null) return;
            List<LapRecord> sel = SelectedLaps();
            if (sel.Count != 1)
            {
                MessageBox.Show(this, "開始を合わせる周回を、1 つだけ選んでください。", "周回を選んでください", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            double vt = double.IsNaN(_shownT) ? _t : _shownT;
            LapRecord r = sel[0];
            SetT0(r.StartT - vt, string.Format(Inv, "{0} の開始 = 動画の {1} で合わせました。保存しました ({2})。", VideoKit.Label(_sess, r), VideoKit.Clock(vt), Path.GetFileName(VideoStore.SidecarPath(_log))));
        }

        void Nudge_Click(object sender, RoutedEventArgs e)
        {
            if (_reader == null || double.IsNaN(_t0)) return;
            string tag = (string)((Button)sender).Tag;
            double d = tag == "f" ? 1.0 / _vi.Fps : tag == "-f" ? -1.0 / _vi.Fps : double.Parse(tag, Inv);
            // 表示を動画に対して d 秒進める = 動画の同じコマに対応するログの時刻を d 秒後にする
            SetT0(_t0 + d, string.Format(Inv, "表示を {0:+0.000;-0.000} 秒動かしました。保存しました。", d));
        }

        void Overlay_Changed(object sender, RoutedEventArgs e) { if (IsLoaded) UpdateInfo(); }

        // ---------------- 切り出し ----------------

        static double Num(TextBox b, double def)
        {
            double v;
            return double.TryParse(b.Text, NumberStyles.Float, Inv, out v) && v >= 0 ? v : def;
        }

        async void Clip_Click(object sender, RoutedEventArgs e)
        {
            if (_reader == null || double.IsNaN(_t0)) return;
            List<LapRecord> sel = SelectedLaps();
            if (sel.Count == 0)
            {
                MessageBox.Show(this, "切り出す周回を選んでください (Ctrl や Shift を押しながらクリックで複数選択)。", "周回を選んでください", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            double before = Num(BeforeBox, 5), after = Num(AfterBox, 5);
            bool audio = AudioCheck.IsChecked == true && _vi.HasAudio;
            string src = _video; double t0 = _t0; double dur = _vi.Duration;
            string dir = Path.GetDirectoryName(src), stem = Path.GetFileNameWithoutExtension(src);
            var jobs = new List<Tuple<LapRecord, double, double, string>>();
            List<string> skipped = new List<string>();
            foreach (LapRecord r in sel)
            {
                double a = r.StartT - before - t0, b = r.EndT + after - t0;
                if (b <= 0 || a >= dur) { skipped.Add(VideoKit.Label(_sess, r)); continue; }
                a = Math.Max(0, a); b = Math.Min(dur, b);
                string lab = VideoKit.Label(_sess, r);
                jobs.Add(Tuple.Create(r, a, b - a, Path.Combine(dir, stem + "_" + lab.Replace("-", "_") + ".mp4")));
            }
            if (jobs.Count == 0) { Status("選んだ周回は、この動画に映っていません: " + string.Join(", ", skipped)); return; }
            List<string> exist = jobs.Where(j => File.Exists(j.Item4)).Select(j => Path.GetFileName(j.Item4)).ToList();
            if (exist.Count > 0 && MessageBox.Show(this, "次のファイルは既にあります。上書きしますか？\n" + string.Join("\n", exist), "上書きの確認", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;

            _cancel = new CancellationTokenSource();
            CancellationToken ct = _cancel.Token;
            ClipButton.IsEnabled = false; CancelButton.IsEnabled = true;
            string ff = _ffmpeg;
            int done = 0;
            try
            {
                for (int k = 0; k < jobs.Count; k++)
                {
                    var j = jobs[k];
                    int kk = k;
                    Status(string.Format(Inv, "切り出し中 ({0}/{1}): {2}", k + 1, jobs.Count, Path.GetFileName(j.Item4)));
                    await Task.Run(() => FfVideo.ExportClip(ff, src, j.Item2, j.Item3, j.Item4, audio,
                        p => Dispatcher.BeginInvoke(new Action(() => Progress.Value = (kk + p) / jobs.Count)), ct));
                    VideoStore.Put(_log, new VideoStore.Entry { Path = j.Item4, T0 = t0 + j.Item2, Duration = j.Item3, ClipOf = src, Label = VideoKit.Label(_sess, j.Item1) });
                    done++;
                }
                Progress.Value = 1;
                Status(string.Format(Inv, "{0} 本を切り出しました ({1})。", done, dir) + (skipped.Count > 0 ? "  動画に映っていない周回: " + string.Join(", ", skipped) : ""));
            }
            catch (OperationCanceledException) { Status(string.Format(Inv, "中止しました ({0} 本は切り出し済み)。", done)); }
            catch (Exception ex)
            {
                Status("切り出しに失敗しました: " + ex.Message);
                MessageBox.Show(this, ex.Message, "切り出しに失敗しました", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                ClipButton.IsEnabled = true; CancelButton.IsEnabled = false;
                _cancel = null;
                FillVideos(_video, false);
            }
        }

        void Cancel_Click(object sender, RoutedEventArgs e) { if (_cancel != null) _cancel.Cancel(); }
    }

}
