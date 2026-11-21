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
    //  映像・サブ動画・表示は、高さ 1080 の「舞台」(幅は映像の縦横比) に重ねて Viewbox で縮める。
    //  サブ動画 (ペダルなど) はレイアウトの [subvideo] の枠に、トリミングして縦横比を保って収める (SubVideoLayout)
    // =====================================================================
    public sealed class VideoPane : Grid
    {
        readonly Image _img = new Image { Stretch = Stretch.Fill };
        readonly Image _ovImg = new Image { Stretch = Stretch.Fill, IsHitTestVisible = false };
        readonly Grid _stage = new Grid { Width = 1920, Height = 1080 };
        readonly Viewbox _view = new Viewbox { Stretch = Stretch.Uniform };
        readonly Canvas _subLayer = new Canvas { IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        readonly Image _subImg = new Image { Stretch = Stretch.Fill };
        readonly System.Windows.Shapes.Rectangle _subBorder = new System.Windows.Shapes.Rectangle { IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        WriteableBitmap _subWb;
        SubVideoLayout _subLay;
        double[] _subCrop;
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
            _subLayer.Children.Add(_subImg);
            _stage.Children.Add(_img);
            _stage.Children.Add(_subLayer);
            _stage.Children.Add(_subBorder);
            _stage.Children.Add(_ovImg);
            _view.Child = _stage;
            Children.Add(_view);
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
                SetAspect((double)r.W / r.H);
            }
            _wb.WritePixels(new Int32Rect(0, 0, r.W, r.H), f.Px, r.W * 4, 0);
        }

        public void ClearFrame() { _img.Source = null; _wb = null; HideSub(); }

        /// <summary>舞台の縦横比 (幅 / 高さ)。高さは 1080 のまま</summary>
        void SetAspect(double aspect)
        {
            double w = Math.Round(1080 * Math.Max(0.5, Math.Min(4, aspect)));
            if (Math.Abs(_stage.Width - w) < 0.5) return;
            _stage.Width = w;
            PlaceSub();
        }

        // ---------------- サブ動画 (ペダルなど別のカメラ) ----------------

        /// <summary>サブ動画の枠とトリミング。lay = null か show = 0 なら出さない</summary>
        public void SetSubLayout(SubVideoLayout lay, double[] crop)
        {
            _subLay = lay; _subCrop = crop;
            PlaceSub();
        }

        /// <summary>サブ動画のコマを出す (枠は SetSubLayout で決めたもの)</summary>
        public void ShowSubFrame(FrameReader r, FrameReader.Frame f)
        {
            if (_subLay == null || !_subLay.Show) { HideSub(); return; }
            if (_subWb == null || _subWb.PixelWidth != r.W || _subWb.PixelHeight != r.H)
            {
                _subWb = new WriteableBitmap(r.W, r.H, 96, 96, PixelFormats.Bgra32, null);
                _subImg.Source = _subWb;
                PlaceSub();
            }
            _subWb.WritePixels(new Int32Rect(0, 0, r.W, r.H), f.Px, r.W * 4, 0);
            if (_subLayer.Visibility != Visibility.Visible) { _subLayer.Visibility = Visibility.Visible; PlaceSub(); }
        }

        /// <summary>サブ動画を消す (範囲外の時刻・サブ動画が無いとき)</summary>
        public void HideSub()
        {
            _subLayer.Visibility = Visibility.Collapsed;
            _subBorder.Visibility = Visibility.Collapsed;
        }

        /// <summary>今出している映像のコマの写し (無ければ null)。レイアウトエディタの背景用</summary>
        public BitmapSource CurrentFrame { get { return Snapshot(_wb); } }

        /// <summary>今出しているサブ動画のコマの写し (トリミング前の全体。無ければ null)</summary>
        public BitmapSource CurrentSubFrame { get { return SubVisible ? Snapshot(_subWb) : null; } }

        static BitmapSource Snapshot(WriteableBitmap b)
        {
            if (b == null) return null;
            WriteableBitmap c = b.Clone();
            c.Freeze();
            return c;
        }

        /// <summary>サブ動画が見えているか</summary>
        public bool SubVisible { get { return _subLayer.Visibility == Visibility.Visible; } }

        void PlaceSub()
        {
            if (_subLay == null || !_subLay.Show || _subWb == null) { HideSub(); return; }
            double W = _stage.Width, H = _stage.Height, sw = _subWb.PixelWidth, sh = _subWb.PixelHeight;
            Rect src = SubVideoLayout.Source(sw, sh, _subCrop);
            Rect dst = _subLay.Dest(W, H, sw, sh, _subCrop);
            double k = dst.Width / src.Width;
            // 絵の全体を拡大縮小して置き、トリミングで残す所だけを見せる
            _subImg.Width = sw * k; _subImg.Height = sh * k;
            Canvas.SetLeft(_subImg, dst.X - src.X * k);
            Canvas.SetTop(_subImg, dst.Y - src.Y * k);
            _subLayer.Width = W; _subLayer.Height = H;
            _subLayer.Clip = new RectangleGeometry(dst);
            _subLayer.Opacity = _subLay.Opacity;
            double bw = _subLay.Border * H / 1080.0;
            if (bw > 0 && _subLayer.Visibility == Visibility.Visible)
            {
                _subBorder.HorizontalAlignment = HorizontalAlignment.Left; _subBorder.VerticalAlignment = VerticalAlignment.Top;
                _subBorder.Margin = new Thickness(dst.X - bw, dst.Y - bw, 0, 0);
                _subBorder.Width = dst.Width + 2 * bw; _subBorder.Height = dst.Height + 2 * bw;
                _subBorder.Stroke = new SolidColorBrush(_subLay.BorderColor);
                _subBorder.StrokeThickness = bw;
                _subBorder.Visibility = Visibility.Visible;
            }
            else _subBorder.Visibility = Visibility.Collapsed;
        }

        // ---------------- 重ねる表示 (別スレッドで描く) ----------------

        Thread _thread;
        volatile bool _stop;
        volatile int _gen;              // StartOverlay のたびに増やす。古い描画スレッドは自分の番号でなくなったら終わる
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
            StartOverlay(make, stateAt, aspect, _ovWidth);
        }

        // 描き直しに使うもの (SetOverlayWidth で幅を変えて描き直すため)
        Func<int, int, OverlayRenderer> _ovMake;
        Func<OverlayRenderer, double, OverlayState> _ovStateAt;
        double _ovAspect;
        int _ovWidth = 1280;

        void StartOverlay(Func<int, int, OverlayRenderer> make, Func<OverlayRenderer, double, OverlayState> stateAt, double aspect, int width)
        {
            StopOverlay();
            _stop = false;
            _ovMake = make; _ovStateAt = stateAt; _ovAspect = aspect; _ovWidth = width;
            int gen = ++_gen;
            int w = width, h = (int)Math.Round(w / Math.Max(0.5, aspect) / 2) * 2;
            _ovW = w; _ovH = h;
            SetAspect(aspect);
            Dispatcher ui = Dispatcher;
            Thread t = new Thread(() => Worker(make, stateAt, w, h, ui, gen)) { IsBackground = true, Name = "OverlayRender" };
            t.SetApartmentState(ApartmentState.STA);
            _thread = t;
            t.Start();
        }

        /// <summary>
        /// 重ねる表示を描く画像の幅を変える (既定 1280。画面いっぱいに出すときは大きく)。動いていれば描き直す
        /// </summary>
        public void SetOverlayWidth(int width)
        {
            width = Math.Max(640, width) / 2 * 2;
            if (width == _ovWidth) return;
            _ovWidth = width;
            if (_ovMake == null || _thread == null) return;
            StartOverlay(_ovMake, _ovStateAt, _ovAspect, width);
            RefreshOverlay();
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

        void Worker(Func<int, int, OverlayRenderer> make, Func<OverlayRenderer, double, OverlayState> stateAt, int w, int h, Dispatcher ui, int gen)
        {
            OverlayRenderer r;
            try { r = make(w, h); }
            catch (Exception) { return; }
            if (r == null) return;
            byte[][] bufs = { new byte[w * h * 4], new byte[w * h * 4] };
            int bi = 0;
            DateTime last = DateTime.MinValue;
            while (!_stop && gen == _gen)
            {
                _wake.WaitOne(500);
                if (_stop || gen != _gen) break;
                double wait = 50 - (DateTime.Now - last).TotalMilliseconds;      // 1 秒に 20 回まで
                if (wait > 0) Thread.Sleep((int)wait);
                double t;
                if (gen != _gen) break;
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
                    if (_stop || gen != _gen) return;
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
