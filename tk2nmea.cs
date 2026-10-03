// tk2nmea - Wintec TK2 ログを GPS週番号ロールオーバー補正して NMEA と KMZ/KML (Google Earth 用) に変換する
//
// Windows 標準の C# コンパイラ (.NET Framework 4.x の csc.exe, C# 5) でビルドできるように書いています。
// ビルド: build.bat をダブルクリック
//
// 使い方:
//   tk2nmea.exe の上に .tk2 ファイル(複数可)またはフォルダをドラッグ&ドロップ
//   → 時間範囲と停止区間を表示し、開始/終了時刻を聞く (Enter のみならカットしない)
//   コマンドライン: tk2nmea.exe [オプション] file.tk2 ...
//
// オプション:
//   -start T     この時刻より前をカット (現地時刻 HH:mm:ss / HH:mm / yyyy-MM-dd HH:mm:ss)
//   -end T       この時刻より後をカット
//   -localdate   RMC の日付を現地(ヘッダのタイムゾーン)日付で出力(既定は NMEA 規格どおり UTC 日付)
//   -weeks N     補正する週数を指定(既定: 自動 = 2019-04-07 より前の日付に 1024 週単位で加算)
//   -o DIR       出力先フォルダ(既定は tk2 と同じフォルダ)
//   -kml         Google Earth 用を KMZ ではなく KML で出力 (凡例画像なし)
//   -nokml       Google Earth 用ファイルを出力しない(NMEA のみ)
//   -nopause     時刻の問い合わせと終了時の Enter 待ちをしない
//   -kmicon X / -kmcolor C / -kmscale S / -speedstep N / -speedmax N
//   -altitude gps|ground / -altsmooth 秒 / -altoffset m / -groundoffset m / -extrude 0|1
//   -tour 0|1 / -tourspeed 倍 / -tourstep 秒 / -tourrange m / -tourtilt 度 / -tourheading follow|north  (tk2nmea.ini と同じ設定)
//   -trackline 0|1 / -trackcolor C / -trackwidth N / -carname 名前 / -carlabel S
//   -tourseg N / -tourlead 秒 / -tourheadwin 秒 / -tourheadtau 秒  (軌跡の先端位置 / 進行方向を求める範囲 / カメラ向きの追従の遅れ)
//
// 設定ファイル: exe と同じフォルダの tk2nmea.ini (key=value)。コマンドラインのオプションが優先。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace Tk2Nmea
{
    struct Record
    {
        public ushort Flags;
        public DateTime Time;   // UTC (補正後)
        public int LatE7;       // 緯度 ×1e-7 度
        public int LonE7;       // 経度 ×1e-7 度
        public short AltM;      // 高度 [m]
    }

    class Track
    {
        public Record[] Recs;
        public TimeSpan Tz;
        public int Weeks;
    }

    class Options
    {
        public bool LocalDate = false;
        public int Weeks = -1;          // -1 = 自動
        public string OutDir = null;
        public bool NoPause = false;
        public bool NoKml = false;
        public bool PlainKml = false;
        public string Start = null;
        public string End = null;
        public List<string> Inputs = new List<string>();

        // Google Earth の見た目 (tk2nmea.ini で変更可)
        public string KmIcon = "pin";       // Km ポストのシンボル (プリセット名 / URL / png ファイル)
        public string KmColor = "yellow";   // シンボルとラベルの色
        public double KmScale = 1.3;        // シンボルの大きさ
        public double KmLabel = 1.2;        // ラベル文字の大きさ (0 で非表示)
        public int SpeedStep = 5;           // Speed_Track の色分け幅 [km/h]
        public int SpeedMax = 120;          // この速度以上は最高色 [km/h]
        public bool AltGps = true;          // true: GPS 高度で表示 (橋・高架も再現) / false: 地表に沿わせる
        public int AltSmooth = 7;           // GPS 高度の移動平均の幅 [秒] (0 で平滑化なし)
        public double AltOffset = 0;        // GPS 高度に足す補正値 [m]
        public double GroundOffset = 0;     // altitude=ground のとき、線・マーカー・カメラを地表から浮かせる高さ [m]
        public bool Lift { get { return !AltGps && GroundOffset > 0; } }
        public bool Extrude = false;        // 線から地面まで幕を下ろして表示

        // 走行アニメーション (Google Earth のツアー)
        public bool Tour = true;            // ツアーを作る (線が走行に合わせて描かれていく)
        public double TourSpeed = 10;       // 再生の倍速
        public int TourStep = 2;            // カメラ位置の間隔 [秒] (実時間)
        public double TourRange = 500;      // カメラから車までの距離 [m]
        public double TourTilt = 60;        // カメラの傾き [度] (0 = 真上から, 90 = 水平)
        public bool TourFollow = true;      // true: 進行方向を向く / false: 北が上
        public bool TrackLine = false;      // true: 走行軌跡 (gx:Track) 自身の線を表示 (単色。Google Earth が時間に合わせて滑らかに伸ばす)。Speed_Track は非表示
        public string TrackColor = "#FFA500"; // TrackLine の線の色
        public double TrackWidth = 4;       // TrackLine の線の太さ
        public string CarName = "";         // 自車マーカーの下に出すラベル (車種名など)。空なら出さない
        public double CarLabel = 1.2;       // ラベルの大きさ
        public int TourSeg = 1;             // 記録点の間 (通常 1 秒) を何分割して軌跡を伸ばすか (2 なら 0.5 秒刻み。時刻は小数秒で出力)
        public int TourLead = 0;            // 軌跡の表示タイミングの補正 [秒]。+ で先端が前へ(車より先行), - で後ろへ
        public double TourHeadWin = 5;      // 進行方向を求める範囲 [秒] (前後それぞれ)。大きいほど細かい曲がりに反応しにくい
        public double TourHeadTau = 0;      // カメラ向きの追従の遅れ [秒]。0 = すぐ向く。大きいほどゆっくり追従
    }

    static class Program
    {
        const int HeaderSize = 1024;
        const int RecordSize = 16;
        const string Magic = "WintecLogTk2";
        const double EarthR = 6378137.0;
        const double KnotMps = 1852.0 / 3600.0;

        // 2回目の GPS 週番号ロールオーバー日。これより前の日付は誤りとみなして補正する。
        static readonly DateTime RolloverRef = new DateTime(2019, 4, 7, 0, 0, 0, DateTimeKind.Utc);
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // 速度色分けの基準色 (TimeMachineX の Speed_Track の 7 色, RGB)。
        // 0 ～ SpeedMax を 6 等分した位置にこの色を置き、間は線形補間する。
        // SpeedStep=20, SpeedMax=120 なら TimeMachineX と同じ色分けになる。
        static readonly int[,] SpeedAnchors = {
            { 186, 85, 211 },   // 紫
            { 0, 0, 255 },      // 青
            { 135, 206, 255 },  // 水色
            { 127, 255, 0 },    // 緑
            { 255, 255, 0 },    // 黄
            { 255, 127, 0 },    // 橙
            { 255, 0, 0 }       // 赤
        };

        // Km ポストのシンボル (Google Earth 標準アイコン。白いので色付けできる)
        const string IconBase = "http://maps.google.com/mapfiles/kml/";
        static readonly string[,] IconPresets = {
            { "pin", "pushpin/wht-pushpin.png" },
            { "flag", "shapes/flag.png" },
            { "star", "shapes/star.png" },
            { "circle", "shapes/placemark_circle.png" },
            { "square", "shapes/placemark_square.png" },
            { "diamond", "shapes/open-diamond.png" },
            { "triangle", "shapes/triangle.png" },
            { "target", "shapes/target.png" },
            { "donut", "shapes/donut.png" },
            { "dot", "shapes/shaded_dot.png" },
            { "paddle", "paddle/wht-blank.png" }
        };

        static readonly string[,] ColorNames = {
            { "white", "ffffffff" }, { "yellow", "ff00ffff" }, { "orange", "ff00a5ff" }, { "red", "ff0000ff" },
            { "pink", "ffcbc0ff" }, { "magenta", "ffff00ff" }, { "purple", "ffd355ba" }, { "blue", "ffff0000" },
            { "cyan", "ffffff00" }, { "green", "ff00ff00" }, { "black", "ff000000" }
        };

        static int Main(string[] args)
        {
            Options opt;
            try
            {
                opt = ParseArgs(args);
            }
            catch (Exception ex)
            {
                Console.WriteLine("エラー: " + ex.Message);
                Usage();
                Pause(false);
                return 2;
            }

            if (opt.Inputs.Count == 0)
            {
                Usage();
                Pause(opt.NoPause);
                return 0;
            }

            List<string> files = Collect(opt.Inputs);
            int ok = 0, ng = 0;
            foreach (string f in files)
            {
                try
                {
                    string info;
                    List<string> outPaths = ConvertFile(f, opt, out info);
                    Console.WriteLine("OK  " + Path.GetFileName(f));
                    foreach (string op in outPaths) Console.WriteLine(" -> " + op);
                    Console.WriteLine("    " + info);
                    ok++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("NG  " + Path.GetFileName(f));
                    Console.WriteLine("    " + ex.Message);
                    ng++;
                }
                Console.WriteLine();
            }
            Console.WriteLine(string.Format("完了: 成功 {0} 件, 失敗 {1} 件", ok, ng));
            Pause(opt.NoPause);
            return ng > 0 ? 1 : 0;
        }

        static void Usage()
        {
            Console.WriteLine("tk2nmea - Wintec TK2 → NMEA / KMZ 変換 (GPS週番号ロールオーバー補正付き)");
            Console.WriteLine();
            Console.WriteLine("使い方: .tk2 ファイルまたはフォルダを tk2nmea.exe にドラッグ&ドロップ");
            Console.WriteLine("        tk2nmea.exe [オプション] ファイル.tk2 ...");
            Console.WriteLine();
            Console.WriteLine("  -start T    この時刻より前をカット (例 08:05:00, 2026-09-26 08:05:00)");
            Console.WriteLine("  -end T      この時刻より後をカット");
            Console.WriteLine("  -localdate  RMC の日付を現地日付で出力 (既定は UTC 日付)");
            Console.WriteLine("  -weeks N    補正週数 (既定: 自動, 0 で補正なし)");
            Console.WriteLine("  -o DIR      出力先フォルダ");
            Console.WriteLine("  -kml        Google Earth 用を KMZ ではなく KML で出力");
            Console.WriteLine("  -nokml      Google Earth 用ファイルを出力しない (NMEA のみ)");
            Console.WriteLine("  -nopause    時刻の問い合わせと終了時の Enter 待ちをしない");
        }

        static void Pause(bool noPause)
        {
            if (noPause) return;
            Console.Write("Enter キーで終了します...");
            Console.ReadLine();
        }

        static Options ParseArgs(string[] args)
        {
            Options o = new Options();
            LoadIni(o);
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                string key = a.TrimStart('-', '/').ToLowerInvariant();
                bool isOpt = a.StartsWith("-") || (a.StartsWith("/") && a.Length > 1 && !File.Exists(a) && !Directory.Exists(a));
                if (!isOpt)
                {
                    o.Inputs.Add(a);
                    continue;
                }
                switch (key)
                {
                    case "localdate": o.LocalDate = true; break;
                    case "nopause": o.NoPause = true; break;
                    case "nokml": o.NoKml = true; break;
                    case "kml": o.PlainKml = true; break;
                    case "weeks": o.Weeks = int.Parse(NextArg(args, ref i, a), Inv); break;
                    case "o": o.OutDir = NextArg(args, ref i, a); break;
                    case "start": o.Start = NextArg(args, ref i, a); break;
                    case "end": o.End = NextArg(args, ref i, a); break;
                    case "kmicon":
                    case "kmcolor":
                    case "kmscale":
                    case "kmlabel":
                    case "speedstep":
                    case "speedmax":
                    case "altitude":
                    case "altsmooth":
                    case "altoffset":
                    case "groundoffset":
                    case "extrude":
                    case "tour":
                    case "tourspeed":
                    case "tourstep":
                    case "tourrange":
                    case "tourtilt":
                    case "tourheading":
                    case "tourlead":
                    case "tourseg":
                    case "trackline":
                    case "trackcolor":
                    case "trackwidth":
                    case "carname":
                    case "carlabel":
                    case "tourheadwin":
                    case "tourheadtau":
                        ApplySetting(o, key, NextArg(args, ref i, a));
                        break;
                    default: throw new ArgumentException("不明なオプション: " + a);
                }
            }
            return o;
        }

        static string ExeDir()
        {
            return AppDomain.CurrentDomain.BaseDirectory;
        }

        // exe と同じフォルダの tk2nmea.ini を読む (key=value, ; または # 以降はコメント)
        static void LoadIni(Options o)
        {
            string path = Path.Combine(ExeDir(), "tk2nmea.ini");
            if (!File.Exists(path)) return;
            string[] lines = File.ReadAllLines(path, Encoding.UTF8);
            for (int n = 0; n < lines.Length; n++)
            {
                string line = lines[n];
                // コメント: ; 以降、または行頭の # (値の #RRGGBB は残す)
                int c = line.IndexOf(';');
                if (c >= 0) line = line.Substring(0, c);
                line = line.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("[")) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) throw new ArgumentException(string.Format("tk2nmea.ini {0} 行目: key=value の形式ではありません", n + 1));
                string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                string val = line.Substring(eq + 1).Trim();
                try
                {
                    ApplySetting(o, key, val);
                }
                catch (Exception ex)
                {
                    throw new ArgumentException(string.Format("tk2nmea.ini {0} 行目: {1}", n + 1, ex.Message));
                }
            }
        }

        static void ApplySetting(Options o, string key, string val)
        {
            switch (key)
            {
                case "kmicon": o.KmIcon = val; break;
                case "kmcolor": ParseColor(val); o.KmColor = val; break;
                case "kmscale": o.KmScale = ParsePositive(val, key); break;
                case "kmlabel": o.KmLabel = double.Parse(val, Inv); break;
                case "speedstep": o.SpeedStep = (int)ParsePositive(val, key); break;
                case "speedmax": o.SpeedMax = (int)ParsePositive(val, key); break;
                case "localdate": o.LocalDate = IsTrue(val); break;
                case "altitude":
                    string am = val.ToLowerInvariant();
                    if (am == "gps") o.AltGps = true;
                    else if (am == "ground") o.AltGps = false;
                    else throw new ArgumentException("altitude は gps / ground のどちらかです: " + val);
                    break;
                case "altsmooth":
                    o.AltSmooth = int.Parse(val, Inv);
                    if (o.AltSmooth < 0) throw new ArgumentException("altsmooth は 0 以上にしてください");
                    break;
                case "altoffset": o.AltOffset = double.Parse(val, Inv); break;
                case "groundoffset":
                    o.GroundOffset = double.Parse(val, Inv);
                    if (o.GroundOffset < 0) throw new ArgumentException("groundoffset は 0 以上にしてください");
                    break;
                case "extrude": o.Extrude = IsTrue(val); break;
                case "tour": o.Tour = IsTrue(val); break;
                case "tourspeed": o.TourSpeed = ParsePositive(val, key); break;
                case "tourstep":
                    o.TourStep = int.Parse(val, Inv);
                    if (o.TourStep < 1) throw new ArgumentException("tourstep は 1 以上の整数にしてください");
                    break;
                case "tourrange": o.TourRange = ParsePositive(val, key); break;
                case "tourtilt":
                    o.TourTilt = double.Parse(val, Inv);
                    if (o.TourTilt < 0 || o.TourTilt > 90) throw new ArgumentException("tourtilt は 0～90 にしてください");
                    break;
                case "tourheading":
                    string th = val.ToLowerInvariant();
                    if (th == "follow") o.TourFollow = true;
                    else if (th == "north") o.TourFollow = false;
                    else throw new ArgumentException("tourheading は follow / north のどちらかです: " + val);
                    break;
                case "tourlead": o.TourLead = int.Parse(val, Inv); break;
                case "trackline": o.TrackLine = IsTrue(val); break;
                case "trackcolor": ParseColor(val); o.TrackColor = val; break;
                case "trackwidth": o.TrackWidth = ParsePositive(val, key); break;
                case "carname": o.CarName = val.Trim(); break;
                case "carlabel": o.CarLabel = ParsePositive(val, key); break;
                case "tourseg":
                    o.TourSeg = int.Parse(val, Inv);
                    if (o.TourSeg < 1 || o.TourSeg > 10) throw new ArgumentException("tourseg は 1～10 にしてください");
                    break;
                case "tourheadwin": o.TourHeadWin = ParsePositive(val, key); break;
                case "tourheadtau":
                    o.TourHeadTau = double.Parse(val, Inv);
                    if (o.TourHeadTau < 0) throw new ArgumentException("tourheadtau は 0 以上にしてください");
                    break;
                case "googleearth":
                    string v = val.ToLowerInvariant();
                    if (v == "kmz") { o.NoKml = false; o.PlainKml = false; }
                    else if (v == "kml") { o.NoKml = false; o.PlainKml = true; }
                    else if (v == "none") { o.NoKml = true; }
                    else throw new ArgumentException("googleearth は kmz / kml / none のどれかです: " + val);
                    break;
                default: throw new ArgumentException("不明な設定: " + key);
            }
        }

        static double ParsePositive(string val, string key)
        {
            double d = double.Parse(val, Inv);
            if (d <= 0) throw new ArgumentException(key + " は正の数にしてください: " + val);
            return d;
        }

        static bool IsTrue(string v)
        {
            v = v.Trim().ToLowerInvariant();
            return v == "1" || v == "true" || v == "yes" || v == "on";
        }

        // 色: 名前 (yellow 等) / #RRGGBB / aabbggrr(KML形式 8 桁) → KML の aabbggrr
        static string ParseColor(string v)
        {
            string s = v.Trim().ToLowerInvariant();
            for (int i = 0; i < ColorNames.GetLength(0); i++)
                if (ColorNames[i, 0] == s) return ColorNames[i, 1];
            if (Regex.IsMatch(s, "^#[0-9a-f]{6}$"))
                return "ff" + s.Substring(5, 2) + s.Substring(3, 2) + s.Substring(1, 2);
            if (Regex.IsMatch(s, "^[0-9a-f]{8}$")) return s;
            throw new ArgumentException("色の指定が正しくありません: " + v + " (例 yellow, #FFCC00)");
        }

        static string NextArg(string[] args, ref int i, string name)
        {
            if (i + 1 >= args.Length) throw new ArgumentException(name + " の値がありません");
            i++;
            return args[i];
        }

        static List<string> Collect(List<string> inputs)
        {
            List<string> files = new List<string>();
            foreach (string a in inputs)
            {
                if (Directory.Exists(a))
                {
                    string[] found = Directory.GetFiles(a, "*.tk2");
                    Array.Sort(found, StringComparer.OrdinalIgnoreCase);
                    files.AddRange(found);
                }
                else
                {
                    files.Add(a); // 存在しなければ ConvertFile でエラー表示
                }
            }
            return files;
        }

        // ================= 読み込み =================

        // ヘッダ(offset 433〜)から "+09:00" 形式のタイムゾーンを取り出す(見つからなければ +9 時間)
        static TimeSpan HeaderTz(byte[] data)
        {
            string s = Encoding.ASCII.GetString(data, 433, 460 - 433);
            Match m = Regex.Match(s, @"([+-])(\d\d):(\d\d)");
            if (m.Success)
            {
                int hh = int.Parse(m.Groups[2].Value, Inv);
                int mm = int.Parse(m.Groups[3].Value, Inv);
                TimeSpan off = new TimeSpan(hh, mm, 0);
                return m.Groups[1].Value == "-" ? off.Negate() : off;
            }
            return TimeSpan.FromHours(9);
        }

        // 日時ビットパック: 秒 bit0-5 / 分 6-11 / 時 12-16 / 日 17-21 / 月 22-25 / 年-2000 26-31
        static DateTime DecodeTime(uint v)
        {
            int sec = (int)(v & 0x3f);
            int min = (int)((v >> 6) & 0x3f);
            int hour = (int)((v >> 12) & 0x1f);
            int day = (int)((v >> 17) & 0x1f);
            int mon = (int)((v >> 22) & 0x0f);
            int year = 2000 + (int)(v >> 26);
            return new DateTime(year, mon, day, hour, min, sec, DateTimeKind.Unspecified); // 値は UTC
        }

        static Track LoadTrack(string path, Options opt)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("ファイルが見つかりません: " + path);
            byte[] data = File.ReadAllBytes(path);
            if (data.Length < HeaderSize || Encoding.ASCII.GetString(data, 0, Magic.Length) != Magic)
                throw new InvalidDataException("Wintec TK2 形式ではありません");
            int n = (data.Length - HeaderSize) / RecordSize;
            if (n == 0) throw new InvalidDataException("位置データがありません");

            Record[] recs = new Record[n];
            for (int i = 0; i < n; i++)
            {
                int b = HeaderSize + i * RecordSize;
                Record r = new Record();
                r.Flags = BitConverter.ToUInt16(data, b + 0);
                r.Time = DecodeTime(BitConverter.ToUInt32(data, b + 2));
                r.LatE7 = BitConverter.ToInt32(data, b + 6);
                r.LonE7 = BitConverter.ToInt32(data, b + 10);
                r.AltM = BitConverter.ToInt16(data, b + 14);
                recs[i] = r;
            }

            // ロールオーバー補正週数の決定
            DateTime rollRef = DateTime.SpecifyKind(RolloverRef, DateTimeKind.Unspecified);
            int weeks = opt.Weeks;
            if (weeks < 0)
            {
                weeks = 0;
                while (recs[0].Time.AddDays(weeks * 7) < rollRef) weeks += 1024;
            }
            for (int i = 0; i < n; i++) recs[i].Time = recs[i].Time.AddDays(weeks * 7);

            Track t = new Track();
            t.Recs = recs;
            t.Tz = HeaderTz(data);
            t.Weeks = weeks;
            return t;
        }

        // ================= 区間カット =================

        static readonly string[] TimeOnlyFormats = { "H:mm:ss", "HH:mm:ss", "H:mm", "HH:mm", "HHmmss" };
        static readonly string[] DateTimeFormats = { "yyyy-MM-dd H:mm:ss", "yyyy-MM-dd H:mm", "yyyy/MM/dd H:mm:ss", "yyyy/MM/dd H:mm" };

        // 入力文字列を現地時刻として解釈する。時刻のみなら、ログの範囲に入る日付を補う。
        static bool TryParseLocal(string s, DateTime firstLocal, DateTime lastLocal, out DateTime result)
        {
            s = s.Trim();
            DateTime dt;
            if (DateTime.TryParseExact(s, DateTimeFormats, Inv, DateTimeStyles.None, out dt))
            {
                result = dt;
                return true;
            }
            if (DateTime.TryParseExact(s, TimeOnlyFormats, Inv, DateTimeStyles.None, out dt))
            {
                result = firstLocal.Date + dt.TimeOfDay;
                // 日付をまたぐログで、翌日側の時刻を指定した場合
                if (result < firstLocal.AddMinutes(-1) && result.AddDays(1) <= lastLocal.AddMinutes(1)) result = result.AddDays(1);
                return true;
            }
            result = DateTime.MinValue;
            return false;
        }

        // 時刻を聞く (空 Enter なら null)
        static DateTime? AskTime(string prompt, DateTime firstLocal, DateTime lastLocal)
        {
            while (true)
            {
                Console.Write(prompt);
                string line = Console.ReadLine();
                if (line == null || line.Trim().Length == 0) return null;
                DateTime t;
                if (TryParseLocal(line, firstLocal, lastLocal, out t)) return t;
                Console.WriteLine("    時刻の形式が正しくありません (例 08:05:00)");
            }
        }

        // 時間範囲と停止区間(記録が 60 秒以上途切れた所)を表示
        static void PrintSummary(Track t)
        {
            Record[] r = t.Recs;
            int n = r.Length;
            Console.WriteLine(string.Format("  記録: {0} ～ {1}  ({2} 点)",
                (r[0].Time + t.Tz).ToString("yyyy-MM-dd HH:mm:ss", Inv), (r[n - 1].Time + t.Tz).ToString("HH:mm:ss", Inv), n));
            List<string> stops = new List<string>();
            for (int i = 1; i < n; i++)
            {
                TimeSpan gap = r[i].Time - r[i - 1].Time;
                if (gap.TotalSeconds >= 60)
                    stops.Add(string.Format("    {0} ～ {1}  ({2} 分 {3} 秒)",
                        (r[i - 1].Time + t.Tz).ToString("HH:mm:ss", Inv), (r[i].Time + t.Tz).ToString("HH:mm:ss", Inv),
                        (int)gap.TotalMinutes, gap.Seconds));
            }
            if (stops.Count > 0)
            {
                Console.WriteLine("  停止区間 (60 秒以上記録なし):");
                int max = 30;
                for (int i = 0; i < stops.Count && i < max; i++) Console.WriteLine(stops[i]);
                if (stops.Count > max) Console.WriteLine(string.Format("    ... ほか {0} 件", stops.Count - max));
            }
        }

        static Record[] Trim(Track t, DateTime? startLocal, DateTime? endLocal)
        {
            if (startLocal == null && endLocal == null) return t.Recs;
            DateTime s = startLocal.HasValue ? startLocal.Value - t.Tz : DateTime.MinValue;
            DateTime e = endLocal.HasValue ? endLocal.Value - t.Tz : DateTime.MaxValue;
            if (e < s) throw new ArgumentException("終了時刻が開始時刻より前です");
            List<Record> list = new List<Record>();
            foreach (Record r in t.Recs)
            {
                if (r.Time >= s && r.Time <= e) list.Add(r);
            }
            if (list.Count < 2) throw new ArgumentException("指定した時間範囲にデータがほとんどありません");
            return list.ToArray();
        }

        // ================= 変換 =================

        static List<string> ConvertFile(string path, Options opt, out string info)
        {
            Track t = LoadTrack(path, opt);
            Record[] all = t.Recs;
            DateTime firstLocal = all[0].Time + t.Tz;
            DateTime lastLocal = all[all.Length - 1].Time + t.Tz;

            Console.WriteLine("■ " + Path.GetFileName(path));
            DateTime? startLocal = null, endLocal = null;
            DateTime tmp;
            if (opt.Start != null)
            {
                if (!TryParseLocal(opt.Start, firstLocal, lastLocal, out tmp)) throw new ArgumentException("-start の時刻が不正です: " + opt.Start);
                startLocal = tmp;
            }
            if (opt.End != null)
            {
                if (!TryParseLocal(opt.End, firstLocal, lastLocal, out tmp)) throw new ArgumentException("-end の時刻が不正です: " + opt.End);
                endLocal = tmp;
            }
            if (opt.Start == null && opt.End == null && !opt.NoPause)
            {
                PrintSummary(t);
                Console.WriteLine("  カットする場合は時刻を入力 (例 08:05:00)。Enter のみでカットしません。");
                startLocal = AskTime("  開始時刻: ", firstLocal, lastLocal);
                endLocal = AskTime("  終了時刻: ", firstLocal, lastLocal);
            }

            Record[] recs = Trim(t, startLocal, endLocal);
            int n = recs.Length;
            TimeSpan tz = t.Tz;

            DateTime startT = recs[0].Time + tz;
            DateTime endT = recs[n - 1].Time + tz;
            string outDir = opt.OutDir ?? Path.GetDirectoryName(Path.GetFullPath(path));
            Directory.CreateDirectory(outDir);
            string baseName = startT.ToString("yyyyMMdd_HHmmss", Inv);
            List<string> outputs = new List<string>();

            // 各点の速度[ノット]・方位[度]・距離[m]
            double[] speeds = new double[n];
            double[] courses = new double[n];
            double[] dists = new double[n];
            for (int i = 1; i < n; i++) SpeedCourse(recs[i - 1], recs[i], out speeds[i], out courses[i], out dists[i]);

            // ---- NMEA ----
            string nmeaPath = Path.Combine(outDir, baseName + ".nmea");
            StringBuilder sb = new StringBuilder(n * 140);
            for (int i = 0; i < n; i++)
            {
                Record r = recs[i];
                string hms = r.Time.ToString("HHmmss", Inv);
                DateTime dateSrc = opt.LocalDate ? r.Time + tz : r.Time;
                string ns, ew;
                string lat = FmtCoord(r.LatE7, 2, "N", "S", out ns);
                string lon = FmtCoord(r.LonE7, 3, "E", "W", out ew);

                string rmc = string.Format(Inv, "GPRMC,{0},A,{1},{2},{3},{4},{5:F1},{6:F1},{7},,,,",
                    hms, lat, ns, lon, ew, speeds[i], courses[i], dateSrc.ToString("ddMMyy", Inv));
                string gga = string.Format(Inv, "GPGGA,{0},{1},{2},{3},{4},1,,,{5},M,,M,,",
                    hms, lat, ns, lon, ew, r.AltM);
                sb.Append(Sentence(rmc));
                sb.Append(Sentence(gga));
            }
            File.WriteAllText(nmeaPath, sb.ToString(), new UTF8Encoding(false)); // 中身は ASCII のみ
            outputs.Add(nmeaPath);

            double total = 0;
            for (int i = 1; i < n; i++) total += dists[i];
            string tzs = (tz < TimeSpan.Zero ? "-" : "+") + tz.Duration().ToString(@"hh\:mm", Inv);
            string cut = (n == all.Length) ? "" : string.Format(Inv, " / 全 {0} 点からカット", all.Length);
            info = string.Format(Inv, "{0} 点, {1} ～ {2} {3}, {4:F2} km (補正 {5} 週){6}",
                n, startT.ToString("yyyy-MM-dd HH:mm:ss", Inv), endT.ToString("HH:mm:ss", Inv), tzs, total / 1000, t.Weeks, cut);

            // ---- Google Earth (KMZ / KML) ----
            if (!opt.NoKml)
            {
                bool kmz = !opt.PlainKml;
                string localIcon = LocalIconPath(opt.KmIcon);
                string kml = BuildKml(baseName, recs, dists, tz, tzs, info, kmz, opt, localIcon);
                byte[] kmlBytes = new UTF8Encoding(false).GetBytes(kml);
                if (kmz)
                {
                    string kmzPath = Path.Combine(outDir, baseName + ".kmz");
                    using (FileStream fs = new FileStream(kmzPath, FileMode.Create))
                    using (ZipArchive za = new ZipArchive(fs, ZipArchiveMode.Create))
                    {
                        WriteEntry(za, "doc.kml", kmlBytes); // KMZ は先頭の .kml が本体
                        WriteEntry(za, "legend.png", LegendPng(opt));
                        if (localIcon != null) WriteEntry(za, "kmicon" + Path.GetExtension(localIcon).ToLowerInvariant(), File.ReadAllBytes(localIcon));
                    }
                    outputs.Add(kmzPath);
                }
                else
                {
                    string kmlPath = Path.Combine(outDir, baseName + ".kml");
                    File.WriteAllBytes(kmlPath, kmlBytes);
                    outputs.Add(kmlPath);
                }
            }
            return outputs;
        }

        static void WriteEntry(ZipArchive za, string name, byte[] bytes)
        {
            ZipArchiveEntry e = za.CreateEntry(name, CompressionLevel.Optimal);
            using (Stream s = e.Open())
            {
                s.Write(bytes, 0, bytes.Length);
            }
        }

        // ================= KML =================
        //  - スタート / ゴールの目印
        //  - 走行軌跡(時間再生): gx:Track。時間スライダーで走行位置のアイコンが動く。各点の速度[km/h]付き
        //  - Speed_Track(Km): 速度で 20km/h 刻みに色分けした線 (TimeMachineX と同じ色)
        //  - Km_Posts: スタートから 1km ごとの目印
        static string BuildKml(string name, Record[] recs, double[] dists, TimeSpan tz, string tzs, string info, bool withLegend,
                               Options opt, string localIcon)
        {
            int topBand = TopBand(opt);
            int n = recs.Length;
            DateTime startT = recs[0].Time + tz;
            DateTime endT = recs[n - 1].Time + tz;

            // 各点の速度 [km/h, 整数切り捨て] (TimeMachineX と同じ)
            int[] kmh = new int[n];
            int maxKmh = 0;
            for (int i = 1; i < n; i++)
            {
                double dt = (recs[i].Time - recs[i - 1].Time).TotalSeconds;
                kmh[i] = dt > 0 ? (int)Math.Floor(dists[i] / dt * 3.6) : 0;
                if (kmh[i] > maxKmh) maxKmh = kmh[i];
            }

            // 表示用の高度 [m]: GPS 高度を時間で移動平均して補正値を足す
            double[] alt = SmoothAlt(recs, opt);
            string altMode = opt.AltGps ? "absolute" : (opt.Lift ? "relativeToGround" : "clampToGround");
            string altModeTag = "<altitudeMode>" + altMode + "</altitudeMode>";

            StringBuilder k = new StringBuilder(n * 200);
            k.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
            k.Append("<kml xmlns=\"http://www.opengis.net/kml/2.2\" xmlns:gx=\"http://www.google.com/kml/ext/2.2\">\n");
            k.Append("<Document>\n");
            k.Append("  <name>").Append(Xml(name)).Append("</name>\n");
            k.Append("  <open>1</open>\n");
            k.Append("  <description>").Append(Xml("Wintec TK2 から変換: " + info + string.Format(Inv, " / 最高速度 {0} km/h", maxKmh))).Append("</description>\n");

            // スタイル
            k.Append("  <Style id=\"track\">\n");
            k.Append("    <IconStyle><scale>0.8</scale><Icon><href>http://maps.google.com/mapfiles/kml/shapes/track.png</href></Icon></IconStyle>\n");
            k.Append("    <LabelStyle><scale>").Append(opt.CarName.Length > 0 ? opt.CarLabel.ToString("0.##", Inv) : "0").Append("</scale></LabelStyle>\n");
            if (opt.TrackLine)
                k.Append("    <LineStyle><color>").Append(ParseColor(opt.TrackColor)).Append("</color><width>").Append(opt.TrackWidth.ToString("0.##", Inv)).Append("</width></LineStyle>\n");
            else
                k.Append("    <LineStyle><color>00ffffff</color><width>1</width></LineStyle>\n"); // 線は Speed_Track で描くので透明
            k.Append("  </Style>\n");
            k.Append("  <Style id=\"start\"><IconStyle><Icon><href>http://maps.google.com/mapfiles/kml/paddle/grn-circle.png</href></Icon></IconStyle></Style>\n");
            k.Append("  <Style id=\"goal\"><IconStyle><Icon><href>http://maps.google.com/mapfiles/kml/paddle/red-square.png</href></Icon></IconStyle></Style>\n");
            // Km ポスト
            string kmColor = ParseColor(opt.KmColor);
            string iconHref;
            string hotSpot = "";
            if (localIcon != null)
                iconHref = withLegend ? "kmicon" + Path.GetExtension(localIcon).ToLowerInvariant() : new Uri(localIcon).AbsoluteUri;
            else
            {
                iconHref = IconHref(opt.KmIcon);
                string ki = opt.KmIcon.ToLowerInvariant();
                if (ki == "pin") hotSpot = "<hotSpot x=\"20\" y=\"2\" xunits=\"pixels\" yunits=\"pixels\"/>";
                else if (ki == "paddle") hotSpot = "<hotSpot x=\"32\" y=\"1\" xunits=\"pixels\" yunits=\"pixels\"/>";
            }
            k.Append("  <Style id=\"kmpost\">");
            k.Append("<IconStyle><color>").Append(kmColor).Append("</color><scale>").Append(opt.KmScale.ToString("0.##", Inv)).Append("</scale>");
            k.Append("<Icon><href>").Append(Xml(iconHref)).Append("</href></Icon>").Append(hotSpot).Append("</IconStyle>");
            k.Append("<LabelStyle><color>").Append(kmColor).Append("</color><scale>").Append(opt.KmLabel.ToString("0.##", Inv)).Append("</scale></LabelStyle>");
            k.Append("</Style>\n");
            // 速度帯の線
            for (int b = 0; b <= topBand; b++)
                k.Append("  <Style id=\"spd").Append(b).Append("\"><LineStyle><color>").Append(BandKmlColor(b, opt)).Append("</color><width>5</width></LineStyle></Style>\n");
            k.Append("  <Schema id=\"trackdata\">\n");
            k.Append("    <gx:SimpleArrayField name=\"speed\" type=\"int\"><displayName>速度 (km/h)</displayName></gx:SimpleArrayField>\n");
            k.Append("  </Schema>\n");

            // 凡例 (KMZ 内の legend.png)
            if (withLegend)
            {
                k.Append("  <ScreenOverlay>\n");
                k.Append("    <name>速度凡例 (km/h)</name>\n");
                k.Append("    <Icon><href>legend.png</href></Icon>\n");
                k.Append("    <overlayXY x=\"0\" y=\"0\" xunits=\"fraction\" yunits=\"fraction\"/>\n");
                k.Append("    <screenXY x=\"10\" y=\"40\" xunits=\"pixels\" yunits=\"pixels\"/>\n");
                k.Append("    <size x=\"-1\" y=\"-1\" xunits=\"pixels\" yunits=\"pixels\"/>\n");
                k.Append("  </ScreenOverlay>\n");
            }

            // スタート / ゴール
            AppendPoint(k, "スタート " + startT.ToString("HH:mm:ss", Inv), "#start", recs[0], alt[0], opt);
            AppendPoint(k, "ゴール " + endT.ToString("HH:mm:ss", Inv), "#goal", recs[n - 1], alt[n - 1], opt);
            // (ツアー時、ゴールはゴール時刻から表示)

            // 走行軌跡 (時間再生)
            k.Append("  <Placemark>\n");
            k.Append("    <name>").Append(opt.CarName.Length > 0 ? Xml(opt.CarName) : "走行軌跡 (時間再生)").Append("</name>\n");
            k.Append("    <description>").Append(Xml(string.Format(Inv, "{0} ～ {1} {2} / {3} 点",
                startT.ToString("yyyy-MM-dd HH:mm:ss", Inv), endT.ToString("HH:mm:ss", Inv), tzs, n))).Append("</description>\n");
            k.Append("    <styleUrl>#track</styleUrl>\n");
            k.Append("    <gx:Track>\n");
            k.Append("      ").Append(altModeTag).Append("\n");
            for (int i = 0; i < n; i++)
                k.Append("      <when>").Append(recs[i].Time.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", Inv)).Append("</when>\n");
            for (int i = 0; i < n; i++)
                k.Append("      <gx:coord>").Append(FmtDeg(recs[i].LonE7)).Append(' ').Append(FmtDeg(recs[i].LatE7))
                 .Append(' ').Append(FmtAlt(opt.Lift ? opt.GroundOffset : alt[i])).Append("</gx:coord>\n");
            k.Append("      <ExtendedData><SchemaData schemaUrl=\"#trackdata\">\n");
            k.Append("        <gx:SimpleArrayData name=\"speed\">\n");
            for (int i = 0; i < n; i++)
                k.Append("          <gx:value>").Append(kmh[i].ToString(Inv)).Append("</gx:value>\n");
            k.Append("        </gx:SimpleArrayData>\n");
            k.Append("      </SchemaData></ExtendedData>\n");
            k.Append("    </gx:Track>\n");
            k.Append("  </Placemark>\n");

            // Speed_Track(Km): 色が変わる点で区切る (区間の終点 = 次区間の始点で線をつなぐ)
            k.Append("  <Folder>\n");
            k.Append("    <name>Speed_Track(Km)</name>\n");
            k.Append("    <open>0</open>\n");
            if (opt.TrackLine) k.Append("    <visibility>0</visibility>\n"); // 走行軌跡の線を使うので、色分けは必要なときにチェックして表示
            bool tg = opt.Tour && !opt.TrackLine; // ツアーに合わせて線を伸ばす
            int seg = 0;
            int a = 0;
            for (int i = 1; i <= n; i++)
            {
                // ツアー時は線が車に追従して伸びるように、記録点ごと (通常 1 秒) に区切る
                if (i < n && Band(kmh[i], opt) == Band(kmh[a], opt) && !tg) continue;
                int last = (i < n) ? i : n - 1;
                if (last > a && tg && opt.TourSeg > 1)
                {
                    // 記録点の間を TourSeg 等分し、各小区間を通過した時刻 (小数秒) から表示する
                    int bs = Band(kmh[a], opt);
                    double dtSec = (recs[last].Time - recs[a].Time).TotalSeconds;
                    for (int p = 1; p <= opt.TourSeg; p++)
                    {
                        seg++;
                        k.Append("    <Placemark><name>").Append(string.Format(Inv, "Segment {0} ({1} km/h)", seg, BandLabel(bs, opt)))
                         .Append("</name><styleUrl>#spd").Append(bs).Append("</styleUrl>");
                        DateTime tb = recs[a].Time.AddSeconds(dtSec * p / opt.TourSeg - opt.TourLead);
                        k.Append("<TimeSpan><begin>").Append(IsoF(tb)).Append("</begin></TimeSpan><LineString>");
                        if (opt.AltGps && opt.Extrude) k.Append("<extrude>1</extrude>");
                        k.Append("<tessellate>1</tessellate>").Append(altModeTag).Append("<coordinates>");
                        for (int q = 0; q < 2; q++)
                        {
                            double f = (double)(p - 1 + q) / opt.TourSeg;
                            int lonE7 = (int)Math.Round(recs[a].LonE7 + f * ((double)recs[last].LonE7 - recs[a].LonE7));
                            int latE7 = (int)Math.Round(recs[a].LatE7 + f * ((double)recs[last].LatE7 - recs[a].LatE7));
                            if (q > 0) k.Append(' ');
                            k.Append(FmtDeg(lonE7)).Append(',').Append(FmtDeg(latE7));
                            if (opt.AltGps) k.Append(',').Append(FmtAlt(alt[a] + f * (alt[last] - alt[a])));
                            else if (opt.Lift) k.Append(',').Append(FmtAlt(opt.GroundOffset));
                        }
                        k.Append("</coordinates></LineString></Placemark>\n");
                    }
                }
                else if (last > a)
                {
                    seg++;
                    int b = Band(kmh[a], opt);
                    k.Append("    <Placemark><name>").Append(string.Format(Inv, "Segment {0} ({1} km/h)", seg, BandLabel(b, opt)))
                     .Append("</name><styleUrl>#spd").Append(b).Append("</styleUrl>");
                    // ツアー時: 区間の終点を通過した時刻から表示 (以後ずっと残る)。TourLead で先端の位置を前後に補正
                    if (tg) k.Append("<TimeSpan><begin>").Append(Iso(recs[last].Time.AddSeconds(-opt.TourLead))).Append("</begin></TimeSpan>");
                    k.Append("<LineString>");
                    if (opt.AltGps && opt.Extrude) k.Append("<extrude>1</extrude>");
                    k.Append("<tessellate>1</tessellate>").Append(altModeTag).Append("<coordinates>");
                    for (int j = a; j <= last; j++)
                    {
                        if (j > a) k.Append(' ');
                        k.Append(FmtDeg(recs[j].LonE7)).Append(',').Append(FmtDeg(recs[j].LatE7));
                        if (opt.AltGps || opt.Lift) k.Append(',').Append(FmtAlt(opt.Lift ? opt.GroundOffset : alt[j]));
                    }
                    k.Append("</coordinates></LineString></Placemark>\n");
                }
                a = i;
            }
            k.Append("  </Folder>\n");

            // Km_Posts: 累積距離が 1km の倍数になる位置を前後の点から直線補間
            k.Append("  <Folder>\n");
            k.Append("    <name>Km_Posts</name>\n");
            k.Append("    <open>0</open>\n");
            double cum = 0;
            int km = 1;
            for (int i = 1; i < n; i++)
            {
                double prev = cum;
                cum += dists[i];
                while (dists[i] > 0 && cum >= km * 1000.0)
                {
                    double f = (km * 1000.0 - prev) / dists[i];
                    double lat = (recs[i - 1].LatE7 + f * (recs[i].LatE7 - recs[i - 1].LatE7)) / 1e7;
                    double lon = (recs[i - 1].LonE7 + f * (recs[i].LonE7 - recs[i - 1].LonE7)) / 1e7;
                    DateTime at = recs[i - 1].Time.AddSeconds(Math.Round(f * (recs[i].Time - recs[i - 1].Time).TotalSeconds)) + tz;
                    TimeSpan el = at - startT;
                    string desc = string.Format(Inv, "通過 {0} / 経過 {1:00}:{2:00}:{3:00} / 速度 {4} km/h",
                        at.ToString("HH:mm:ss", Inv), (int)el.TotalHours, el.Minutes, el.Seconds, kmh[i]);
                    k.Append("    <Placemark><name>").Append(km.ToString(Inv)).Append(" Km</name>");
                    k.Append("<description>").Append(Xml(desc)).Append("</description>");
                    if (opt.Tour) k.Append("<TimeSpan><begin>").Append(Iso(at - tz)).Append("</begin></TimeSpan>");
                    double pa = alt[i - 1] + f * (alt[i] - alt[i - 1]);
                    k.Append("<styleUrl>#kmpost</styleUrl><Point>").Append(altModeTag).Append("<coordinates>")
                     .Append(lon.ToString("F7", Inv)).Append(',').Append(lat.ToString("F7", Inv));
                    if (opt.AltGps || opt.Lift) k.Append(',').Append(FmtAlt(opt.Lift ? opt.GroundOffset : pa));
                    k.Append("</coordinates></Point></Placemark>\n");
                    km++;
                }
            }
            k.Append("  </Folder>\n");

            if (opt.Tour) AppendTour(k, recs, alt, opt);

            k.Append("</Document>\n");
            k.Append("</kml>\n");
            return k.ToString();
        }

        // ---- 速度帯 ----
        static int TopBand(Options opt)
        {
            return Math.Max(1, opt.SpeedMax / opt.SpeedStep);
        }

        static int Band(int kmh, Options opt)
        {
            int b = kmh / opt.SpeedStep;
            int top = TopBand(opt);
            return b < 0 ? 0 : (b > top ? top : b);
        }

        static string BandLabel(int b, Options opt)
        {
            int lo = b * opt.SpeedStep;
            if (b >= TopBand(opt)) return lo.ToString(Inv) + "-";
            return lo.ToString(Inv) + "-" + (lo + opt.SpeedStep - 1).ToString(Inv);
        }

        static Color BandColor(int b, Options opt)
        {
            double t = (double)b / TopBand(opt);          // 0 … 1
            double pos = t * (SpeedAnchors.GetLength(0) - 1);
            int i0 = (int)Math.Floor(pos);
            if (i0 >= SpeedAnchors.GetLength(0) - 1) i0 = SpeedAnchors.GetLength(0) - 2;
            double f = pos - i0;
            int[] c = new int[3];
            for (int j = 0; j < 3; j++)
                c[j] = (int)Math.Round(SpeedAnchors[i0, j] + f * (SpeedAnchors[i0 + 1, j] - SpeedAnchors[i0, j]));
            return Color.FromArgb(c[0], c[1], c[2]);
        }

        static string BandKmlColor(int b, Options opt)
        {
            Color c = BandColor(b, opt);
            return "ff" + c.B.ToString("x2", Inv) + c.G.ToString("x2", Inv) + c.R.ToString("x2", Inv);
        }

        // ---- Km ポストのアイコン ----
        static string IconHref(string icon)
        {
            string s = icon.Trim();
            for (int i = 0; i < IconPresets.GetLength(0); i++)
                if (IconPresets[i, 0] == s.ToLowerInvariant()) return IconBase + IconPresets[i, 1];
            if (s.StartsWith("http://") || s.StartsWith("https://")) return s;
            throw new ArgumentException("kmicon が不明です: " + icon + " (pin, flag, star, circle, square, diamond, triangle, target, donut, dot, paddle, URL, png ファイル)");
        }

        // kmicon が画像ファイルならフルパスを返す (exe のフォルダからの相対パスも可)
        static string LocalIconPath(string icon)
        {
            string s = icon.Trim().Trim('"');
            string ext = Path.GetExtension(s).ToLowerInvariant();
            if (s.StartsWith("http://") || s.StartsWith("https://")) return null;
            if (ext != ".png" && ext != ".gif" && ext != ".jpg") return null;
            string p = Path.IsPathRooted(s) ? s : Path.Combine(ExeDir(), s);
            if (!File.Exists(p)) throw new FileNotFoundException("kmicon の画像が見つかりません: " + p);
            return Path.GetFullPath(p);
        }

        static void AppendPoint(StringBuilder k, string name, string style, Record r, double alt, Options opt)
        {
            k.Append("  <Placemark>\n");
            k.Append("    <name>").Append(Xml(name)).Append("</name>\n");
            if (opt.Tour) k.Append("    <TimeSpan><begin>").Append(Iso(r.Time)).Append("</begin></TimeSpan>\n");
            k.Append("    <styleUrl>").Append(style).Append("</styleUrl>\n");
            k.Append("    <Point><altitudeMode>").Append(opt.AltGps ? "absolute" : (opt.Lift ? "relativeToGround" : "clampToGround")).Append("</altitudeMode><coordinates>")
             .Append(FmtDeg(r.LonE7)).Append(',').Append(FmtDeg(r.LatE7));
            if (opt.AltGps || opt.Lift) k.Append(',').Append(FmtAlt(opt.Lift ? opt.GroundOffset : alt));
            k.Append("</coordinates></Point>\n");
            k.Append("  </Placemark>\n");
        }

        // GPS 高度を前後 AltSmooth 秒の範囲で移動平均し、AltOffset を足す (GPS の高さは 1 点ごとのばらつきが大きいため)
        static double[] SmoothAlt(Record[] recs, Options opt)
        {
            int n = recs.Length;
            double[] res = new double[n];
            double half = opt.AltSmooth / 2.0;
            int lo = 0, hi = 0;
            double sum = 0;
            for (int i = 0; i < n; i++)
            {
                // 窓 [t-half, t+half] に入る点の合計を尺取りで求める
                while (hi < n && (recs[hi].Time - recs[i].Time).TotalSeconds <= half)
                {
                    sum += recs[hi].AltM;
                    hi++;
                }
                while ((recs[i].Time - recs[lo].Time).TotalSeconds > half)
                {
                    sum -= recs[lo].AltM;
                    lo++;
                }
                res[i] = sum / (hi - lo) + opt.AltOffset;
            }
            return res;
        }

        static string IsoF(DateTime utc)
        {
            return utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", Inv);
        }

        static string Iso(DateTime utc)
        {
            return utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", Inv);
        }

        // ---- 走行アニメーション (gx:Tour) ----
        // TourStep 秒ごとに車の位置へカメラを滑らかに移動 (gx:FlyTo smooth)。
        // 各カメラに時間範囲 [スタート, その時刻] を持たせるので、通過済みの Speed_Track / Km ポストだけが表示され、
        // gx:Track のアイコンがその時刻の位置に出る。再生時間 = 走行時間 / TourSpeed。
        static void AppendTour(StringBuilder k, Record[] recs, double[] alt, Options opt)
        {
            int n = recs.Length;
            DateTime t0 = recs[0].Time;
            double total = (recs[n - 1].Time - t0).TotalSeconds;
            string begin = Iso(t0);
            string altMode = opt.AltGps ? "absolute" : "relativeToGround";
            double heading = 0;     // 連続した角度 (360 をまたいでも飛ばさない。GE は 360 超の値も補間できる)
            bool headingSet = false;

            k.Append("  <gx:Tour>\n");
            k.Append("    <name>走行アニメーション</name>\n");
            k.Append("    <description>").Append(Xml(string.Format(Inv, "{0} 倍速 / 再生時間 約 {1:F1} 分。ダブルクリックで再生",
                opt.TourSpeed, total / opt.TourSpeed / 60))).Append("</description>\n");
            k.Append("    <gx:Playlist>\n");
            bool first = true;
            for (double sec = 0; ; sec += opt.TourStep)
            {
                if (sec > total) sec = total;
                DateTime t = t0.AddSeconds(sec);
                double lat, lon, a;
                Interp(recs, alt, t, out lat, out lon, out a);

                if (opt.TourFollow)
                {
                    // 前後 TourHeadWin 秒の位置から進行方向を求める (停止中は直前の向きを保つ)
                    double la1, lo1, a1, la2, lo2, a2;
                    Interp(recs, alt, t.AddSeconds(-opt.TourHeadWin), out la1, out lo1, out a1);
                    Interp(recs, alt, t.AddSeconds(opt.TourHeadWin), out la2, out lo2, out a2);
                    if (HavDist(la1, lo1, la2, lo2) > 5 || !headingSet)
                    {
                        double target = Bearing(la1, lo1, la2, lo2);
                        if (!headingSet)
                        {
                            heading = target;
                            headingSet = true;
                        }
                        else
                        {
                            // 直前の向きから最短の回り方で目標へ。TourHeadTau 秒の遅れで追従 (0 ならすぐ向く)
                            double delta = ((target - heading) % 360 + 540) % 360 - 180;
                            double kf = opt.TourHeadTau <= 0 ? 1.0 : 1.0 - Math.Exp(-opt.TourStep / opt.TourHeadTau);
                            heading += kf * delta;
                        }
                    }
                }

                double dur = first ? 3.0 : opt.TourStep / opt.TourSpeed;
                k.Append("      <gx:FlyTo><gx:duration>").Append(dur.ToString("0.###", Inv)).Append("</gx:duration>");
                k.Append("<gx:flyToMode>").Append(first ? "bounce" : "smooth").Append("</gx:flyToMode>");
                k.Append("<LookAt><gx:TimeSpan><begin>").Append(begin).Append("</begin><end>").Append(Iso(t)).Append("</end></gx:TimeSpan>");
                k.Append("<longitude>").Append(lon.ToString("F7", Inv)).Append("</longitude>");
                k.Append("<latitude>").Append(lat.ToString("F7", Inv)).Append("</latitude>");
                k.Append("<altitude>").Append(opt.AltGps ? FmtAlt(a) : FmtAlt(opt.GroundOffset)).Append("</altitude>");
                k.Append("<heading>").Append(heading.ToString("0.#", Inv)).Append("</heading>");
                k.Append("<tilt>").Append(opt.TourTilt.ToString("0.#", Inv)).Append("</tilt>");
                k.Append("<range>").Append(opt.TourRange.ToString("0.#", Inv)).Append("</range>");
                k.Append("<altitudeMode>").Append(altMode).Append("</altitudeMode></LookAt></gx:FlyTo>\n");
                first = false;
                if (sec >= total) break;
            }
            k.Append("    </gx:Playlist>\n");
            k.Append("  </gx:Tour>\n");
        }

        // 時刻 t の位置と高さを前後の記録点から直線補間
        static void Interp(Record[] recs, double[] alt, DateTime t, out double lat, out double lon, out double a)
        {
            int n = recs.Length;
            if (t <= recs[0].Time)
            {
                lat = recs[0].LatE7 / 1e7; lon = recs[0].LonE7 / 1e7; a = alt[0];
                return;
            }
            if (t >= recs[n - 1].Time)
            {
                lat = recs[n - 1].LatE7 / 1e7; lon = recs[n - 1].LonE7 / 1e7; a = alt[n - 1];
                return;
            }
            int lo = 0, hi = n - 1; // recs[lo].Time <= t < recs[hi].Time
            while (hi - lo > 1)
            {
                int mid = (lo + hi) / 2;
                if (recs[mid].Time <= t) lo = mid; else hi = mid;
            }
            double span = (recs[hi].Time - recs[lo].Time).TotalSeconds;
            double f = span > 0 ? (t - recs[lo].Time).TotalSeconds / span : 0;
            lat = (recs[lo].LatE7 + f * (recs[hi].LatE7 - recs[lo].LatE7)) / 1e7;
            lon = (recs[lo].LonE7 + f * (recs[hi].LonE7 - recs[lo].LonE7)) / 1e7;
            a = alt[lo] + f * (alt[hi] - alt[lo]);
        }

        static double HavDist(double lat1, double lon1, double lat2, double lon2)
        {
            double p1 = lat1 * Math.PI / 180, p2 = lat2 * Math.PI / 180, dl = (lon2 - lon1) * Math.PI / 180;
            double s1 = Math.Sin((p2 - p1) / 2), s2 = Math.Sin(dl / 2);
            return 2 * EarthR * Math.Asin(Math.Sqrt(s1 * s1 + Math.Cos(p1) * Math.Cos(p2) * s2 * s2));
        }

        static double Bearing(double lat1, double lon1, double lat2, double lon2)
        {
            double p1 = lat1 * Math.PI / 180, p2 = lat2 * Math.PI / 180, dl = (lon2 - lon1) * Math.PI / 180;
            double c = Math.Atan2(Math.Sin(dl) * Math.Cos(p2), Math.Cos(p1) * Math.Sin(p2) - Math.Sin(p1) * Math.Cos(p2) * Math.Cos(dl)) * 180 / Math.PI;
            return ((c % 360) + 360) % 360;
        }

        static string FmtAlt(double a)
        {
            return a.ToString("0.#", Inv);
        }

        // 凡例画像 (PNG) を作る。帯が 8 個以下なら 1 帯 1 行、多いときはカラーバー + 目盛り
        static byte[] LegendPng(Options opt)
        {
            int bands = TopBand(opt) + 1;
            bool rows = bands <= 8;
            int top = 22, w = 118;
            int bandH = rows ? 18 : Math.Max(3, Math.Min(12, 260 / bands));
            int h = top + bandH * bands + (rows ? 6 : 14);
            using (Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            using (Graphics g = Graphics.FromImage(bmp))
            using (Font title = new Font("Arial", 9f, FontStyle.Bold))
            using (Font font = new Font("Arial", 9f))
            using (SolidBrush bg = new SolidBrush(Color.FromArgb(170, 0, 0, 0)))
            using (Pen tick = new Pen(Color.White))
            using (MemoryStream ms = new MemoryStream())
            {
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.Transparent);
                g.FillRectangle(bg, 0, 0, w, h);
                g.DrawString("Speed (km/h)", title, Brushes.White, 6, 4);
                // 目盛りの間隔 (ラベルが 14px 以上離れるように)
                int every = rows ? 1 : (int)Math.Ceiling(14.0 / bandH);
                // 上から速い順
                for (int row = 0; row < bands; row++)
                {
                    int b = bands - 1 - row;
                    int y = top + row * bandH;
                    using (SolidBrush br = new SolidBrush(BandColor(b, opt)))
                    {
                        if (rows) g.FillRectangle(br, 8, y + 4, 26, 9);
                        else g.FillRectangle(br, 8, y + 7, 22, bandH);
                    }
                    if (rows)
                        g.DrawString(BandLabel(b, opt), font, Brushes.White, 42, y);
                    else if (b % every == 0 || b == bands - 1)
                    {
                        // 帯の下端 = その帯の下限速度
                        int ly = y + 7 + bandH;
                        if (b == bands - 1 && b % every != 0) continue;
                        g.DrawLine(tick, 30, ly, 34, ly);
                        string lab = (b * opt.SpeedStep).ToString(Inv) + (b == bands - 1 ? "-" : "");
                        g.DrawString(lab, font, Brushes.White, 36, ly - 7);
                    }
                }
                bmp.Save(ms, ImageFormat.Png);
                return ms.ToArray();
            }
        }

        // ================= 共通 =================

        // "$" + 本文 + "*" + XORチェックサム + CRLF
        static string Sentence(string body)
        {
            byte cs = 0;
            foreach (char c in body) cs ^= (byte)c;
            return "$" + body + "*" + cs.ToString("X2", Inv) + "\r\n";
        }

        // 1e-7 度単位の整数から NMEA の ddmm.mmmmmm / dddmm.mmmmmm を整数演算で厳密に作る
        static string FmtCoord(int v, int degDigits, string pos, string neg, out string hemi)
        {
            long a = v;
            hemi = pos;
            if (a < 0)
            {
                hemi = neg;
                a = -a;
            }
            long deg = a / 10000000;
            long minE6 = (a % 10000000) * 6; // 分 × 1e6
            return deg.ToString(new string('0', degDigits), Inv)
                 + (minE6 / 1000000).ToString("00", Inv) + "."
                 + (minE6 % 1000000).ToString("000000", Inv);
        }

        // 1e-7 度単位の整数を "136.6366733" 形式の10進度に(整数演算で厳密に)
        static string FmtDeg(int v)
        {
            long a = v;
            string sign = "";
            if (a < 0)
            {
                sign = "-";
                a = -a;
            }
            return sign + (a / 10000000).ToString(Inv) + "." + (a % 10000000).ToString("0000000", Inv);
        }

        static string Xml(string s)
        {
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        }

        // 前の点からの速度[ノット]・方位[度]・距離[m] (TimeMachineX と同等の計算)
        //  距離: ハバーサイン(半径 6378137m)
        //  方位: 大圏初期方位を 0-360 に正規化して整数に切り捨て。同一座標なら 360
        static void SpeedCourse(Record a, Record b, out double speed, out double course, out double dist)
        {
            if (a.LatE7 == b.LatE7 && a.LonE7 == b.LonE7)
            {
                speed = 0;
                course = 360;
                dist = 0;
                return;
            }
            double p1 = Rad(a.LatE7), l1 = Rad(a.LonE7), p2 = Rad(b.LatE7), l2 = Rad(b.LonE7);
            double dl = l2 - l1;
            double s1 = Math.Sin((p2 - p1) / 2), s2 = Math.Sin(dl / 2);
            double h = s1 * s1 + Math.Cos(p1) * Math.Cos(p2) * s2 * s2;
            dist = 2 * EarthR * Math.Asin(Math.Sqrt(h));
            double dt = (b.Time - a.Time).TotalSeconds;
            speed = dt > 0 ? dist / dt / KnotMps : 0;

            double c = Math.Atan2(Math.Sin(dl) * Math.Cos(p2),
                                  Math.Cos(p1) * Math.Sin(p2) - Math.Sin(p1) * Math.Cos(p2) * Math.Cos(dl)) * 180 / Math.PI;
            c = ((c % 360) + 360) % 360;
            course = Math.Floor(c);
        }

        static double Rad(int v)
        {
            return v / 1e7 * Math.PI / 180;
        }
    }
}
