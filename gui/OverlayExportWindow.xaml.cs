using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
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

            Settings.Binder st = new Settings.Binder("Overlay");
            st.Text(LayoutBox, "Layout");
            st.Text(FfmpegBox, "Ffmpeg");
            st.Combo(SizeCombo, "Size");
            st.Combo(FpsCombo, "Fps");
            st.Text(BeforeBox, "Before");
            st.Text(AfterBox, "After");
            st.Combo(FormatCombo, "Format");
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
            _data = new OverlayData(_def, _sess, _sectorNames, _gps, MakeOptions(), Num(BeforeBox, 10), Num(AfterBox, 10));
            int w, h; GetSize(out w, out h);
            _preview = new OverlayRenderer(_layout, _data, w, h);
            PreviewFrame.Width = w; PreviewFrame.Height = h;
            TimeSlider.Minimum = 0;
            TimeSlider.Maximum = _data.EndT - _data.StartT;
            // はじめは、最初の周回の途中あたりを見せる
            if (keep < 0)
            {
                double t0 = _sess.Laps.Count > 1 ? _sess.Laps[1].StartT + 20 : _data.StartT + 30;
                keep = Math.Max(0, Math.Min(TimeSlider.Maximum, t0 - _data.StartT));
            }
            TimeSlider.Value = Math.Min(TimeSlider.Maximum, keep);
            UpdatePreview();
            Status((_preview.Notice ?? "") + (_preview.Notice != null ? "\n" : "") +
                   string.Format(Inv, "長さ {0}  (最高速 {1:0} km/h)", OverlayData.FmtLap(_data.EndT - _data.StartT), _data.MaxSpeedKmh));
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

        string DefaultName()
        {
            DateTime local = new DateTime(2000, 1, 1).AddSeconds(_data.StartT).AddHours(9);
            return local.ToString("yyyyMMdd_HHmmss", Inv) + "_S" + _sess.No + "_overlay";
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
            OverlayData data = new OverlayData(_def, _sess, _sectorNames, _gps, MakeOptions(), Num(BeforeBox, 10), Num(AfterBox, 10));
            _cancel = new CancellationTokenSource();
            CancellationToken ct = _cancel.Token;
            SetBusy(true);
            Stopwatch sw = Stopwatch.StartNew();
            Status("書き出し中...");
            try
            {
                await Task.Run(() => OverlayEncoder.Export(() => new OverlayRenderer(layout, data, w, h), data, fr, outPath, png, anim, ff,
                    (n, total) => Dispatcher.BeginInvoke(new Action(() =>
                    {
                        Progress.Maximum = total; Progress.Value = n;
                        double el = sw.Elapsed.TotalSeconds, rest = n > 0 ? el / n * (total - n) : 0;
                        StatusText.Text = string.Format(Inv, "書き出し中 {0} / {1} フレーム  (残り約 {2:0} 秒)", n, total, rest);
                    })), ct));
                Status(string.Format(Inv, "書き出しました ({0:0} 秒): {1}", sw.Elapsed.TotalSeconds, outPath));
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
