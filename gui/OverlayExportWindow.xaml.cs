using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace Tk2Gui
{
    /// <summary>動画素材 (速度計・G・コース図・ラップ表など) の書き出し画面。1 セッション分を書き出す。</summary>
    public partial class OverlayExportWindow : Window
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        readonly CourseDefinition _def;
        readonly SessionRecord _sess;
        readonly string[] _sectorNames;
        readonly List<GpsSample> _gps;
        OverlayLayout _layout;
        OverlayData _data;
        List<LapRecord> _runs = new List<LapRecord>();   // ジムカーナの走行 (無ければ空)
        LapRecord _previewRun;                            // プレビュー中の走行 (null = セッション全体)
        OverlayRenderer _preview;
        CancellationTokenSource _cancel;
        bool _ready;

        public OverlayExportWindow(CourseDefinition def, SessionRecord sess, string[] sectorNames, List<GpsSample> gps)
        {
            InitializeComponent();
            Theme.ApplyDark(this);
            _def = def; _sess = sess; _sectorNames = sectorNames; _gps = gps;
            Title = "動画素材の書き出し - Session " + sess.No + "  " + App.VersionText;
            foreach (FrameRate f in FrameRate.All) FpsCombo.Items.Add(f);
            FpsCombo.SelectedIndex = 3;   // 29.97

            // ジムカーナ: 走行ごとに書き出せるようにする (コース外の待ち時間は使わないので)
            _runs = sess.Laps.Where(l => l.IsGymRun).OrderBy(l => l.StartT).ToList();
            if (_runs.Count > 0)
            {
                RangeCombo.Items.Add("セッション全体 (1 ファイル)");
                RangeCombo.Items.Add("走行ごと (" + _runs.Count + " ファイル)");
                foreach (LapRecord r in _runs) RangeCombo.Items.Add("Heat " + r.Label + " だけ");
                RangeCombo.SelectedIndex = 1;
                RangePanel.Visibility = Visibility.Visible;
                BeforeLabel.Text = "走行の前";
            }

            Settings.Binder st = new Settings.Binder("Overlay");
            st.Text(LayoutBox, "Layout");
            st.Text(FfmpegBox, "Ffmpeg");
            st.Combo(SizeCombo, "Size");
            st.Combo(FpsCombo, "Fps");
            st.Text(BeforeBox, "Before");
            st.Text(AfterBox, "After");
            st.Combo(FormatCombo, "Format");
            OverlayPrefs.BindGraph(GraphCheck, UpdatePreview);   // 速度のグラフ (時刻合わせ・動画で比較と共通の設定)
            if (_runs.Count > 0) st.Custom("GymRange", () => RangeCombo.SelectedIndex <= 1 ? RangeCombo.SelectedIndex.ToString(Inv) : null,
                v => { int k; if (int.TryParse(v, out k) && k >= 0 && k <= 1) RangeCombo.SelectedIndex = k; });
            Settings.Track(this, "OverlayExportWindow", st.SaveAll);

            if (string.IsNullOrWhiteSpace(LayoutBox.Text) || !File.Exists(LayoutBox.Text)) LayoutBox.Text = OverlayLayout.DefaultPath;
            if (string.IsNullOrWhiteSpace(FfmpegBox.Text) || !File.Exists(FfmpegBox.Text)) FfmpegBox.Text = OverlayEncoder.FindFfmpeg(null) ?? "";

            SessionText.Text = string.Format(Inv, "Session {0}{1}   {2} 周   ベスト {3}   ({4})", sess.No,
                string.IsNullOrWhiteSpace(sess.Driver) ? "" : "  " + sess.Driver, sess.FullLaps, LapAnalyzer.FormatTime(sess.BestLap), sess.FileName);
            _ready = true;
            Loaded += (s, e) => Rebuild();
        }

        void Status(string s) { StatusText.Text = s; }

        void GetSize(out int w, out int h)
        {
            string tag = (SizeCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "1920x1080";
            string[] p = tag.Split('x');
            w = int.Parse(p[0], Inv); h = int.Parse(p[1], Inv);
        }

        double Num(TextBox tb, double def)
        {
            double d;
            return double.TryParse(tb.Text, NumberStyles.Float, Inv, out d) && d >= 0 ? d : def;
        }

        OverlayOptions MakeOptions()
        {
            OverlayOptions o = new OverlayOptions();
            if (_layout != null)
            {
                o.LapHoldSec = _layout["laptime"].Num("hold", 3);
                o.SectorHoldSec = _layout["sector"].Num("hold", 3);
                o.GSmoothSec = _layout["gmeter"].Num("smooth", 1.0);
                o.SpeedSmoothSec = _layout["speedometer"].Num("smooth", 2.0);
            }
            return o;
        }

        /// <summary>レイアウト・余白を読み直して、プレビューを作り直す</summary>
        void Rebuild()
        {
            if (!_ready) return;
            try
            {
                _layout = OverlayLayout.Load(LayoutBox.Text);
            }
            catch (Exception ex)
            {
                _layout = null; _preview = null; PreviewImage.Source = null;
                Status("レイアウトを読めません: " + ex.Message);
                return;
            }
            double keep = _data != null ? TimeSlider.Value : -1;
            LapRecord only = PreviewRun();
            if (only != _previewRun) keep = -1;      // 範囲を変えたら、はじめの位置から
            _previewRun = only;
            _data = new OverlayData(_def, _sess, _sectorNames, _gps, MakeOptions(), Num(BeforeBox, 10), Num(AfterBox, 10), only);
            int w, h; GetSize(out w, out h);
            _preview = new OverlayRenderer(_layout, _data, w, h);
            PreviewFrame.Width = w; PreviewFrame.Height = h;
            TimeSlider.Minimum = 0;
            TimeSlider.Maximum = _data.EndT - _data.StartT;
            // はじめは、最初の周回の途中あたりを見せる
            if (keep < 0)
            {
                double t0 = only != null ? only.StartT + 20 : _sess.Laps.Count > 1 ? _sess.Laps[1].StartT + 20 : _data.StartT + 30;
                keep = Math.Max(0, Math.Min(TimeSlider.Maximum, t0 - _data.StartT));
            }
            TimeSlider.Value = Math.Min(TimeSlider.Maximum, keep);
            UpdatePreview();
            string len = string.Format(Inv, "長さ {0}", OverlayData.FmtLap(_data.EndT - _data.StartT));
            if (RangeCombo.SelectedIndex == 1)
            {
                double sum = 0;
                foreach (LapRecord r in _runs) { OverlayData d = RunData(r); sum += d.EndT - d.StartT; }
                len = string.Format(Inv, "プレビューは {0}。{1} ファイルの合計 {2}", "Heat " + _runs[0].Label, _runs.Count, OverlayData.FmtLap(sum));
            }
            Status((_preview.Notice ?? "") + (_preview.Notice != null ? "\n" : "") +
                   string.Format(Inv, "{0}  (最高速 {1:0} km/h)", len, _data.MaxSpeedKmh));
        }

        /// <summary>範囲の選択: null = セッション全体。「走行ごと」のときは 1 本目をプレビューする</summary>
        LapRecord PreviewRun()
        {
            int k = RangeCombo.SelectedIndex;
            if (_runs.Count == 0 || k <= 0) return null;
            if (k == 1) return _runs[0];
            return k - 2 < _runs.Count ? _runs[k - 2] : null;
        }

        OverlayData RunData(LapRecord r)
        {
            return new OverlayData(_def, _sess, _sectorNames, _gps, MakeOptions(), Num(BeforeBox, 10), Num(AfterBox, 10), r);
        }

        void UpdatePreview()
        {
            if (_preview == null || _data == null) return;
            double t = _data.StartT + TimeSlider.Value;
            TimeText.Text = OverlayData.FmtLap(TimeSlider.Value);
            try
            {
                FrameRate fr = FpsCombo.SelectedItem as FrameRate ?? FrameRate.All[3];
                OverlayState st = _data.At(t, fr.Fps, _preview.TrailFrames);
                System.Windows.Media.Imaging.WriteableBitmap wb = new System.Windows.Media.Imaging.WriteableBitmap(_preview.RenderBitmap(st));
                wb.Freeze();
                PreviewImage.Source = wb;
            }
            catch (Exception ex) { Status("描画に失敗しました: " + ex.Message); }
        }

        void TimeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { UpdatePreview(); }
        void Option_Changed(object sender, SelectionChangedEventArgs e) { Rebuild(); }
        void Option_LostFocus(object sender, RoutedEventArgs e) { Rebuild(); }
        void Layout_LostFocus(object sender, RoutedEventArgs e) { Rebuild(); }
        void ReloadLayout_Click(object sender, RoutedEventArgs e) { Rebuild(); }
        void Ffmpeg_LostFocus(object sender, RoutedEventArgs e) { }

        void BrowseLayout_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog d = new OpenFileDialog { Filter = "レイアウト (*.ini)|*.ini|すべて (*.*)|*.*", Title = "レイアウトの ini を選ぶ" };
            try { d.InitialDirectory = Path.GetDirectoryName(LayoutBox.Text); } catch (Exception) { }
            if (d.ShowDialog(this) != true) return;
            LayoutBox.Text = d.FileName;
            Rebuild();
        }

        void BrowseFfmpeg_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog d = new OpenFileDialog { Filter = "ffmpeg.exe|ffmpeg.exe|exe (*.exe)|*.exe", Title = "ffmpeg.exe を選ぶ" };
            if (d.ShowDialog(this) != true) return;
            FfmpegBox.Text = d.FileName;
        }

        string DefaultName() { return DefaultName(_data, _previewRun); }

        string DefaultName(OverlayData data, LapRecord run)
        {
            DateTime local = new DateTime(2000, 1, 1).AddSeconds(data.StartT).AddHours(9);
            return local.ToString("yyyyMMdd_HHmmss", Inv) + "_S" + _sess.No + (run != null ? "_R" + run.LapNo : "") + "_overlay";
        }

        /// <summary>走行ごとのとき: 1 本目のファイル名から、ほかの走行のファイル名を作る (日時と _R1 を入れ替える)</summary>
        string RunPath(string firstPath, OverlayData data, LapRecord run)
        {
            string dir = Path.GetDirectoryName(firstPath), ext = Path.GetExtension(firstPath);
            string name = Path.GetFileNameWithoutExtension(firstPath);
            string def1 = DefaultName(RunData(_runs[0]), _runs[0]);
            string mine = DefaultName(data, run);
            // 既定の名前のままなら走行ごとの既定の名前、変えてあればその名前の後ろに _R番号
            string n = name == def1 ? mine : name + "_R" + run.LapNo;
            return Path.Combine(dir ?? "", n + ext);
        }

        void SaveFrame_Click(object sender, RoutedEventArgs e)
        {
            if (_preview == null) return;
            SaveFileDialog d = new SaveFileDialog { Filter = "PNG (*.png)|*.png", FileName = DefaultName() + "_" + ((int)TimeSlider.Value).ToString(Inv) + "s.png" };
            Settings.InitialDir(d, "OverlayOut");
            if (d.ShowDialog(this) != true) return;
            Settings.RememberDir("OverlayOut", d.FileName);
            FrameRate fr = FpsCombo.SelectedItem as FrameRate ?? FrameRate.All[3];
            _preview.SavePng(_data.At(_data.StartT + TimeSlider.Value, fr.Fps, _preview.TrailFrames), d.FileName);
            Status("保存しました: " + d.FileName);
        }

        async void Export_Click(object sender, RoutedEventArgs e)
        {
            if (_layout == null || _data == null) { Status("レイアウトを読めていません"); return; }
            bool png = FormatCombo.SelectedIndex == 1;
            bool anim = FormatCombo.SelectedIndex == 2;   // QuickTime Animation (可逆・透明部分が多いと小さい)
            string ff = null;
            if (!png)
            {
                ff = OverlayEncoder.FindFfmpeg(FfmpegBox.Text);
                if (ff == null)
                {
                    MessageBox.Show(this, "ffmpeg.exe が見つかりません。場所を指定するか、形式を「透明 PNG 連番」にしてください。\n(入れるには: winget install Gyan.FFmpeg)", "ffmpeg がありません", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                FfmpegBox.Text = ff;
            }
            string outPath;
            if (png)
            {
                OpenFolderDialog d = new OpenFolderDialog { Title = "PNG 連番を入れるフォルダ (この中に新しいフォルダを作ります)" };
                Settings.InitialDir(d, "OverlayOut");
                if (d.ShowDialog(this) != true) return;
                Settings.RememberDir("OverlayOut", d.FolderName, true);
                outPath = Path.Combine(d.FolderName, DefaultName());
            }
            else
            {
                SaveFileDialog d = new SaveFileDialog { Filter = "QuickTime (*.mov)|*.mov", FileName = DefaultName() + ".mov" };
                Settings.InitialDir(d, "OverlayOut");
                if (d.ShowDialog(this) != true) return;
                Settings.RememberDir("OverlayOut", d.FileName);
                outPath = d.FileName;
            }

            FrameRate fr = FpsCombo.SelectedItem as FrameRate ?? FrameRate.All[3];
            int w, h; GetSize(out w, out h);
            OverlayLayout layout = _layout;
            // 書き出す組 (データと出力先)。走行ごとのときは走行の数だけ
            List<KeyValuePair<OverlayData, string>> jobs = new List<KeyValuePair<OverlayData, string>>();
            if (RangeCombo.SelectedIndex == 1 && _runs.Count > 0)
                foreach (LapRecord r in _runs)
                {
                    OverlayData d = RunData(r);
                    jobs.Add(new KeyValuePair<OverlayData, string>(d, RunPath(outPath, d, r)));
                }
            else
                jobs.Add(new KeyValuePair<OverlayData, string>(
                    new OverlayData(_def, _sess, _sectorNames, _gps, MakeOptions(), Num(BeforeBox, 10), Num(AfterBox, 10), PreviewRun()), outPath));
            _cancel = new CancellationTokenSource();
            CancellationToken ct = _cancel.Token;
            SetBusy(true);
            Stopwatch sw = Stopwatch.StartNew();
            Status("書き出し中...");
            try
            {
                for (int j = 0; j < jobs.Count; j++)
                {
                    OverlayData data = jobs[j].Key;
                    string path = jobs[j].Value;
                    string head = jobs.Count > 1 ? string.Format(Inv, "{0} / {1} 本目のファイル  ", j + 1, jobs.Count) : "";
                    Stopwatch one = Stopwatch.StartNew();
                    await Task.Run(() => OverlayEncoder.Export(() => new OverlayRenderer(layout, data, w, h), data, fr, path, png, anim, ff,
                        (n, total) => Dispatcher.BeginInvoke(new Action(() =>
                        {
                            Progress.Maximum = total; Progress.Value = n;
                            double el = one.Elapsed.TotalSeconds, rest = n > 0 ? el / n * (total - n) : 0;
                            StatusText.Text = head + string.Format(Inv, "書き出し中 {0} / {1} フレーム  (残り約 {2:0} 秒)", n, total, rest);
                        })), ct));
                }
                if (jobs.Count == 1)
                    Status(string.Format(Inv, "書き出しました ({0:0} 秒): {1}", sw.Elapsed.TotalSeconds, outPath));
                else
                    Status(string.Format(Inv, "{0} ファイルを書き出しました ({1:0} 秒): {2}", jobs.Count, sw.Elapsed.TotalSeconds,
                        string.Join("\n", jobs.Select(x => x.Value))));
            }
            catch (OperationCanceledException) { Status("中止しました"); }
            catch (Exception ex)
            {
                Status("書き出しに失敗しました");
                MessageBox.Show(this, ex.Message, "書き出しに失敗しました", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally { SetBusy(false); }
        }

        void SetBusy(bool b)
        {
            ExportButton.IsEnabled = !b; PngButton.IsEnabled = !b; CancelButton.IsEnabled = b;
            if (!b) Progress.Value = 0;
        }

        void Cancel_Click(object sender, RoutedEventArgs e) { if (_cancel != null) _cancel.Cancel(); }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (_cancel != null) _cancel.Cancel();
            base.OnClosing(e);
        }
    }
}
