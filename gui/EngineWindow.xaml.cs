using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Tk2Gui
{
    /// <summary>回転数・アクセルの解析結果を、スペクトログラムに重ねて確かめる画面</summary>
    public partial class EngineWindow : Window
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        const int SpecH = 360, StripH = 22, Top = 14;
        double FMax = 1200;          // 図の縦の上限 [Hz] (変速機付きは 600)
        readonly EngineAnalyzer.Work _w;
        readonly string _log;
        EngineStore.FileData _file;
        byte[] _base;                // スペクトログラムだけの画素 (線を描き直すときに使う)
        int _width, _height;

        public EngineWindow(string log, EngineAnalyzer.Work w, EngineStore.FileData file)
        {
            InitializeComponent();
            Theme.ApplyDark(this);
            _log = log; _w = w; _file = file;
            if (_file.IsGeared) FMax = 600;
            Title = "回転数・アクセルの解析 - " + System.IO.Path.GetFileName(w.Seg.Video) + "  " + App.VersionText;
            Settings.Binder st = new Settings.Binder("Engine");
            st.Combo(ZoomCombo, "Zoom");
            Settings.Track(this, "EngineWindow", st.SaveAll);
            BuildBase();
            ShowCoef();
        }

        static readonly Color[] GearCol = { Color.FromRgb(0, 0, 0), Color.FromRgb(40, 210, 70), Color.FromRgb(60, 160, 255),
            Color.FromRgb(255, 210, 40), Color.FromRgb(255, 120, 40), Color.FromRgb(220, 80, 220), Color.FromRgb(200, 200, 200) };

        void ShowCoef()
        {
            if (_file.IsGeared) { ShowCar(); return; }
            CoefBox.Text = _file.RpmPerKmh.ToString("0.0", Inv);
            EngineStore.Segment s = _w.Seg;
            int known = s.Rpm.Count(x => x > 0), on = s.Throttle.Count(x => x == 1), off = s.Throttle.Count(x => x == 0);
            SummaryText.Text = string.Format(Inv,
                "この動画で求めた比: {0:0.000} Hz / (km/h) = {1:0.0} rpm / (km/h)  (100 km/h で {2:0} rpm)。保存している値: {3:0.0}{4}。" +
                "音から回転数が取れたコマ {5:0} %。アクセル ON {6:0} % / OFF {7:0} %  (倍音らしさ: 加速中 {8:0.00}, 減速中 {9:0.00}, しきい値 {10:0.00})。保存先: {11}",
                s.HzPerKmh, s.HzPerKmh * 60, s.HzPerKmh * 6000, _file.RpmPerKmh, _file.Manual ? " (手で直した値)" : "",
                100.0 * known / s.Rpm.Length, 100.0 * on / s.Throttle.Length, 100.0 * off / s.Throttle.Length,
                s.OnMean, s.OffMean, s.Threshold, System.IO.Path.GetFileName(EngineStore.SidecarPath(_log)));
            Draw();
        }

        void BuildBase()
        {
            int n = _w.Spec.Length;
            _width = n; _height = Top + SpecH + 4 + StripH;
            _base = new byte[_width * _height * 4];
            // 明るさ: 全体の 40〜99.7 % 点で 0〜1 に
            float[] all = _w.Spec.SelectMany(r => r).Where((x, i) => i % 7 == 0).OrderBy(x => x).ToArray();
            float lo = all[(int)(all.Length * 0.4)], hi = all[Math.Min(all.Length - 1, (int)(all.Length * 0.997))];
            if (hi <= lo) hi = lo + 1;
            int nb = _w.Spec[0].Length;
            for (int x = 0; x < n; x++)
            {
                float[] row = _w.Spec[x];
                for (int y = 0; y < SpecH; y++)
                {
                    double f = (SpecH - 1 - y) * FMax / SpecH;
                    int b = Math.Min(nb - 1, (int)(f / _w.BinHz));
                    int v = (int)(Math.Max(0, Math.Min(1, (row[b] - lo) / (hi - lo))) * 255);
                    Set(_base, x, Top + y, (byte)v, (byte)v, (byte)v);
                }
            }
            // 目盛り
            for (int hz = 100; hz < FMax; hz += 100)
            {
                int y = Top + SpecH - 1 - (int)(hz / FMax * SpecH);
                for (int x = 0; x < n; x += 40) for (int k = 0; k < 6 && x + k < n; k++) Set(_base, x + k, y, 120, 120, 0);
            }
            int per10 = (int)Math.Round(10 / EngineAnalyzer.Dt);
            for (int x = 0; x < n; x += per10) for (int y = 0; y < Top; y++) Set(_base, x, y, 255, 255, 255);
        }

        void Set(byte[] px, int x, int y, byte r, byte g, byte b)
        {
            if (x < 0 || x >= _width || y < 0 || y >= _height) return;
            int i = (y * _width + x) * 4;
            px[i] = b; px[i + 1] = g; px[i + 2] = r; px[i + 3] = 255;
        }

        void Dot(byte[] px, int x, double hz, byte r, byte g, byte b)
        {
            if (hz <= 0 || hz >= FMax) return;
            int y = Top + SpecH - 1 - (int)(hz / FMax * SpecH);
            Set(px, x, y, r, g, b); Set(px, x, y - 1, r, g, b);
        }

        // ---------------- 変速機付き ----------------

        void ShowCar()
        {
            CoefPanel.Visibility = Visibility.Collapsed; GearPanel.Visibility = Visibility.Visible;
            Title = "回転数・ギアの解析 - " + System.IO.Path.GetFileName(_w.Seg.Video) + "  " + App.VersionText;
            GearsBox.Text = string.Join(", ", (_file.GearRatios ?? new double[0]).Select(x => x.ToString("0.###", Inv)));
            FinalBox.Text = _file.FinalRatio.ToString("0.###", Inv);
            MaxGearBox.Text = _file.MaxGear.ToString(Inv);
            TireBox.Text = _file.TireSize ?? "";
            CircBox.Text = _file.TireCircM.ToString("0.###", Inv);
            CylBox.Text = _file.Cylinders.ToString(Inv);
            TachoBox.Text = string.Format(Inv, "{0}, {1}, {2}", _file.TachoMax, _file.TachoYellow, _file.TachoRed);
            LegendText.Text = "図: 横 = 時間 (白い目盛りは 10 秒ごと)、縦 = 周波数 (0〜600 Hz、黄色の目盛りは 100 Hz ごと)。明るい線がエンジン音です。" +
                "赤 = 音から求めた回転数 (1・2 倍)、水色の点線 = 各ギアで速度から計算した回転数。下の帯 = ギア (緑 1 速、青 2 速、黄 3 速、橙 4 速、紫 5 速、灰 6 速、黒 = 分からない・止まっている)。" +
                "グリップしているときは赤が水色のどれかに重なります。上に離れているのは空転、減速中に跳ね上がるのはブリッピングです。";

            EngineStore.Segment s = _w.Seg;
            int n = s.Rpm.Length, known = s.Rpm.Count(x => x > 0);
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendFormat(Inv, "変速機付き ({0} 気筒、回転数 = 音の基本周波数 × 60 / {1:0.#})。音から回転数が取れたコマ {2:0} %。タイヤ 1 周 {3:0.000} m。",
                _file.Cylinders, _file.PulsesPerRev, 100.0 * known / Math.Max(1, n), _file.TireCirc);
            int ng = _file.TopGear;
            for (int g = 1; g <= ng; g++)
            {
                double e = _file.GearRpmPerKmh(g);
                var rs = Enumerable.Range(0, n).Where(i => s.Rpm[i] > 0 && _w.Speed[i] >= 20 && _file.GearFor(s.Rpm[i], _w.Speed[i]) == g)
                                   .Select(i => s.Rpm[i] / _w.Speed[i]).OrderBy(x => x).ToArray();
                if (rs.Length < 10) continue;
                double p10 = rs[rs.Length / 10], p50 = rs[rs.Length / 2];
                sb.AppendFormat(Inv, "  {0} 速: 計算 {1:0.0} / 音 {2:0.0} (下位 10 % {3:+0;-0} %、中央 {4:+0;-0} %) rpm/(km/h), {5:0} 秒。",
                    g, e, p50, (p10 / e - 1) * 100, (p50 / e - 1) * 100, rs.Length * EngineAnalyzer.Dt);
            }
            sb.Append("  保存先: " + System.IO.Path.GetFileName(EngineStore.SidecarPath(_log)));
            SummaryText.Text = sb.ToString();
            Draw();
        }

        void SaveCar_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                double[] gears = GearsBox.Text.Split(new[] { ',', ' ', '	', '、' }, StringSplitOptions.RemoveEmptyEntries)
                                     .Select(x => double.Parse(x, NumberStyles.Float, Inv)).ToArray();
                double fin = double.Parse(FinalBox.Text, NumberStyles.Float, Inv);
                double circ = string.IsNullOrWhiteSpace(CircBox.Text) ? 0 : double.Parse(CircBox.Text, NumberStyles.Float, Inv);
                int cyl = int.Parse(CylBox.Text.Trim(), Inv);
                int maxg = string.IsNullOrWhiteSpace(MaxGearBox.Text) ? 0 : int.Parse(MaxGearBox.Text.Trim(), Inv);
                if (maxg < 0) throw new FormatException();
                int[] tc = TachoBox.Text.Split(new[] { ',', ' ', '/', '、' }, StringSplitOptions.RemoveEmptyEntries).Select(x => int.Parse(x, Inv)).ToArray();
                if (gears.Length == 0 || gears.Any(x => x <= 0) || fin <= 0 || cyl <= 0 || tc.Length != 3) throw new FormatException();
                if (circ <= 0 && EngineStore.FileData.TireDiameterMm(TireBox.Text) <= 0) throw new FormatException();
                _file.GearRatios = gears; _file.FinalRatio = fin; _file.TireSize = TireBox.Text.Trim(); _file.TireCircM = circ;
                _file.Cylinders = cyl; _file.MaxGear = maxg; _file.TachoMax = tc[0]; _file.TachoYellow = tc[1]; _file.TachoRed = tc[2];
                EngineStore.Save(_log, _file);
                ShowCar();
            }
            catch (FormatException)
            {
                MessageBox.Show(this, "ギア比 (カンマ区切り)、最終減速比、タイヤ (215/45R17 の形、または 1 周 [m])、気筒数、目盛り 3 つ (最高, 黄, 赤) を数字で入れてください。",
                    "値が正しくありません", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        void DrawGeared(byte[] px)
        {
            EngineStore.Segment s = _w.Seg;
            double hzPerRpm = _file.PulsesPerRev / 60;
            int ng = _file.TopGear;
            int[] raw = new int[_width];
            for (int x = 0; x < _width; x++) raw[x] = s.Rpm[x] > 0 ? _file.GearFor(s.Rpm[x], _w.Speed[x]) : 0;
            for (int x = 0; x < _width; x++)
            {
                double v = _w.Speed[x];
                if (v > 3 && (x & 1) == 0)
                    for (int g = 1; g <= ng; g++) Dot(px, x, _file.GearRpmPerKmh(g) * v * hzPerRpm, 0, 200, 255);
                if (s.Rpm[x] > 0) foreach (int h in new[] { 1, 2 }) Dot(px, x, h * s.Rpm[x] * hzPerRpm, 255, 70, 70);
                // ギア: 前後 0.5 秒の多数決 (回転計と同じ)
                int[] cnt = new int[ng + 1];
                for (int j = Math.Max(0, x - 10); j <= Math.Min(_width - 1, x + 10); j += 2) cnt[raw[j]]++;
                int bg = 0, bc = 3;
                for (int g = 1; g <= ng; g++) if (cnt[g] > bc) { bc = cnt[g]; bg = g; }
                Color c = GearCol[Math.Min(bg, GearCol.Length - 1)];
                for (int y = Top + SpecH + 4; y < _height; y++) Set(px, x, y, c.R, c.G, c.B);
            }
        }

        void Draw()
        {
            byte[] px = (byte[])_base.Clone();
            EngineStore.Segment s = _w.Seg;
            if (_file.IsGeared) { DrawGeared(px); Show(px); return; }
            double k = _file.RpmPerKmh / 60;   // Hz / (km/h)
            for (int x = 0; x < _width; x++)
            {
                double v = _w.Speed[x];
                if (v > 3) foreach (int h in new[] { 1, 2, 4 }) Dot(px, x, h * k * v, 0, 220, 255);
                if (s.Rpm[x] > 0) foreach (int h in new[] { 1, 2, 4 }) Dot(px, x, h * s.Rpm[x] / 60.0, 255, 70, 70);
                int th = s.Throttle[x];
                byte c = th == 1 ? (byte)1 : th == 0 ? (byte)2 : (byte)0;
                for (int y = Top + SpecH + 4; y < _height; y++)
                {
                    if (c == 1) Set(px, x, y, 40, 210, 70);
                    else if (c == 2) Set(px, x, y, 90, 90, 90);
                    else Set(px, x, y, 0, 0, 0);
                }
            }
            Show(px);
        }

        void Show(byte[] px)
        {
            WriteableBitmap wb = new WriteableBitmap(_width, _height, 96, 96, PixelFormats.Bgra32, null);
            wb.WritePixels(new Int32Rect(0, 0, _width, _height), px, _width * 4, 0);
            SpecImage.Source = wb;
            Zoom_Changed(null, null);
        }

        void Zoom_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (SpecImage == null || _width == 0) return;
            ComboBoxItem it = ZoomCombo.SelectedItem as ComboBoxItem;
            double z = it != null ? double.Parse((string)it.Tag, Inv) : 1;
            SpecImage.Width = _width * z; SpecImage.Height = _height * 1.4;
        }

        void SaveCoef_Click(object sender, RoutedEventArgs e)
        {
            double v;
            if (!double.TryParse(CoefBox.Text, NumberStyles.Float, Inv, out v) || v <= 0)
            {
                MessageBox.Show(this, "数字を入れてください (例: 171)。", "値が正しくありません", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            _file.RpmPerKmh = v; _file.Manual = true;
            EngineStore.Save(_log, _file);
            ShowCoef();
        }

        void ResetCoef_Click(object sender, RoutedEventArgs e)
        {
            _file.Manual = false;
            double w = _file.Segments.Sum(s => s.EndT - s.T0);
            _file.RpmPerKmh = Math.Round(_file.Segments.Sum(s => s.HzPerKmh * 60 * (s.EndT - s.T0)) / w, 2);
            EngineStore.Save(_log, _file);
            ShowCoef();
        }
    }
}
