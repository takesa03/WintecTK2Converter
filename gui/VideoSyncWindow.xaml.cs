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
    //  動画の 0 秒がログの何秒か (t0) が決まり、ログの付属情報 (ログ名.log.json) に保存する。
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
        readonly string[] _sectorNames;

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
            _def = def; _sess = sess; _gps = gps; _log = sess.FilePath; _sectorNames = sectorNames;
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
            st.Check(FadeCheck, "Fade");
            st.Text(FadeBox, "FadeSec");
            st.Check(BurnCheck, "Burn");
            st.Check(MergeCheck, "Merge");
            st.Check(OverlayCheck, "Overlay");
            st.Check(SubCheck, "Sub");
            OverlayPrefs.BindGraph(GraphCheck, () => _pane.RefreshOverlay());   // 速度のグラフ (書き出し・動画で比較と共通)
            FillVehicles();
            Settings.Track(this, "VideoSyncWindow", () => { st.SaveAll(); Settings.Set("VideoSync.VehicleName", SelectedVehicle()); });

            try
            {
                _layout = VideoKit.LoadLayout();
                _data = new OverlayData(def, sess, sectorNames, gps, VideoKit.Options(_layout), 600, 600);
            }
            catch (Exception ex) { Status("表示を重ねる準備ができません: " + ex.Message); }
            UpdateFitText();
            UpdateSubText();

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
            Closed += (s, e) => { CompositionTarget.Rendering -= OnRender; if (_cancel != null) _cancel.Cancel(); _pane.StopOverlay(); if (_reader != null) _reader.Dispose(); if (_subReader != null) _subReader.Dispose(); };
        }

        void Status(string s) { StatusText.Text = s; }

        /// <summary>重ねる表示を今のレイアウトで描き直す (動画を開いたとき・レイアウトを変えたとき)</summary>
        void RestartOverlay()
        {
            if (_data == null || _vi == null || _layout == null) return;
            OverlayLayout lay = _layout; OverlayData d = _data; double fps = _vi.Fps;
            _pane.StartOverlay((w, h) => new OverlayRenderer(lay, d, w, h), (r, t) => d.At(t, fps, r.TrailFrames), (double)_vi.Width / _vi.Height);
            _pane.RefreshOverlay();
        }

        // ---------------- 動画の選択 ----------------

        bool _fill;
        void FillVideos(string select, bool open = true)
        {
            _fill = true;
            VideoCombo.Items.Clear();
            // このセッションに重なる動画を先に
            foreach (VideoStore.Entry v in VideoStore.LoadMain(_log).OrderByDescending(v => v.T0 < _sess.EndT + 60 && v.EndT > _sess.StartT - 60).ThenBy(v => v.T0))
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
            CloseSub();
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
            RestartOverlay();
            _sliderBusy = true; TimeSlider.Maximum = _vi.Duration; _sliderBusy = false;
            // はじめは、最初の周回の開始の所
            double t = _laps.Count > 0 ? _laps[0].StartT - _t0 : 0;
            SeekTo(Math.Max(0, Math.Min(_vi.Duration - 0.1, t)));
            UpdateSyncText();
            LoadSubFor();
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
            SeekSub();
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
            RenderSub();
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
            double old = _t0;
            _t0 = t0;
            SubFollowMain(old);
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
            bool burn = BurnCheck.IsChecked == true;
            double fade = FadeCheck.IsChecked == true ? Num(FadeBox, 1) : 0;
            OverlayLayout layout = _layout;
            if (burn && layout == null)
            {
                MessageBox.Show(this, "表示のレイアウトを読めないので、焼き込めません。", "焼き込めません", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            VideoInfo vi = _vi;
            string src = _video; double t0 = _t0; double dur = _vi.Duration;
            // サブ動画は表示を焼き込むときだけ入れる (焼き込まない切り出しは比較の画面で使い、表示もサブも上から重ねるため)
            bool withSub = burn && _subPath != null && SubCheck.IsChecked == true && SubLayout().Show;
            string subPath = _subPath; VideoInfo subVi = _subVi; double subT0 = _subT0; double[] subCrop = (double[])_subCrop.Clone(); SubVideoLayout subLay = SubLayout();
            string dir = Path.GetDirectoryName(src), stem = Path.GetFileNameWithoutExtension(src);
            // 切り出す単位: 1 周ずつ、または続いている周回 (前の周回の終わり = 次の周回の始まり) をひとまとめ
            var groups = new List<List<LapRecord>>();
            bool merge = MergeCheck.IsChecked == true;
            foreach (LapRecord r in sel)
            {
                List<LapRecord> last = groups.Count > 0 ? groups[groups.Count - 1] : null;
                if (merge && last != null && r.StartT - last[last.Count - 1].EndT <= 0.5) last.Add(r);
                else groups.Add(new List<LapRecord> { r });
            }
            // (Item1 = 中心の周回。まとめたときは null、Item2 = 動画の開始秒、Item3 = 長さ、Item4 = 出力先、Item5 = 名前、Item6 = 周回の範囲)
            var jobs = new List<Tuple<LapRecord, double, double, string, string, Tuple<double, double>>>();
            List<string> skipped = new List<string>();
            foreach (List<LapRecord> g in groups)
            {
                LapRecord f = g[0], l = g[g.Count - 1];
                string lab = VideoKit.Label(_sess, f);
                if (g.Count > 1) lab += "-" + VideoKit.Label(_sess, l).Substring(("S" + _sess.No + "-").Length);
                double a = f.StartT - before - t0, b = l.EndT + after - t0;
                if (b <= 0 || a >= dur) { skipped.Add(lab); continue; }
                a = Math.Max(0, a); b = Math.Min(dur, b);
                string file = stem + "_" + VideoKit.Label(_sess, f).Replace("-", "_") + (g.Count > 1 ? "-" + VideoKit.Label(_sess, l).Substring(("S" + _sess.No + "-").Length) : "");
                jobs.Add(Tuple.Create(g.Count == 1 ? f : null, a, b - a, Path.Combine(dir, file + (burn ? "_overlay" : "") + ".mp4"), lab, Tuple.Create(f.StartT, l.EndT)));
            }
            if (jobs.Count == 0) { Status("選んだ周回は、この動画に映っていません: " + string.Join(", ", skipped)); return; }
            List<string> exist = jobs.Where(j => File.Exists(j.Item4)).Select(j => Path.GetFileName(j.Item4)).ToList();
            if (exist.Count > 0 && MessageBox.Show(this, "次のファイルは既にあります。上書きしますか？\n" + string.Join("\n", exist), "上書きの確認", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;

            _cancel = new CancellationTokenSource();
            CancellationToken ct = _cancel.Token;
            ClipButton.IsEnabled = false; CancelButton.IsEnabled = true;
            string ff = _ffmpeg;
            int done = 0, subUsed = 0;
            try
            {
                for (int k = 0; k < jobs.Count; k++)
                {
                    var j = jobs[k];
                    int kk = k;
                    Status(string.Format(Inv, "切り出し中 ({0}/{1}): {2}", k + 1, jobs.Count, Path.GetFileName(j.Item4)));
                    Action<double> prog = p => Dispatcher.BeginInvoke(new Action(() => Progress.Value = (kk + p) / jobs.Count));
                    string subLabel = " (表示入り)";
                    if (burn)
                    {
                        // その周回 (走行) を中心にした表示を、映像と同じ大きさ・コマ数で描いて重ねる
                        // まとめた周回は、セッション全体の表示から必要な時刻だけを描く
                        LapRecord lap = j.Item1;
                        double before2 = j.Item6.Item1 - (t0 + j.Item2) + 1, after2 = (t0 + j.Item2 + j.Item3) - j.Item6.Item2 + 1;
                        OverlayData d = new OverlayData(_def, _sess, _sectorNames, _gps, VideoKit.Options(layout), Math.Max(1, before2), Math.Max(1, after2), lap);
                        double fps = vi.Fps, clipT0 = t0 + j.Item2;
                        int total = (int)Math.Ceiling(j.Item3 * fps) + 1;
                        int w = vi.Width, h = vi.Height;
                        SubClip sc = withSub ? SubClip.Make(subPath, subVi, subCrop, subLay, w, h, clipT0 - subT0, j.Item3) : null;
                        if (sc != null) subUsed++;
                        await Task.Run(() =>
                        {
                            using (OverlayFrameSource ov = new OverlayFrameSource(() => new OverlayRenderer(layout, d, w, h),
                                       (r, n) => d.At(clipT0 + n / fps, fps, r.TrailFrames), total, ct))
                                FfVideo.ExportClip(ff, src, j.Item2, j.Item3, j.Item4, audio, ov, w, h, fps, sc, prog, ct, fade);
                        });
                        if (sc != null) subLabel = " (表示・サブ動画入り)";
                    }
                    else
                        await Task.Run(() => FfVideo.ExportClip(ff, src, j.Item2, j.Item3, j.Item4, audio, prog, ct, fade));
                    VideoStore.Put(_log, new VideoStore.Entry { Path = j.Item4, T0 = t0 + j.Item2, Duration = j.Item3, ClipOf = src, Label = j.Item5 + (burn ? subLabel : ""), Overlay = burn });
                    done++;
                }
                Progress.Value = 1;
                Status(string.Format(Inv, "{0} 本を切り出しました ({1})。", done, dir) + (subUsed > 0 ? string.Format(Inv, "サブ動画は {0} 本に入れました。", subUsed) : "") + (skipped.Count > 0 ? "  動画に映っていない周回: " + string.Join(", ", skipped) : ""));
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

        // ---------------- 回転数・アクセルの解析 ----------------

        /// <summary>
        /// 「車」の一覧: カートと車両のセット (CarPresets)。最初に選ぶのは、このログの解析結果の車 → 前に選んだもの → カート
        /// </summary>
        void FillVehicles()
        {
            VehicleCombo.Items.Clear();
            VehicleCombo.Items.Add(new ComboBoxItem { Content = "カート (直結)", Tag = "kart" });
            foreach (CarPreset p in CarPresets.All())
                VehicleCombo.Items.Add(new ComboBoxItem { Content = p.Name + " (変速機付き)", Tag = p.Name });
            string want = null;
            EngineStore.FileData old = _log != null ? EngineStore.Load(_log) : null;
            if (old != null) want = old.IsGeared ? (string.IsNullOrEmpty(old.CarModel) ? CarPresets.BuiltIn[0].Name : old.CarModel) : "kart";
            if (want == null) want = Settings.Get("VideoSync.VehicleName");
            if (want == null && Settings.Get("VideoSync.Vehicle") == "1") want = CarPresets.BuiltIn[0].Name;   // 前の版の「変速機付き」
            int idx = 0;
            for (int i = 0; i < VehicleCombo.Items.Count; i++)
                if ((string)((ComboBoxItem)VehicleCombo.Items[i]).Tag == want) idx = i;
            VehicleCombo.SelectedIndex = idx;
        }

        // ---------------- 心拍 (FIT ファイル) ----------------

        async void Fit_Click(object sender, RoutedEventArgs e)
        {
            if (_data == null) { Status("表示を重ねる準備ができていないので、心拍を合わせられません。"); return; }
            OpenFileDialog d = new OpenFileDialog { Filter = "FIT ファイル (*.fit)|*.fit|すべて (*.*)|*.*", Title = "心拍の FIT ファイルを開く" };
            Settings.InitialDir(d, "FitDir");
            if (d.ShowDialog(this) != true) return;
            Settings.RememberDir("FitDir", d.FileName);
            FitFile fit;
            try { fit = FitReader.Load(d.FileName); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "FIT ファイルを読めません", MessageBoxButton.OK, MessageBoxImage.Error); return; }
            HeartStore.Entry en = HeartStore.FromFit(fit, _sess, 0);
            if (en.T.Length == 0) { MessageBox.Show(this, "この FIT ファイルには心拍の記録がありません。", "心拍がありません", MessageBoxButton.OK, MessageBoxImage.Information); return; }

            // 時計のずれ: 腕時計の時計は日付ごと狂っていることがあるので、FIT の時刻は使わず、
            // このセッションの前後 10 分の中で、速度の形がいちばん合う所を探す。
            // セッションが FIT より短い (GPS のずれで周回が取れずセッションが途中で切れた など) ときは、
            // FIT が丸ごと入るように、セッションの中央から FIT の長さの半分 + 10 分まで広げる
            OverlayData data = _data;
            double mid = (_sess.StartT + _sess.EndT) / 2;
            double half = Math.Max(_sess.EndT - _sess.StartT, en.To - en.From) / 2 + 600;
            double from = Math.Max(_gps[0].T, mid - half), to = Math.Min(_gps[_gps.Count - 1].T, mid + half);
            FitButton.IsEnabled = false;
            Status("FIT の速度とログの速度を比べています...");
            List<HeartStore.Candidate> cands;
            try { cands = await Task.Run(() => HeartStore.EstimateOffsets(fit, data.SpeedAt, from, to, _sess.StartT, _sess.EndT)); }
            finally { FitButton.IsEnabled = true; }

            if (cands.Count == 0)
            {
                // 速度で合わせられない (FIT に速度が無い・短い)。FIT の時刻のまま、セッションに重なるかだけ見る
                if (en.From > _sess.EndT || en.To < _sess.StartT)
                {
                    Func<double, string> dt = t => new DateTime(2000, 1, 1).AddSeconds(t).AddHours(9).ToString("yyyy/MM/dd HH:mm:ss", Inv);
                    MessageBox.Show(this, string.Format(Inv, "FIT ファイルに速度の記録が無い (または短い) ので速度で合わせられず、時刻もこのセッションに重なっていません。\n\nFIT:\t\t{0} 〜 {1}\nこのセッション:\t{2} 〜 {3}\n\n別の日・別の時間の記録ではないか確かめてください。",
                        dt(en.From), dt(en.To), dt(_sess.StartT), dt(_sess.EndT)), "時刻を合わせられません", MessageBoxButton.OK, MessageBoxImage.Warning);
                    Status("");
                    return;
                }
                Status("FIT の速度で時計のずれを求められなかったので、FIT の時刻のまま保存しました。映像と見比べて、下のボタンで直してください。");
            }
            else
            {
                HeartStore.Candidate c = cands[0];
                en.Offset = c.Offset; en.AutoOffset = c.Offset; en.Candidates = cands;
                Status(string.Format(Inv, "速度の形で合わせました: FIT の先頭 = {0} (腕時計の時計のずれ {1})。{2} に保存しました。{3}",
                    VideoKit.Local(en.T[0] + c.Offset), FmtOffset(c.Offset), Path.GetFileName(HeartStore.SidecarPath(_log)),
                    c.Corr < 0.5 || c.Agree * 2 <= c.Windows ? "合い方が弱いので、ほかの候補も見比べてください。" : ""));
            }
            if (!PutHeart(en)) return;
            HeartChanged();
        }

        /// <summary>ずれの表示 (腕時計の時計が日単位で狂っていることがあるので、日・時刻で)</summary>
        static string FmtOffset(double off)
        {
            string sign = off < 0 ? "−" : "+";
            TimeSpan ts = TimeSpan.FromSeconds(Math.Abs(off));
            if (ts.TotalSeconds < 60) return string.Format(Inv, "{0}{1:0.0} 秒", sign, ts.TotalSeconds);
            return string.Format(Inv, "{0}{1}{2:hh\\:mm\\:ss\\.f}", sign, ts.Days > 0 ? ts.Days + " 日 " : "", ts);
        }

        void FitNudge_Click(object sender, RoutedEventArgs e)
        {
            HeartStore.Entry en = HeartStore.Get(_log, _sess);
            if (en == null) { Status("先に「FIT を開く...」で心拍のファイルを選んでください。"); return; }
            string tag = (string)((Button)sender).Tag;
            if (tag == "auto")
            {
                if (!en.AutoOffset.HasValue) { Status("自動で求めたずれがありません (速度で合わせられなかった FIT です)。"); return; }
                en.Offset = en.AutoOffset.Value;
            }
            else en.Offset = Math.Round(en.Offset + double.Parse(tag, Inv), 1);
            if (!PutHeart(en)) return;
            HeartChanged();
        }

        void FitCand_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_fitFill) return;
            ComboBoxItem it = FitCandCombo.SelectedItem as ComboBoxItem;
            HeartStore.Entry en = HeartStore.Get(_log, _sess);
            if (it == null || en == null) return;
            HeartStore.Candidate c = (HeartStore.Candidate)it.Tag;
            en.Offset = c.Offset; en.AutoOffset = c.Offset;
            if (!PutHeart(en)) return;
            Status("候補を切り替えました: FIT の先頭 = " + VideoKit.Local(en.T[0] + c.Offset));
            HeartChanged();
        }

        void FitRemove_Click(object sender, RoutedEventArgs e)
        {
            try { HeartStore.Remove(_log, _sess); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "心拍を外せません", MessageBoxButton.OK, MessageBoxImage.Error); return; }
            Status("このセッションの心拍を外しました (heart\\ にコピーした FIT は残しています)。");
            HeartChanged();
        }

        /// <summary>心拍を保存する (FIT はログの横の heart\ にコピー)。できなければ知らせて false</summary>
        bool PutHeart(HeartStore.Entry en)
        {
            try { HeartStore.Put(_log, en); return true; }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "心拍を保存できません", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        /// <summary>心拍 (log.json の heart) を変えたあと: 表示を読み直す</summary>
        void HeartChanged()
        {
            if (_data != null) _data.ReloadHeart();
            UpdateFitText();
            _pane.RefreshOverlay();
        }

        bool _fitFill;
        void UpdateFitText()
        {
            HeartStore.Entry en = HeartStore.Get(_log, _sess);
            _fitFill = true;
            FitCandCombo.Items.Clear();
            if (en != null && en.Candidates != null)
                foreach (HeartStore.Candidate c in en.Candidates)
                {
                    ComboBoxItem it = new ComboBoxItem { Content = HeartStore.CandidateText(c, en.T[0]), Tag = c };
                    FitCandCombo.Items.Add(it);
                    if (en.AutoOffset.HasValue && Math.Abs(c.Offset - en.AutoOffset.Value) < 0.05) FitCandCombo.SelectedItem = it;
                }
            FitCandCombo.IsEnabled = FitCandCombo.Items.Count > 1;
            _fitFill = false;
            if (en == null) { FitText.Text = "心拍なし (表示しません)"; return; }
            double lo = double.MaxValue, hi = 0;
            for (int i = 0; i < en.T.Length; i++)
            {
                double t = en.T[i] + en.Offset;
                if (t < _sess.StartT || t > _sess.EndT) continue;
                lo = Math.Min(lo, en.Bpm[i]); hi = Math.Max(hi, en.Bpm[i]);
            }
            FitText.Text = string.Format(Inv, "{0}  {1}\nFIT の先頭 = {2}\n腕時計のずれ {3}{4}\n{5}",
                en.Device, Path.GetFileName(en.Fit), VideoKit.Local(en.T[0] + en.Offset), FmtOffset(en.Offset),
                en.AutoOffset.HasValue ? (Math.Abs(en.Offset - en.AutoOffset.Value) < 0.05 ? " (自動)" : string.Format(Inv, " (自動から {0:+0.0;-0.0;0.0} 秒)", en.Offset - en.AutoOffset.Value)) : " (自動で求められず)",
                hi > 0 ? string.Format(Inv, "このセッションの心拍 {0:0}〜{1:0} bpm", lo, hi) : "このセッションの時間に心拍の記録がありません");
        }
        string SelectedVehicle()
        {
            ComboBoxItem it = VehicleCombo.SelectedItem as ComboBoxItem;
            return it != null ? (string)it.Tag : "kart";
        }

        async void Engine_Click(object sender, RoutedEventArgs e)
        {
            if (_reader == null || double.IsNaN(_t0) || _data == null)
            {
                MessageBox.Show(this, "先に動画を開いて、時刻を合わせてください。", "動画の時刻が未設定です", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (!_vi.HasAudio) { Status("この動画には音声がありません。"); return; }
            string ff = _ffmpeg, video = _video, log = _log;
            double t0 = _t0;
            // 解析する範囲: このセッションの走っている所だけ (サーキットはセッションの前後 60 秒まで)。
            // ジムカーナは 1 ログ = 1 セッションで、動画全体 (待ち時間・パドック) に広がるので、動画に重なる走行の最初のスタートの 20 秒前〜最後の走行の 15 秒後
            // サーキットは、最初のラインより前のピットを出る所 (止まっていた所) からも含める
            double from = Math.Max(Math.Min(_sess.StartT - 60, _data != null ? _data.DepartureBefore(_sess.StartT) - 10 : double.MaxValue), t0),
                   to = Math.Min(_sess.EndT + 60, t0 + _vi.Duration);
            if (_def.IsGymkhana)
            {
                double vEnd = t0 + _vi.Duration;
                List<LapRecord> runs = _laps.Where(r => r.IsGymRun && r.EndT > t0 && r.StartT < vEnd).ToList();
                if (runs.Count == 0) { Status("動画の時間に重なる走行がありません。"); return; }
                from = Math.Max(runs.Min(r => r.StartT) - 20, t0);
                to = Math.Min(runs.Max(r => double.IsNaN(r.EndT) ? r.StartT + 60 : r.EndT) + 15, vEnd);   // DNF で終わりが無い走行は 60 秒とみなす
            }
            if (to - from < 5) { Status("動画がこのセッションの時間に重なっていません。"); return; }
            OverlayData d = _data;
            // 車の種類: 前の解析結果と同じ車両のセットなら、その値 (解析結果の画面で直した値) を引き継ぐ。違う車ならセットの値に切り替える
            string sel = SelectedVehicle();
            EngineStore.FileData car = new EngineStore.FileData();
            EngineStore.FileData old = EngineStore.Load(log);
            if (old != null) EngineStore.CopyCar(old, car);
            if (sel == "kart") car.Vehicle = "kart";
            else
            {
                string oldModel = old != null && old.IsGeared ? (string.IsNullOrEmpty(old.CarModel) ? CarPresets.BuiltIn[0].Name : old.CarModel) : null;
                CarPreset p = CarPresets.Find(sel);
                if (oldModel == sel || p == null) { car.SetGearedDefaults(); car.CarModel = sel; }
                else { p.ApplyTo(car); car.MaxGear = 0; car.MaxGearManual = false; }
            }
            _cancel = new CancellationTokenSource();
            CancellationToken ct = _cancel.Token;
            EngineButton.IsEnabled = false; ClipButton.IsEnabled = false; CancelButton.IsEnabled = true;
            try
            {
                EngineAnalyzer.Work w = await Task.Run(() => EngineAnalyzer.Analyze(ff, video, t0, from, to, d.SpeedAt, car,
                    (msg, p) => Dispatcher.BeginInvoke(new Action(() => { Progress.Value = p; EngineText.Text = msg; })), ct));
                EngineStore.FileData file = EngineStore.Put(log, w.Seg, car);
                if (file.IsGeared)
                {
                    int known = w.Seg.Rpm.Count(x => x > 0);
                    EngineText.Text = string.Format(Inv, "音から回転数が取れたコマ {0:0} %", 100.0 * known / Math.Max(1, w.Seg.Rpm.Length));
                    Status(string.Format(Inv, "解析しました (変速機付き)。{0} に保存しました。ギア比などは開いた画面で確かめてください。",
                        Path.GetFileName(EngineStore.SidecarPath(log))));
                }
                else
                {
                    EngineText.Text = string.Format(Inv, "{0:0.0} rpm / (km/h)", file.RpmPerKmh);
                    Status(string.Format(Inv, "解析しました: 回転数 = {0:0.0} × 速度 (100 km/h で {1:0} rpm)。{2} に保存しました。",
                        file.RpmPerKmh, file.RpmPerKmh * 100, Path.GetFileName(EngineStore.SidecarPath(log))));
                }
                EngineWindow win = new EngineWindow(log, w, file);
                win.Show();
            }
            catch (OperationCanceledException) { Status("解析を中止しました。"); EngineText.Text = ""; }
            catch (Exception ex)
            {
                Status("解析に失敗しました: " + ex.Message);
                MessageBox.Show(this, ex.Message, "解析に失敗しました", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                EngineButton.IsEnabled = true; ClipButton.IsEnabled = true; CancelButton.IsEnabled = false;
                _cancel = null;
            }
        }
    }

}
