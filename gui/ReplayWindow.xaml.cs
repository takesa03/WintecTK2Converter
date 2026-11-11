using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Tk2Gui
{
    /// <summary>比較再生する 1 周。コントロールラインの通過 (周回の開始) を 0 秒として、一定間隔 (Step) の位置を持つ。</summary>
    public sealed class ReplayLap
    {
        public const double Step = 0.05;                  // 再生用に作り直した位置の間隔 [s]
        public string Name, Label;                        // 凡例の名前 / 地図上の短い名前
        public double LapTime;                            // ラップタイム = 再生の長さ [s]
        public Color Color;
        public double[] X, Y, D;                          // 局所平面 (m) の位置と、スタートからの走行距離
        public double Total;                              // 走行距離の合計

        int N { get { return X.Length; } }

        /// <summary>GPS の点 (通算秒 T) から、周回の開始時刻 start からの位置を作る。点は滑らかな曲線 (Catmull-Rom) でつなぐ。</summary>
        public static ReplayLap Build(string name, string label, double start, double lapTime, List<GpsSample> gps, double lat0, double lon0, Color color)
        {
            double ky = 111132.0, kx = 111320.0 * Math.Cos(lat0 * Math.PI / 180);
            int lo = LowerBound(gps, start - 6), hi = LowerBound(gps, start + lapTime + 6);
            hi = Math.Min(gps.Count - 1, hi + 1);
            if (hi - lo < 3) throw new InvalidOperationException("この周回の GPS の点が足りません: " + name);
            int n = hi - lo + 1;
            double[] T = new double[n], X = new double[n], Y = new double[n];
            for (int i = 0; i < n; i++)
            {
                GpsSample g = gps[lo + i];
                T[i] = g.T; X[i] = (g.Lon - lon0) * kx; Y[i] = (g.Lat - lat0) * ky;
            }
            int m = (int)Math.Ceiling(lapTime / Step) + 1;
            ReplayLap r = new ReplayLap { Name = name, Label = label, LapTime = lapTime, Color = color, X = new double[m], Y = new double[m], D = new double[m] };
            for (int i = 0; i < m; i++)
            {
                double t = Math.Min(lapTime, i * Step) + start;
                Eval(T, X, Y, t, out r.X[i], out r.Y[i]);
                if (i > 0) r.D[i] = r.D[i - 1] + Math.Sqrt(Math.Pow(r.X[i] - r.X[i - 1], 2) + Math.Pow(r.Y[i] - r.Y[i - 1], 2));
            }
            r.Total = r.D[m - 1];
            return r;
        }

        static int LowerBound(List<GpsSample> g, double t)
        {
            int a = 0, b = g.Count - 1;
            while (a < b) { int mid = (a + b) / 2; if (g[mid].T < t) a = mid + 1; else b = mid; }
            return a;
        }

        // 時刻 t の位置。点の間が 3 秒を超えて離れているときは (欠測) 直線でつなぐ
        static void Eval(double[] T, double[] X, double[] Y, double t, out double x, out double y)
        {
            int n = T.Length;
            int i = Array.BinarySearch(T, t);
            if (i < 0) i = ~i - 1;
            i = Math.Max(0, Math.Min(n - 2, i));
            double dt = T[i + 1] - T[i];
            double u = dt > 0 ? (t - T[i]) / dt : 0;
            u = Math.Max(0, Math.Min(1, u));
            int p0 = i > 0 && T[i] - T[i - 1] <= 3 ? i - 1 : i;
            int p3 = i + 2 < n && T[i + 2] - T[i + 1] <= 3 ? i + 2 : i + 1;
            if (dt > 3) { p0 = i; p3 = i + 1; }
            x = CR(X[p0], X[i], X[i + 1], X[p3], u);
            y = CR(Y[p0], Y[i], Y[i + 1], Y[p3], u);
        }

        static double CR(double a, double b, double c, double d, double u)
        {
            return 0.5 * ((2 * b) + (-a + c) * u + (2 * a - 5 * b + 4 * c - d) * u * u + (-a + 3 * b - 3 * c + d) * u * u * u);
        }

        void Locate(double t, out int i, out double u)
        {
            double f = t / Step;
            if (f <= 0) { i = 0; u = 0; }
            else if (f >= N - 1) { i = N - 2; u = 1; }
            else { i = (int)f; u = f - i; }
        }

        public void PosAt(double t, out double x, out double y)
        {
            int i; double u; Locate(t, out i, out u);
            x = X[i] + (X[i + 1] - X[i]) * u; y = Y[i] + (Y[i + 1] - Y[i]) * u;
        }

        public double DistAt(double t)
        {
            int i; double u; Locate(t, out i, out u);
            return D[i] + (D[i + 1] - D[i]) * u;
        }

        /// <summary>時刻 t の前後 0.3 秒の移動から求めた速度 [km/h]</summary>
        public double SpeedAt(double t)
        {
            double a = Math.Max(0, t - 0.3), b = Math.Min(LapTime, t + 0.3);
            if (b - a < 1e-6) return 0;
            return (DistAt(b) - DistAt(a)) / (b - a) * 3.6;
        }

        /// <summary>周回の進み具合 (0〜1)</summary>
        public double ProgressAt(double t) { return Total > 0 ? DistAt(Math.Max(0, Math.Min(LapTime, t))) / Total : 0; }

        /// <summary>進み具合 p (0〜1) に着く時刻 [s]</summary>
        public double TimeAtProgress(double p)
        {
            double target = Math.Max(0, Math.Min(1, p)) * Total;
            int lo = 0, hi = N - 1;
            while (lo < hi) { int mid = (lo + hi) / 2; if (D[mid] < target) lo = mid + 1; else hi = mid; }
            if (lo == 0) return 0;
            double d0 = D[lo - 1], d1 = D[lo];
            double u = d1 > d0 ? (target - d0) / (d1 - d0) : 0;
            return Math.Min(LapTime, (lo - 1 + u) * Step);
        }
    }

    public partial class ReplayWindow : Window
    {
        readonly CourseDefinition _def;
        readonly List<ReplayLap> _laps;
        readonly double _lat0, _lon0, _kx, _ky;
        readonly double _maxDur;
        double _t;
        bool _playing, _sliderBusy;
        double _speed = 1;
        DateTime _last;
        double _scale, _ox, _oy, _cx, _cy;                  // 地図の拡大率と中心
        Ellipse[] _dots; TextBlock[] _labels;
        TextBlock[] _tLap, _tSpeed, _tGap;
        Polyline[] _traces;
        int _ref;
        // 地図の描き方: コース・ライン・軌跡は実際の位置 (m) で 1 枚の層に一度だけ描き、拡大・移動は表示の変換だけで行う
        Canvas _world;
        MatrixTransform _xf;
        readonly List<Shape> _pxShapes = new List<Shape>();      // 画面上で一定の太さにする線 (Tag = 太さ [px])
        Polyline _road;
        TextBlock[] _gateLabels; Point[] _gateLabelAt;
        Line _scaleBar; TextBlock _scaleText;
        double _fitScale, _fitOx, _fitOy, _appliedScale;
        bool _follow;                                             // 基準の周回の自車位置を中心に拡大する
        double _zoomWidthM = 120;                                 // 拡大表示のときの、画面の横幅に入る距離 [m]
        const double RoadWidthM = 10;                             // 路面の幅の目安 [m]
        // 背景の航空写真 (国土地理院の地理院タイル「全国最新写真 (シームレス)」)
        Canvas _photo;
        readonly Dictionary<string, Image> _tiles = new Dictionary<string, Image>();
        int _tileZ = -1;
        static readonly HttpClient Http = MakeHttp();
        static readonly SemaphoreSlim Downloads = new SemaphoreSlim(4);
        const string TileUrl = "https://cyberjapandata.gsi.go.jp/xyz/seamlessphoto/{0}/{1}/{2}.jpg";
        static readonly string TileCache = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WintecTK2Converter", "tiles", "seamlessphoto");
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static readonly Color[] Palette =
        {
            Color.FromRgb(0xFF, 0x5A, 0x5F), Color.FromRgb(0x4F, 0xA3, 0xFF), Color.FromRgb(0x4C, 0xD9, 0x64), Color.FromRgb(0xFF, 0xC8, 0x3D),
            Color.FromRgb(0xC0, 0x7B, 0xFF), Color.FromRgb(0x2E, 0xE6, 0xD6), Color.FromRgb(0xFF, 0x8F, 0x3D), Color.FromRgb(0xF2, 0x7F, 0xC8),
            Color.FromRgb(0xB4, 0xD8, 0x3A), Color.FromRgb(0xA0, 0xA0, 0xA0)
        };

        public ReplayWindow(CourseDefinition def, List<ReplayLap> laps)
        {
            InitializeComponent();
            _def = def; _laps = laps;
            _lat0 = def.Lat0; _lon0 = def.Lon0;
            _ky = 111132.0; _kx = 111320.0 * Math.Cos(_lat0 * Math.PI / 180);
            _maxDur = laps.Max(l => l.LapTime);
            TimeSlider.Maximum = _maxDur;
            // 基準は、いちばん速い周回
            _ref = 0;
            for (int i = 1; i < laps.Count; i++) if (laps[i].LapTime < laps[_ref].LapTime) _ref = i;
            foreach (ReplayLap l in laps) RefCombo.Items.Add(l.Name);
            RefCombo.SelectedIndex = _ref;
            BuildLegend();
            Title = "周回の比較再生 - " + def.Name + " (" + laps.Count + " 周)  " + App.VersionText;
            Loaded += (s, e) => { System.Windows.Media.CompositionTarget.Rendering += OnRender; Redraw(); };
            Closed += (s, e) => System.Windows.Media.CompositionTarget.Rendering -= OnRender;
            _last = DateTime.Now;
            _playing = true; PlayButton.Content = "⏸ 一時停止";
        }

        void BuildLegend()
        {
            int n = _laps.Count;
            _tLap = new TextBlock[n]; _tSpeed = new TextBlock[n]; _tGap = new TextBlock[n];
            for (int i = 0; i < n; i++)
            {
                ReplayLap l = _laps[i];
                Grid g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                for (int c = 0; c < 3; c++) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) });
                Rectangle sw = new Rectangle { Width = 10, Height = 10, Fill = new SolidColorBrush(l.Color), RadiusX = 5, RadiusY = 5, VerticalAlignment = VerticalAlignment.Center };
                TextBlock name = new TextBlock { Text = l.Name, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
                _tLap[i] = new TextBlock { Text = LapAnalyzer.FormatTime(l.LapTime), TextAlignment = TextAlignment.Right, FontFamily = new FontFamily("Consolas"), VerticalAlignment = VerticalAlignment.Center };
                _tSpeed[i] = new TextBlock { TextAlignment = TextAlignment.Right, FontFamily = new FontFamily("Consolas"), VerticalAlignment = VerticalAlignment.Center };
                _tGap[i] = new TextBlock { TextAlignment = TextAlignment.Right, FontFamily = new FontFamily("Consolas"), FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(name, 1); Grid.SetColumn(_tLap[i], 2); Grid.SetColumn(_tSpeed[i], 3); Grid.SetColumn(_tGap[i], 4);
                g.Children.Add(sw); g.Children.Add(name); g.Children.Add(_tLap[i]); g.Children.Add(_tSpeed[i]); g.Children.Add(_tGap[i]);
                LegendPanel.Children.Add(g);
            }
        }

        // ---------------- 地図 ----------------

        Point ToScreen(double x, double y) { return new Point(_cx + (x - _ox) * _scale, _cy - (y - _oy) * _scale); }
        static Point W(double x, double y) { return new Point(x, -y); }       // 層の座標 (北が上なので y を反転)

        Shape Px(Shape sh, double px) { sh.Tag = px; _pxShapes.Add(sh); return sh; }

        void Redraw()
        {
            double w = MapCanvas.ActualWidth, h = MapCanvas.ActualHeight;
            if (w < 50 || h < 50) return;
            MapCanvas.Children.Clear();
            _pxShapes.Clear();
            _appliedScale = 0;
            _cx = w / 2; _cy = h / 2;

            // 全体表示の範囲 (中心線と、すべての周回の位置)
            double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
            Action<double, double> grow = (x, y) => { minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y); };
            foreach (LatLon p in _def.Centerline) grow((p.Lon - _lon0) * _kx, (p.Lat - _lat0) * _ky);
            foreach (ReplayLap l in _laps) for (int i = 0; i < l.X.Length; i += 10) grow(l.X[i], l.Y[i]);
            double spanX = Math.Max(1, maxX - minX), spanY = Math.Max(1, maxY - minY);
            const double margin = 40;
            _fitScale = Math.Min((w - 2 * margin) / spanX, (h - 2 * margin) / spanY);
            _fitOx = (minX + maxX) / 2; _fitOy = (minY + maxY) / 2;

            _world = new Canvas();
            _xf = new MatrixTransform();
            _world.RenderTransform = _xf;
            MapCanvas.Children.Add(_world);
            _photo = new Canvas();
            _world.Children.Add(_photo);
            _tiles.Clear(); _tileZ = -1;

            // 路面 (中心線を太く) と、中心の破線
            PointCollection road = new PointCollection();
            foreach (LatLon p in _def.Centerline) road.Add(W((p.Lon - _lon0) * _kx, (p.Lat - _lat0) * _ky));
            if (road.Count > 0) road.Add(road[0]);
            _road = new Polyline { Points = road, Stroke = new SolidColorBrush(Color.FromRgb(0x3A, 0x40, 0x4A)), StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
            _world.Children.Add(_road);
            _world.Children.Add(Px(new Polyline { Points = road, Stroke = new SolidColorBrush(Color.FromRgb(0x5A, 0x62, 0x70)), StrokeDashArray = new DoubleCollection { 4, 4 } }, 1));

            // ライン (コントロール = 白, セクター = 黄)。名前は画面の上に、一定の大きさで出す
            int ng = _def.Gates.Count;
            _gateLabels = new TextBlock[ng]; _gateLabelAt = new Point[ng];
            for (int k = 0; k < ng; k++)
            {
                Gate g = _def.Gates[k];
                double ax = (g.A.Lon - _lon0) * _kx, ay = (g.A.Lat - _lat0) * _ky, bx = (g.B.Lon - _lon0) * _kx, by = (g.B.Lat - _lat0) * _ky;
                Brush c = k == 0 ? Brushes.White : new SolidColorBrush(Color.FromRgb(0xFF, 0xD2, 0x3F));
                Point a = W(ax, ay), b = W(bx, by);
                _world.Children.Add(Px(new Line { X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y, Stroke = c }, k == 0 ? 3 : 2));
                _gateLabels[k] = new TextBlock { Text = k == 0 ? "Control" : "S" + k, Foreground = c, FontSize = 11 };
                _gateLabelAt[k] = new Point(bx, by);
                MapCanvas.Children.Add(_gateLabels[k]);
            }

            // 軌跡 (拡大したときに走行ラインの違いが分かるように、点を間引きすぎない)
            _traces = new Polyline[_laps.Count];
            for (int i = 0; i < _laps.Count; i++)
            {
                ReplayLap l = _laps[i];
                PointCollection pc = new PointCollection();
                for (int j = 0; j < l.X.Length; j += 2) pc.Add(W(l.X[j], l.Y[j]));
                _traces[i] = new Polyline { Points = pc, Stroke = new SolidColorBrush(l.Color), StrokeLineJoin = PenLineJoin.Round,
                    Visibility = TraceCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed };
                _world.Children.Add(Px(_traces[i], 1.5));
            }

            // 走行位置の丸と名前 (画面の上に、一定の大きさで出す)
            _dots = new Ellipse[_laps.Count]; _labels = new TextBlock[_laps.Count];
            for (int i = 0; i < _laps.Count; i++)
            {
                _dots[i] = new Ellipse { Width = 14, Height = 14, Fill = new SolidColorBrush(_laps[i].Color), Stroke = Brushes.White, StrokeThickness = 2 };
                _labels[i] = new TextBlock { Text = _laps[i].Label, Foreground = new SolidColorBrush(_laps[i].Color), FontWeight = FontWeights.Bold, FontSize = 12,
                    Visibility = LabelCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed };
                MapCanvas.Children.Add(_dots[i]); MapCanvas.Children.Add(_labels[i]);
            }

            // 縮尺 (拡大表示のときだけ)
            _scaleBar = new Line { Stroke = Brushes.White, StrokeThickness = 3 };
            _scaleText = new TextBlock { Foreground = Brushes.White, FontSize = 12 };
            MapCanvas.Children.Add(_scaleBar); MapCanvas.Children.Add(_scaleText);
            UpdateFrame();
        }

        // 表示の位置と拡大率を決める (拡大表示では、毎フレーム基準の周回の位置を中心にする)
        void SetView()
        {
            if (_xf == null) return;
            double w = MapCanvas.ActualWidth, h = MapCanvas.ActualHeight;
            if (_follow)
            {
                ReplayLap rf = _laps[_ref];
                double x, y; rf.PosAt(Math.Min(_t, rf.LapTime), out x, out y);
                _scale = w / _zoomWidthM; _ox = x; _oy = y;
            }
            else { _scale = _fitScale; _ox = _fitOx; _oy = _fitOy; }
            _xf.Matrix = new Matrix(_scale, 0, 0, _scale, _cx - _ox * _scale, _cy + _oy * _scale);

            if (Math.Abs(_scale - _appliedScale) > 1e-9)
            {
                // 拡大率が変わったら、線の太さを画面上で一定にする (路面は実際の幅、ただし細くなりすぎない)
                foreach (Shape sh in _pxShapes) sh.StrokeThickness = (double)sh.Tag / _scale;
                _road.StrokeThickness = Math.Max(RoadWidthM, 8 / _scale);
                bool photo = PhotoCheck.IsChecked == true;   // 写真の上では、全体表示でも軌跡を濃く・太めにする (見えにくいため)
                foreach (Polyline p in _traces)
                {
                    p.StrokeThickness = (_follow ? 2.5 : photo ? 2 : 1.5) / _scale;
                    p.Opacity = _follow || photo ? 0.9 : 0.45;
                }
                _appliedScale = _scale;
                UpdateScaleBar(w, h);
            }
            for (int k = 0; k < _gateLabels.Length; k++)
            {
                Point q = ToScreen(_gateLabelAt[k].X, _gateLabelAt[k].Y);
                Canvas.SetLeft(_gateLabels[k], q.X + 3); Canvas.SetTop(_gateLabels[k], q.Y - 8);
            }
            EnsureTiles(w, h);
        }

        // ---------------- 航空写真 (地理院タイル) ----------------

        static HttpClient MakeHttp()
        {
            HttpClient c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("WintecTK2Converter/1.0");
            return c;
        }

        static int TileX(double lon, int z) { return (int)Math.Floor((lon + 180) / 360 * (1 << z)); }
        static int TileY(double lat, int z)
        {
            double r = lat * Math.PI / 180;
            return (int)Math.Floor((1 - Math.Log(Math.Tan(r) + 1 / Math.Cos(r)) / Math.PI) / 2 * (1 << z));
        }
        static double TileLon(int x, int z) { return (double)x / (1 << z) * 360 - 180; }
        static double TileLat(int y, int z) { return Math.Atan(Math.Sinh(Math.PI * (1 - 2.0 * y / (1 << z)))) * 180 / Math.PI; }

        // いま見えている範囲の写真を並べる (足りないものだけ読み込む)。拡大率に合う解像度 (ズームレベル) を選ぶ
        void EnsureTiles(double w, double h)
        {
            if (_photo == null) return;
            bool on = PhotoCheck.IsChecked == true;
            _photo.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            _road.Visibility = on ? Visibility.Collapsed : Visibility.Visible;     // 写真のときは、写真のコースをそのまま見せる
            PhotoCredit.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            if (!on) return;

            double latC = _lat0 + _oy / _ky;
            double mpp = 1 / _scale;                                               // 画面の 1 px が何 m か
            int z = (int)Math.Round(Math.Log(156543.034 * Math.Cos(latC * Math.PI / 180) / mpp, 2));
            z = Math.Max(10, Math.Min(18, z));
            if (z != _tileZ) { _photo.Children.Clear(); _tiles.Clear(); _tileZ = z; }

            double x0 = _ox - _cx / _scale, x1 = _ox + (w - _cx) / _scale;
            double y0 = _oy - (h - _cy) / _scale, y1 = _oy + _cy / _scale;
            int tx0 = TileX(_lon0 + x0 / _kx, z), tx1 = TileX(_lon0 + x1 / _kx, z);
            int ty0 = TileY(_lat0 + y1 / _ky, z), ty1 = TileY(_lat0 + y0 / _ky, z);
            if ((long)(tx1 - tx0 + 1) * (ty1 - ty0 + 1) > 300) return;            // 念のため (広すぎる範囲は読まない)
            for (int tx = tx0; tx <= tx1; tx++)
                for (int ty = ty0; ty <= ty1; ty++)
                {
                    string key = z + "/" + tx + "/" + ty;
                    if (_tiles.ContainsKey(key)) continue;
                    double xl = (TileLon(tx, z) - _lon0) * _kx, xr = (TileLon(tx + 1, z) - _lon0) * _kx;
                    double yt = (TileLat(ty, z) - _lat0) * _ky, yb = (TileLat(ty + 1, z) - _lat0) * _ky;
                    double ov = (xr - xl) * 0.004;                                  // 継ぎ目に細い隙間が見えないよう、わずかに重ねる
                    Image img = new Image { Stretch = Stretch.Fill, Width = xr - xl + ov, Height = yt - yb + ov };
                    Canvas.SetLeft(img, xl); Canvas.SetTop(img, -yt);
                    _photo.Children.Add(img);
                    _tiles[key] = img;
                    LoadTile(img, z, tx, ty);
                }
        }

        // 写真を 1 枚読む。保存してあればそれを使い、なければ国土地理院から取ってきて保存する
        async void LoadTile(Image img, int z, int x, int y)
        {
            string path = System.IO.Path.Combine(TileCache, z.ToString(Inv), x.ToString(Inv), y.ToString(Inv) + ".jpg");
            byte[] data = null;
            try
            {
                if (File.Exists(path)) data = await Task.Run(() => File.ReadAllBytes(path));
                else
                {
                    await Downloads.WaitAsync();
                    try
                    {
                        if (img.Parent == null) return;                              // 拡大率が変わって、もう要らない
                        data = await Http.GetByteArrayAsync(string.Format(Inv, TileUrl, z, x, y));
                    }
                    finally { Downloads.Release(); }
                    string dir = System.IO.Path.GetDirectoryName(path);
                    byte[] d = data;
                    await Task.Run(() => { Directory.CreateDirectory(dir); File.WriteAllBytes(path, d); });
                }
                BitmapImage bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.StreamSource = new MemoryStream(data);
                bi.EndInit();
                bi.Freeze();
                img.Source = bi;
            }
            catch (Exception)
            {
                // 写真がない場所 (海など)・通信できないときは、その部分は背景なしのまま
            }
        }

        void UpdateScaleBar(double w, double h)
        {
            Visibility v = _follow ? Visibility.Visible : Visibility.Collapsed;
            _scaleBar.Visibility = v; _scaleText.Visibility = v;
            if (!_follow) return;
            double target = _zoomWidthM / 5, len = 1;
            foreach (double c in new double[] { 1, 2, 5, 10, 20, 50, 100, 200, 500, 1000 }) if (c <= target) len = c;
            double px = len * _scale;
            _scaleBar.X1 = 20; _scaleBar.X2 = 20 + px; _scaleBar.Y1 = _scaleBar.Y2 = h - 20;
            _scaleText.Text = len.ToString("0", Inv) + " m";
            Canvas.SetLeft(_scaleText, 20); Canvas.SetTop(_scaleText, h - 40);
            ZoomText.Text = string.Format(Inv, "表示幅 {0:0} m", _zoomWidthM);
        }

        void UpdateFrame()
        {
            if (_dots == null) return;
            SetView();
            ReplayLap rf = _laps[_ref];
            for (int i = 0; i < _laps.Count; i++)
            {
                ReplayLap l = _laps[i];
                double tt = Math.Min(_t, l.LapTime);
                double x, y; l.PosAt(tt, out x, out y);
                Point p = ToScreen(x, y);
                Canvas.SetLeft(_dots[i], p.X - 7); Canvas.SetTop(_dots[i], p.Y - 7);
                Canvas.SetLeft(_labels[i], p.X + 9); Canvas.SetTop(_labels[i], p.Y - 18);
                _tSpeed[i].Text = _t >= l.LapTime ? "終了" : l.SpeedAt(tt).ToString("0", Inv) + " km/h";
                if (i == _ref) { _tGap[i].Text = "基準"; _tGap[i].Foreground = Brushes.Gray; }
                else
                {
                    // 同じ進み具合に基準の周回が着いた時刻との差 (＋ = 基準より遅れ)
                    double gap = tt - rf.TimeAtProgress(l.ProgressAt(tt));
                    if (_t >= l.LapTime && _t >= rf.LapTime) gap = l.LapTime - rf.LapTime;
                    _tGap[i].Text = (gap >= 0 ? "+" : "-") + Math.Abs(gap).ToString("0.00", Inv);
                    _tGap[i].Foreground = gap > 0.005 ? Brushes.Firebrick : gap < -0.005 ? Brushes.SeaGreen : Brushes.Gray;
                }
            }
            TimeText.Text = Fmt(_t) + " / " + Fmt(_maxDur);
            _sliderBusy = true; TimeSlider.Value = _t; _sliderBusy = false;
        }

        static string Fmt(double s) { int m = (int)(s / 60); return string.Format(Inv, "{0}:{1:00.0}", m, s - m * 60); }

        // ---------------- 再生 ----------------

        void OnRender(object sender, EventArgs e)
        {
            DateTime now = DateTime.Now;
            double dt = (now - _last).TotalSeconds; _last = now;
            if (!_playing) return;
            _t += dt * _speed;
            if (_t >= _maxDur)
            {
                if (LoopCheck.IsChecked == true) _t = 0;
                else { _t = _maxDur; _playing = false; PlayButton.Content = "▶ 再生"; }
            }
            UpdateFrame();
        }

        void Play_Click(object sender, RoutedEventArgs e)
        {
            if (!_playing && _t >= _maxDur) _t = 0;
            _playing = !_playing;
            _last = DateTime.Now;
            PlayButton.Content = _playing ? "⏸ 一時停止" : "▶ 再生";
        }

        void Restart_Click(object sender, RoutedEventArgs e) { _t = 0; UpdateFrame(); }

        void Speed_Changed(object sender, SelectionChangedEventArgs e)
        {
            ComboBoxItem it = SpeedCombo.SelectedItem as ComboBoxItem;
            double v;
            if (it != null && double.TryParse(it.Tag as string, NumberStyles.Float, Inv, out v)) _speed = v;
        }

        void Slider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_sliderBusy) return;
            _t = TimeSlider.Value;
            UpdateFrame();
        }

        void Ref_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (RefCombo.SelectedIndex >= 0 && _laps != null) { _ref = RefCombo.SelectedIndex; UpdateFrame(); }
        }

        void Photo_Changed(object sender, RoutedEventArgs e)
        {
            _appliedScale = 0;   // 軌跡の濃さ・太さを直す
            if (_xf != null) UpdateFrame();
        }

        void View_Changed(object sender, RoutedEventArgs e)
        {
            if (_traces == null || _labels == null) return;
            foreach (Polyline p in _traces) p.Visibility = TraceCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            foreach (TextBlock t in _labels) t.Visibility = LabelCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }

        // ---------------- 表示の切り替え (全体 / 基準の周回を中心に拡大) ----------------

        void ViewMode_Changed(object sender, SelectionChangedEventArgs e)
        {
            _follow = ViewCombo.SelectedIndex == 1;
            if (ZoomInButton != null) { ZoomInButton.IsEnabled = _follow; ZoomOutButton.IsEnabled = _follow; }
            if (ZoomText != null) ZoomText.Text = _follow ? string.Format(Inv, "表示幅 {0:0} m", _zoomWidthM) : "";
            _appliedScale = 0;
            UpdateFrame();
        }

        void Zoom(double factor)
        {
            _zoomWidthM = Math.Max(20, Math.Min(3000, _zoomWidthM * factor));
            _appliedScale = 0;
            UpdateFrame();
        }

        void ZoomIn_Click(object sender, RoutedEventArgs e) { Zoom(1 / 1.5); }
        void ZoomOut_Click(object sender, RoutedEventArgs e) { Zoom(1.5); }

        // マウスホイール: 拡大表示のときは拡大・縮小。全体表示のときは、拡大表示に切り替える
        void Map_MouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            if (!_follow) { if (e.Delta > 0) ViewCombo.SelectedIndex = 1; return; }
            Zoom(e.Delta > 0 ? 1 / 1.25 : 1.25);
        }

        void Map_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (IsLoaded) Redraw();
        }
    }
}
