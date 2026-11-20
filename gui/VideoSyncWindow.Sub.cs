using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace Tk2Gui
{
    // =====================================================================
    //  走行動画の時刻合わせ画面の「サブ動画」(ペダルなど別のカメラ)。設計は docs/subvideo_design.md
    //  サブ動画もログの時刻に対する t0 で登録する (VideoStore.Entry の role = "sub")。
    //  画面では「ずれ」= サブの時刻 − メインの時刻 で見せる (サブの t0 = メインの t0 − ずれ)。
    //  メインの t0 を合わせ直したときは、ずれを保ってサブも動かす (サブはメインの映像に合わせたものなので)
    // =====================================================================
    public partial class VideoSyncWindow
    {
        string _subPath;
        VideoInfo _subVi;
        FrameReader _subReader;
        double _subT0 = double.NaN;             // サブの 0 秒 = ログの何秒か
        double[] _subCrop = new double[4];
        SubVideoLayout _subLay;
        DateTime _subLastSeek;
        bool _subFill;

        double SubOffset { get { return _t0 - _subT0; } }   // サブの時刻 = メインの時刻 + これ

        SubVideoLayout SubLayout()
        {
            if (_subLay == null) _subLay = _layout != null ? SubVideoLayout.From(_layout) : new SubVideoLayout { Show = true };
            return _subLay;
        }

        void CloseSub()
        {
            if (_subAudioCancel != null) _subAudioCancel.Cancel();
            if (_subReader != null) { _subReader.Dispose(); _subReader = null; }
            _subPath = null; _subVi = null; _subT0 = double.NaN;
            _pane.HideSub();
            UpdateSubText();
        }

        /// <summary>メインの動画を開いたとき: その時間に重なる登録済みのサブ動画を開く</summary>
        void LoadSubFor()
        {
            CloseSub();
            if (_video == null || double.IsNaN(_t0) || _vi == null) return;
            VideoStore.Entry s = VideoStore.FindSub(_log, _t0, _t0 + _vi.Duration);
            if (s != null) OpenSub(s);
        }

        void OpenSub(VideoStore.Entry s)
        {
            CloseSub();
            try { _subVi = FfVideo.Probe(_ffmpeg, s.Path); }
            catch (Exception ex) { Status("サブ動画を開けません: " + ex.Message); return; }
            _subPath = s.Path; _subT0 = s.T0; _subCrop = s.CropOrZero;
            if (Math.Abs(s.Duration - _subVi.Duration) > 0.01) { s.Duration = _subVi.Duration; VideoStore.Put(_log, s); }
            _subReader = new FrameReader(_ffmpeg, s.Path, _subVi, 960);
            _pane.SetSubLayout(SubLayout(), _subCrop);
            SeekSub();
            UpdateSubText();
        }

        // ---------------- 再生 ----------------

        /// <summary>メインの時刻 vt に当たるサブの時刻 (範囲外なら NaN)</summary>
        double SubTimeAt(double vt)
        {
            if (_subReader == null || double.IsNaN(_t0)) return double.NaN;
            double st = vt + SubOffset;
            return st >= 0 && st < _subVi.Duration ? st : double.NaN;
        }

        void SeekSub()
        {
            double st = SubTimeAt(_t);
            if (double.IsNaN(st)) return;
            _subReader.Seek(st);
            _subLastSeek = DateTime.Now;
        }

        /// <summary>毎フレーム (OnRender から): メインの今の時刻に合うサブのコマを出す</summary>
        void RenderSub()
        {
            if (_subReader == null) return;
            double st = SubCheck.IsChecked == true ? SubTimeAt(_t) : double.NaN;
            if (double.IsNaN(st)) { _pane.HideSub(); return; }
            bool needSeek;
            FrameReader.Frame f = _subReader.Get(st, out needSeek);
            if (needSeek && (DateTime.Now - _subLastSeek).TotalSeconds > 0.3) { _subReader.Seek(st); _subLastSeek = DateTime.Now; }
            if (f != null)
            {
                _pane.ShowSubFrame(_subReader, f);
                _subReader.Recycle(f);
            }
        }

        // ---------------- 開く・外す ----------------

        void SubOpen_Click(object sender, RoutedEventArgs e)
        {
            if (_ffmpeg == null) return;
            if (_video == null || double.IsNaN(_t0))
            {
                MessageBox.Show(this, "先に走行動画を開いて、時刻を合わせてください。", "サブ動画", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            OpenFileDialog d = new OpenFileDialog { Filter = "動画 (*.mp4;*.mov;*.mkv;*.avi;*.mts;*.m4v;*.webm)|*.mp4;*.mov;*.mkv;*.avi;*.mts;*.m4v;*.webm|すべて (*.*)|*.*", Title = "サブ動画を開く" };
            Settings.InitialDir(d, "VideoDir");
            if (d.ShowDialog(this) != true) return;
            Settings.RememberDir("VideoDir", d.FileName);
            string p = d.FileName;
            if (string.Equals(Path.GetFullPath(p), Path.GetFullPath(_video), StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this, "いま開いている走行動画と同じファイルです。別のカメラの動画を選んでください。", "サブ動画", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            VideoStore.Entry en = VideoStore.Get(_log, p);
            if (en != null && !en.IsSub)
            {
                if (MessageBox.Show(this, "この動画は走行動画 (メイン) として登録されています。サブ動画に切り替えますか？\n(走行動画の一覧からは消えます)\n\n" + p,
                    "サブ動画", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                en.Role = VideoStore.RoleSub; en.T0 = _t0; en.SyncNote = "manual";
                VideoStore.Put(_log, en);
            }
            else if (en == null)
            {
                VideoInfo vi;
                try { vi = FfVideo.Probe(_ffmpeg, p); }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "動画を開けません", MessageBoxButton.OK, MessageBoxImage.Error); return; }
                // はじめは「2 本が同時に録り始めた」とする (ずれ 0)
                en = new VideoStore.Entry { Path = p, T0 = _t0, Duration = vi.Duration, Role = VideoStore.RoleSub, SyncNote = "manual" };
                VideoStore.Put(_log, en);
                Status("サブ動画を登録しました。はじめは「走行動画と同時に録り始めた」として重ねています。ずれのボタンで合わせてください。");
            }
            OpenSub(en);
            if (!SubLayout().Show) Status("レイアウトの [subvideo] が show = 0 なので、サブ動画は表示されません。");
        }

        void SubRemove_Click(object sender, RoutedEventArgs e)
        {
            if (_subPath == null) return;
            if (MessageBox.Show(this, "このサブ動画の登録を外しますか？ (動画のファイルは消しません)\n\n" + _subPath, "サブ動画",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            string p = _subPath;
            LogFile.Update(_log, d => { if (d.Videos != null) d.Videos.RemoveAll(v => v.IsSub && string.Equals(v.Path, p, StringComparison.OrdinalIgnoreCase)); });
            CloseSub();
            Status("サブ動画の登録を外しました: " + Path.GetFileName(p));
        }

        void SubCheck_Click(object sender, RoutedEventArgs e) { if (SubCheck.IsChecked != true) _pane.HideSub(); }

        // ---------------- 音声で合わせる ----------------

        System.Threading.CancellationTokenSource _subAudioCancel;

        async void SubAudio_Click(object sender, RoutedEventArgs e)
        {
            if (_subPath == null || _video == null) return;
            if (_subAudioCancel != null) { _subAudioCancel.Cancel(); return; }    // 2 度目の押下で取りやめ
            string ff = _ffmpeg, main = _video, sub = _subPath;
            double mainDur = _vi.Duration, subDur = _subVi.Duration, guess = SubOffset;
            double mainT = double.IsNaN(_shownT) ? _t : _shownT;
            System.Threading.CancellationTokenSource cts = new System.Threading.CancellationTokenSource();
            _subAudioCancel = cts;
            SubAudioButton.Content = "取りやめ";
            SubCandCombo.Items.Clear();
            Status(string.Format(Inv, "音声で合わせています (メインの {0} の前後 60 秒を、サブの前後 5 分の中で探します)...", VideoKit.Clock(mainT)));
            try
            {
                System.Collections.Generic.List<SubAudioSync.Candidate> c = await System.Threading.Tasks.Task.Run(() =>
                    SubAudioSync.Find(ff, main, mainDur, mainT, sub, subDur, guess, 60, 300, cts.Token));
                if (_subPath != sub) return;                 // 待っている間に別のサブにした
                if (c.Count == 0) { Status("音声で合わせられませんでした (音声が無いか、重なる範囲が短すぎます)。手で合わせてください。"); return; }
                _subFill = true;
                foreach (SubAudioSync.Candidate x in c) SubCandCombo.Items.Add(new ComboBoxItem { Content = x.ToString(), Tag = x });
                _subFill = false;
                SubCandCombo.SelectedIndex = 0;              // 1 位にする (SubCand_Changed で保存)
                SubAudioSync.Candidate b = c[0];
                Status(string.Format(Inv, "音声で合わせました: サブ = メイン {0:+0.000;-0.000} 秒 (相関 {1:0.00}、際立ち {2:0.0})。{3}保存しました。",
                    b.Offset, b.R, b.Z, b.Z < 5 ? "際立ちが低いので、ほかの候補か手で確かめてください。" : ""));
            }
            catch (OperationCanceledException) { Status("音声で合わせるのを取りやめました。"); }
            catch (Exception ex) { Status("音声で合わせられません: " + ex.Message); }
            finally
            {
                _subAudioCancel = null;
                SubAudioButton.Content = "音声で合わせる";
            }
        }

        void SubCand_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_subFill || _subPath == null) return;
            ComboBoxItem it = SubCandCombo.SelectedItem as ComboBoxItem;
            SubAudioSync.Candidate c = it == null ? null : it.Tag as SubAudioSync.Candidate;
            if (c == null) return;
            SetSubT0(_t0 - c.Offset, string.Format(Inv, "audio r={0:0.00} z={1:0.0}", c.R, c.Z),
                string.Format(Inv, "候補のずれ {0:+0.000;-0.000} 秒にしました。保存しました。", c.Offset));
        }

        // ---------------- ずれ・トリミング ----------------

        /// <summary>サブの t0 を変えて保存</summary>
        void SetSubT0(double t0, string note, string why)
        {
            if (_subPath == null) return;
            _subT0 = t0;
            VideoStore.Entry en = VideoStore.Get(_log, _subPath);
            if (en != null) { en.T0 = t0; if (note != null) en.SyncNote = note; VideoStore.Put(_log, en); }
            SeekSub();
            UpdateSubText();
            if (why != null) Status(why);
        }

        /// <summary>メインの t0 が old から変わったとき: ずれを保ってサブも動かす</summary>
        void SubFollowMain(double oldT0)
        {
            if (_subPath == null || double.IsNaN(oldT0)) return;
            SetSubT0(_subT0 + (_t0 - oldT0), null, null);
        }

        void SubNudge_Click(object sender, RoutedEventArgs e)
        {
            if (_subPath == null) return;
            string tag = (string)((Button)sender).Tag;
            double d = tag == "f" ? 1.0 / _subVi.Fps : tag == "-f" ? -1.0 / _subVi.Fps : double.Parse(tag, Inv);
            // サブを d 秒進める = メインの同じ時刻に、サブの d 秒後のコマを出す = サブの t0 を d 秒前にする
            double t0 = _subT0 - d;
            SetSubT0(t0, "manual", string.Format(Inv, "サブ動画を {0:+0.000;-0.000} 秒動かしました (ずれ {1:+0.000;-0.000} 秒)。保存しました。", d, _t0 - t0));
        }

        void SubBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            if (sender == SubOffsetBox) ApplySubOffset(); else ApplySubCrop();
            e.Handled = true;
        }

        void SubOffset_LostFocus(object sender, RoutedEventArgs e) { ApplySubOffset(); }

        void ApplySubOffset()
        {
            if (_subPath == null || _subFill) return;
            double v;
            if (!double.TryParse(SubOffsetBox.Text.Trim().Replace("+", ""), NumberStyles.Float, Inv, out v)) { UpdateSubText(); return; }
            if (Math.Abs(v - SubOffset) < 0.0005) return;
            SetSubT0(_t0 - v, "manual", string.Format(Inv, "サブ動画のずれを {0:+0.000;-0.000} 秒にしました。保存しました。", v));
        }

        TextBox[] CropBoxes { get { return new[] { SubCropL, SubCropT, SubCropR, SubCropB }; } }

        void SubCrop_LostFocus(object sender, RoutedEventArgs e) { ApplySubCrop(); }

        void SubCrop_Wheel(object sender, MouseWheelEventArgs e)
        {
            if (_subPath == null) return;
            TextBox b = (TextBox)sender;
            double v;
            if (!double.TryParse(b.Text.Trim(), NumberStyles.Float, Inv, out v)) v = 0;
            int step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 10 : 2;
            v = Math.Max(0, Math.Round(v) + (e.Delta > 0 ? step : -step));
            b.Text = v.ToString(Inv);
            ApplySubCrop();
            e.Handled = true;
        }

        void ApplySubCrop()
        {
            if (_subPath == null || _subFill) return;
            if (_subVi == null || _subVi.Width <= 0 || _subVi.Height <= 0) return;
            int sw = _subVi.Width, sh = _subVi.Height;
            int[] dim = { sw, sh, sw, sh };
            int[] px = new int[4];
            double[] c = new double[4];
            TextBox[] bs = CropBoxes;
            for (int i = 0; i < 4; i++)
            {
                double v;
                if (!double.TryParse(bs[i].Text.Trim(), NumberStyles.Float, Inv, out v)) v = _subCrop[i] * dim[i];
                px[i] = (int)Math.Max(0, Math.Round(v));
            }
            // 残りが元の 10 % より小さくならないように、右・下を減らす
            int minW = Math.Max(2, sw / 10), minH = Math.Max(2, sh / 10);
            px[0] = Math.Min(px[0], sw - minW); px[2] = Math.Min(px[2], sw - minW - px[0]);
            px[1] = Math.Min(px[1], sh - minH); px[3] = Math.Min(px[3], sh - minH - px[1]);
            for (int i = 0; i < 4; i++) c[i] = (double)px[i] / dim[i];
            if (Enumerable.Range(0, 4).All(i => Math.Abs(c[i] - _subCrop[i]) < 1e-9)) { UpdateSubText(); return; }
            SetSubCrop(c);
            Status(string.Format(Inv, "サブ動画のトリミングを 左 {0} 上 {1} 右 {2} 下 {3} 画素にしました (切り落とした後 {4} x {5})。保存しました。",
                px[0], px[1], px[2], px[3], sw - px[0] - px[2], sh - px[1] - px[3]));
        }

        /// <summary>トリミングを変えて保存 (この画面とレイアウトエディタから)</summary>
        void SetSubCrop(double[] c)
        {
            if (_subPath == null) return;
            _subCrop = (double[])c.Clone();
            VideoStore.Entry en = VideoStore.Get(_log, _subPath);
            if (en != null) { en.Crop = c.All(x => x == 0) ? null : (double[])c.Clone(); VideoStore.Put(_log, en); }
            _pane.SetSubLayout(SubLayout(), _subCrop);
            UpdateSubText();
        }

        // ---------------- 表示の編集 (レイアウトエディタ) ----------------

        LayoutEditorWindow _editor;

        void EditLayout_Click(object sender, RoutedEventArgs e)
        {
            if (_editor != null) { _editor.Activate(); return; }
            if (_layout == null || _data == null) { Status("表示のレイアウトを読めていないので、編集できません。"); return; }
            double vt = double.IsNaN(_shownT) ? _t : _shownT;
            double logT = !double.IsNaN(_t0) && _vi != null ? _t0 + vt : (_laps.Count > 0 ? _laps[0].StartT + 20 : _sess.StartT);
            SubPreview sp = _subPath == null ? null : new SubPreview
            {
                Frame = _pane.CurrentSubFrame, Crop = (double[])_subCrop.Clone(),
                SourceWidth = _subVi != null ? _subVi.Width : 0, SourceHeight = _subVi != null ? _subVi.Height : 0,
                CropChanged = c => SetSubCrop(c)
            };
            try
            {
                _editor = new LayoutEditorWindow(_layout.Path ?? OverlayLayout.DefaultPath, _data, logT, _vi != null ? _vi.Fps : 30, _pane.CurrentFrame, sp, l =>
                {
                    _layout = l;
                    _subLay = null;
                    RestartOverlay();
                    _pane.SetSubLayout(SubLayout(), _subCrop);
                }) { Owner = this };
            }
            catch (Exception ex) { Status("レイアウトを開けません: " + ex.Message); return; }
            _editor.Closed += (s, a) => _editor = null;
            _editor.Show();
        }

        void UpdateSubText()
        {
            bool on = _subPath != null;
            _subFill = true;
            SubRemoveButton.IsEnabled = on;
            SubAudioButton.IsEnabled = on;
            SubCandCombo.IsEnabled = on;
            if (!on) SubCandCombo.Items.Clear();
            SubOffsetBox.IsEnabled = on;
            foreach (TextBox b in CropBoxes) b.IsEnabled = on;
            if (!on)
            {
                SubText.Text = "(サブ動画なし)";
                SubOffsetBox.Text = "";
                foreach (TextBox b in CropBoxes) b.Text = "";
            }
            else
            {
                double off = SubOffset;
                SubText.Text = Path.GetFileName(_subPath) + string.Format(Inv, "  {0}x{1} {2:0.##}fps\nサブ = メイン {3:+0.000;-0.000} 秒", _subVi.Width, _subVi.Height, _subVi.Fps, off)
                    + (off >= 0 ? "  (サブが先に録り始めた)" : "  (サブが後から録り始めた)");
                SubOffsetBox.Text = off.ToString("+0.000;-0.000", Inv);
                TextBox[] bs = CropBoxes;
                int[] dim = { _subVi.Width, _subVi.Height, _subVi.Width, _subVi.Height };
                for (int i = 0; i < 4; i++) bs[i].Text = Math.Round(_subCrop[i] * dim[i]).ToString(Inv);
            }
            _subFill = false;
        }
    }
}
