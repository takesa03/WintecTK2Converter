using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Tk2Gui
{
    // =====================================================================
    //  動画の 1 画面: 映像のコマ (WriteableBitmap) と、その上に動画素材と同じ表示
    //  (速度計・ラップ・セクター表など) を重ねる。
    //  表示の描画は文字が多くて重い (1 回 20 ms 前後) ので、別のスレッドで画像にして、
    //  出来たものを貼る (1 秒に 20 回まで)。画面の操作が止まらないように
    // =====================================================================
    public sealed class VideoPane : Grid
    {
        readonly Image _img = new Image { Stretch = Stretch.Uniform };
        readonly Image _ovImg = new Image { Stretch = Stretch.Uniform, IsHitTestVisible = false };
        readonly TextBlock _msg = new TextBlock
        {
            Foreground = Brushes.White, FontSize = 14, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12)
        };
        WriteableBitmap _wb, _ovWb;

        public VideoPane()
        {
            Background = new SolidColorBrush(Color.FromRgb(0x10, 0x12, 0x16));
            ClipToBounds = true;
            Children.Add(_img);
            Children.Add(_ovImg);
            Children.Add(_msg);
            Message = null;
            Unloaded += (s, e) => StopOverlay();
        }

        public string Message
        {
            get { return _msg.Text; }
            set { _msg.Text = value ?? ""; _msg.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible; }
        }

        public void ShowFrame(FrameReader r, FrameReader.Frame f)
        {
            if (_wb == null || _wb.PixelWidth != r.W || _wb.PixelHeight != r.H)
            {
                _wb = new WriteableBitmap(r.W, r.H, 96, 96, PixelFormats.Bgra32, null);
                _img.Source = _wb;
            }
            _wb.WritePixels(new Int32Rect(0, 0, r.W, r.H), f.Px, r.W * 4, 0);
        }

        public void ClearFrame() { _img.Source = null; _wb = null; }

        // ---------------- 重ねる表示 (別スレッドで描く) ----------------

        Thread _thread;
        volatile bool _stop;
        readonly AutoResetEvent _wake = new AutoResetEvent(false);
        readonly object _reqLock = new object();
        double _reqT = double.NaN;      // 描いてほしい時刻 (ログの通算秒)。NaN = 表示しない
        bool _reqDirty;
        int _ovW, _ovH;

        /// <summary>
        /// 重ねる表示を使えるようにする。make(幅, 高さ) は描画のスレッドで呼ばれ、そのスレッドで使う OverlayRenderer を作る。
        /// 大きさは映像の縦横比に合わせる (aspect = 幅 / 高さ)
        /// </summary>
        public void StartOverlay(Func<int, int, OverlayRenderer> make, Func<OverlayRenderer, double, OverlayState> stateAt, double aspect)
        {
            StopOverlay();
            _stop = false;
            int w = 1280, h = (int)Math.Round(w / Math.Max(0.5, aspect) / 2) * 2;
            _ovW = w; _ovH = h;
            Dispatcher ui = Dispatcher;
            Thread t = new Thread(() => Worker(make, stateAt, w, h, ui)) { IsBackground = true, Name = "OverlayRender" };
            t.SetApartmentState(ApartmentState.STA);
            _thread = t;
            t.Start();
        }

        /// <summary>時刻 logT の表示を描いてもらう (NaN で消す)。何度呼んでも、描くのは最新の 1 つだけ</summary>
        public void RequestOverlay(double logT)
        {
            lock (_reqLock)
            {
                if (_reqT.Equals(logT)) return;
                _reqT = logT; _reqDirty = true;
            }
            if (double.IsNaN(logT)) _ovImg.Visibility = Visibility.Collapsed;
            _wake.Set();
        }

        /// <summary>同じ時刻のまま描き直してもらう (表示のデータを変えたとき)</summary>
        public void RefreshOverlay()
        {
            lock (_reqLock) { if (double.IsNaN(_reqT)) return; _reqDirty = true; }
            _wake.Set();
        }

        public void StopOverlay()
        {
            _stop = true;
            _wake.Set();
            _thread = null;
        }

        void Worker(Func<int, int, OverlayRenderer> make, Func<OverlayRenderer, double, OverlayState> stateAt, int w, int h, Dispatcher ui)
        {
            OverlayRenderer r;
            try { r = make(w, h); }
            catch (Exception) { return; }
            if (r == null) return;
            byte[][] bufs = { new byte[w * h * 4], new byte[w * h * 4] };
            int bi = 0;
            DateTime last = DateTime.MinValue;
            while (!_stop)
            {
                _wake.WaitOne(500);
                if (_stop) break;
                double wait = 50 - (DateTime.Now - last).TotalMilliseconds;      // 1 秒に 20 回まで
                if (wait > 0) Thread.Sleep((int)wait);
                double t;
                lock (_reqLock) { if (!_reqDirty) continue; t = _reqT; _reqDirty = false; }
                if (double.IsNaN(t)) continue;
                last = DateTime.Now;
                byte[] px = bufs[bi]; bi ^= 1;
                try
                {
                    OverlayState st = stateAt(r, t);
                    BitmapSource b = r.RenderBitmap(st);
                    b.CopyPixels(px, w * 4, 0);
                }
                catch (Exception) { continue; }
                ui.BeginInvoke(new Action(() =>
                {
                    if (_stop) return;
                    lock (_reqLock) { if (double.IsNaN(_reqT)) return; }
                    if (_ovWb == null || _ovWb.PixelWidth != w || _ovWb.PixelHeight != h)
                    {
                        _ovWb = new WriteableBitmap(w, h, 96, 96, PixelFormats.Pbgra32, null);
                        _ovImg.Source = _ovWb;
                    }
                    _ovWb.WritePixels(new Int32Rect(0, 0, w, h), px, w * 4, 0);
                    _ovImg.Visibility = Visibility.Visible;
                }), DispatcherPriority.Render);
            }
        }
    }
}
