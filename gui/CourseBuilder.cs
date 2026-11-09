using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tk2Nmea;

namespace Tk2Gui
{
    // =====================================================================
    //  コース定義の作成
    //  GPS ログ (.tk2 / .nmea) の複数周回を平均化して、コースの中心線を求める。
    //  1 秒間隔の GPS は 1 周ごとに数 m ずれるので、
    //    1. 低速 (ピット・停止) と欠測を除く
    //    2. 参照ラインに各点を投影し、弧長ごとのオフセットの中央値でラインを動かす (反復)
    //    3. 周回ごとに所要時間・欠測を調べ、外れた周回 (ピットイン等) を除いてやり直す
    //  という手順で求める。線分ではなく点そのものを使うので、1 秒間隔でも曲がり角を切らない。
    // =====================================================================

    public struct LatLon
    {
        [JsonPropertyName("lat")] public double Lat { get; set; }
        [JsonPropertyName("lon")] public double Lon { get; set; }
        public LatLon(double lat, double lon) : this() { Lat = lat; Lon = lon; }
    }

    /// <summary>ログの 1 点 (時刻は通算秒)。</summary>
    public sealed class GpsSample
    {
        public double T;
        public double Lat;
        public double Lon;
        public double SpeedKmh = double.NaN;   // ロガーが記録した速度 (NMEA の RMC)。無ければ NaN
    }

    /// <summary>.tk2 / .nmea から位置列を読む。</summary>
    public static class GpsLoader
    {
        public static List<GpsSample> Load(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".tk2") return LoadTk2(path);
            return LoadNmea(path);
        }

        static List<GpsSample> LoadTk2(string path)
        {
            Track t = Program.LoadTrack(path, new Options());
            DateTime epoch = new DateTime(2000, 1, 1);
            List<GpsSample> list = new List<GpsSample>(t.Recs.Length);
            foreach (Record r in t.Recs)
                list.Add(new GpsSample { T = (r.Time - epoch).TotalSeconds, Lat = r.LatE7 / 1e7, Lon = r.LonE7 / 1e7 });
            return list;
        }

