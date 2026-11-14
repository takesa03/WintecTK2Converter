using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32;

namespace Tk2Gui
{
    public partial class CourseWindow : Window
    {
        CourseResult _res;          // 解析結果 (保存済み定義を開いた場合は Samples が null)
        CourseDefinition _def;
        bool _busy;
        bool _updating;             // コントロールの値を書き換え中はイベントを無視する

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public CourseWindow(IEnumerable<string> initialFiles)
        {
            InitializeComponent();
            Theme.ApplyDark(this);
            Title += "  " + App.VersionText;
            // 前回の解析の設定。閉じるときに覚える
            Settings.Binder st = new Settings.Binder("Course");
            st.Text(NameBox, "Name");
            st.Text(MinSpeedBox, "MinSpeed");
            st.Text(LapRatioBox, "LapRatio");
            st.Text(SectorBox, "Sectors");
            st.Text(HalfWidthBox, "HalfWidth");
            Settings.Track(this, "CourseWindow", st.SaveAll);
            if (initialFiles != null)
                foreach (string f in initialFiles) AddFile(f);
        }

        // ================= 入力ファイル =================

        void AddFile(string path)
        {
            string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            if (ext != ".tk2" && ext != ".nmea" && ext != ".txt" && ext != ".log") return;
            if (!FileList.Items.Cast<string>().Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)))
                FileList.Items.Add(path);
        }

        void Window_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        void Window_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            foreach (string p in (string[])e.Data.GetData(DataFormats.FileDrop))
            {
                if (Directory.Exists(p))
                    foreach (string f in Directory.GetFiles(p)) AddFile(f);
                else AddFile(p);
            }
        }

        void AddFile_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog dlg = new OpenFileDialog();
            dlg.Title = "走行ログを選択";
            dlg.Filter = "走行ログ (*.tk2;*.nmea)|*.tk2;*.nmea|すべてのファイル (*.*)|*.*";
            dlg.Multiselect = true;
            Settings.InitialDir(dlg, "logs");
            if (dlg.ShowDialog(this) == true)
            {
                Settings.RememberDir("logs", dlg.FileName);
                foreach (string f in dlg.FileNames) FileList.Items.Add(f);
            }
        }

        void RemoveFile_Click(object sender, RoutedEventArgs e)
        {
            foreach (object o in FileList.SelectedItems.Cast<object>().ToList()) FileList.Items.Remove(o);
        }

        // ================= 解析 =================

        async void Analyze_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            if (FileList.Items.Count == 0)
            {
                MessageBox.Show(this, "走行ログ (.tk2 / .nmea) を追加してください。", "コース定義の作成", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            CourseParams prm = new CourseParams();
            try
            {
                prm.Name = NameBox.Text.Trim().Length == 0 ? "Course" : NameBox.Text.Trim();
                prm.MinSpeedKmh = ParseD(MinSpeedBox.Text, "速度の下限", 0, 200);
                prm.LapMaxRatio = ParseD(LapRatioBox.Text, "周回の所要時間倍率", 1.0, 10);
                prm.SectorCount = (int)ParseD(SectorBox.Text, "セクター数", 0, 9);
                prm.GateHalfWidthM = ParseD(HalfWidthBox.Text, "ラインの片側", 1, 100);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "設定を確認してください", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            List<string> paths = FileList.Items.Cast<string>().ToList();
            SetBusy(true);
            InfoText.Foreground = Theme.Ok;
            InfoText.Text = "解析中...";
            Log("--- 解析開始: " + string.Join(", ", paths.Select(System.IO.Path.GetFileName)));
            try
            {
                CourseResult res = await Task.Run(() =>
                {
                    List<List<GpsSample>> files = paths.Select(GpsLoader.Load).ToList();
                    return CourseBuilder.Build(files, paths, prm);
                });
                _res = res; _def = res.Def;
                foreach (string l in res.Log) Log(l);
                ShowResult(true);
            }
            catch (Exception ex)
            {
                Log("失敗: " + ex.Message);
                InfoText.Foreground = Theme.Error;
                InfoText.Text = "失敗: " + ex.Message;
            }
            finally { SetBusy(false); }
        }

        static double ParseD(string s, string name, double min, double max)
        {
            double v;
            if (!double.TryParse(s.Trim(), NumberStyles.Float, Inv, out v) || v < min || v > max)
                throw new ArgumentException(string.Format("{0} は {1}～{2} の数値にしてください", name, min, max));
            return v;
        }

        void SetBusy(bool busy)
        {
            _busy = busy;
            AnalyzeButton.IsEnabled = !busy;
        }

        // ================= 結果表示・ゲート調整 =================

        void ShowResult(bool freshAnalysis)
        {
            _updating = true;
            GateGroup.IsEnabled = true;
            OutGroup.IsEnabled = true;
            PosSlider.Maximum = Math.Floor(_def.LengthM);
            SectorBox.Text = Math.Max(0, _def.Gates.Count - 1).ToString(Inv);
            HalfWidthBox.Text = _def.GateHalfWidthM.ToString("0.#", Inv);
            _updating = false;
            RefreshGateList(0);

            CourseStats s = _def.Stats;
            InfoText.Foreground = Theme.Ok;
            InfoText.Text = string.Format(Inv, "{0}: 周長 {1:F0} m / {2} / 採用 {3} 周 (全 {4} 周) / ばらつき 標準偏差 {5:F1} m",
                _def.Name, _def.LengthM, _def.Clockwise ? "時計回り" : "反時計回り", s.LapsUsed, s.LapsTotal, s.ScatterStdevM);
            Redraw();
        }

        // 一覧を作り直して sel 番目を選ぶ
        void RefreshGateList(int sel)
        {
            _updating = true;
            GateList.Items.Clear();
            foreach (Gate g in _def.Gates)
                GateList.Items.Add(string.Format(Inv, "{0}   {1:F1} m", g.Name == "ControlLine" ? "ControlLine (スタート/ゴール)" : g.Name, g.S));
            if (sel >= _def.Gates.Count) sel = _def.Gates.Count - 1;
            GateList.SelectedIndex = sel;
            _updating = false;
            LoadSelected();
        }

        // 選択中のラインの位置をスライダーと数値欄に出す
        void LoadSelected()
        {
            int i = GateList.SelectedIndex;
            _updating = true;
            if (_def != null && i >= 0 && i < _def.Gates.Count)
            {
                PosSlider.Value = _def.Gates[i].S;
                PosBox.Text = _def.Gates[i].S.ToString("0.0", Inv);
            }
            _updating = false;
        }

        void GateList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_updating || _def == null) return;
            LoadSelected();
            Redraw();
        }

        double CurrentHalfWidth()
        {
            double h;
            return double.TryParse(HalfWidthBox.Text.Trim(), NumberStyles.Float, Inv, out h) && h >= 1 && h <= 100 ? h : _def.GateHalfWidthM;
        }

        // 選択中のラインだけを s [m] へ動かす (ほかは動かさない)
        void SetSelectedPos(double s)
        {
            int i = GateList.SelectedIndex;
            if (_def == null || i < 0 || i >= _def.Gates.Count) return;
            _def.UpdateGate(i, s, CurrentHalfWidth());
            RefreshGateList(i);
            Redraw();
        }

        void PosSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_updating || _def == null || GateList.SelectedIndex < 0) return;
            SetSelectedPos(PosSlider.Value);
        }

        void PosBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_updating || _def == null) return;
            double v;
            if (double.TryParse(PosBox.Text.Trim(), NumberStyles.Float, Inv, out v)) SetSelectedPos(v);
            else LoadSelected();
        }

        void Nudge_Click(object sender, RoutedEventArgs e)
        {
            int i = GateList.SelectedIndex;
            if (_def == null || i < 0) return;
            SetSelectedPos(_def.Gates[i].S + double.Parse((string)((Button)sender).Tag, Inv));
        }

        // 選択中のラインを既定の位置に戻す (ControlLine = 自動位置, Sector = コントロールラインから等間隔)
        void AutoPos_Click(object sender, RoutedEventArgs e)
        {
            int i = GateList.SelectedIndex;
            if (_def == null || i < 0) return;
            if (i == 0) SetSelectedPos(_res != null ? _res.AutoControlPosM : 0);
            else SetSelectedPos(_def.Gates[0].S + _def.LengthM * i / _def.Gates.Count);
        }

        // セクター数を変えて、コントロールラインから等間隔に置き直す
        void Regen_Click(object sender, RoutedEventArgs e)
        {
            if (_def == null) return;
            try
            {
                int sectors = (int)ParseD(SectorBox.Text, "セクター数", 0, 9);
                double half = ParseD(HalfWidthBox.Text, "ラインの片側", 1, 100);
                _def.MakeGates(_def.Gates.Count > 0 ? _def.Gates[0].S : 0, sectors, half);
                RefreshGateList(0);
                Redraw();
            }
            catch (Exception ex)
            {
                InfoText.Foreground = Theme.Error;
                InfoText.Text = ex.Message;
            }
        }

        // ラインの長さ (片側) は全ラインに適用 (位置は変えない)
        void HalfWidth_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_updating || _def == null) return;
            double h;
            if (!double.TryParse(HalfWidthBox.Text.Trim(), NumberStyles.Float, Inv, out h) || h < 1 || h > 100)
            {
                HalfWidthBox.Text = _def.GateHalfWidthM.ToString("0.#", Inv);
                return;
            }
            _def.SetGateHalfWidth(h);
            Redraw();
        }
        // ================= プレビュー =================

        void Preview_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            Redraw();
        }

        void Redraw()
        {
            Preview.Children.Clear();
            if (_def == null) return;
            double w = Preview.ActualWidth, h = Preview.ActualHeight;
            if (w < 50 || h < 50) return;

            double lat0 = _def.Lat0, lon0 = _def.Lon0;
            double ky = 111132.0, kx = 111320.0 * Math.Cos(lat0 * Math.PI / 180);
            double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
            foreach (LatLon p in _def.Centerline)
            {
                double x = (p.Lon - lon0) * kx, y = (p.Lat - lat0) * ky;
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            }
            double margin = 40;
            double scale = Math.Min((w - margin) / Math.Max(1, maxX - minX), (h - margin) / Math.Max(1, maxY - minY));
            double cx = (minX + maxX) / 2, cy = (minY + maxY) / 2;
            Func<double, double, Point> toScr = (lat, lon) =>
                new Point(w / 2 + ((lon - lon0) * kx - cx) * scale, h / 2 - ((lat - lat0) * ky - cy) * scale);

            // 元データ (解析直後のみ): 採用 = 青, 除外した周回 = オレンジ
            if (_res != null && _res.Samples != null)
            {
                for (int cls = 1; cls <= 2; cls++)
                {
                    StreamGeometry geo = new StreamGeometry();
                    using (StreamGeometryContext ctx = geo.Open())
                    {
                        for (int i = 0; i < _res.Samples.Count; i++)
                        {
                            if ((int)_res.Classes[i] != cls) continue;
                            Point p = toScr(_res.Samples[i].Lat, _res.Samples[i].Lon);
                            bool continuing = i > 0 && (int)_res.Classes[i - 1] == cls && _res.LapIds[i - 1] == _res.LapIds[i]
                                              && _res.Samples[i].T - _res.Samples[i - 1].T <= 2.5;
                            if (continuing) ctx.LineTo(p, true, false);
                            else ctx.BeginFigure(p, false, false);
                        }
                    }
                    geo.Freeze();
                    Preview.Children.Add(new System.Windows.Shapes.Path
                    {
                        Data = geo,
                        Stroke = new SolidColorBrush(cls == 2 ? Color.FromArgb(120, 32, 144, 255) : Color.FromArgb(110, 255, 160, 64)),
                        StrokeThickness = 1
                    });
                }
            }

            // 中心線
            Polyline line = new Polyline { Stroke = Brushes.Red, StrokeThickness = 3, StrokeLineJoin = PenLineJoin.Round };
            foreach (LatLon p in _def.Centerline) line.Points.Add(toScr(p.Lat, p.Lon));
            line.Points.Add(line.Points[0]);
            Preview.Children.Add(line);

            // 進行方向の矢印 (8 か所)
            for (int i = 0; i < 8; i++)
            {
                LatLon pos; double hd;
                _def.PointAt(_def.LengthM * i / 8 + _def.LengthM / 16, out pos, out hd);
                Point p = toScr(pos.Lat, pos.Lon);
                double th = hd * Math.PI / 180;
                Point dir = new Point(Math.Sin(th), -Math.Cos(th));      // 画面上の進行方向 (上 = 北)
                Point nrm = new Point(-dir.Y, dir.X);
                Polygon arrow = new Polygon { Fill = Brushes.Red };
                arrow.Points.Add(new Point(p.X + dir.X * 8, p.Y + dir.Y * 8));
                arrow.Points.Add(new Point(p.X - dir.X * 5 + nrm.X * 5, p.Y - dir.Y * 5 + nrm.Y * 5));
                arrow.Points.Add(new Point(p.X - dir.X * 5 - nrm.X * 5, p.Y - dir.Y * 5 - nrm.Y * 5));
                Preview.Children.Add(arrow);
            }

            // コントロールライン・セクター
            for (int gi = 0; gi < _def.Gates.Count; gi++)
            {
                Gate g = _def.Gates[gi];
                bool selected = gi == GateList.SelectedIndex;
                bool ctrl = g.Name == "ControlLine";
                Point a = toScr(g.A.Lat, g.A.Lon), b = toScr(g.B.Lat, g.B.Lon);
                Preview.Children.Add(new Line
                {
                    X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y, StrokeThickness = selected ? 7 : (ctrl ? 5 : 4),
                    Stroke = selected ? Brushes.DodgerBlue : (ctrl ? Brushes.White : Brushes.DarkOrange)
                });
                TextBlock tb = new TextBlock { Text = ctrl ? "Control" : g.Name, FontWeight = FontWeights.Bold,
                                               Foreground = selected ? Brushes.DodgerBlue : (ctrl ? Brushes.White : Brushes.DarkOrange) };
                Canvas.SetLeft(tb, Math.Max(a.X, b.X) + 4);
                Canvas.SetTop(tb, (a.Y + b.Y) / 2 - 9);
                Preview.Children.Add(tb);
            }

            // 凡例 / 縮尺
            double barM = scale * 50 > 40 ? 50 : 100;
            Line bar = new Line { X1 = 16, Y1 = h - 16, X2 = 16 + barM * scale, Y2 = h - 16, Stroke = Theme.Text, StrokeThickness = 2 };
            Preview.Children.Add(bar);
            TextBlock bt = new TextBlock { Text = barM.ToString("0") + " m", FontSize = 11 };
            Canvas.SetLeft(bt, 16); Canvas.SetTop(bt, h - 34);
            Preview.Children.Add(bt);
            TextBlock legend = new TextBlock
            {
                Text = "赤: 中心線 (平均化)　青: 採用した周回の軌跡　橙: 除外した周回　上が北",
                FontSize = 11, Foreground = Theme.TextSecondary
            };
            Canvas.SetLeft(legend, 16); Canvas.SetTop(legend, 6);
            Preview.Children.Add(legend);
        }

        // ================= 保存・確認 =================

        string SafeName()
        {
            string n = _def.Name;
            foreach (char c in System.IO.Path.GetInvalidFileNameChars()) n = n.Replace(c, '_');
            return n.Trim().Length == 0 ? "course" : n.Trim();
        }

        void Save_Click(object sender, RoutedEventArgs e)
        {
            if (_def == null) return;
            SaveFileDialog dlg = new SaveFileDialog();
            dlg.Title = "コース定義を保存";
            dlg.Filter = "コース定義 (*.course.json)|*.course.json";
            dlg.FileName = SafeName() + ".course.json";
            if (_res != null && _res.Samples != null && FileList.Items.Count > 0)
                dlg.InitialDirectory = System.IO.Path.GetDirectoryName((string)FileList.Items[0]);
            Settings.InitialDir(dlg, "course");
            if (dlg.ShowDialog(this) != true) return;
            Settings.RememberDir("course", dlg.FileName);
            try
            {
                string path = dlg.FileName;
                _def.Save(path);
                string kml = path.EndsWith(".course.json", StringComparison.OrdinalIgnoreCase)
                    ? path.Substring(0, path.Length - ".course.json".Length) + ".kml" : System.IO.Path.ChangeExtension(path, ".kml");
                File.WriteAllText(kml, BuildKml(), new UTF8Encoding(false));
                Log("保存: " + path);
                Log("保存: " + kml + " (Google Earth 用)");
                InfoText.Foreground = Theme.Ok;
                InfoText.Text = "保存しました: " + System.IO.Path.GetFileName(path);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "保存できませんでした", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        string BuildKml()
        {
            CourseResult r = _res ?? new CourseResult();
            r.Def = _def;
            return CourseKml.Build(r, r.Samples != null);
        }

        // いま調整しているコース (保存前でも可) を使って、ラップ解析の画面を開く
        void OpenLap_Click(object sender, RoutedEventArgs e)
        {
            if (_def == null) return;
            LapWindow w = new LapWindow(_def, "(作成中のコース定義)", FileList.Items.Cast<string>());
            w.Owner = this;
            w.Show();
        }

        void OpenEarth_Click(object sender, RoutedEventArgs e)
        {
            if (_def == null) return;
            try
            {
                string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "WintecTK2Converter");
                Directory.CreateDirectory(dir);
                string path = System.IO.Path.Combine(dir, SafeName() + "_course.kml");
                File.WriteAllText(path, BuildKml(), new UTF8Encoding(false));
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                Log("Google Earth で開きました: " + path);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "KML を開けませんでした (.kml に関連付けられたアプリがありません)。\n" + ex.Message,
                    "Google Earth", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        void OpenDef_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog dlg = new OpenFileDialog();
            dlg.Title = "コース定義を開く";
            dlg.Filter = "コース定義 (*.course.json)|*.course.json|すべてのファイル (*.*)|*.*";
            Settings.InitialDir(dlg, "course");
            if (dlg.ShowDialog(this) != true) return;
            Settings.RememberDir("course", dlg.FileName);
            try
            {
                _def = CourseDefinition.Load(dlg.FileName);
                _res = null;
                NameBox.Text = _def.Name;
                Log("開く: " + dlg.FileName);
                ShowResult(false);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "開けませんでした", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        void Log(string line)
        {
            LogBox.AppendText(line + Environment.NewLine);
            LogBox.ScrollToEnd();
        }
    }
}
