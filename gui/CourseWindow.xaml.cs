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
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
            PreviewKeyDown += Window_PreviewKeyDown;     // 地図の上で Tab = 地図クリックの候補を切り替え
            // 前回の解析の設定。閉じるときに覚える
            Settings.Binder st = new Settings.Binder("Course");
            st.Text(NameBox, "Name");
            st.Text(MinSpeedBox, "MinSpeed");
            st.Text(LapRatioBox, "LapRatio");
            st.Text(SectorBox, "Sectors");
            st.Text(HalfWidthBox, "HalfWidth");
            st.Check(GymkhanaRadio, "Gymkhana");
            st.Check(PhotoCheck, "Photo");
            Settings.Track(this, "CourseWindow", st.SaveAll);
            UpdateModeUi();
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

        // ================= 種類 (サーキット / ジムカーナ) =================

        // ジムカーナも 12 m (6 m だと、スタート位置のずれや走行ラインのばらつきで通過を検出できないことがあった)。
        // 同じ所を通る別の通過は、向きと走行距離で区別する
        const string CircuitHalfWidth = "12", GymkhanaHalfWidth = "12", OldGymkhanaHalfWidth = "6";

        bool IsGym { get { return GymkhanaRadio != null && GymkhanaRadio.IsChecked == true; } }

        void Mode_Checked(object sender, RoutedEventArgs e)
        {
            if (HalfWidthBox == null || RunGroup == null) return;     // InitializeComponent の途中
            // ラインの長さが前の種類の既定値のままなら、今の種類の既定値に替える
            string t = HalfWidthBox.Text.Trim();
            if (IsGym && (t == CircuitHalfWidth || t == OldGymkhanaHalfWidth)) HalfWidthBox.Text = GymkhanaHalfWidth;
            if (!IsGym && t == GymkhanaHalfWidth) HalfWidthBox.Text = CircuitHalfWidth;
            UpdateModeUi();
        }

        void UpdateModeUi()
        {
            Visibility circuitOnly = IsGym ? Visibility.Collapsed : Visibility.Visible;
            MinSpeedLabel.Visibility = MinSpeedBox.Visibility = LapRatioLabel.Visibility = LapRatioBox.Visibility = circuitOnly;
        }

        // 表示中のコース定義の種類に合わせて、ラインの説明などを替える
        void UpdateGateUi()
        {
            bool gym = _def != null && _def.IsGymkhana;
            GateGroup.Header = gym ? "スタート・フィニッシュ・セクター" : "コントロールライン・セクター";
            GateHelp.Text = gym
                ? "一覧で選んだラインだけを前後に動かせます (距離は基準の走行の始点から進行方向に測ります。+ で進行方向へ)。地図をクリックすると、いちばん近い所に置きます。同じ所を何度も通る所では、ホイールか Tab で候補 (何 m 目・何秒目・向き) を切り替えます。下の速度のグラフをクリックしても置けます。"
                : "一覧で選んだラインだけを前後に動かせます (距離は中心線の始点から進行方向に測ります。+ で進行方向へ)。地図をクリックすると、いちばん近い所に置きます (近くを何度も通る所では、ホイールか Tab で候補を切り替え)。";
            AutoPosButton.ToolTip = gym
                ? "スタート・フィニッシュは自動で決めた位置、セクターはスタートとフィニッシュの間の等間隔の位置に戻します"
                : "コントロールラインは自動で決めた位置、セクターはコントロールラインから等間隔の位置に戻します";
            RegenButton.ToolTip = gym
                ? "セクターを指定の数だけ、スタートとフィニッシュの間に等間隔に置き直します (個別に動かした位置は失われます)"
                : "セクターを指定の数だけ、コントロールラインから等間隔に置き直します (個別に動かした位置は失われます)";
            RunGroup.Visibility = gym && _res != null && _res.Gym != null ? Visibility.Visible : Visibility.Collapsed;

            // 基準の走行の時刻・速度で置く補助 (解析した直後のジムカーナだけ)
            GymkhanaData g = Gym;
            GymPlacePanel.Visibility = GraphBorder.Visibility = g != null ? Visibility.Visible : Visibility.Collapsed;
            bool was = _updating;
            _updating = true;
            MinimaCombo.Items.Clear();
            _minima = g == null ? new List<SpeedMin>() : GymkhanaBuilder.SpeedMinima(g, StartS() + 5, _def.Gates[_def.Gates.Count - 1].S);
            double t0 = g == null ? 0 : GymkhanaBuilder.TimeAtS(g, StartS());
            foreach (SpeedMin m in _minima)
                MinimaCombo.Items.Add(string.Format(Inv, "{0:0} m  ({1:0.0} 秒目)  {2:0} km/h", m.S, GymkhanaBuilder.TimeAtS(g, m.S) - t0, m.Kmh));
            MinimaCombo.SelectedIndex = -1;
            _updating = was;
        }

        // ---- ジムカーナの走行一覧 ----

        void FillRunList()
        {
            _updating = true;
            RunList.Items.Clear();
            RefCombo.Items.Clear();
            GymkhanaData g = _res.Gym;
            foreach (GymRun r in g.Runs)
            {
                CheckBox cb = new CheckBox { Content = GymkhanaBuilder.RunShortText(r), IsChecked = r.Use, Tag = r, FontSize = 12 };
                cb.ToolTip = GymkhanaBuilder.RunText(r, true) + "\nチェックを替えると、スタート・フィニッシュを自動の位置に置き直します";
                cb.Checked += RunCheck_Changed;
                cb.Unchecked += RunCheck_Changed;
                RunList.Items.Add(cb);
                RefCombo.Items.Add(GymkhanaBuilder.RunShortText(r));
            }
            RefCombo.SelectedIndex = g.ReferenceIndex;
            _updating = false;
        }

        void RunCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_updating || _res == null || _res.Gym == null) return;
            CheckBox cb = (CheckBox)sender;
            ((GymRun)cb.Tag).Use = cb.IsChecked == true;
            if (!_res.Gym.Runs.Any(r => r.Use))
            {
                _updating = true; cb.IsChecked = true; _updating = false;
                ((GymRun)cb.Tag).Use = true;
                return;
            }
            Recompose();
        }

        void RefCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_updating || _res == null || _res.Gym == null || RefCombo.SelectedIndex < 0) return;
            _res.Gym.ReferenceIndex = RefCombo.SelectedIndex;
            Recompose();
        }

        // 使う走行・基準の走行を変えたので、中心線とラインを作り直す (ラインは自動の位置に戻る)
        void Recompose()
        {
            try
            {
                int sectors = Math.Max(0, _def.Gates.Count - 2);
                string name = NameBox.Text.Trim().Length == 0 ? "Gymkhana" : NameBox.Text.Trim();
                GymkhanaBuilder.Compose(_res, name, sectors, CurrentHalfWidth(), null);
                _def = _res.Def;
                Log(string.Format(Inv, "作り直し: 基準 = {0} 本目, 使う走行 = {1}", _res.Gym.Runs[_res.Gym.ReferenceIndex].No,
                    string.Join(", ", _res.Gym.Runs.Where(r => r.Use).Select(r => r.No + " 本目"))));
                ShowResult(false);
            }
            catch (Exception ex)
            {
                InfoText.Foreground = Theme.Error;
                InfoText.Text = "失敗: " + ex.Message;
            }
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
            if (IsGym) { AnalyzeGymkhana(); return; }
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

        async void AnalyzeGymkhana()
        {
            GymkhanaParams prm = new GymkhanaParams();
            try
            {
                prm.Name = NameBox.Text.Trim().Length == 0 ? "Gymkhana" : NameBox.Text.Trim();
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
            Log("--- 解析開始 (ジムカーナ): " + string.Join(", ", paths.Select(System.IO.Path.GetFileName)));
            try
            {
                CourseResult res = await Task.Run(() =>
                {
                    List<List<GpsSample>> files = paths.Select(GpsLoader.Load).ToList();
                    return GymkhanaBuilder.Build(files, paths, prm);
                });
                _res = res; _def = res.Def;
                foreach (string l in res.Log) Log(l);
                Log(string.Format(Inv, "基準の走行: {0} 本目", res.Gym.Runs[res.Gym.ReferenceIndex].No));
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
            ClearCands();
            _updating = true;
            GateGroup.IsEnabled = true;
            OutGroup.IsEnabled = true;
            PosSlider.Maximum = Math.Floor(_def.LengthM);
            SectorBox.Text = Math.Max(0, _def.Gates.Count - (_def.IsGymkhana ? 2 : 1)).ToString(Inv);
            HalfWidthBox.Text = _def.GateHalfWidthM.ToString("0.#", Inv);
            _updating = false;
            UpdateGateUi();
            if (_def.IsGymkhana && _res != null && _res.Gym != null) FillRunList();
            RefreshGateList(0);

            CourseStats s = _def.Stats;
            InfoText.Foreground = Theme.Ok;
            InfoText.Text = _def.IsGymkhana ? _def.Name + ": " + GymkhanaBuilder.Summary(_def)
                : string.Format(Inv, "{0}: 周長 {1:F0} m / {2} / 採用 {3} 周 (全 {4} 周) / ばらつき 標準偏差 {5:F1} m",
                _def.Name, _def.LengthM, _def.Clockwise ? "時計回り" : "反時計回り", s.LapsUsed, s.LapsTotal, s.ScatterStdevM);
            Redraw();
        }

        // 一覧を作り直して sel 番目を選ぶ
        void RefreshGateList(int sel)
        {
            _updating = true;
            GateList.Items.Clear();
            GymkhanaData gd = Gym;
            double t0 = gd == null ? 0 : GymkhanaBuilder.TimeAtS(gd, StartS());
            foreach (Gate g in _def.Gates)
                GateList.Items.Add(string.Format(Inv, "{0}   {1:F1} m", GateLabel(g.Name), g.S)
                    + (gd == null ? "" : string.Format(Inv, "   ({0:0.0} 秒目)", GymkhanaBuilder.TimeAtS(gd, g.S) - t0)));
            if (sel >= _def.Gates.Count) sel = _def.Gates.Count - 1;
            GateList.SelectedIndex = sel;
            _updating = false;
            LoadSelected();
        }

        static string GateLabel(string name)
        {
            if (name == "ControlLine") return "ControlLine (スタート/ゴール)";
            if (name == CourseDefinition.StartGateName) return "Start (スタート)";
            if (name == CourseDefinition.FinishGateName) return "Finish (フィニッシュ)";
            return name;
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
                GymkhanaData g = Gym;
                if (g != null) TimeBox.Text = (GymkhanaBuilder.TimeAtS(g, _def.Gates[i].S) - GymkhanaBuilder.TimeAtS(g, StartS())).ToString("0.0", Inv);
            }
            _updating = false;
        }

        void GateList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_updating || _def == null) return;
            ClearCands();
            LoadSelected();
            Redraw();
        }

        double CurrentHalfWidth()
        {
            double h;
            return double.TryParse(HalfWidthBox.Text.Trim(), NumberStyles.Float, Inv, out h) && h >= 1 && h <= 100 ? h : _def.GateHalfWidthM;
        }

        // 選択中のラインだけを s [m] へ動かす (ほかは動かさない)。keepCands = 地図クリックの候補を残す (候補の切り替え中)
        void SetSelectedPos(double s, bool keepCands = false)
        {
            int i = GateList.SelectedIndex;
            if (_def == null || i < 0 || i >= _def.Gates.Count) return;
            if (!keepCands) ClearCands();
            if (_def.IsGymkhana)
            {
                // ジムカーナ: タイム計算はラインを距離の順に Start → Sector → Finish と見るので、順番が崩れないようにする。
                // セクターはスタートとフィニッシュの間に限り、動かした後は距離の順に Sector1, 2, ... と付け直す
                int last = _def.Gates.Count - 1;
                List<Gate> secs = _def.Gates.Skip(1).Take(Math.Max(0, last - 1)).ToList();
                double req = s;
                if (i == 0) s = Math.Min(s, (secs.Count > 0 ? secs.Min(g => g.S) : _def.Gates[last].S) - 1);
                else if (i == last) s = Math.Max(s, (secs.Count > 0 ? secs.Max(g => g.S) : _def.Gates[0].S) + 1);
                else s = Math.Max(_def.Gates[0].S + 1, Math.Min(_def.Gates[last].S - 1, s));
                if (Math.Abs(req - s) > 0.05)
                    Log(string.Format(Inv, "{0:0.0} m には置けないので {1:0.0} m に置きました (スタート → セクター → フィニッシュの順を保つため)", req, s));
                _def.UpdateGate(i, s, CurrentHalfWidth());
                Gate moved = _def.Gates[i];
                List<Gate> ordered = _def.Gates.Skip(1).Take(Math.Max(0, last - 1)).OrderBy(g => g.S).ToList();
                for (int k = 0; k < ordered.Count; k++) ordered[k].Name = "Sector" + (k + 1);
                List<Gate> all = new List<Gate> { _def.Gates[0] };
                all.AddRange(ordered);
                all.Add(_def.Gates[last]);
                _def.Gates.Clear(); _def.Gates.AddRange(all);
                RefreshGateList(_def.Gates.IndexOf(moved));
                Redraw();
                return;
            }
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
            if (_def.IsGymkhana)
            {
                int last = _def.Gates.Count - 1;
                GymkhanaData g = _res != null ? _res.Gym : null;
                if (i == 0) { if (g != null) SetSelectedPos(g.AutoStartS); }
                else if (i == last) { if (g != null) SetSelectedPos(g.AutoFinishS); }
                else SetSelectedPos(_def.Gates[0].S + (_def.Gates[last].S - _def.Gates[0].S) * i / last);
                return;
            }
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
                if (_def.IsGymkhana)
                    _def.MakeGymkhanaGates(_def.Gates[0].S, _def.Gates[_def.Gates.Count - 1].S, sectors, half);
                else
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

            // 背景の航空写真
            bool photo = PhotoCheck.IsChecked == true;
            PhotoCredit.Visibility = photo ? Visibility.Visible : Visibility.Collapsed;
            if (photo) DrawPhoto(toScr, scale, lat0 + cy / ky, lon0 + cx / kx, w, h);

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
            if (!_def.IsGymkhana) line.Points.Add(line.Points[0]);
            Preview.Children.Add(line);

            // 地図クリック用: 中心線の各点の画面座標と始点からの距離
            int ncl = _def.Centerline.Count;
            _clScr = new Point[ncl]; _clCum = new double[ncl];
            for (int i = 0; i < ncl; i++)
            {
                _clScr[i] = line.Points[i];
                if (i > 0)
                {
                    double dx = (_def.Centerline[i].Lon - _def.Centerline[i - 1].Lon) * kx, dy = (_def.Centerline[i].Lat - _def.Centerline[i - 1].Lat) * ky;
                    _clCum[i] = _clCum[i - 1] + Math.Sqrt(dx * dx + dy * dy);
                }
            }

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
                bool start = g.Name == CourseDefinition.StartGateName, finish = g.Name == CourseDefinition.FinishGateName;
                bool ctrl = g.Name == "ControlLine" || finish;
                Brush color = selected ? Brushes.DodgerBlue : start ? Brushes.LimeGreen : ctrl ? Brushes.White : Brushes.DarkOrange;
                Point a = toScr(g.A.Lat, g.A.Lon), b = toScr(g.B.Lat, g.B.Lon);
                Preview.Children.Add(new Line
                {
                    X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y, StrokeThickness = selected ? 7 : (ctrl || start ? 5 : 4),
                    Stroke = color
                });
                TextBlock tb = new TextBlock { Text = g.Name == "ControlLine" ? "Control" : g.Name, FontWeight = FontWeights.Bold,
                                               Foreground = color };
                Canvas.SetLeft(tb, Math.Max(a.X, b.X) + 4);
                Canvas.SetTop(tb, (a.Y + b.Y) / 2 - 9);
                Preview.Children.Add(tb);
            }

            // 地図クリックの候補 (番号付きの丸。今の候補は塗りつぶし)
            if (_cands != null)
                for (int k = 0; k < _cands.Count; k++)
                {
                    LatLon pos; double hd;
                    _def.PointAt(_cands[k], out pos, out hd);
                    Point p = toScr(pos.Lat, pos.Lon);
                    bool cur = k == _candIdx;
                    System.Windows.Shapes.Ellipse el = new System.Windows.Shapes.Ellipse
                    {
                        Width = 16, Height = 16, Stroke = Brushes.DodgerBlue, StrokeThickness = 2,
                        Fill = cur ? Brushes.DodgerBlue : new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0))
                    };
                    Canvas.SetLeft(el, p.X - 8); Canvas.SetTop(el, p.Y - 8);
                    Preview.Children.Add(el);
                    TextBlock nt = new TextBlock { Text = (k + 1).ToString(Inv), Foreground = Brushes.White, FontSize = 10, FontWeight = FontWeights.Bold };
                    Canvas.SetLeft(nt, p.X - 3); Canvas.SetTop(nt, p.Y - 7);
                    Preview.Children.Add(nt);
                }

            // 凡例 / 縮尺
            double barM = scale * 50 > 40 ? 50 : 100;
            Line bar = new Line { X1 = 16, Y1 = h - 16, X2 = 16 + barM * scale, Y2 = h - 16, Stroke = Theme.Text, StrokeThickness = 2 };
            Preview.Children.Add(bar);
            TextBlock bt = new TextBlock { Text = barM.ToString("0") + " m", FontSize = 11,
                Background = photo ? new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)) : null };
            Canvas.SetLeft(bt, 16); Canvas.SetTop(bt, h - 34);
            Preview.Children.Add(bt);
            TextBlock legend = new TextBlock
            {
                Text = _def.IsGymkhana
                    ? "赤: 中心線 (基準の走行)　青: 基準の走行の軌跡　橙: ほかの走行　緑: スタート　白: フィニッシュ　上が北"
                    : "赤: 中心線 (平均化)　青: 採用した周回の軌跡　橙: 除外した周回　上が北",
                FontSize = 11, Foreground = photo ? Brushes.White : Theme.TextSecondary, Padding = new Thickness(4, 1, 4, 1),
                Background = photo ? new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)) : null
            };
            Canvas.SetLeft(legend, 16); Canvas.SetTop(legend, 6);
            Preview.Children.Add(legend);
            DrawGraph();
        }

        // ================= ラインの置き方の補助 (地図クリック・候補の切り替え・時刻・速度の極小点・速度のグラフ) =================

        Point[] _clScr;              // 中心線の各点の画面座標 (Redraw で更新)
        double[] _clCum;             // 中心線の各点の始点からの距離 [m]
        List<double> _cands;         // 地図クリックの候補 (中心線の距離 [m])。クリックした所に近い順
        int _candIdx;
        List<SpeedMin> _minima = new List<SpeedMin>();
        double _hoverS = double.NaN; // 速度のグラフでマウスのある距離

        /// <summary>表示中のジムカーナの定義を作った基準の走行 (解析した直後だけ。保存済みの定義を開いたときは null)。</summary>
        GymkhanaData Gym
        {
            get
            {
                if (_def == null || !_def.IsGymkhana || _res == null || _res.Gym == null || !ReferenceEquals(_res.Def, _def)) return null;
                return GymkhanaBuilder.HasProfile(_res.Gym) ? _res.Gym : null;
            }
        }

        double StartS()
        {
            Gate st = _def.Gates.FirstOrDefault(x => x.Name == CourseDefinition.StartGateName) ?? _def.Gates[0];
            return st.S;
        }

        void ClearCands()
        {
            _cands = null;
            if (PickText != null) PickText.Visibility = Visibility.Collapsed;
        }

        // 地図をクリック: 選んだラインを、クリックした所にいちばん近い中心線の位置に置く。
        // 近くを何度も通る所では、通るごとに候補を作り (近い順)、ホイール / Tab で切り替える
        void Preview_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_def == null || _clScr == null || _clScr.Length < 2) return;
            if (GateList.SelectedIndex < 0) { ShowPick("先に一覧でラインを選んでください"); return; }
            Point p = e.GetPosition(Preview);
            const double thr = 24;     // 画面上でこの px 以内の所を候補にする
            int n = _clScr.Length, segs = _def.IsGymkhana ? n - 1 : n;
            List<double[]> found = new List<double[]>();     // {距離 [m], 画面上の離れ [px]}
            bool inGroup = false; double bestD = 0, bestS = 0;
            for (int i = 0; i < segs; i++)
            {
                Point a = _clScr[i], b = _clScr[(i + 1) % n];
                double sa = _clCum[i], sb = i + 1 < n ? _clCum[i + 1] : _def.LengthM;
                double dx = b.X - a.X, dy = b.Y - a.Y, l2 = dx * dx + dy * dy;
                double u = l2 > 0 ? Math.Max(0, Math.Min(1, ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / l2)) : 0;
                double ex = a.X + dx * u - p.X, ey = a.Y + dy * u - p.Y, d = Math.Sqrt(ex * ex + ey * ey);
                if (d < thr)
                {
                    double s = sa + (sb - sa) * u;
                    if (!inGroup || d < bestD) { bestD = d; bestS = s; }
                    inGroup = true;
                }
                else if (inGroup) { found.Add(new[] { bestS, bestD }); inGroup = false; }
            }
            if (inGroup) found.Add(new[] { bestS, bestD });
            if (found.Count == 0) { ClearCands(); ShowPick("近くに中心線がありません (赤い線の近くをクリックしてください)"); e.Handled = true; return; }
            _cands = found.OrderBy(f => f[1]).Select(f => f[0]).ToList();
            _candIdx = 0;
            ApplyCand();
            e.Handled = true;
        }

        void Preview_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (_cands == null || _cands.Count < 2) return;
            NextCand(e.Delta < 0 ? 1 : -1);
            e.Handled = true;
        }

        // 地図の上で Tab (Shift+Tab で戻る) でも候補を切り替える
        void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Tab || _cands == null || _cands.Count < 2 || !Preview.IsMouseOver) return;
            NextCand((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : 1);
            e.Handled = true;
        }

        void NextCand(int step)
        {
            _candIdx = (_candIdx + step + _cands.Count) % _cands.Count;
            ApplyCand();
        }

        void ApplyCand()
        {
            double s = _cands[_candIdx];
            SetSelectedPos(s, true);
            LatLon pos; double hd;
            _def.PointAt(s, out pos, out hd);
            string txt = string.Format(Inv, "候補 {0}/{1}: {2:0.0} m 目", _candIdx + 1, _cands.Count, s);
            GymkhanaData g = Gym;
            if (g != null)
                txt += string.Format(Inv, "・スタートから {0:0.0} 秒目・{1:0} km/h", GymkhanaBuilder.TimeAtS(g, s) - GymkhanaBuilder.TimeAtS(g, StartS()), GymkhanaBuilder.SpeedAtS(g, s));
            txt += string.Format(Inv, "・向き {0:0}° ({1})", ((hd % 360) + 360) % 360, Compass(hd));
            if (_cands.Count > 1) txt += "\nホイール / Tab で次の候補 (近くを " + _cands.Count + " 回通っています)";
            ShowPick(txt);
        }

        void ShowPick(string text)
        {
            PickText.Text = text;
            PickText.Visibility = Visibility.Visible;
        }

        static string Compass(double deg)
        {
            string[] d = { "北", "北東", "東", "南東", "南", "南西", "西", "北西" };
            int i = (int)Math.Round((((deg % 360) + 360) % 360) / 45) % 8;
            return d[i] + "向き";
        }

        // ---- 時刻で置く ----
        void PlaceAtTime_Click(object sender, RoutedEventArgs e)
        {
            PlaceAtTime();
        }

        void TimeBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { PlaceAtTime(); e.Handled = true; }
        }

        void PlaceAtTime()
        {
            GymkhanaData g = Gym;
            double sec;
            if (g == null || GateList.SelectedIndex < 0) return;
            if (!double.TryParse(TimeBox.Text.Trim(), NumberStyles.Float, Inv, out sec)) { LoadSelected(); return; }
            double s = GymkhanaBuilder.SAtTime(g, GymkhanaBuilder.TimeAtS(g, StartS()) + sec);
            SetSelectedPos(s);
            Log(string.Format(Inv, "スタートから {0:0.0} 秒目 → {1:0.0} m に置きました", sec, s));
        }

        // ---- 速度の極小点 ----
        void MinimaCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_updating || MinimaCombo.SelectedIndex < 0 || MinimaCombo.SelectedIndex >= _minima.Count) return;
            SetSelectedPos(_minima[MinimaCombo.SelectedIndex].S);
        }

        // ---- 距離 → 速度のグラフ ----
        const double GL = 40, GR = 10, GT = 18, GB = 18;    // グラフの余白

        void SpeedGraph_SizeChanged(object sender, SizeChangedEventArgs e) { DrawGraph(); }

        double GraphS(double x)
        {
            double w = SpeedGraph.ActualWidth;
            return Math.Max(0, Math.Min(_def.LengthM, (x - GL) / Math.Max(1, w - GL - GR) * _def.LengthM));
        }

        void SpeedGraph_MouseMove(object sender, MouseEventArgs e)
        {
            if (Gym == null) return;
            _hoverS = GraphS(e.GetPosition(SpeedGraph).X);
            DrawGraph();
        }

        void SpeedGraph_MouseLeave(object sender, MouseEventArgs e)
        {
            _hoverS = double.NaN;
            DrawGraph();
        }

        void SpeedGraph_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (Gym == null || GateList.SelectedIndex < 0) return;
            SetSelectedPos(GraphS(e.GetPosition(SpeedGraph).X));
        }

        void DrawGraph()
        {
            if (SpeedGraph == null) return;
            SpeedGraph.Children.Clear();
            GymkhanaData g = Gym;
            GraphBorder.Visibility = g != null ? Visibility.Visible : Visibility.Collapsed;
            if (g == null) return;
            double w = SpeedGraph.ActualWidth, h = SpeedGraph.ActualHeight;
            if (w < 100 || h < 50) return;
            double L = _def.LengthM, vmax = Math.Max(20, Math.Ceiling(g.ProfV.Max() / 20) * 20);
            Func<double, double> X = s => GL + s / L * (w - GL - GR);
            Func<double, double> Y = v => GT + (1 - v / vmax) * (h - GT - GB);
            Brush grid = new SolidColorBrush(Color.FromRgb(0x3A, 0x40, 0x4A));

            for (double v = 0; v <= vmax + 0.1; v += 20)
            {
                SpeedGraph.Children.Add(new Line { X1 = GL, X2 = w - GR, Y1 = Y(v), Y2 = Y(v), Stroke = grid, StrokeThickness = 1 });
                TextBlock t = new TextBlock { Text = v.ToString("0", Inv), FontSize = 10, Foreground = Theme.TextSecondary };
                Canvas.SetLeft(t, 4); Canvas.SetTop(t, Y(v) - 7);
                SpeedGraph.Children.Add(t);
            }
            for (double s = 0; s <= L; s += 100)
            {
                TextBlock t = new TextBlock { Text = s.ToString("0", Inv) + (s == 0 ? " m" : ""), FontSize = 10, Foreground = Theme.TextSecondary };
                Canvas.SetLeft(t, X(s) - 6); Canvas.SetTop(t, h - GB + 2);
                SpeedGraph.Children.Add(t);
            }
            TextBlock unit = new TextBlock { Text = "km/h", FontSize = 10, Foreground = Theme.TextSecondary };
            Canvas.SetLeft(unit, 4); Canvas.SetTop(unit, 0);
            SpeedGraph.Children.Add(unit);

            // 速度
            Polyline pl = new Polyline { Stroke = new SolidColorBrush(Color.FromRgb(0x4F, 0xC3, 0xF7)), StrokeThickness = 1.5, StrokeLineJoin = PenLineJoin.Round };
            for (int i = 0; i < g.ProfS.Length; i++) pl.Points.Add(new Point(X(g.ProfS[i]), Y(g.ProfV[i])));
            SpeedGraph.Children.Add(pl);

            // 速度の極小点 (▲)
            foreach (SpeedMin m in _minima)
            {
                Polygon tri = new Polygon { Fill = Brushes.Gold };
                double x = X(m.S), y = Y(m.Kmh) + 4;
                tri.Points.Add(new Point(x, y)); tri.Points.Add(new Point(x - 4, y + 7)); tri.Points.Add(new Point(x + 4, y + 7));
                SpeedGraph.Children.Add(tri);
            }

            // ライン
            for (int gi = 0; gi < _def.Gates.Count; gi++)
            {
                Gate gt = _def.Gates[gi];
                bool selected = gi == GateList.SelectedIndex;
                bool start = gt.Name == CourseDefinition.StartGateName, finish = gt.Name == CourseDefinition.FinishGateName;
                Brush c = selected ? Brushes.DodgerBlue : start ? Brushes.LimeGreen : finish ? Brushes.White : Brushes.DarkOrange;
                SpeedGraph.Children.Add(new Line { X1 = X(gt.S), X2 = X(gt.S), Y1 = GT - 2, Y2 = h - GB, Stroke = c, StrokeThickness = selected ? 3 : 2 });
                TextBlock t = new TextBlock { Text = start ? "Start" : finish ? "Finish" : gt.Name.Replace("Sector", "S"), FontSize = 10, FontWeight = FontWeights.Bold, Foreground = c };
                t.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                double lx = X(gt.S) + 3;
                if (lx + t.DesiredSize.Width > w - 2) lx = X(gt.S) - 3 - t.DesiredSize.Width;     // 右端では線の左に出す
                Canvas.SetLeft(t, lx); Canvas.SetTop(t, 2);
                SpeedGraph.Children.Add(t);
            }

            // マウスの位置
            if (!double.IsNaN(_hoverS))
            {
                double x = X(_hoverS);
                SpeedGraph.Children.Add(new Line { X1 = x, X2 = x, Y1 = GT, Y2 = h - GB, Stroke = Theme.TextSecondary, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 3, 3 } });
                TextBlock t = new TextBlock
                {
                    Text = string.Format(Inv, "{0:0.0} m / スタートから {1:0.0} 秒目 / {2:0} km/h  (クリックで、選んだラインをここに置く)", _hoverS,
                        GymkhanaBuilder.TimeAtS(g, _hoverS) - GymkhanaBuilder.TimeAtS(g, StartS()), GymkhanaBuilder.SpeedAtS(g, _hoverS)),
                    FontSize = 11, Foreground = Brushes.White, Background = new SolidColorBrush(Color.FromArgb(0xB0, 0, 0, 0)), Padding = new Thickness(4, 0, 4, 0)
                };
                t.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(t, Math.Max(GL, Math.Min(w - GR - t.DesiredSize.Width, x + 6))); Canvas.SetTop(t, GT + 2);
                SpeedGraph.Children.Add(t);
            }
        }

        // ---- 航空写真 (国土地理院の地理院タイル) ----

        readonly Dictionary<string, BitmapSource> _tileCache = new Dictionary<string, BitmapSource>();
        readonly HashSet<string> _tileLoading = new HashSet<string>();
        bool _redrawQueued;

        void Photo_Changed(object sender, RoutedEventArgs e)
        {
            if (Preview != null) Redraw();
        }

        // 見えている範囲の写真を、画面の座標に合わせて並べる。まだ無い写真は読み込み、届いたら描き直す
        void DrawPhoto(Func<double, double, Point> toScr, double scale, double latC, double lonC, double w, double h)
        {
            int z = GsiTiles.ZoomFor(latC, 1 / scale);
            double halfLon = w / 2 / scale / (111320.0 * Math.Cos(latC * Math.PI / 180)), halfLat = h / 2 / scale / 111132.0;
            int tx0 = GsiTiles.TileX(lonC - halfLon, z), tx1 = GsiTiles.TileX(lonC + halfLon, z);
            int ty0 = GsiTiles.TileY(latC + halfLat, z), ty1 = GsiTiles.TileY(latC - halfLat, z);
            if ((long)(tx1 - tx0 + 1) * (ty1 - ty0 + 1) > 300) return;
            for (int tx = tx0; tx <= tx1; tx++)
                for (int ty = ty0; ty <= ty1; ty++)
                {
                    string key = z + "/" + tx + "/" + ty;
                    BitmapSource src;
                    if (!_tileCache.TryGetValue(key, out src))
                    {
                        LoadTile(key, z, tx, ty);
                        continue;
                    }
                    if (src == null) continue;      // 写真がない場所
                    Point a = toScr(GsiTiles.TileLat(ty, z), GsiTiles.TileLon(tx, z));
                    Point b = toScr(GsiTiles.TileLat(ty + 1, z), GsiTiles.TileLon(tx + 1, z));
                    Image img = new Image { Source = src, Stretch = Stretch.Fill, Width = b.X - a.X + 0.5, Height = b.Y - a.Y + 0.5, Opacity = 0.85 };
                    Canvas.SetLeft(img, a.X); Canvas.SetTop(img, a.Y);
                    Preview.Children.Add(img);
                }
        }

        async void LoadTile(string key, int z, int x, int y)
        {
            if (!_tileLoading.Add(key)) return;
            BitmapSource bi = await GsiTiles.LoadAsync(z, x, y, null);
            _tileLoading.Remove(key);
            _tileCache[key] = bi;
            // 何枚も続けて届くので、描き直しはまとめて 1 回にする
            if (_redrawQueued) return;
            _redrawQueued = true;
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            _redrawQueued = false;
            Redraw();
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
                if (_def.IsGymkhana) GymkhanaRadio.IsChecked = true; else CircuitRadio.IsChecked = true;
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
