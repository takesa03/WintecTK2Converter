using System;
using System.Windows;
using System.Windows.Media;

namespace Tk2Gui
{
    // =====================================================================
    //  サブ動画 (ペダルなど別のカメラ) をメインの映像に重ねる位置の計算。設計は docs/subvideo_design.md
    //  枠の位置・大きさはレイアウト ini の [subvideo] (1920x1080 基準)、トリミングはサブ動画ごと (VideoStore.Entry.Crop)
    //  トリミングした絵を、縦横比を変えずに枠に収まる最大の大きさにして、align で寄せる
    //  重なり順: メインの映像 → サブ動画 → 表示 (速度計など)
    // =====================================================================
    public sealed class SubVideoLayout
    {
        public bool Show;
        public string Anchor = "bottomleft";
        public double X = 15, Y = 220, Width = 480, Height = 270;   // 1920x1080 基準
        public string Align = "center";                              // center / left / right / top / bottom (組み合わせ可: "top left" など)
        public double Border = 2;                                    // 枠線の太さ (1080p 基準。0 で無し)
        public Color BorderColor = Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF);
        public double Opacity = 1.0;

        public static SubVideoLayout From(OverlayLayout l)
        {
            OverlaySection s = l["subvideo"];
            SubVideoLayout v = new SubVideoLayout();
            v.Show = l.Shown("subvideo");
            v.Anchor = s.Str("anchor", v.Anchor).ToLowerInvariant();
            v.X = s.Num("x", v.X); v.Y = s.Num("y", v.Y);
            v.Width = Math.Max(16, s.Num("width", v.Width)); v.Height = Math.Max(16, s.Num("height", v.Height));
            v.Align = s.Str("align", v.Align).ToLowerInvariant();
            v.Border = Math.Max(0, s.Num("border", v.Border));
            v.BorderColor = s.Col("bordercolor", "#80FFFFFF");
            v.Opacity = Math.Max(0, Math.Min(1, s.Num("opacity", v.Opacity)));
            return v;
        }

        /// <summary>出力 W x H の画面での枠 (OverlayRenderer.Place と同じ anchor の規則。U = H / 1080)</summary>
        public Rect Box(double W, double H)
        {
            double u = H / 1080.0, w = Width * u, h = Height * u, x = X * u, y = Y * u;
            string a = Anchor ?? "topleft";
            double left = a.Contains("right") ? W - x - w : (a == "top" || a == "bottom" ? (W - w) / 2 + x : x);
            double top = a.Contains("bottom") ? H - y - h : y;
            return new Rect(left, top, w, h);
        }

        /// <summary>元の絵 (srcW x srcH) のうち、トリミングで残す範囲 (元の画素)</summary>
        public static Rect Source(double srcW, double srcH, double[] crop)
        {
            double l = C(crop, 0), t = C(crop, 1), r = C(crop, 2), b = C(crop, 3);
            return new Rect(srcW * l, srcH * t, Math.Max(1, srcW * (1 - l - r)), Math.Max(1, srcH * (1 - t - b)));
        }

        /// <summary>トリミングした絵を枠に収めたときの、出力画面での位置と大きさ</summary>
        public Rect Dest(double W, double H, double srcW, double srcH, double[] crop)
        {
            Rect box = Box(W, H), src = Source(srcW, srcH, crop);
            double k = Math.Min(box.Width / src.Width, box.Height / src.Height);
            double w = src.Width * k, h = src.Height * k;
            string a = Align ?? "center";
            double x = a.Contains("left") ? box.X : a.Contains("right") ? box.Right - w : box.X + (box.Width - w) / 2;
            double y = a.Contains("top") ? box.Y : a.Contains("bottom") ? box.Bottom - h : box.Y + (box.Height - h) / 2;
            return new Rect(x, y, w, h);
        }

        static double C(double[] c, int i)
        {
            if (c == null || i >= c.Length || double.IsNaN(c[i])) return 0;
            return Math.Max(0, Math.Min(0.49, c[i]));
        }
    }
}
