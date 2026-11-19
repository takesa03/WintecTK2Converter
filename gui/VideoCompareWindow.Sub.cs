using System;
using System.Threading.Tasks;
using System.Windows;

namespace Tk2Gui
{
    // =====================================================================
    //  動画で比較: サブ動画の表示と、表示のレイアウトの編集 (docs/subvideo_design.md の 7.2)
    //  サブ動画は表示だけ (音声は出さない)。走行動画の時刻合わせで登録したもの (log.json の videos、role = "sub") を使う
    // =====================================================================
    public partial class VideoCompareWindow
    {
        LayoutEditorWindow _editor;

        SubVideoLayout SubLayout()
        {
            return _layout != null ? SubVideoLayout.From(_layout) : new SubVideoLayout { Show = true };
        }

        /// <summary>その周回の時間に重なるサブ動画を開く (無ければ何もしない)</summary>
        async Task OpenSubAsync(Lane l)
        {
            if (_ffmpeg == null || l.Sess == null || string.IsNullOrEmpty(l.Sess.FilePath)) return;
            VideoStore.Entry s;
            try { s = VideoStore.FindSub(l.Sess.FilePath, l.Lap.StartT - Before - 5, l.Lap.EndT + After + 5); }
            catch (Exception) { return; }
            if (s == null) return;
            string ff = _ffmpeg;
            try
            {
                l.SubVi = await Task.Run(() => FfVideo.Probe(ff, s.Path));
                // 小さく表示するので、読むのも小さく (2 本とも開くので軽くする)
                l.SubReader = new FrameReader(ff, s.Path, l.SubVi, 640);
                l.Sub = s;
                l.Pane.SetSubLayout(SubLayout(), s.CropOrZero);
            }
            catch (Exception ex)
            {
                if (l.SubReader != null) { l.SubReader.Dispose(); l.SubReader = null; }
                l.Sub = null;
                Status("サブ動画を開けません: " + ex.Message);
            }
        }

        /// <summary>毎フレーム: τ に合うサブのコマを出す</summary>
        void RenderSub(Lane l, double tau)
        {
            if (l.SubReader == null) return;
            double st = SubCheck.IsChecked == true ? l.SubT(tau) : double.NaN;
            if (double.IsNaN(st) || st < 0 || st >= l.SubVi.Duration) { l.Pane.HideSub(); return; }
            bool needSeek;
            FrameReader.Frame f = l.SubReader.Get(st, out needSeek);
            if (needSeek && (DateTime.Now - l.SubLastSeek).TotalSeconds > 0.3) { l.SubReader.Seek(st); l.SubLastSeek = DateTime.Now; }
            if (f != null)
            {
                l.Pane.ShowSubFrame(l.SubReader, f);
                l.SubReader.Recycle(f);
            }
        }

        void SubCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_lanes == null) return;
            foreach (Lane l in _lanes)
                if (SubCheck.IsChecked != true) l.Pane.HideSub();
                else RenderSub(l, double.IsNaN(l.Shown) ? _tau : l.Shown);
        }

        void RestartOverlays()
        {
            foreach (Lane l in _lanes)
            {
                if (l.Vi == null || l.Data == null) continue;
                OverlayLayout lay = _layout; OverlayData d = l.Data; double fps = l.Vi.Fps;
                l.Pane.StopOverlay();
                l.Pane.StartOverlay((w, h) => new OverlayRenderer(lay, d, w, h), (r, t) => d.At(t, fps, r.TrailFrames), (double)l.Vi.Width / l.Vi.Height);
                if (l.Sub != null) l.Pane.SetSubLayout(SubLayout(), l.Sub.CropOrZero);
                l.Pane.RefreshOverlay();   // 時刻が同じだと描き直さないので
            }
            UpdateInfo();
        }

        void EditLayout_Click(object sender, RoutedEventArgs e)
        {
            if (_editor != null) { _editor.Activate(); return; }
            Lane a = _lanes.Count > 0 ? _lanes[0] : null;
            if (_layout == null || a == null || a.Data == null) { Status("表示のレイアウトを読めていないので、編集できません。"); return; }
            double tau = double.IsNaN(a.Shown) ? _tau : a.Shown;
            SubPreview sp = a.Sub == null ? null : new SubPreview
            {
                Frame = a.Pane.CurrentSubFrame, Crop = (double[])a.Sub.CropOrZero.Clone(),
                CropChanged = c =>
                {
                    // トリミングはサブ動画ごと (log.json)。A のサブ動画に保存し、同じ動画を使う B にも反映
                    bool zero = c[0] == 0 && c[1] == 0 && c[2] == 0 && c[3] == 0;
                    VideoStore.Entry en = VideoStore.Get(a.Sess.FilePath, a.Sub.Path);
                    if (en != null) { en.Crop = zero ? null : (double[])c.Clone(); VideoStore.Put(a.Sess.FilePath, en); }
                    foreach (Lane l in _lanes)
                        if (l.Sub != null && string.Equals(l.Sub.Path, a.Sub.Path, StringComparison.OrdinalIgnoreCase))
                        {
                            l.Sub.Crop = (double[])c.Clone();
                            l.Pane.SetSubLayout(SubLayout(), l.Sub.CropOrZero);
                        }
                }
            };
            try
            {
                _editor = new LayoutEditorWindow(_layout.Path ?? OverlayLayout.DefaultPath, a.Data, a.Base + tau, a.Vi != null ? a.Vi.Fps : 30, a.Pane.CurrentFrame, sp, l =>
                {
                    _layout = l;
                    RestartOverlays();
                }) { Owner = this };
            }
            catch (Exception ex) { Status("レイアウトを開けません: " + ex.Message); return; }
            _editor.Closed += (s, a2) => _editor = null;
            _editor.Show();
        }
    }
}