        // $GPRMC のみ使う。日付は最初の 1 点にだけ使い、以降は時刻の巻き戻りで日をまたぐ
        // (-localdate 付きで出力した NMEA は日付が UTC とずれるため、日付を信用しない)
        static List<GpsSample> LoadNmea(string path)
        {
            List<GpsSample> list = new List<GpsSample>();
            CultureInfo inv = CultureInfo.InvariantCulture;
            double day0 = -1, prevTod = -1, dayOffset = 0;
            foreach (string line in File.ReadLines(path))
            {
                if (!line.StartsWith("$GPRMC", StringComparison.Ordinal)) continue;
                string[] f = line.Split(',');
                if (f.Length < 10 || f[2] != "A" || f[1].Length < 6 || f[3].Length < 4 || f[5].Length < 5) continue;
                double tod = int.Parse(f[1].Substring(0, 2), inv) * 3600 + int.Parse(f[1].Substring(2, 2), inv) * 60
                             + double.Parse(f[1].Substring(4), inv);
                if (day0 < 0)
                {
                    if (f[9].Length >= 6)
                    {
                        DateTime d = new DateTime(2000 + int.Parse(f[9].Substring(4, 2), inv), int.Parse(f[9].Substring(2, 2), inv), int.Parse(f[9].Substring(0, 2), inv));
                        day0 = (d - new DateTime(2000, 1, 1)).TotalDays;
                    }
                    else day0 = 0;
                }
                if (prevTod >= 0 && tod < prevTod - 43200) dayOffset += 1;
                prevTod = tod;
                double lat = int.Parse(f[3].Substring(0, 2), inv) + double.Parse(f[3].Substring(2), inv) / 60.0;
                double lon = int.Parse(f[5].Substring(0, 3), inv) + double.Parse(f[5].Substring(3), inv) / 60.0;
                if (f[4] == "S") lat = -lat;
                if (f[6] == "W") lon = -lon;
                double kn;
                double sp = f[7].Length > 0 && double.TryParse(f[7], NumberStyles.Float, inv, out kn) ? kn * 1.852 : double.NaN;
                list.Add(new GpsSample { T = (day0 + dayOffset) * 86400 + tod, Lat = lat, Lon = lon, SpeedKmh = sp });
            }
            if (list.Count < 10) throw new InvalidDataException("位置データが読めません: " + Path.GetFileName(path));
            return list;
        }
    }

    // ---------------------------------------------------------------------

    public sealed class CourseParams
    {
        public string Name = "Course";
        public double MinSpeedKmh = 18;      // これ未満の点は使わない (ピット・停止・ばらつきの大きい低速域)
        public double MaxGapSec = 2;         // この秒数を超えて途切れたら別の走行区間
        public double LapMaxRatio = 1.2;     // 所要時間が周回の中央値のこの倍率を超える周回は使わない (ピットイン等)
        public double LapMinSpeedKmh = 15;   // 周回中にこれ未満まで落ちた周回は使わない
        public int SectorCount = 3;          // コントロールラインのほかに置くセクターライン数
        public double GateHalfWidthM = 12;   // ライン (ゲート) の片側の長さ
        public double? ControlPosM = null;   // 中心線の始点からコントロールラインまでの距離 (null = 自動)
    }

    public enum PointClass { Unused = 0, ExcludedLap = 1, Used = 2 }

    public sealed class Gate
    {
        [JsonPropertyName("name")] public string Name { get; set; }
        [JsonPropertyName("distanceM")] public double S { get; set; }   // 中心線の始点からの距離
        [JsonPropertyName("a")] public LatLon A { get; set; }
        [JsonPropertyName("b")] public LatLon B { get; set; }
    }

    public sealed class CourseStats
    {
        [JsonPropertyName("lapsUsed")] public int LapsUsed { get; set; }
        [JsonPropertyName("lapsTotal")] public int LapsTotal { get; set; }
        [JsonPropertyName("pointsUsed")] public int PointsUsed { get; set; }
        [JsonPropertyName("pointsTotal")] public int PointsTotal { get; set; }
        [JsonPropertyName("medianLapSec")] public double MedianLapSec { get; set; }
        [JsonPropertyName("scatterStdevM")] public double ScatterStdevM { get; set; }
        [JsonPropertyName("scatterP10P90M")] public double ScatterP10P90M { get; set; }
    }

    /// <summary>コース定義 (ファイルに保存する内容)。</summary>
    public sealed class CourseDefinition
    {
        [JsonPropertyName("format")] public string Format { get; set; } = "WintecTK2Course";
        [JsonPropertyName("version")] public int Version { get; set; } = 1;
        [JsonPropertyName("name")] public string Name { get; set; } = "Course";
        [JsonPropertyName("created")] public string Created { get; set; }
        [JsonPropertyName("sources")] public List<string> Sources { get; set; } = new List<string>();
        [JsonPropertyName("lengthM")] public double LengthM { get; set; }
        [JsonPropertyName("clockwise")] public bool Clockwise { get; set; }
        [JsonPropertyName("gateHalfWidthM")] public double GateHalfWidthM { get; set; }
        [JsonPropertyName("stats")] public CourseStats Stats { get; set; } = new CourseStats();
        [JsonPropertyName("gates")] public List<Gate> Gates { get; set; } = new List<Gate>();
        [JsonPropertyName("centerline")] public List<LatLon> Centerline { get; set; } = new List<LatLon>();

        static readonly JsonSerializerOptions JsonOpt = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            NumberHandling = JsonNumberHandling.AllowReadingFromString
        };

        public void Save(string path)
        {
            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOpt), new UTF8Encoding(false));
        }

        public static CourseDefinition Load(string path)
        {
            CourseDefinition d = JsonSerializer.Deserialize<CourseDefinition>(File.ReadAllText(path, Encoding.UTF8), JsonOpt);
            if (d == null || d.Format != "WintecTK2Course" || d.Centerline.Count < 8)
                throw new InvalidDataException("コース定義ファイルではありません: " + Path.GetFileName(path));
            return d;
        }

        // ---- 局所平面座標 (m) との変換 ----
        public double Lat0 { get { return Centerline.Average(p => p.Lat); } }
        public double Lon0 { get { return Centerline.Average(p => p.Lon); } }

        /// <summary>中心線の始点からの距離 s [m] での位置と進行方向 (北基準 [度]) を返す。</summary>
        public void PointAt(double s, out LatLon pos, out double headingDeg)
        {
            double lat0 = Lat0, lon0 = Lon0;
            double ky = 111132.0, kx = 111320.0 * Math.Cos(lat0 * Math.PI / 180);
            int n = Centerline.Count;
            double[] cum = new double[n + 1];
            for (int i = 0; i < n; i++)
            {
                LatLon a = Centerline[i], b = Centerline[(i + 1) % n];
                cum[i + 1] = cum[i] + Math.Sqrt(Math.Pow((b.Lon - a.Lon) * kx, 2) + Math.Pow((b.Lat - a.Lat) * ky, 2));
            }
            double L = cum[n];
            s = ((s % L) + L) % L;
            int k = 0;
            while (k < n - 1 && cum[k + 1] < s) k++;
            LatLon p0 = Centerline[k], p1 = Centerline[(k + 1) % n];
            double seg = cum[k + 1] - cum[k];
            double f = seg > 0 ? (s - cum[k]) / seg : 0;
            pos = new LatLon(p0.Lat + (p1.Lat - p0.Lat) * f, p0.Lon + (p1.Lon - p0.Lon) * f);
            double dx = (p1.Lon - p0.Lon) * kx, dy = (p1.Lat - p0.Lat) * ky;
            headingDeg = (Math.Atan2(dx, dy) * 180 / Math.PI + 360) % 360;
        }

        /// <summary>
        /// コントロールラインとセクターラインを作り直す。
        /// controlPosM: 中心線の始点からコントロールラインまでの距離。セクターは周長を (N+1) 等分した位置。
        /// </summary>
        public void MakeGates(double controlPosM, int sectorCount, double halfWidthM)
        {
            GateHalfWidthM = halfWidthM;
            Gates.Clear();
            for (int k = 0; k <= sectorCount; k++)
                Gates.Add(CreateGate(k == 0 ? "ControlLine" : "Sector" + k, controlPosM + LengthM * k / (sectorCount + 1), halfWidthM));
        }

        /// <summary>index 番目のラインだけを、中心線の始点から s [m] の位置へ動かす (ほかのラインは動かさない)。</summary>
        public void UpdateGate(int index, double s, double halfWidthM)
        {
            GateHalfWidthM = halfWidthM;
            Gates[index] = CreateGate(Gates[index].Name, s, halfWidthM);
        }

        /// <summary>全ラインの長さ (片側) を変える。位置はそのまま。</summary>
        public void SetGateHalfWidth(double halfWidthM)
        {
            for (int i = 0; i < Gates.Count; i++) UpdateGate(i, Gates[i].S, halfWidthM);
        }

        Gate CreateGate(string name, double s, double halfWidthM)
        {
            double ky = 111132.0, kx = 111320.0 * Math.Cos(Lat0 * Math.PI / 180);
            s = ((s % LengthM) + LengthM) % LengthM;
            LatLon pos; double hd;
            PointAt(s, out pos, out hd);
            double th = hd * Math.PI / 180;
            // 進行方向に直交する向き (左右) に halfWidth ずつ伸ばす
            double nx = Math.Cos(th), ny = -Math.Sin(th);
            return new Gate
            {
                Name = name,
                S = s,
                A = new LatLon(pos.Lat + ny * halfWidthM / ky, pos.Lon + nx * halfWidthM / kx),
                B = new LatLon(pos.Lat - ny * halfWidthM / ky, pos.Lon - nx * halfWidthM / kx)
            };
        }
    }

    /// <summary>作成結果 (定義 + 確認用の情報)。</summary>
    public sealed class CourseResult
    {
        public CourseDefinition Def;
        public List<GpsSample> Samples;     // 全データ (時刻順に連結)
        public PointClass[] Classes;        // 点ごとの扱い
        public int[] LapIds;                // 点ごとの周回番号
        public List<string> Log = new List<string>();
        public double AutoControlPosM;      // 自動で決めたコントロールラインの位置
    }

    // ---------------------------------------------------------------------

    public static class CourseBuilder
    {
        const double Step = 2.0;     // 中心線の点間隔 [m]
        const double Bin = 4.0;      // オフセットを集計する弧長の幅 [m]

        sealed class Proj
        {
            public double Lat0, Lon0, Kx, Ky;
            public Proj(double lat0, double lon0)
            {
                Lat0 = lat0; Lon0 = lon0; Ky = 111132.0; Kx = 111320.0 * Math.Cos(lat0 * Math.PI / 180);
            }
            public double X(double lon) { return (lon - Lon0) * Kx; }
            public double Y(double lat) { return (lat - Lat0) * Ky; }
            public double Lat(double y) { return Lat0 + y / Ky; }
            public double Lon(double x) { return Lon0 + x / Kx; }
        }

        struct Pt { public double X, Y; }

        sealed class Data
        {
            public double[] T, X, Y, V, Course;   // V: km/h, Course: 北基準 [度] (直前の点から)
            public int N;
        }

        /// <summary>複数ファイルのサンプルを時刻順に連結し、ファイルの境目は欠測として扱う。</summary>
        public static CourseResult Build(IList<List<GpsSample>> files, IList<string> sourceNames, CourseParams prm)
        {
            CourseResult res = new CourseResult();
            List<GpsSample> all = new List<GpsSample>();
            double offset = 0;
            foreach (List<GpsSample> f in files)
            {
                // 異なるファイルの間は必ず「欠測」になるように時刻を離す
                double baseT = f[0].T;
                foreach (GpsSample s in f) all.Add(new GpsSample { T = s.T - baseT + offset, Lat = s.Lat, Lon = s.Lon });
                offset = all[all.Count - 1].T + 3600;
            }
            if (all.Count < 100) throw new InvalidOperationException("データが少なすぎます (100 点以上必要)");

            Proj pj = new Proj(all.Average(s => s.Lat), all.Average(s => s.Lon));
            Data d = new Data();
            d.N = all.Count;
            d.T = new double[d.N]; d.X = new double[d.N]; d.Y = new double[d.N]; d.V = new double[d.N]; d.Course = new double[d.N];
            for (int i = 0; i < d.N; i++)
            {
                d.T[i] = all[i].T; d.X[i] = pj.X(all[i].Lon); d.Y[i] = pj.Y(all[i].Lat);
            }
            for (int i = 1; i < d.N; i++)
            {
                double dt = d.T[i] - d.T[i - 1];
                double dx = d.X[i] - d.X[i - 1], dy = d.Y[i] - d.Y[i - 1];
                d.V[i] = dt > 0 ? Math.Sqrt(dx * dx + dy * dy) / dt * 3.6 : 0;
                d.Course[i] = (Math.Atan2(dx, dy) * 180 / Math.PI + 360) % 360;
            }
            d.V[0] = d.N > 1 ? d.V[1] : 0; d.Course[0] = d.N > 1 ? d.Course[1] : 0;

            // ---- 1. 連続した走行区間 (欠測・低速で分割) ----
            List<List<int>> runs = new List<List<int>>();
            List<int> cur = new List<int>();
            for (int i = 0; i < d.N; i++)
            {
                bool ok = d.V[i] >= prm.MinSpeedKmh && (i == 0 || d.T[i] - d.T[i - 1] <= prm.MaxGapSec);
                if (!ok)
                {
                    if (cur.Count >= 2) runs.Add(cur);
                    cur = new List<int>();
                    if (d.V[i] >= prm.MinSpeedKmh) cur.Add(i);   // 欠測の直後の点は新しい区間の先頭
                }
                else cur.Add(i);
            }
            if (cur.Count >= 2) runs.Add(cur);
            List<int> moving = runs.SelectMany(r => r).ToList();
            res.Log.Add(string.Format("走行区間 {0} 個, 使える点 {1} / {2}", runs.Count, moving.Count, d.N));
            if (moving.Count < 50) throw new InvalidOperationException("走行している点が少なすぎます (速度の下限を下げてみてください)");

            // ---- 2. 初期の 1 周 ----
            List<Pt> loop = null;
            foreach (List<int> run in runs.OrderByDescending(r => r.Count))
            {
                loop = FindLoop(d, run);
                if (loop != null) break;
            }
            if (loop == null) throw new InvalidOperationException("周回を検出できませんでした (1 周以上つながった走行区間がありません)");
            double L;
            List<Pt> reference = Smooth(ResampleClosed(loop, Step, out L), 3);
            res.Log.Add(string.Format("初期の 1 周: {0:F0} m", L));

            // ---- 3. 全走行点で反復 → 周回の選別 → 選んだ周回だけで反復 ----
            reference = Refine(d, moving, reference, 40, out L);
            res.Log.Add(string.Format("1 回目の中心線: {0:F1} m", L));

            int[] lapIds; List<int> goodLaps; double medDur; int lapsTotal;
            SelectLaps(d, reference, prm, out lapIds, out goodLaps, out medDur, out lapsTotal);
            HashSet<int> good = new HashSet<int>(goodLaps);
            List<int> used = moving.Where(i => good.Contains(lapIds[i])).ToList();
            res.Log.Add(string.Format("周回 {0} 中 {1} を採用 (所要時間の中央値 {2:F0} 秒), 点 {3} / {4}", lapsTotal, goodLaps.Count, medDur, used.Count, moving.Count));
            if (goodLaps.Count < 3 || used.Count < 100)
                throw new InvalidOperationException("採用できる周回が少なすぎます (周回フィルタの倍率を上げるか、速度の下限を下げてください)");

            reference = Refine(d, used, reference, 25, out L);

            // ---- 4. 結果 ----
            double stdev, p1090;
            Scatter(d, used, reference, out stdev, out p1090);

            res.Samples = all;
            res.LapIds = lapIds;
            res.Classes = new PointClass[d.N];
            foreach (int i in moving) res.Classes[i] = PointClass.ExcludedLap;
            foreach (int i in used) res.Classes[i] = PointClass.Used;

            CourseDefinition def = new CourseDefinition();
            def.Name = prm.Name;
            def.Created = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            def.Sources = sourceNames.Select(Path.GetFileName).ToList();
            def.LengthM = L;
            double area = 0;
            for (int i = 0; i < reference.Count; i++)
            {
                Pt a = reference[i], b = reference[(i + 1) % reference.Count];
                area += a.X * b.Y - b.X * a.Y;
            }
            def.Clockwise = area < 0;   // 北が上の平面で面積が負なら時計回り
            foreach (Pt p in reference) def.Centerline.Add(new LatLon(pj.Lat(p.Y), pj.Lon(p.X)));
            def.Stats = new CourseStats
            {
                LapsUsed = goodLaps.Count, LapsTotal = lapsTotal, PointsUsed = used.Count, PointsTotal = d.N,
                MedianLapSec = medDur, ScatterStdevM = stdev, ScatterP10P90M = p1090
            };
            res.AutoControlPosM = AutoControlPos(d, reference, L, res);
            double ctrl = prm.ControlPosM ?? res.AutoControlPosM;
            def.MakeGates(ctrl, prm.SectorCount, prm.GateHalfWidthM);
            res.Def = def;
            res.Log.Add(string.Format("中心線 {0:F1} m ({1}), ばらつき 標準偏差 {2:F1} m / P10-P90 幅 {3:F1} m",
                L, def.Clockwise ? "時計回り" : "反時計回り", stdev, p1090));
            return res;
        }

        // ---- 幾何ユーティリティ ----

        static double AngDiff(double a, double b)
        {
            double x = ((a - b + 180) % 360 + 360) % 360 - 180;
            return Math.Abs(x);
        }

        /// <summary>走行区間の中から、出発点が再び (後ろの線分に) 近づく最初の 1 周を取り出す。</summary>
        static List<Pt> FindLoop(Data d, List<int> run)
        {
            for (int a = 0; a < run.Count; a++)
            {
                int ia = run[a];
                for (int b = a + 20; b < run.Count; b++)
                {
                    int p = run[b - 1], q = run[b];
                    double dx = d.X[q] - d.X[p], dy = d.Y[q] - d.Y[p];
                    double l2 = dx * dx + dy * dy;
                    if (l2 <= 0) continue;
                    double t = Math.Max(0, Math.Min(1, ((d.X[ia] - d.X[p]) * dx + (d.Y[ia] - d.Y[p]) * dy) / l2));
                    double dist = Math.Sqrt(Math.Pow(d.X[ia] - (d.X[p] + dx * t), 2) + Math.Pow(d.Y[ia] - (d.Y[p] + dy * t), 2));
                    double hd = (Math.Atan2(dx, dy) * 180 / Math.PI + 360) % 360;
                    if (dist < 5 && AngDiff(hd, d.Course[ia]) < 30)
                    {
                        List<Pt> loop = new List<Pt>();
                        for (int k = a; k < b; k++) loop.Add(new Pt { X = d.X[run[k]], Y = d.Y[run[k]] });
                        return loop;
                    }
                }
            }
            return null;
        }

        static List<Pt> ResampleClosed(List<Pt> pts, double step, out double length)
        {
            List<Pt> p = new List<Pt>(pts) { pts[0] };
            int m = p.Count - 1;
            double[] seg = new double[m];
            double L = 0;
            for (int k = 0; k < m; k++) { seg[k] = Math.Sqrt(Math.Pow(p[k + 1].X - p[k].X, 2) + Math.Pow(p[k + 1].Y - p[k].Y, 2)); L += seg[k]; }
            int n = Math.Max(8, (int)Math.Round(L / step));
            List<Pt> outp = new List<Pt>(n);
            int idx = 0; double acc = 0;
            for (int i = 0; i < n; i++)
            {
                double s = L * i / n;
                while (idx < m - 1 && acc + seg[idx] < s) { acc += seg[idx]; idx++; }
                double f = seg[idx] > 0 ? (s - acc) / seg[idx] : 0;
                outp.Add(new Pt { X = p[idx].X + (p[idx + 1].X - p[idx].X) * f, Y = p[idx].Y + (p[idx + 1].Y - p[idx].Y) * f });
            }
            length = L;
            return outp;
        }

        static List<Pt> Smooth(List<Pt> c, int w)
        {
            int n = c.Count;
            List<Pt> o = new List<Pt>(n);
            for (int i = 0; i < n; i++)
            {
                double sx = 0, sy = 0;
                for (int k = -w; k <= w; k++) { Pt q = c[((i + k) % n + n) % n]; sx += q.X; sy += q.Y; }
                o.Add(new Pt { X = sx / (2 * w + 1), Y = sy / (2 * w + 1) });
            }
            return o;
        }

        /// <summary>点 (px,py) を参照ラインに投影する。u = 0..n の弧位置, off = 左が正の距離。</summary>
        static void Project(List<Pt> r, double px, double py, out double dist, out double u, out double off, out double heading)
        {
            int n = r.Count;
            dist = double.MaxValue; u = 0; off = 0; heading = 0;
            for (int i = 0; i < n; i++)
            {
                Pt a = r[i], b = r[(i + 1) % n];
                double dx = b.X - a.X, dy = b.Y - a.Y, l2 = dx * dx + dy * dy;
                double t = l2 > 0 ? Math.Max(0, Math.Min(1, ((px - a.X) * dx + (py - a.Y) * dy) / l2)) : 0;
                double qx = a.X + dx * t, qy = a.Y + dy * t;
                double dd = Math.Sqrt((px - qx) * (px - qx) + (py - qy) * (py - qy));
                if (dd < dist)
                {
                    dist = dd; u = i + t;
                    heading = (Math.Atan2(dx, dy) * 180 / Math.PI + 360) % 360;
                    double side = dx * (py - qy) - dy * (px - qx);
                    off = side >= 0 ? dd : -dd;
                }
            }
        }

        static double Median(List<double> v)
        {
            v.Sort();
            int n = v.Count;
            return n % 2 == 1 ? v[n / 2] : (v[n / 2 - 1] + v[n / 2]) / 2;
        }

        static List<double>[] FitOffsets(Data d, List<int> idx, List<Pt> r)
        {
            int n = r.Count;
            int nb = Math.Max(8, (int)Math.Round(n * Step / Bin));
            List<double>[] bins = new List<double>[nb];
            for (int i = 0; i < nb; i++) bins[i] = new List<double>();
            foreach (int i in idx)
            {
                double dist, u, off, hd;
                Project(r, d.X[i], d.Y[i], out dist, out u, out off, out hd);
                if (dist > 25 || AngDiff(hd, d.Course[i]) > 60) continue;
                bins[(int)(u / n * nb) % nb].Add(off);
            }
            return bins;
        }

        static List<Pt> ApplyOffsets(List<Pt> r, List<double>[] bins, int ker)
        {
            int n = r.Count, nb = bins.Length;
            double[] med = new double[nb];
            for (int i = 0; i < nb; i++) med[i] = bins[i].Count >= 4 ? Median(new List<double>(bins[i])) : 0;
            double[] sm = new double[nb];
            for (int i = 0; i < nb; i++)
            {
                double acc = 0, ws = 0;
                for (int k = -ker; k <= ker; k++) { double w = ker + 1 - Math.Abs(k); acc += med[((i + k) % nb + nb) % nb] * w; ws += w; }
                sm[i] = acc / ws;
            }
            List<Pt> res = new List<Pt>(n);
            for (int i = 0; i < n; i++)
            {
                Pt a = r[(i - 1 + n) % n], b = r[(i + 1) % n];
                double tx = b.X - a.X, ty = b.Y - a.Y, tl = Math.Sqrt(tx * tx + ty * ty);
                if (tl == 0) tl = 1;
                double nx = -ty / tl, ny = tx / tl;   // 左向きの法線
                double pos = (double)i / n * nb; int i0 = (int)pos % nb, i1 = (i0 + 1) % nb; double f = pos - Math.Floor(pos);
                double off = sm[i0] * (1 - f) + sm[i1] * f;
                res.Add(new Pt { X = r[i].X + nx * off, Y = r[i].Y + ny * off });
            }
            return res;
        }

        static List<Pt> Refine(Data d, List<int> idx, List<Pt> r, int iterations, out double length)
        {
            length = 0;
            for (int it = 0; it < iterations; it++)
            {
                List<Pt> moved = ApplyOffsets(r, FitOffsets(d, idx, r), 2);
                r = ResampleClosed(Smooth(moved, 3), Step, out length);
            }
            return r;
        }

        /// <summary>周回ごとの所要時間・欠測・最低速度を調べ、ピットイン等で外れた周回を除く。</summary>
        static void SelectLaps(Data d, List<Pt> r, CourseParams prm, out int[] lapIds, out List<int> good, out double medianDur, out int lapsTotal)
        {
            int n = r.Count;
            lapIds = new int[d.N];
            int lap = 0; double prevU = -1, prevT = 0;
            for (int i = 0; i < d.N; i++)
            {
                double dist, u, off, hd;
                Project(r, d.X[i], d.Y[i], out dist, out u, out off, out hd);
                u /= n;
                if (prevU >= 0 && d.T[i] - prevT <= 3)
                {
                    if (prevU > 0.7 && u < 0.3) lap++;        // 1 周を超えた
                    else if (prevU < 0.3 && u > 0.7) lap--;   // ゴール付近で戻った (ばらつき)
                }
                prevU = u; prevT = d.T[i];
                lapIds[i] = lap;
            }
            // 周回ごとにまとめる (最初と最後は途中からなので除く)
            int minLap = lapIds.Min(), maxLap = lapIds.Max();
            List<int> laps = new List<int>();
            for (int l = minLap + 1; l < maxLap; l++) laps.Add(l);
            lapsTotal = laps.Count;
            Dictionary<int, List<int>> byLap = new Dictionary<int, List<int>>();
            for (int i = 0; i < d.N; i++)
            {
                List<int> list;
                if (!byLap.TryGetValue(lapIds[i], out list)) { list = new List<int>(); byLap[lapIds[i]] = list; }
                list.Add(i);
            }
            List<double> durs = new List<double>();
            foreach (int l in laps)
            {
                List<int> pts = byLap[l];
                if (pts.Count >= 5) durs.Add(d.T[pts[pts.Count - 1]] - d.T[pts[0]]);
            }
            medianDur = durs.Count > 0 ? Median(durs) : 0;
            good = new List<int>();
            foreach (int l in laps)
            {
                List<int> pts = byLap[l];
                if (pts.Count < 5) continue;
                double dur = d.T[pts[pts.Count - 1]] - d.T[pts[0]];
                double maxGap = 0, vmin = double.MaxValue;
                for (int k = 0; k < pts.Count; k++)
                {
                    if (k > 0) maxGap = Math.Max(maxGap, d.T[pts[k]] - d.T[pts[k - 1]]);
                    vmin = Math.Min(vmin, d.V[pts[k]]);
                }
                if (dur <= medianDur * prm.LapMaxRatio && maxGap <= prm.MaxGapSec && vmin >= prm.LapMinSpeedKmh) good.Add(l);
            }
        }

        static void Scatter(Data d, List<int> idx, List<Pt> r, out double stdev, out double p1090)
        {
            List<double>[] bins = FitOffsets(d, idx, r);
            List<double> all = bins.SelectMany(b => b).ToList();
            if (all.Count == 0) { stdev = 0; p1090 = 0; return; }
            double mean = all.Average();
            stdev = Math.Sqrt(all.Sum(v => (v - mean) * (v - mean)) / all.Count);
            List<double> widths = new List<double>();
            foreach (List<double> b in bins)
            {
                if (b.Count < 10) continue;
                List<double> s = new List<double>(b); s.Sort();
                widths.Add(s[(int)(s.Count * 0.9)] - s[(int)(s.Count * 0.1)]);
            }
            p1090 = widths.Count > 0 ? Median(widths) : 0;
        }

        /// <summary>
        /// コントロールラインの既定位置。停止・低速の点 (ピットやスタート位置) が集まる場所の「手前」に置く。
        /// ピットインは「コントロールラインを通過したあとで停止」する前提なので、停止位置より前 (5 パーセンタイル
        /// の位置の 8m 手前) にする。停止点がなければ中心線の始点。
        /// </summary>
        static double AutoControlPos(Data d, List<Pt> r, double L, CourseResult res)
        {
            int n = r.Count;
            List<double> us = new List<double>();
            double sx = 0, sy = 0;
            for (int i = 0; i < d.N; i++)
            {
                if (d.V[i] >= 3) continue;
                double dist, u, off, hd;
                Project(r, d.X[i], d.Y[i], out dist, out u, out off, out hd);
                if (dist > 30) continue;
                double ang = u / n * 2 * Math.PI;
                sx += Math.Cos(ang); sy += Math.Sin(ang);
                us.Add(u / n * L);
            }
            if (us.Count < 5) { res.Log.Add("停止点が少ないため、コントロールラインは中心線の始点に置きました"); return 0; }
            double a = Math.Atan2(sy, sx); if (a < 0) a += 2 * Math.PI;
            double mean = a / (2 * Math.PI) * L;
            // 停止点の位置を、平均位置からの前後の距離にして (周回をまたぐので ±L/2 に収める)、手前側の 5 パーセンタイルを求める
            List<double> rel = us.Select(v => { double dlt = (v - mean) % L; if (dlt > L / 2) dlt -= L; if (dlt < -L / 2) dlt += L; return dlt; }).OrderBy(v => v).ToList();
            double p5 = rel[(int)(rel.Count * 0.05)];
            double pos = mean + p5 - 8;
            pos = ((pos % L) + L) % L;
            res.Log.Add(string.Format("停止・低速の点 {0} 個 (コース上 {1:F0}〜{2:F0} m 付近) の手前 {3:F0} m にコントロールラインを置きました",
                us.Count, ((mean + p5) % L + L) % L, ((mean + rel[(int)(rel.Count * 0.95)]) % L + L) % L, pos));
            return pos;
        }
    }
    // ---------------------------------------------------------------------
    //  Google Earth で確認するための KML
    // ---------------------------------------------------------------------
    public static class CourseKml
    {
        static string C(double v) { return v.ToString("0.#######", CultureInfo.InvariantCulture); }
        static string Xml(string s) { return System.Security.SecurityElement.Escape(s ?? ""); }

        static string Coords(IEnumerable<LatLon> pts)
        {
            StringBuilder sb = new StringBuilder();
            foreach (LatLon p in pts) sb.Append(C(p.Lon)).Append(',').Append(C(p.Lat)).Append(",0 ");
            return sb.ToString().TrimEnd();
        }

        public static string Build(CourseResult res, bool showData)
        {
            CourseDefinition def = res.Def;
            StringBuilder k = new StringBuilder();
            k.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            k.AppendLine("<kml xmlns=\"http://www.opengis.net/kml/2.2\"><Document>");
            k.AppendLine("<name>" + Xml(def.Name) + " (コース定義)</name>");
            k.AppendLine("<description>" + Xml(string.Format(CultureInfo.InvariantCulture,
                "周長 {0:F0} m / {1} / 採用 {2} 周 (全 {3} 周) / ばらつき 標準偏差 {4:F1} m",
                def.LengthM, def.Clockwise ? "時計回り" : "反時計回り", def.Stats.LapsUsed, def.Stats.LapsTotal, def.Stats.ScatterStdevM)) + "</description>");
            k.AppendLine("<Style id=\"center\"><LineStyle><color>ff0000ff</color><width>4</width></LineStyle></Style>");
            k.AppendLine("<Style id=\"gate\"><LineStyle><color>ff00ffff</color><width>5</width></LineStyle><LabelStyle><color>ff00ffff</color><scale>1.1</scale></LabelStyle></Style>");
            k.AppendLine("<Style id=\"ctrl\"><LineStyle><color>ffffffff</color><width>6</width></LineStyle><LabelStyle><color>ffffffff</color><scale>1.2</scale></LabelStyle></Style>");
            k.AppendLine("<Style id=\"used\"><LineStyle><color>66ff9020</color><width>1.5</width></LineStyle></Style>");
            k.AppendLine("<Style id=\"excl\"><LineStyle><color>5560a0ff</color><width>1.5</width></LineStyle></Style>");
            k.AppendLine("<Style id=\"arrow\"><IconStyle><scale>0.8</scale><Icon><href>http://maps.google.com/mapfiles/kml/shapes/arrow.png</href></Icon></IconStyle><LabelStyle><scale>0</scale></LabelStyle></Style>");

            k.AppendLine("<Folder><name>中心線 (平均化したコース)</name><open>1</open>");
            List<LatLon> loop = new List<LatLon>(def.Centerline) { def.Centerline[0] };
            k.AppendLine("<Placemark><name>中心線</name><styleUrl>#center</styleUrl><LineString><tessellate>1</tessellate><altitudeMode>clampToGround</altitudeMode><coordinates>"
                + Coords(loop) + "</coordinates></LineString></Placemark>");
            // 進行方向の矢印 (周長の 1/8 ごと)。Google Earth 標準の arrow.png は heading=0 で
            // 「下向き」に描かれるため、進行方向に 180° を足して先端が進行方向を向くようにする
            // (3D 地形に貼り付くポリゴンは崩れて見えるので、アイコンを使う)
            for (int i = 0; i < 8; i++)
            {
                LatLon pos; double hd;
                def.PointAt(def.LengthM * i / 8 + def.LengthM / 16, out pos, out hd);
                double iconHeading = (hd + 180) % 360;
                k.AppendLine("<Placemark><name></name><styleUrl>#arrow</styleUrl><Style><IconStyle><heading>" + C(iconHeading) + "</heading></IconStyle></Style><Point><coordinates>"
                    + C(pos.Lon) + "," + C(pos.Lat) + ",0</coordinates></Point></Placemark>");
            }
            k.AppendLine("</Folder>");

            k.AppendLine("<Folder><name>コントロールライン・セクター</name><open>1</open>");
            foreach (Gate g in def.Gates)
            {
                k.AppendLine("<Placemark><name>" + Xml(g.Name) + "</name><styleUrl>#" + (g.Name == "ControlLine" ? "ctrl" : "gate")
                    + "</styleUrl><LineString><tessellate>1</tessellate><altitudeMode>clampToGround</altitudeMode><coordinates>"
                    + Coords(new[] { g.A, g.B }) + "</coordinates></LineString></Placemark>");
            }
            k.AppendLine("</Folder>");

            if (showData && res.Samples != null)
            {
                // 周回ごとに線にする (青 = 採用, オレンジ = 除外した周回)。欠測・低速で切る
                k.AppendLine("<Folder><name>元データ (青=採用, オレンジ=除外した周回)</name><visibility>1</visibility>");
                for (int cls = 2; cls >= 1; cls--)
                {
                    List<LatLon> run = new List<LatLon>();
                    int lastLap = int.MinValue;
                    Action flush = () =>
                    {
                        if (run.Count >= 2)
                            k.AppendLine("<Placemark><styleUrl>#" + (cls == 2 ? "used" : "excl") + "</styleUrl><LineString><tessellate>1</tessellate><altitudeMode>clampToGround</altitudeMode><coordinates>"
                                + Coords(run) + "</coordinates></LineString></Placemark>");
                        run = new List<LatLon>();
                    };
                    for (int i = 0; i < res.Samples.Count; i++)
                    {
                        bool match = (int)res.Classes[i] == cls;
                        bool brk = !match || res.LapIds[i] != lastLap || (i > 0 && res.Samples[i].T - res.Samples[i - 1].T > 2.5);
                        if (brk) flush();
                        if (match) { run.Add(new LatLon(res.Samples[i].Lat, res.Samples[i].Lon)); lastLap = res.LapIds[i]; }
                    }
                    flush();
                }
                k.AppendLine("</Folder>");
            }
            k.AppendLine("</Document></kml>");
            return k.ToString();
        }
    }
}
