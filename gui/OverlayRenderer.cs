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
        double _lapRight;   // ラップタイムの表の右端 (ジムカーナでセクターをその下に置くため)

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

        /// <summary>節の background = 1 なら、枠 r の下に薄い背景 (節の panel の色) を敷く (レイアウトエディタで要素ごとに ON/OFF)</summary>
        void PanelBack(DrawingContext dc, OverlaySection s, Rect r, double defPad = 10)
        {
            if (!s.Bool("background", false) || r.IsEmpty) return;
            double pad = s.Num("panelpad", defPad) * U, rad = s.Num("panelradius", 8) * U;
            r.Inflate(pad, pad);
            Color pc = s.Col("panel", "#1F000000");
            double op = s.Num("panelopacity", double.NaN);      // 濃さ (0〜1)。指定があれば色の先頭 2 桁より優先
            if (!double.IsNaN(op)) pc.A = (byte)Math.Round(Math.Max(0, Math.Min(1, op)) * 255);
            dc.DrawRoundedRectangle(Br(pc), null, r, rad, rad);
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
        /// <summary>数字を等幅で描く (数字は 1 文字ずつ一番広い数字の幅の枠の中央に置く。値が変わっても左右に揺れない)</summary>
        void TextDigits(DrawingContext dc, string s, double size, Color c, double x, double y, double ax, double ay, double outline, Color oc)
        {
            if (string.IsNullOrEmpty(s)) return;
            double dw = 0;
            for (char d = '0'; d <= '9'; d++) dw = Math.Max(dw, Ft(d.ToString(), size, c).WidthIncludingTrailingWhitespace);
            double[] cw = new double[s.Length];
            double total = 0;
            for (int i = 0; i < s.Length; i++)
            {
                cw[i] = char.IsDigit(s[i]) ? dw : Ft(s[i].ToString(), size, c).WidthIncludingTrailingWhitespace;
                total += cw[i];
            }
            double cx = x - total * ax;
            for (int i = 0; i < s.Length; i++)
            {
                Text(dc, s[i].ToString(), size, c, cx + cw[i] / 2, y, 0.5, ay, outline, oc);
                cx += cw[i];
            }
        }

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

        /// <summary>
        /// true にすると、Draw のたびに要素ごとの外枠 (この描画の画素) を Bounds に残す (レイアウトエディタの当たり判定用)。
        /// 要素を 1 つずつ DrawingGroup に描いてから貼るので、少し重くなる
        /// </summary>
        public bool CollectBounds;

        /// <summary>前の Draw で描いた要素の外枠 (節の名前 → 枠)。心拍は session に含む。CollectBounds が true のときだけ</summary>
        public readonly Dictionary<string, Rect> Bounds = new Dictionary<string, Rect>(StringComparer.OrdinalIgnoreCase);

        /// <summary>1920x1080 基準の 1 画素が、この描画の何画素か</summary>
        public double Unit { get { return U; } }

        void Part(DrawingContext dc, string name, Action<DrawingContext> draw)
        {
            if (!CollectBounds) { draw(dc); return; }
            DrawingGroup g = new DrawingGroup();
            using (DrawingContext c = g.Open()) draw(c);
            dc.DrawDrawing(g);
            Rect b = g.Bounds;
            if (!b.IsEmpty) Bounds[name] = b;
        }

        public void Draw(DrawingContext dc, OverlayState st)
        {
            _bottom.Clear();
            _sectorBox = Rect.Empty;
            Bounds.Clear();
            if (L.Shown("map")) Part(dc, "map", c => DrawMap(c, st));
            if (L.Shown("speedometer")) Part(dc, "speedometer", c => DrawSpeed(c, st));
            if (L.Shown("gmeter")) Part(dc, "gmeter", c => DrawG(c, st));
            if (L.Shown("session")) Part(dc, "session", c => DrawSession(c, st));
            if (L.Shown("laptime")) Part(dc, "laptime", c => DrawLap(c, st));
            if (L.Shown("sector")) Part(dc, "sector", c => DrawSector(c, st));
            if (OverlayPrefs.Graph) Part(dc, "graph", c => DrawGraph(c, st));   // 画面のチェックで出し入れ (ini の [graph] は色・大きさだけ)。セクター表示の上に置くので、その後に
            if (L.Shown("info")) Part(dc, "info", c => DrawInfo(c, st));
            // 回転計: 解析結果 (log.json の engine・rpm.json) があるときだけ。ini に節が無いレイアウトでも既定の位置に出す
            if (D.Engine != null && (L.Shown("tacho") || !L.Sections.ContainsKey("tacho"))) Part(dc, "tacho", c => DrawTacho(c, st));
        }

        /// <summary>回転計 (横棒) とアクセル (縦棒)。既定は加速度計と情報の上 (少し透過)</summary>
        void DrawTacho(DrawingContext dc, OverlayState st)
        {
            OverlaySection s;
            if (!L.Sections.TryGetValue("tacho", out s))
            {
                s = new OverlaySection("tacho");
                s.Values["anchor"] = "bottomleft"; s.Values["x"] = "278"; s.Values["y"] = "174";
            }
            dc.PushOpacity(Math.Max(0.05, Math.Min(1, s.Num("opacity", 0.8))));
            DrawTachoBody(dc, st, s);
            dc.Pop();
        }

        void DrawTachoBody(DrawingContext dc, OverlayState st, OverlaySection s)
        {
            double k = s.Num("scale", 1) * U;
            double w = 430 * k, h = 78 * k, pad = 8 * k;
            Point p = Place(s, w, h);
            double mx = Math.Max(1000, s.Num("max", 16000)), yel = s.Num("yellow", 13000), red = s.Num("red", 15000);
            bool geared = D.Engine != null && D.Engine.IsGeared;
            // 変速機付きは、解析結果 (log.json の engine) に車の目盛りがあればそちらを使う
            if (geared && D.Engine.TachoMax > 0)
            {
                mx = Math.Max(1000, D.Engine.TachoMax);
                if (D.Engine.TachoYellow > 0) yel = D.Engine.TachoYellow;
                if (D.Engine.TachoRed > 0) red = D.Engine.TachoRed;
            }
            Color fc = s.Col("color", "#FFFFFF");
            dc.DrawRoundedRectangle(Br(s.Col("back", "#70000000")), null, new Rect(p.X, p.Y, w, h), 6 * k, 6 * k);

            // アクセル (右端の縦棒)
            // 変速機付きはアクセルを求めないので、同じ所にギアを出す
            bool thrShown = !geared && s.Bool("throttle", true), gearShown = geared && s.Bool("gear", true);
            double tw = thrShown ? 30 * k : gearShown ? 46 * k : 0, tgap = thrShown || gearShown ? 12 * k : 0;
            double bx = p.X + pad, bw = w - pad * 2 - tw - tgap;
            if (gearShown)
            {
                double gx = p.X + w - pad - tw, gy = p.Y + pad, gh = h - pad * 2;
                dc.DrawRoundedRectangle(Br(Color.FromArgb(0x60, 0x80, 0x80, 0x80)), null, new Rect(gx, gy, tw, gh), 4 * k, 4 * k);
                string gs = st.Gear > 0 ? st.Gear.ToString(Inv) : "-";
                double gsz = 46 * k;
                Text(dc, gs, gsz, st.Gear > 0 ? s.Col("gearcolor", "#FFFFFF") : Color.FromArgb(0x80, 0xA0, 0xA0, 0xA0),
                     gx + tw / 2, gy + gh / 2, 0.5, 0.5, Math.Max(1, gsz / 18), Colors.Black);
            }
            if (thrShown)
            {
                double tx = p.X + w - pad - tw, ty = p.Y + pad, th = h - pad * 2 - 18 * k;
                Color on = s.Col("throttlecolor", "#40E060");
                dc.DrawRectangle(Br(Color.FromArgb(0x60, 0x80, 0x80, 0x80)), null, new Rect(tx, ty, tw, th));
                if (!double.IsNaN(st.Throttle) && st.Throttle > 0)
                {
                    double fh = th * st.Throttle;
                    dc.DrawRectangle(Br(on), null, new Rect(tx, ty + th - fh, tw, fh));
                }
                Text(dc, "THR", 15 * k, double.IsNaN(st.Throttle) ? Color.FromArgb(0x80, 0xA0, 0xA0, 0xA0) : fc, tx + tw / 2, p.Y + h - pad, 0.5, 1, 0, Colors.Black);
            }

            // 数字
            double rpm = double.IsNaN(st.Rpm) ? 0 : Math.Max(0, st.Rpm);
            double size = 30 * k;
            Text(dc, "RPM", 16 * k, fc, bx, p.Y + pad + size * 0.55, 0, 0.5, 0, Colors.Black);
            TextDigits(dc, rpm < 100 ? "0" : (Math.Round(rpm / 10) * 10).ToString("#,0", Inv), size, fc, bx + bw, p.Y + pad - 2 * k, 1, 0, Math.Max(1, size / 18), Colors.Black);
            // 後輪の空転・スライド: 音の回転数が速度の回転数より slip % 以上高い
            double slip = s.Num("slip", 8);
            // (変速機付きは、そのギアで速度から計算した回転数より、加速中に slip % 以上高い)
            if (slip > 0 && st.SpeedKmh > 15 && !double.IsNaN(st.AudioRpm) && st.SlipBase > 0 && st.AudioRpm > st.SlipBase * (1 + slip / 100))
                Text(dc, "SLIP", 16 * k, s.Col("slipcolor", "#FF9030"), bx + 44 * k, p.Y + pad + size * 0.55, 0, 0.5, 0, Colors.Black);

            // 横棒 (区切った目盛り)
            int segs = Math.Max(5, s.Int("segments", 32));
            double by = p.Y + h - pad - 22 * k, bh = 22 * k, gap = Math.Max(1, 2 * k), sw = (bw - gap * (segs - 1)) / segs;
            for (int i = 0; i < segs; i++)
            {
                double r0 = mx * i / segs;
                Color c = r0 >= red ? s.Col("redcolor", "#FF3030") : r0 >= yel ? s.Col("yellowcolor", "#FFD020") : s.Col("barcolor", "#40C0FF");
                bool lit = rpm > r0 + mx / segs * 0.5;
                if (!lit) c = Color.FromArgb(0x50, c.R, c.G, c.B);
                dc.DrawRectangle(Br(c), null, new Rect(bx + i * (sw + gap), by, sw, bh));
            }
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
            // 最高値が wide を超えるメーター (鈴鹿など) は、数字が詰まらないように step×2 ごとにだけ数字を書く (目盛りの線はそのまま)
            int wide = s.Int("wide", 140);
            int every = wide > 0 && mx > wide ? 2 : 1;
            Func<double, double, Point> pol = (r, deg) => new Point(cx + r * Math.Cos(deg * Math.PI / 180), cy + r * Math.Sin(deg * Math.PI / 180));
            Pen big = P(fc, Math.Max(1, 2 * k)), small = P(fc, Math.Max(1, k));
            for (int i = 0; i <= nb * 5; i++)
            {
                double deg = 135 + 270.0 * i / (nb * 5);
                bool b = i % 5 == 0;
                double ln = R * (b ? 0.1 : 0.05);
                dc.DrawLine(b ? big : small, pol(R - ln, deg), pol(R, deg));
                if (b && (i / 5) % every == 0)
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
            PanelBack(dc, s, new Rect(ox, oy, _bounds.Width * sc, _bounds.Height * sc), 16);

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
            string head = D.Def.IsGymkhana && st.HeatNo > 0 ? "Heat " + st.HeatNo.ToString(Inv) : "Session " + st.SessionNo.ToString(Inv);
            string t = head + (string.IsNullOrWhiteSpace(st.Driver) ? "" : "   Driver " + st.Driver.Trim());
            FormattedText ft = Ft(t, size, Colors.White);
            double w = ft.WidthIncludingTrailingWhitespace;
            // 心拍: 心拍 (log.json の heart) があるときだけ、ドライバー名の右にハートと 3 桁の数字。
            // 幅はいつも取っておく (記録の途切れで文字の位置が動かないように)
            bool heart = D.Heart != null && (L.Shown("heart") || !L.Sections.ContainsKey("heart"));
            double cell = Cell(size), hw = size * 0.85, gap = size * 0.45, hwAll = heart ? gap + hw + size * 0.2 + cell * 3 : 0;
            Point p = Place(s, w + hwAll, size);
            double sw = Math.Max(1, size / 18);
            Text(dc, t, size, s.Col("color", "#FFFFFF"), p.X + w, p.Y, 1, 0, sw, Colors.Black);
            if (heart && !double.IsNaN(st.HeartRate)) DrawHeart(dc, st, p.X + w + gap, p.Y + ft.Height / 2, hw, size, cell, p.X + w + hwAll, p.Y, sw);
            _bottom["session"] = p.Y + size * 1.1;
        }

        /// <summary>
        /// ハート (鼓動に合わせて明暗) と心拍の数字。x = ハートの左端、cy = 行の中央、xr = 数字の右端、ty = 文字の上端
        /// </summary>
        void DrawHeart(DrawingContext dc, OverlayState st, double x, double cy, double hw, double size, double cell, double xr, double ty, double sw)
        {
            OverlaySection s = L["heart"];
            Color bright = s.Col("color", "#FF2040"), dim = s.Col("dim", "#601018");
            // 拍の瞬間に明るくなり、次の拍までに暗くなる
            double k = Math.Exp(-st.HeartPhase * s.Num("decay", 4.0));
            Color c = Color.FromArgb((byte)(dim.A + (bright.A - dim.A) * k), (byte)(dim.R + (bright.R - dim.R) * k),
                                     (byte)(dim.G + (bright.G - dim.G) * k), (byte)(dim.B + (bright.B - dim.B) * k));
            double hh = hw * 0.9, y0 = cy - hh / 2;
            Func<double, double, Point> pt = (u, v) => new Point(x + u * hw, y0 + v * hh);
            StreamGeometry g = new StreamGeometry();
            using (StreamGeometryContext gc = g.Open())
            {
                gc.BeginFigure(pt(0.5, 1), true, true);
                gc.BezierTo(pt(0.12, 0.72), pt(-0.04, 0.38), pt(0.14, 0.14), true, true);
                gc.BezierTo(pt(0.3, -0.04), pt(0.46, 0.04), pt(0.5, 0.24), true, true);
                gc.BezierTo(pt(0.54, 0.04), pt(0.7, -0.04), pt(0.86, 0.14), true, true);
                gc.BezierTo(pt(1.04, 0.38), pt(0.88, 0.72), pt(0.5, 1), true, true);
            }
            g.Freeze();
            dc.DrawGeometry(Br(c), P(Colors.Black, sw), g);
            string n = Math.Round(st.HeartRate).ToString("0", Inv);
            Mono(dc, n, size, cell, xr, ty, s.Col("numcolor", "#FFFFFF"), sw);
        }

        void DrawLap(DrawingContext dc, OverlayState st)
        {
            OverlaySection s = L["laptime"];
            double size = s.Num("size", 34) * U, cell = Cell(size), lh = size * 1.1, sw = Math.Max(1, size / 18);
            int n = Math.Max(0, s.Int("laps", 3));
            bool gym = D.Def.IsGymkhana;   // ジムカーナ: 1 本ずつの素材なので、ベストと履歴の行は出さない (タイムと差分だけ)
            double w = cell * 14, h = gym ? lh * 2 : lh * (4 + n);
            Point p = Place(s, w, h);
            double R = p.X + w, y = p.Y;
            _lapRight = R;
            {
                // 左にはみ出す周回名 (Lap 3 / Heat 5 など) も背景に入れる。名前は Time の欄の左に右寄せで出る。
                // 幅はセッションでいちばん長い名前に合わせて固定する (周回ごとに背景が伸び縮みしないように)
                double left = p.X;
                int nl = D.MaxLapNameLength;
                if (nl > 0) left = Math.Min(left, R - cell * 14 - cell * nl);
                PanelBack(dc, s, new Rect(left, p.Y, R - left, h));
            }
            Color col = s.Col("color", "#FFFFFF"), best = s.Col("best", "#00FFFF"), plus = s.Col("plus", "#FF0000"), minus = s.Col("minus", "#4080FF");

            Mono(dc, st.LapTime, size, cell, R, y, col, sw);
            Mono(dc, "Time", size, cell, R - cell * 9, y, col, sw);
            if (!string.IsNullOrEmpty(st.LapName))     // 枠の左にはみ出して出す (タイムの欄の位置は変えない)
                Mono(dc, st.LapName, size, cell, R - cell * 14, y, col, sw);
            y += lh;
            if (st.LapDelta.HasValue)
                Mono(dc, OverlayData.FmtLapDelta(st.LapDelta.Value), size, cell, R, y, st.LapDelta.Value > 0 ? plus : minus, sw);
            if (gym) { _bottom["laptime"] = p.Y + h; return; }
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

        Rect _sectorBox = Rect.Empty;   // このフレームのセクター表示の枠 (グラフをその上に置くため)

        /// <summary>
        /// 距離ごとの速度のグラフ。その時点のベストの周回を透過の面と線で背景に、走行中の周回を線で伸ばしていく
        /// (ラインを通ると消えて描き直す)。既定はセクター表示の上 (above = sector)、右上のラップタイムには重ねない
        /// </summary>
        void DrawGraph(DrawingContext dc, OverlayState st)
        {
            double span = D.GraphSpan;
            if (!(span > 0) || (st.GraphBest == null && st.GraphCur == null)) return;
            OverlaySection s = L["graph"];
            bool gym = D.Def.IsGymkhana;
            double w = s.Num("width", 0) * U, h = s.Num("height", 220) * U;
            bool onSector = !gym && s.Str("above", "sector").Equals("sector", StringComparison.OrdinalIgnoreCase) && !_sectorBox.IsEmpty;
            if (w <= 0) w = onSector ? _sectorBox.Width : 480 * U;
            Point p;
            if (onSector) p = new Point(_sectorBox.Right - w, _sectorBox.Top - s.Num("y", 12) * U - h);
            else p = Place(s, w, h);
            // 右上のラップタイムの表示に重ねない (重なる分は高さを縮める)
            double lapBottom;
            if (L.Shown("laptime") && _bottom.TryGetValue("laptime", out lapBottom) && p.Y < lapBottom + 8 * U && p.X + w > _lapRight - Cell(L["laptime"].Num("size", 34) * U) * 14)
            {
                double bottom = p.Y + h;
                p.Y = lapBottom + 8 * U;
                h = bottom - p.Y;
            }
            if (h < 40 * U || w < 60 * U) return;

            double vmax = Math.Max(30, Math.Ceiling(D.MaxSpeedKmh * 1.05 / 10) * 10);
            double lw = Math.Max(1, s.Num("linewidth", 3) * U);
            Color cc = s.Col("current", "#FFFF80"), bc = s.Col("best", "#00FFFF"), gc = s.Col("gatecolor", "#FFFFFF");
            double fill = Math.Max(0, Math.Min(1, s.Num("fill", 0.10))), back = Math.Max(0, Math.Min(1, s.Num("back", 0.12)));
            Func<double, double> X = d => p.X + Math.Max(0, Math.Min(span, d)) / span * w;
            Func<double, double> Y = v => p.Y + h - Math.Max(0, Math.Min(vmax, double.IsNaN(v) ? 0 : v)) / vmax * h;

            dc.PushOpacity(Math.Max(0, Math.Min(1, s.Num("opacity", 1))));
            if (back > 0) dc.DrawRectangle(Br(Color.FromArgb((byte)(back * 255), 0, 0, 0)), null, new Rect(p.X, p.Y, w, h));
            double fs = Math.Max(8, s.Num("labelsize", 22) * U), sw = Math.Max(1, fs / 18);
            if (s.Bool("gates", true))
            {
                Pen gp = new Pen(Br(Color.FromArgb(80, gc.R, gc.G, gc.B)), Math.Max(1, 2 * U)) { DashStyle = new DashStyle(new double[] { 3, 3 }, 0) };
                gp.Freeze();
                for (int j = 0; j < D.GraphGates.Length; j++)
                {
                    double x = Math.Round(X(D.GraphGates[j])) + 0.5;
                    dc.DrawLine(gp, new Point(x, p.Y), new Point(x, p.Y + h));
                    string name = st.SecNames != null && j < st.SecNames.Length ? st.SecNames[j] : "S" + (j + 1).ToString(Inv);
                    Text(dc, name, fs, Color.FromArgb(200, gc.R, gc.G, gc.B), x + 6 * U, p.Y + 4 * U, 0, 0, sw, Colors.Black);
                }
            }
            Text(dc, "km/h", fs * 0.9, Color.FromArgb(200, 255, 255, 255), p.X + w - 6 * U, p.Y + 4 * U, 1, 0, sw, Colors.Black);

            int stride = 1;   // 1 m ごとの点を、だいたい画面の 1 画素に 1 点まで間引く
            if (st.GraphBest != null) stride = Math.Max(1, (int)(st.GraphBest.V.Length / Math.Max(1, w)));
            Pen edge = P(Colors.Black, lw + 2 * Math.Max(1, U * 1.5));
            CompareRun b = st.GraphBest;
            if (b != null && b.V.Length > 1)
            {
                int n = b.V.Length;
                StreamGeometry area = new StreamGeometry(), line = new StreamGeometry();
                using (StreamGeometryContext g = area.Open())
                {
                    g.BeginFigure(new Point(X(0), Y(0)), true, true);
                    for (int i = 0; i < n; i += stride) g.LineTo(new Point(X(i * RunCompare.Step), Y(b.V[i])), false, false);
                    g.LineTo(new Point(X((n - 1) * RunCompare.Step), Y(b.V[n - 1])), false, false);
                    g.LineTo(new Point(X((n - 1) * RunCompare.Step), Y(0)), false, false);
                }
                using (StreamGeometryContext g = line.Open())
                {
                    g.BeginFigure(new Point(X(0), Y(b.V[0])), false, false);
                    for (int i = stride; i < n; i += stride) g.LineTo(new Point(X(i * RunCompare.Step), Y(b.V[i])), true, true);
                    g.LineTo(new Point(X((n - 1) * RunCompare.Step), Y(b.V[n - 1])), true, true);
                }
                area.Freeze(); line.Freeze();
                if (fill > 0) dc.DrawGeometry(Br(Color.FromArgb((byte)(fill * 255), bc.R, bc.G, bc.B)), null, area);
                dc.DrawGeometry(null, edge, line);
                dc.DrawGeometry(null, P(bc, lw), line);
            }
            CompareRun c = st.GraphCur;
            if (c != null && c.V.Length > 1)
            {
                // 経過時間まで (T は距離の順に増える)
                int k = Array.BinarySearch(c.T, st.GraphEl);
                if (k < 0) k = ~k - 1;
                k = Math.Max(0, Math.Min(c.T.Length - 1, k));
                double lastX = k * RunCompare.Step, lastV = c.V[k];
                if (k + 1 < c.T.Length && c.T[k + 1] > c.T[k])
                {
                    double u = Math.Max(0, Math.Min(1, (st.GraphEl - c.T[k]) / (c.T[k + 1] - c.T[k])));
                    lastX = (k + u) * RunCompare.Step; lastV = c.V[k] + (c.V[k + 1] - c.V[k]) * u;
                }
                StreamGeometry line = new StreamGeometry();
                using (StreamGeometryContext g = line.Open())
                {
                    g.BeginFigure(new Point(X(0), Y(c.V[0])), false, false);
                    for (int i = stride; i <= k; i += stride) g.LineTo(new Point(X(i * RunCompare.Step), Y(c.V[i])), true, true);
                    g.LineTo(new Point(X(lastX), Y(lastV)), true, true);
                }
                line.Freeze();
                dc.DrawGeometry(null, edge, line);
                dc.DrawGeometry(null, P(cc, lw * 1.3), line);
                double r = Math.Max(2, s.Num("dotsize", 8) * U);
                dc.DrawEllipse(Br(cc), P(Colors.Black, Math.Max(1, 2 * U)), new Point(X(lastX), Y(lastV)), r, r);
            }
            dc.Pop();
        }

        void DrawSector(DrawingContext dc, OverlayState st)
        {
            OverlaySection s = L["sector"];
            int M = st.SecNames == null ? 0 : st.SecNames.Length;
            if (M == 0) return;
            double size = s.Num("size", 30) * U, cell = Cell(size), lh = size * 1.1, sw = Math.Max(1, size / 18);
            int n = Math.Max(0, s.Int("laps", 3));
            const int colw = 7, labw = 6;   // 1 列の文字数 ("+12.345" が入る幅), 行の名前の幅
            bool gym = D.Def.IsGymkhana;
            double w = cell * (labw + (colw + 1) * M), h = gym ? lh * 3 : lh * (5 + n);
            Point p = Place(s, w, h);
            double lapBottom;
            if (gym && L.Shown("laptime") && _bottom.TryGetValue("laptime", out lapBottom))
                p = new Point(_lapRight - w, lapBottom + lh * 0.5);   // ラップタイムの下に、右端をそろえて置く
            double y = p.Y;
            _sectorBox = new Rect(p.X, p.Y, w, h);
            PanelBack(dc, s, _sectorBox);
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
            if (gym) return;
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
