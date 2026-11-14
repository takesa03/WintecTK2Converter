using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Tk2Gui
{
    /// <summary>
    /// 距離ごとの比較: 選んだ走行 (周回) の速度と、基準とのタイム差を、スタートからの距離を横軸に並べて表示する。
    /// どこで差がついたかを見る用 (サイドターン・スラロームなど)。
    /// </summary>
    public partial class CompareWindow : Window
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        readonly CourseDefinition _def;
        readonly List<CompareRun> _runs;
        readonly double _startS, _span;
        int _ref;
        double _x0, _x1;                 // 表示している距離の範囲 [m]
        double _hover = double.NaN;      // マウスのある距離
        Point? _drag; double _dragX0, _dragX1;
        readonly List<TextBlock[]> _legendCells = new List<TextBlock[]>();
        const double L = 52, R = 12, T = 22, B = 22, Gap = 26;   // 余白

        public CompareWindow(CourseDefinition def, List<CompareRun> runs)
        {
            InitializeComponent();
            Theme.ApplyDark(this);
            _def = def; _runs = runs;
            RunCompare.Span(def, out _startS, out _span);
            _x0 = 0; _x1 = _span;
            Title = (def.IsGymkhana ? "距離ごとの比較 (走行 " : "距離ごとの比較 (周回 ") + runs.Count + ") - " + def.Name + "  " + App.VersionText;
            _ref = 0;
            for (int i = 1; i < runs.Count; i++) if (runs[i].LapTime < runs[_ref].LapTime) _ref = i;
            foreach (CompareRun r in runs) RefCombo.Items.Add(r.Name);
            RefCombo.SelectedIndex = _ref;
            BuildLegend();
            Settings.Track(this, "CompareWindow");
        }

        void BuildLegend()
        {
            LegendPanel.Children.Clear(); _legendCells.Clear();
            foreach (CompareRun r in _runs)
            {
                Grid g = new Grid { Margin = new Thickness(0, 1, 0, 1) };
                foreach (double w in new[] { 16.0, -1, 66, 52, 58 })
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = w < 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(w) });
                Rectangle sw = new Rectangle { Width = 10, Height = 10, Fill = new SolidColorBrush(r.Color), VerticalAlignment = VerticalAlignment.Center };
                g.Children.Add(sw);
                TextBlock[] cells = new TextBlock[4];
                string[] text = { r.Name, LapAnalyzer.FormatTime(r.LapTime), "", "" };
                for (int c = 0; c < 4; c++)
                {
                    cells[c] = new TextBlock { Text = text[c], TextAlignment = c == 0 ? TextAlignment.Left : TextAlignment.Right, TextTrimming = TextTrimming.CharacterEllipsis };
                    if (c == 0) cells[c].ToolTip = r.Name;
                    Grid.SetColumn(cells[c], c + 1);
                    g.Children.Add(cells[c]);
                }
                LegendPanel.Children.Add(g);
                _legendCells.Add(cells);
            }
        }

        void RefCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (RefCombo.SelectedIndex < 0) return;
            _ref = RefCombo.SelectedIndex;
            Redraw();
        }

        void ResetZoom_Click(object sender, RoutedEventArgs e) { _x0 = 0; _x1 = _span; Redraw(); }
        void Chart_SizeChanged(object sender, SizeChangedEventArgs e) { Redraw(); }

        double XtoS(double x)
        {
            double w = Chart.ActualWidth;
            return _x0 + (x - L) / Math.Max(1, w - L - R) * (_x1 - _x0);
        }

        void Chart_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            double s = Math.Max(_x0, Math.Min(_x1, XtoS(e.GetPosition(Chart).X)));
            double f = e.Delta > 0 ? 0.8 : 1.25;
            double w = Math.Max(20, Math.Min(_span, (_x1 - _x0) * f));
            double a = s - (s - _x0) / (_x1 - _x0) * w;
            _x0 = Math.Max(0, Math.Min(_span - w, a)); _x1 = _x0 + w;
            Redraw();
            e.Handled = true;
        }

        void Chart_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _drag = e.GetPosition(Chart); _dragX0 = _x0; _dragX1 = _x1;
            Chart.CaptureMouse();
        }

        void Chart_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _drag = null;
            Chart.ReleaseMouseCapture();
        }

        void Chart_MouseMove(object sender, MouseEventArgs e)
        {
            Point p = e.GetPosition(Chart);
            if (_drag.HasValue && e.LeftButton == MouseButtonState.Pressed)
            {
                double ds = (p.X - _drag.Value.X) / Math.Max(1, Chart.ActualWidth - L - R) * (_dragX1 - _dragX0);
                double w = _dragX1 - _dragX0;
                _x0 = Math.Max(0, Math.Min(_span - w, _dragX0 - ds)); _x1 = _x0 + w;
            }
            _hover = Math.Max(_x0, Math.Min(_x1, XtoS(p.X)));
            Redraw();
        }

        void Chart_MouseLeave(object sender, MouseEventArgs e)
        {
            _hover = double.NaN;
            Redraw();
        }

        static double At(double[] a, double s)
        {
            double k = s / RunCompare.Step;
            int i = (int)Math.Floor(k);
            if (i < 0) return a[0];
            if (i >= a.Length - 1) return a[a.Length - 1];
            return a[i] + (a[i + 1] - a[i]) * (k - i);
        }

        void Redraw()
        {
            Chart.Children.Clear();
            double w = Chart.ActualWidth, h = Chart.ActualHeight;
            if (w < 200 || h < 150 || _runs.Count == 0) return;
            double plotW = w - L - R;
            double hv = (h - T - B - Gap) * 0.58, hd = (h - T - B - Gap) - hv;     // 速度 / タイム差 の高さ
            double vy0 = T, dy0 = T + hv + Gap;
            Func<double, double> X = s => L + (s - _x0) / (_x1 - _x0) * plotW;
            int i0 = Math.Max(0, (int)Math.Floor(_x0 / RunCompare.Step)), i1 = Math.Min(_runs[0].T.Length - 1, (int)Math.Ceiling(_x1 / RunCompare.Step));

            // 縦軸の範囲 (表示している距離の範囲で)
            double vmax = 20, dmin = 0, dmax = 0;
            CompareRun rf = _runs[_ref];
            foreach (CompareRun r in _runs)
                for (int i = i0; i <= i1 && i < r.V.Length; i++)
                {
                    if (!double.IsNaN(r.V[i])) vmax = Math.Max(vmax, r.V[i]);
                    double d = r.T[i] - rf.T[i];
                    if (!double.IsNaN(d)) { dmin = Math.Min(dmin, d); dmax = Math.Max(dmax, d); }
                }
            vmax = Math.Ceiling(vmax / 20) * 20;
            double dr = Math.Max(0.5, Math.Max(dmax, -dmin));
            double dstep = dr <= 1 ? 0.25 : dr <= 2.5 ? 0.5 : dr <= 6 ? 1 : dr <= 15 ? 2.5 : 5;
            dr = Math.Ceiling(dr / dstep) * dstep;
            Func<double, double> YV = v => vy0 + (1 - v / vmax) * hv;
            Func<double, double> YD = d => dy0 + (1 - (d + dr) / (2 * dr)) * hd;
            Brush grid = new SolidColorBrush(Color.FromRgb(0x3A, 0x40, 0x4A));

            for (double v = 0; v <= vmax + 0.1; v += 20)
            {
                Chart.Children.Add(new Line { X1 = L, X2 = w - R, Y1 = YV(v), Y2 = YV(v), Stroke = grid });
                AddText(v.ToString("0", Inv), 6, YV(v) - 8, Theme.TextSecondary, 10);
            }
            AddText("km/h", 6, 2, Theme.TextSecondary, 10);
            for (double d = -dr; d <= dr + 1e-9; d += dstep)
            {
                Chart.Children.Add(new Line { X1 = L, X2 = w - R, Y1 = YD(d), Y2 = YD(d), Stroke = Math.Abs(d) < 1e-9 ? Theme.TextSecondary : grid });
                AddText((d > 0 ? "+" : "") + d.ToString(dstep < 1 ? "0.00" : "0.0", Inv), 4, YD(d) - 8, Theme.TextSecondary, 10);
            }
            AddText("差 (秒)", 6, dy0 - 18, Theme.TextSecondary, 10);

            // 距離の目盛り
            double range = _x1 - _x0, tick = range > 600 ? 100 : range > 250 ? 50 : range > 100 ? 20 : 10;
            for (double s = Math.Ceiling(_x0 / tick) * tick; s <= _x1; s += tick)
            {
                Chart.Children.Add(new Line { X1 = X(s), X2 = X(s), Y1 = h - B, Y2 = h - B + 4, Stroke = Theme.TextSecondary });
                AddText(s.ToString("0", Inv) + " m", X(s) - 12, h - B + 4, Theme.TextSecondary, 10);
            }

            // ライン (スタートからの距離)
            foreach (Gate g in _def.Gates)
            {
                double s = g.S - _startS;
                if (!_def.IsGymkhana) s = ((s % _def.LengthM) + _def.LengthM) % _def.LengthM;
                bool start = g.Name == CourseDefinition.StartGateName || g.Name == "ControlLine", finish = g.Name == CourseDefinition.FinishGateName;
                if (start && !_def.IsGymkhana) continue;     // サーキットのコントロールラインは両端
                if (s < _x0 || s > _x1) continue;
                Brush c = start ? Brushes.LimeGreen : finish ? Brushes.White : Brushes.DarkOrange;
                Chart.Children.Add(new Line { X1 = X(s), X2 = X(s), Y1 = T - 4, Y2 = h - B, Stroke = c, StrokeThickness = 1.5, Opacity = 0.8 });
                string name = start ? "Start" : finish ? "Finish" : g.Name.Replace("Sector", "S");
                AddText(name, X(s) + 3 + name.Length * 6 > w - 2 ? X(s) - 3 - name.Length * 6 : X(s) + 3, 2, c, 10);
            }

            // 線 (基準を最後に描いて上に出す)
            List<int> order = Enumerable.Range(0, _runs.Count).Where(i => i != _ref).ToList();
            order.Add(_ref);
            foreach (int ri in order)
            {
                CompareRun r = _runs[ri];
                Brush b = new SolidColorBrush(r.Color);
                Polyline pv = new Polyline { Stroke = b, StrokeThickness = ri == _ref ? 2 : 1.4, StrokeLineJoin = PenLineJoin.Round };
                Polyline pd = new Polyline { Stroke = b, StrokeThickness = ri == _ref ? 2 : 1.4, StrokeLineJoin = PenLineJoin.Round };
                int stride = Math.Max(1, (i1 - i0) / 1500);
                for (int i = i0; i <= i1; i += stride)
                {
                    double s = i * RunCompare.Step;
                    if (!double.IsNaN(r.V[i])) pv.Points.Add(new Point(X(s), YV(r.V[i])));
                    double d = r.T[i] - rf.T[i];
                    if (!double.IsNaN(d)) pd.Points.Add(new Point(X(s), YD(Math.Max(-dr, Math.Min(dr, d)))));
                }
                Chart.Children.Add(pv); Chart.Children.Add(pd);
            }

            // マウスの位置と凡例の値
            if (!double.IsNaN(_hover))
            {
                double x = X(_hover);
                Chart.Children.Add(new Line { X1 = x, X2 = x, Y1 = T, Y2 = h - B, Stroke = Brushes.White, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 3, 3 } });
                CursorText.Text = string.Format(Inv, "{0:0} m 地点  (基準: スタートから {1:0.00} 秒)", _hover, At(rf.T, _hover));
            }
            else CursorText.Text = "グラフにマウスを乗せると、その距離の値を出します";
            for (int i = 0; i < _runs.Count; i++)
            {
                CompareRun r = _runs[i];
                double s = double.IsNaN(_hover) ? _span : _hover;
                _legendCells[i][0].FontWeight = i == _ref ? FontWeights.Bold : FontWeights.Normal;
                _legendCells[i][2].Text = double.IsNaN(_hover) ? "" : At(r.V, s).ToString("0", Inv);
                double d = At(r.T, s) - At(rf.T, s);
                _legendCells[i][3].Text = i == _ref ? "基準" : (d >= 0 ? "+" : "") + d.ToString("0.00", Inv);
                _legendCells[i][3].Foreground = i == _ref ? Theme.TextSecondary : d > 0.005 ? new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B)) : d < -0.005 ? new SolidColorBrush(Color.FromRgb(0x6B, 0xD9, 0x6B)) : Theme.Text;
            }
        }

        void AddText(string text, double x, double y, Brush color, double size)
        {
            TextBlock t = new TextBlock { Text = text, Foreground = color, FontSize = size };
            Canvas.SetLeft(t, x); Canvas.SetTop(t, y);
            Chart.Children.Add(t);
        }
    }
}
