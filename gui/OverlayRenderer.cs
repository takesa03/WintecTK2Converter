using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Tk2Gui
{
    // =====================================================================
    //  動画素材の 1 フレームを描く。見た目は VSD の standard_l3 スキンに合わせる
    //  (overlay_test\mock_layout.py の見本と同じ配置)。部品は ini の節と 1 対 1:
    //  [speedometer] [gmeter] [map] [session] [laptime] [sector] [info]
    //  描画は、この部品を作ったスレッド (STA) で行うこと。
    // =====================================================================

    public sealed class OverlayRenderer
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        public readonly int W, H;
        readonly double U;                  // 1080p 基準の 1 画素
        readonly OverlayLayout L;
        readonly OverlayData D;
        readonly FontFamily _family;
        readonly Typeface _face;
        readonly Dictionary<string, double> _bottom = new Dictionary<string, double>();

        // メーター画像 (standard.png)。無ければ代わりの円盤
        readonly BitmapSource _meter;
        readonly Rect _meterVisible;        // 画像の中の見えている範囲 (300x300 に換算した座標)
        public readonly string Notice;      // 画面に知らせること (メーター画像が見つからないなど)

        // コース図
        readonly Point[] _center;
        readonly Rect _bounds;

        /// <summary>G の軌跡の長さ [フレーム] (gmeter.trail)</summary>
        public int TrailFrames { get { return Math.Max(1, L["gmeter"].Int("trail", 20)); } }

        RenderTargetBitmap _rtb;
        byte[] _buf;

        public OverlayRenderer(OverlayLayout layout, OverlayData data, int width, int height)
        {
            L = layout; D = data; W = width; H = height; U = H / 1080.0;
            string font = L["global"].Str("font", "Impact");
            if (font.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) || font.EndsWith(".otf", StringComparison.OrdinalIgnoreCase))
                font = Path.GetFileNameWithoutExtension(font);
            string jfont = L["global"].Str("jfont", "Meiryo UI");
            _family = new FontFamily(font + ", " + jfont);   // Impact に無い文字 (日本語のドライバー名など) は jfont で
            _face = new Typeface(_family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

            string img = L.Resolve(L["speedometer"].Str("image", "standard.png"));
            try
            {
                if (img.Length > 0 && File.Exists(img))
                {
                    BitmapImage bi = new BitmapImage();
                    bi.BeginInit(); bi.CacheOption = BitmapCacheOption.OnLoad; bi.UriSource = new Uri(img); bi.EndInit();
                    FormatConvertedBitmap fc = new FormatConvertedBitmap(bi, PixelFormats.Bgra32, null, 0);
                    fc.Freeze();
                    _meter = fc;
                    _meterVisible = VisibleBox(fc);
                }
            }
            catch (Exception) { _meter = null; }
            if (_meter == null)
            {
                _meterVisible = new Rect(9, 9, 286, 249);   // standard.png と同じ (下が平らな円盤)
                Notice = "メーター画像が見つからないので、代わりの円盤で描きます: " + (img.Length > 0 ? img : "(image が空)");
            }

            _center = D.CenterlineXY();
            double x0 = _center.Min(p => p.X), x1 = _center.Max(p => p.X), y0 = _center.Min(p => p.Y), y1 = _center.Max(p => p.Y);
            _bounds = new Rect(x0, y0, Math.Max(1, x1 - x0), Math.Max(1, y1 - y0));
        }

        /// <summary>不透明な部分 (alpha &gt; 8) の範囲を、300x300 に換算して返す</summary>
        static Rect VisibleBox(BitmapSource b)
        {
            int w = b.PixelWidth, h = b.PixelHeight, stride = w * 4;
            byte[] px = new byte[stride * h];
            b.CopyPixels(px, stride, 0);
            int minX = w, minY = h, maxX = -1, maxY = -1;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (px[y * stride + x * 4 + 3] > 8)
                    {
                        if (x < minX) minX = x; if (x > maxX) maxX = x;
                        if (y < minY) minY = y; if (y > maxY) maxY = y;
                    }
            if (maxX < 0) return new Rect(0, 0, 300, 300);
            double s = 300.0 / w;
            return new Rect(minX * s, minY * s, (maxX - minX + 1) * s, (maxY - minY + 1) * s);
        }

        // ---- 配置 ---------------------------------------------------------

        /// <summary>anchor / x / y (と below) から、幅 w・高さ h の部品の左上を求める</summary>
        Point Place(OverlaySection s, double w, double h)
        {
            string a = s.Str("anchor", "topleft").ToLowerInvariant();
            double x = s.Num("x", 0) * U, y = s.Num("y", 0) * U;
            double left = a.Contains("right") ? W - x - w : (a == "top" || a == "bottom" ? (W - w) / 2 + x : x);
            double top = a.Contains("bottom") ? H - y - h : y;
            string b = s.Str("below", "");
            double bt;
            if (b.Length > 0 && _bottom.TryGetValue(b, out bt)) top = bt + y;
            return new Point(left, top);
        }

        static Brush Br(Color c) { SolidColorBrush b = new SolidColorBrush(c); b.Freeze(); return b; }
        static Pen P(Color c, double w)
        {
            Pen p = new Pen(Br(c), w) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            p.Freeze();
            return p;
        }

        FormattedText Ft(string s, double size, Color c)
        {
            return new FormattedText(s ?? "", Inv, FlowDirection.LeftToRight, _face, Math.Max(4, size), Br(c), 1.0);
        }

        /// <summary>文字を描く。ax = 0 左 / 0.5 中央 / 1 右, ay = 0 上 / 0.5 中央 / 1 下 (行の箱で)</summary>
        void Text(DrawingContext dc, string s, double size, Color c, double x, double y, double ax, double ay, double outline, Color oc)
        {
            if (string.IsNullOrEmpty(s)) return;
            FormattedText ft = Ft(s, size, c);
            Point o = new Point(x - ft.WidthIncludingTrailingWhitespace * ax, y - ft.Height * ay);
            if (outline > 0)
            {
                Geometry g = ft.BuildGeometry(o);
                dc.DrawGeometry(null, P(oc, outline * 2), g);
                dc.DrawGeometry(Br(c), null, g);
            }
            else dc.DrawText(ft, o);
        }

        /// <summary>等幅に 1 文字ずつ並べる (右端 xr に揃える)。VSD の FONT_FIXED に相当</summary>
        void Mono(DrawingContext dc, string s, double size, double cell, double xr, double y, Color c, double outline)
        {
            if (string.IsNullOrEmpty(s)) return;
            double x = xr - cell * s.Length;
            foreach (char ch in s)
            {
                Text(dc, ch.ToString(), size, c, x + cell / 2, y, 0.5, 0, outline, Colors.Black);
                x += cell;
            }
        }

        double Cell(double size) { return Ft("0", size, Colors.White).WidthIncludingTrailingWhitespace * 1.15; }

        // ---- 1 フレーム -------------------------------------------------

        public void Draw(DrawingContext dc, OverlayState st)
        {
            _bottom.Clear();
            if (L.Shown("map")) DrawMap(dc, st);
            if (L.Shown("speedometer")) DrawSpeed(dc, st);
            if (L.Shown("gmeter")) DrawG(dc, st);
            if (L.Shown("session")) DrawSession(dc, st);
            if (L.Shown("laptime")) DrawLap(dc, st);
            if (L.Shown("sector")) DrawSector(dc, st);
            if (L.Shown("info")) DrawInfo(dc, st);
        }

        /// <summary>メーター画像を、見えている部分が anchor/x/y に揃うように置き、画像 (300 換算で size) の左上を返す</summary>
        Point PutMeter(DrawingContext dc, OverlaySection s, double size)
        {
            double k = size / 300.0;
            Point p = Place(s, _meterVisible.Width * k, _meterVisible.Height * k);
            Point tl = new Point(p.X - _meterVisible.X * k, p.Y - _meterVisible.Y * k);
            if (_meter != null)
                dc.DrawImage(_meter, new Rect(tl.X, tl.Y, size, size));
            else
            {
                // 代わりの円盤: 半透明の黒い円 (下を平らに切る) と外周の輪
                Rect clip = new Rect(tl.X + _meterVisible.X * k, tl.Y + _meterVisible.Y * k, _meterVisible.Width * k, _meterVisible.Height * k);
                dc.PushClip(new RectangleGeometry(clip));
                Point c = new Point(tl.X + 150 * k, tl.Y + 150 * k);
                dc.DrawEllipse(Br(Color.FromArgb(0xB0, 0x10, 0x10, 0x10)), P(Color.FromArgb(0xE0, 0x90, 0x90, 0x90), 4 * k), c, 140 * k, 140 * k);
                dc.Pop();
            }
            return tl;
        }

        void DrawSpeed(DrawingContext dc, OverlayState st)
        {
            OverlaySection s = L["speedometer"];
            double k = s.Num("scale", 1) * 1.5 * U;   // 1.5 = VSD の 720 → 1080
            double size = 300 * k;
            Point tl = PutMeter(dc, s, size);
            double cx = tl.X + 150 * k, cy = tl.Y + 150 * k, R = 126 * k;
            Color fc = s.Col("color", "#FFFFFF");
            int step = Math.Max(1, s.Int("step", 10));
            int mx = s.Int("max", 0);
            if (mx <= 0) mx = (int)Math.Ceiling((D.MaxSpeedKmh + s.Num("margin", 5)) / step) * step;
            mx = Math.Max(step, mx);
            int nb = Math.Max(1, mx / step);
            Func<double, double, Point> pol = (r, deg) => new Point(cx + r * Math.Cos(deg * Math.PI / 180), cy + r * Math.Sin(deg * Math.PI / 180));
            Pen big = P(fc, Math.Max(1, 2 * k)), small = P(fc, Math.Max(1, k));
            for (int i = 0; i <= nb * 5; i++)
            {
                double deg = 135 + 270.0 * i / (nb * 5);
                bool b = i % 5 == 0;
                double ln = R * (b ? 0.1 : 0.05);
                dc.DrawLine(b ? big : small, pol(R - ln, deg), pol(R, deg));
                if (b)
                {
                    Point q = pol(R * 0.78, deg);
                    Text(dc, (step * i / 5).ToString(Inv), 24 * k, fc, q.X, q.Y, 0.5, 0.5, 0, Colors.Black);
                }
            }
            Text(dc, ((int)st.SpeedKmh).ToString(Inv), 60 * k, fc, cx, cy + 150 * k * 0.25, 0.5, 0.5, 0, Colors.Black);
            Text(dc, "km/h", 24 * k, fc, cx, cy + 150 * k * 0.5, 0.5, 0.5, 0, Colors.Black);
            double sp = Math.Max(0, Math.Min(mx, st.SpeedKmh));
            double nd = 135 + 270 * sp / mx;
            dc.DrawLine(P(s.Col("needle", "#FF0000"), Math.Max(2, 4 * k)), pol(-R * 0.1, nd), pol(R * 0.95, nd));
        }

        void DrawG(DrawingContext dc, OverlayState st)
        {
            OverlaySection s = L["gmeter"];
            double k = s.Num("scale", 1) * 1.5 * U;
            double size = 150 * k;
            Point tl = PutMeter(dc, s, size);
            double cx = tl.X + 75 * k, cy = tl.Y + 75 * k, R = 63 * k;
            Color lc = s.Col("line", "#802000"), bc = s.Col("ball", "#FF4000");
            Pen thin = P(lc, 1);
            dc.DrawLine(thin, new Point(cx - R, cy), new Point(cx + R, cy));
            dc.DrawLine(thin, new Point(cx, cy - R), new Point(cx, cy + R));
            dc.DrawEllipse(null, thin, new Point(cx, cy), R / 3, R / 3);
            dc.DrawEllipse(null, thin, new Point(cx, cy), R * 2 / 3, R * 2 / 3);
            double g = R / Math.Max(0.1, s.Num("range", 1.5));
            // 体が受ける向きで表示する (右旋回で左、加速で下、減速で上へ振れる)
            Func<Point, Point> map = v => new Point(cx - Math.Max(-R, Math.Min(R, v.X * g)), cy + Math.Max(-R, Math.Min(R, v.Y * g)));
            Point[] tr = st.GTrail ?? new Point[] { new Point(st.Gx, st.Gy) };
            if (tr.Length > 1)
            {
                StreamGeometry sg = new StreamGeometry();
                using (StreamGeometryContext c = sg.Open())
                {
                    c.BeginFigure(map(tr[0]), false, false);
                    for (int i = 1; i < tr.Length; i++) c.LineTo(map(tr[i]), true, true);
                }
                sg.Freeze();
                dc.DrawGeometry(null, P(lc, Math.Max(1, 2 * k)), sg);
            }
            Point bp = map(tr[tr.Length - 1]);
            dc.DrawEllipse(Br(bc), null, bp, 5 * k, 5 * k);
            Text(dc, Math.Sqrt(st.Gx * st.Gx + st.Gy * st.Gy).ToString("0.0", Inv) + "G", 24 * k, Colors.White, cx, tl.Y + size - 10 * k, 0.5, 1, 0, Colors.Black);   // 下端で見切れないよう少し上へ
        }

        void DrawMap(DrawingContext dc, OverlayState st)
        {
            OverlaySection s = L["map"];
            double mw = s.Num("width", 420) * U, mh = s.Num("height", 260) * U;
            Point tl = Place(s, mw, mh);
            double sc = Math.Min(mw / _bounds.Width, mh / _bounds.Height);
            string an = s.Str("anchor", "topleft").ToLowerInvariant();
            // 枠の中で、基準の隅の側に寄せる (左の隅なら左端、右の隅なら右端、上下中央の指定なら中央)
            double ox = an.Contains("right") ? tl.X + mw - _bounds.Width * sc : (an.Contains("left") ? tl.X : tl.X + (mw - _bounds.Width * sc) / 2);
            double oy = an.Contains("bottom") ? tl.Y + mh - _bounds.Height * sc : tl.Y;
            Func<Point, Point> tp = p => new Point(ox + (p.X - _bounds.X) * sc, oy + (_bounds.Bottom - p.Y) * sc);   // 北が上

            StreamGeometry sg = new StreamGeometry();
            using (StreamGeometryContext c = sg.Open())
            {
                c.BeginFigure(tp(_center[0]), false, !D.Def.IsGymkhana);     // ジムカーナの中心線は閉じない
                for (int i = 1; i < _center.Length; i++) c.LineTo(tp(_center[i]), true, true);
            }
            sg.Freeze();
            dc.DrawGeometry(null, P(s.Col("line", "#FFFF00"), Math.Max(1, s.Num("linewidth", 3) * U)), sg);

            if (s.Bool("gates", true))
            {
                double gl = s.Num("gatelength", 22) * U;
                Pen ctl = P(s.Col("controlcolor", "#FFFFFF"), Math.Max(1, s.Num("gatewidth", 3) * U));
                Pen sec = P(s.Col("gatecolor", "#00C0FF"), Math.Max(1, s.Num("gatewidth", 3) * U));
                for (int i = 0; i < D.Def.Gates.Count; i++)
                {
                    Gate gt = D.Def.Gates[i];
                    Point a = tp(D.ToXY(gt.A)), b = tp(D.ToXY(gt.B));
                    Vector v = b - a;
                    if (v.Length < 1e-6) continue;
                    v.Normalize(); v *= gl / 2;
                    Point m = new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2);
                    dc.DrawLine(i == 0 ? ctl : sec, m - v, m + v);
                }
            }
            double r = s.Num("dotsize", 7) * U;
            dc.DrawEllipse(Br(s.Col("dot", "#FF0000")), null, tp(new Point(st.X, st.Y)), r, r);
        }

        void DrawSession(DrawingContext dc, OverlayState st)
        {
            OverlaySection s = L["session"];
            double size = s.Num("size", 30) * U;
            string t = "Session " + st.SessionNo.ToString(Inv) + (string.IsNullOrWhiteSpace(st.Driver) ? "" : "   Driver " + st.Driver.Trim());
            FormattedText ft = Ft(t, size, Colors.White);
            double w = ft.WidthIncludingTrailingWhitespace;
            Point p = Place(s, w, size);
            Text(dc, t, size, s.Col("color", "#FFFFFF"), p.X + w, p.Y, 1, 0, Math.Max(1, size / 18), Colors.Black);
            _bottom["session"] = p.Y + size * 1.1;
        }

        void DrawLap(DrawingContext dc, OverlayState st)
        {
            OverlaySection s = L["laptime"];
            double size = s.Num("size", 34) * U, cell = Cell(size), lh = size * 1.1, sw = Math.Max(1, size / 18);
            int n = Math.Max(0, s.Int("laps", 3));
            double w = cell * 14, h = lh * (4 + n);
            Point p = Place(s, w, h);
            double R = p.X + w, y = p.Y;
            Color col = s.Col("color", "#FFFFFF"), best = s.Col("best", "#00FFFF"), plus = s.Col("plus", "#FF0000"), minus = s.Col("minus", "#4080FF");

            Mono(dc, st.LapTime, size, cell, R, y, col, sw);
            Mono(dc, "Time", size, cell, R - cell * 9, y, col, sw);
            y += lh;
            if (st.LapDelta.HasValue)
                Mono(dc, OverlayData.FmtLapDelta(st.LapDelta.Value), size, cell, R, y, st.LapDelta.Value > 0 ? plus : minus, sw);
            y += lh * 2;
            Color bl = s.Col("bestline", "#A0FF40");   // Best の行
            Mono(dc, st.LapBest, size, cell, R, y, bl, sw);
            Mono(dc, "Best", size, cell, R - cell * 9, y, bl, sw);
            y += lh;
            foreach (LapLine ln in st.LapHistory.Skip(Math.Max(0, st.LapHistory.Count - n)))
            {
                Color c = ln.Best ? best : col;
                Mono(dc, ln.Text, size, cell, R, y, c, sw);
                Mono(dc, ln.Label, size, cell, R - cell * 9, y, c, sw);
                y += lh;
            }
            _bottom["laptime"] = p.Y + h;
        }

        void DrawSector(DrawingContext dc, OverlayState st)
        {
            OverlaySection s = L["sector"];
            int M = st.SecNames == null ? 0 : st.SecNames.Length;
            if (M == 0) return;
            double size = s.Num("size", 30) * U, cell = Cell(size), lh = size * 1.1, sw = Math.Max(1, size / 18);
            int n = Math.Max(0, s.Int("laps", 3));
            const int colw = 7, labw = 6;   // 1 列の文字数 ("+12.345" が入る幅), 行の名前の幅
            double w = cell * (labw + (colw + 1) * M), h = lh * (5 + n);
            Point p = Place(s, w, h);
            double y = p.Y;
            Color col = s.Col("color", "#FFFFFF"), run = s.Col("running", "#FFFF80"), best = s.Col("best", "#00FFFF"),
                  plus = s.Col("plus", "#FF0000"), minus = s.Col("minus", "#4080FF");
            Func<int, double> xr = j => p.X + cell * (labw + (colw + 1) * (j + 1));

            Mono(dc, "Sector", size, cell, p.X + cell * labw, y, col, sw);
            for (int j = 0; j < M; j++) Mono(dc, st.SecNames[j], size, cell, xr(j), y, col, sw);
            y += lh;
            for (int j = 0; j < M; j++) Mono(dc, st.SecCur[j], size, cell, xr(j), y, st.SecRunning[j] ? run : col, sw);
            y += lh;
            for (int j = 0; j < M; j++)
                if (st.SecDelta[j].HasValue)
                    Mono(dc, OverlayData.FmtSecDelta(st.SecDelta[j].Value), size, cell, xr(j), y, st.SecDelta[j].Value > 0 ? plus : minus, sw);
            y += lh * 2;
            Color bl = s.Col("bestline", "#A0FF40");   // Best の行
            Mono(dc, "Best", size, cell, p.X + cell * labw, y, bl, sw);
            for (int j = 0; j < M; j++) Mono(dc, st.SecBest[j], size, cell, xr(j), y, bl, sw);
            y += lh;
            foreach (SectorLine ln in st.SecHistory.Skip(Math.Max(0, st.SecHistory.Count - n)))
            {
                Mono(dc, ln.Label, size, cell, p.X + cell * labw, y, col, sw);
                for (int j = 0; j < M; j++) Mono(dc, ln.Text[j], size, cell, xr(j), y, ln.Best[j] ? best : col, sw);
                y += lh;
            }
        }

        void DrawInfo(DrawingContext dc, OverlayState st)
        {
            OverlaySection s = L["info"];
            double size = s.Num("size", 26) * U;
            List<string> lines = new List<string>();
            foreach (string raw in s.Str("items", "DateTime, Elapsed, Lat, Lng, Alt, Dist").Split(','))
            {
                string it = raw.Trim();
                switch (it.ToLowerInvariant())
                {
                    case "datetime": lines.Add("DateTime:  " + st.Local.ToString("yyyy/MM/dd  HH:mm:ss", Inv)); break;
                    case "elapsed": lines.Add("Elapsed:  " + OverlayData.FmtLap(st.Elapsed)); break;
                    case "lat": lines.Add("Lat:  " + Math.Abs(st.Lat).ToString("0.00000", Inv) + (st.Lat >= 0 ? "N" : "S")); break;
                    case "lng": case "lon": lines.Add("Lng:  " + Math.Abs(st.Lon).ToString("0.00000", Inv) + (st.Lon >= 0 ? "E" : "W")); break;
                    case "alt": lines.Add("Alt:  " + (double.IsNaN(st.Alt) ? "---" : st.Alt.ToString("0.0", Inv) + "m")); break;
                    case "dist": lines.Add("Dist:  " + (st.DistM / 1000).ToString("0.00", Inv) + "km"); break;
                    case "speed": lines.Add("Speed:  " + st.SpeedKmh.ToString("0.0", Inv) + "km/h"); break;
                    case "g": lines.Add("G:  " + st.Gx.ToString("+0.00;-0.00", Inv) + " / " + st.Gy.ToString("+0.00;-0.00", Inv)); break;
                }
            }
            if (lines.Count == 0) return;
            double w = lines.Max(t => Ft(t, size, Colors.White).WidthIncludingTrailingWhitespace);
            Point p = Place(s, w, size * lines.Count);
            Color c = s.Col("color", "#A0A0A0");
            for (int i = 0; i < lines.Count; i++)
                Text(dc, lines[i], size, c, p.X, p.Y + size * i, 0, 0, Math.Max(1, size / 20), Color.FromArgb(96, 0, 0, 0));
        }

        // ---- 画素 ----------------------------------------------------------

        public BitmapSource RenderBitmap(OverlayState st)
        {
            DrawingVisual v = new DrawingVisual();
            using (DrawingContext dc = v.RenderOpen()) Draw(dc, st);
            if (_rtb == null) _rtb = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
            else _rtb.Clear();
            _rtb.Render(v);
            return _rtb;
        }

        /// <summary>1 フレームを BGRA (乗算済みでない) の画素列で返す。返す配列は次の呼び出しで上書きされる</summary>
        public byte[] RenderBgra(OverlayState st)
        {
            BitmapSource b = RenderBitmap(st);
            int stride = W * 4;
            if (_buf == null) _buf = new byte[stride * H];
            b.CopyPixels(_buf, stride, 0);
            byte[] px = _buf;
            for (int i = 0; i < px.Length; i += 4)
            {
                int a = px[i + 3];
                if (a == 0 || a == 255) continue;
                px[i] = (byte)Math.Min(255, px[i] * 255 / a);
                px[i + 1] = (byte)Math.Min(255, px[i + 1] * 255 / a);
                px[i + 2] = (byte)Math.Min(255, px[i + 2] * 255 / a);
            }
            return px;
        }

        /// <summary>1 フレームを透明 PNG で保存 (乗算済みのまま渡してよい。エンコーダーが扱う)</summary>
        public void SavePng(OverlayState st, string path)
        {
            BitmapSource b = RenderBitmap(st);
            PngBitmapEncoder enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(b));
            using (FileStream fs = File.Create(path)) enc.Save(fs);
        }
    }
}
