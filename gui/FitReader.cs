using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Tk2Gui
{
    // =====================================================================
    //  FIT ファイル (Garmin の形式。Polar などの腕時計・サイクルコンピューターも出力する) の読み込み。
    //  外部のパッケージは使わず、必要な項目 (record の時刻・心拍・速度・位置・距離・高度など) だけを読む。
    //  形式: 12 / 14 バイトのヘッダ + レコード列 + CRC。レコードは「定義メッセージ」で
    //  ローカル番号 (0〜15) ごとに項目の並びを決め、「データメッセージ」がその並びで値を持つ。
    //  時刻は 1989-12-31 00:00 UTC からの秒 (u32)。位置は semicircle (× 180 / 2^31 で度)。
    // =====================================================================

    public sealed class FitRecord
    {
        public double T;                        // ログと同じ時刻 (2000-01-01 UTC からの秒)
        public double Lat = double.NaN;         // 度
        public double Lon = double.NaN;
        public double AltM = double.NaN;
        public double SpeedKmh = double.NaN;
        public double DistM = double.NaN;
        public double HeartRate = double.NaN;   // bpm
        public double Cadence = double.NaN;     // rpm
        public double Power = double.NaN;       // W
        public double TempC = double.NaN;       // ℃
    }

    public sealed class FitFile
    {
        public string Path;
        public string Manufacturer = "";        // 番号のまま (例: "123" = Polar)。名前は ManufacturerName
        public string ProductName = "";         // file_id の product_name (Polar は "Polar Ignite" など)
        public string Sport = "";               // session の sport の番号
        public readonly List<FitRecord> Records = new List<FitRecord>();
        public bool CrcOk;

        public string ManufacturerName
        {
            get
            {
                switch (Manufacturer)
                {
                    case "1": return "Garmin";
                    case "123": return "Polar";
                    case "32": return "Wahoo";
                    case "69": return "Stages";
                    case "260": return "Zwift";
                    case "294": return "Coros";
                    case "265": return "Strava";
                    case "40": return "Suunto";
                    default: return Manufacturer;
                }
            }
        }

        public string DeviceText
        {
            get
            {
                if (ProductName.Length > 0) return ProductName;
                return ManufacturerName.Length > 0 ? ManufacturerName : "FIT";
            }
        }
    }

    public static class FitReader
    {
        // FIT の時刻の基準 (1989-12-31 00:00 UTC) から、このツールの基準 (2000-01-01 UTC) までの秒
        static readonly double FitToLogSec = (new DateTime(2000, 1, 1) - new DateTime(1989, 12, 31)).TotalSeconds;

        sealed class FieldDef { public int Num, Size, BaseType; }
        sealed class MsgDef
        {
            public int Global;
            public bool BigEndian;
            public readonly List<FieldDef> Fields = new List<FieldDef>();
            public int DevBytes;                // 開発者の項目 (読まずに飛ばす)
        }

        public static FitFile Load(string path)
        {
            byte[] b = File.ReadAllBytes(path);
            if (b.Length < 14) throw new InvalidDataException("FIT ファイルが短すぎます");
            int hdr = b[0];
            if (hdr < 12 || b[8] != '.' || b[9] != 'F' || b[10] != 'I' || b[11] != 'T')
                throw new InvalidDataException("FIT ファイルではありません");
            int dataSize = (int)(b[4] | (b[5] << 8) | (b[6] << 16) | (b[7] << 24));
            int end = Math.Min(b.Length, hdr + dataSize);

            FitFile fit = new FitFile { Path = path };
            fit.CrcOk = end + 2 <= b.Length && Crc(b, 0, end) == (b[end] | (b[end + 1] << 8));

            MsgDef[] defs = new MsgDef[16];
            long lastTs = -1;                   // 圧縮した時刻のヘッダ用
            int p = hdr;
            while (p < end)
            {
                int h = b[p++];
                if ((h & 0x80) != 0)
                {
                    // 圧縮した時刻のヘッダ: 下位 5 ビットが時刻の下位、ビット 5-6 がローカル番号
                    MsgDef d = defs[(h >> 5) & 3];
                    if (d == null) throw new InvalidDataException("定義の無いメッセージ (位置 " + (p - 1) + ")");
                    int off = h & 0x1F;
                    if (lastTs >= 0) lastTs += (off - (lastTs & 0x1F)) & 0x1F;
                    p = ReadData(b, p, d, fit, ref lastTs, true);
                }
                else if ((h & 0x40) != 0)
                {
                    // 定義メッセージ
                    MsgDef d = new MsgDef();
                    p++;                                    // 予約
                    d.BigEndian = b[p++] == 1;
                    d.Global = d.BigEndian ? (b[p] << 8) | b[p + 1] : b[p] | (b[p + 1] << 8);
                    p += 2;
                    int n = b[p++];
                    for (int i = 0; i < n; i++, p += 3)
                        d.Fields.Add(new FieldDef { Num = b[p], Size = b[p + 1], BaseType = b[p + 2] });
                    if ((h & 0x20) != 0)
                    {
                        int dn = b[p++];
                        for (int i = 0; i < dn; i++, p += 3) d.DevBytes += b[p + 1];
                    }
                    defs[h & 0x0F] = d;
                }
                else
                {
                    MsgDef d = defs[h & 0x0F];
                    if (d == null) throw new InvalidDataException("定義の無いメッセージ (位置 " + (p - 1) + ")");
                    p = ReadData(b, p, d, fit, ref lastTs, false);
                }
            }
            return fit;
        }

        // データメッセージを 1 つ読む。読んだ後の位置を返す
        static int ReadData(byte[] b, int p, MsgDef d, FitFile fit, ref long lastTs, bool compressedTs)
        {
            FitRecord r = d.Global == 20 ? new FitRecord() : null;
            long ts = compressedTs ? lastTs : -1;
            foreach (FieldDef f in d.Fields)
            {
                int at = p;
                p += f.Size;
                if (p > b.Length) throw new InvalidDataException("FIT ファイルが途中で切れています");
                if (f.Num == 253)                           // timestamp (全メッセージ共通)
                {
                    long v = UInt(b, at, 4, d.BigEndian);
                    if (f.Size == 4 && v != 0xFFFFFFFF) { ts = v; lastTs = v; }
                    continue;
                }
                switch (d.Global)
                {
                    case 0:                                 // file_id
                        if (f.Num == 1) fit.Manufacturer = Num(b, at, f, d.BigEndian, 1, 0).ToString("0");
                        else if (f.Num == 8) fit.ProductName = Str(b, at, f.Size);
                        break;
                    case 18:                                // session
                        if (f.Num == 5 && fit.Sport.Length == 0) fit.Sport = Num(b, at, f, d.BigEndian, 1, 0).ToString("0");
                        break;
                    case 20:                                // record
                        ReadRecordField(r, b, at, f, d.BigEndian);
                        break;
                }
            }
            p += d.DevBytes;
            if (r != null && ts >= 0)
            {
                r.T = ts - FitToLogSec;
                fit.Records.Add(r);
            }
            return p;
        }

        static void ReadRecordField(FitRecord r, byte[] b, int at, FieldDef f, bool be)
        {
            const double Semi = 180.0 / 2147483648.0;
            switch (f.Num)
            {
                case 0: r.Lat = Num(b, at, f, be, 1, 0) * Semi; break;
                case 1: r.Lon = Num(b, at, f, be, 1, 0) * Semi; break;
                case 2: if (double.IsNaN(r.AltM)) r.AltM = Num(b, at, f, be, 5, 500); break;   // enhanced_altitude を優先
                case 3: r.HeartRate = Num(b, at, f, be, 1, 0); break;
                case 4: r.Cadence = Num(b, at, f, be, 1, 0); break;
                case 5: r.DistM = Num(b, at, f, be, 100, 0); break;
                case 6: if (double.IsNaN(r.SpeedKmh)) r.SpeedKmh = Num(b, at, f, be, 1000, 0) * 3.6; break;
                case 7: r.Power = Num(b, at, f, be, 1, 0); break;
                case 13: r.TempC = Num(b, at, f, be, 1, 0); break;
                case 73: { double v = Num(b, at, f, be, 1000, 0); if (!double.IsNaN(v)) r.SpeedKmh = v * 3.6; break; }
                case 78: { double v = Num(b, at, f, be, 5, 500); if (!double.IsNaN(v)) r.AltM = v; break; }
            }
        }

        // 数値の項目を読む (無効値は NaN)。scale / offset は FIT の定義どおり: 値 / scale - offset
        static double Num(byte[] b, int at, FieldDef f, bool be, double scale, double offset)
        {
            int bt = f.BaseType & 0x1F;
            double v;
            switch (bt)
            {
                case 0x00: case 0x02: case 0x0A:            // enum / uint8 / uint8z
                    if (f.Size < 1) return double.NaN;
                    v = b[at];
                    if (bt == 0x0A ? v == 0 : v == 0xFF) return double.NaN;
                    break;
                case 0x01:                                  // sint8
                    if (f.Size < 1) return double.NaN;
                    v = (sbyte)b[at];
                    if (v == 0x7F) return double.NaN;
                    break;
                case 0x04: case 0x0B:                       // uint16 / uint16z
                    if (f.Size < 2) return double.NaN;
                    v = UInt(b, at, 2, be);
                    if (bt == 0x0B ? v == 0 : v == 0xFFFF) return double.NaN;
                    break;
                case 0x03:                                  // sint16
                    if (f.Size < 2) return double.NaN;
                    v = (short)UInt(b, at, 2, be);
                    if (v == 0x7FFF) return double.NaN;
                    break;
                case 0x06: case 0x0C:                       // uint32 / uint32z
                    if (f.Size < 4) return double.NaN;
                    v = UInt(b, at, 4, be);
                    if (bt == 0x0C ? v == 0 : v == 0xFFFFFFFF) return double.NaN;
                    break;
                case 0x05:                                  // sint32
                    if (f.Size < 4) return double.NaN;
                    v = (int)UInt(b, at, 4, be);
                    if (v == 0x7FFFFFFF) return double.NaN;
                    break;
                default:
                    return double.NaN;
            }
            return v / scale - offset;
        }

        static long UInt(byte[] b, int at, int n, bool be)
        {
            long v = 0;
            for (int i = 0; i < n; i++)
                v |= (long)b[at + i] << (8 * (be ? n - 1 - i : i));
            return v;
        }

        static string Str(byte[] b, int at, int n)
        {
            int len = 0;
            while (len < n && b[at + len] != 0) len++;
            return Encoding.UTF8.GetString(b, at, len).Trim();
        }

        // FIT の CRC-16
        static readonly int[] CrcTable = { 0x0000, 0xCC01, 0xD801, 0x1400, 0xF001, 0x3C00, 0x2800, 0xE401,
                                           0xA001, 0x6C00, 0x7800, 0xB401, 0x5000, 0x9C01, 0x8801, 0x4400 };
        static int Crc(byte[] b, int start, int end)
        {
            int crc = 0;
            for (int i = start; i < end; i++)
            {
                int tmp = CrcTable[crc & 0xF];
                crc = (crc >> 4) & 0x0FFF;
                crc = crc ^ tmp ^ CrcTable[b[i] & 0xF];
                tmp = CrcTable[crc & 0xF];
                crc = (crc >> 4) & 0x0FFF;
                crc = crc ^ tmp ^ CrcTable[(b[i] >> 4) & 0xF];
            }
            return crc;
        }

        /// <summary>record を GpsSample の列にする (位置のある点だけ)。速度は FIT の値</summary>
        public static List<GpsSample> ToGps(FitFile fit)
        {
            List<GpsSample> list = new List<GpsSample>();
            foreach (FitRecord r in fit.Records)
                if (!double.IsNaN(r.Lat) && !double.IsNaN(r.Lon))
                    list.Add(new GpsSample { T = r.T, Lat = r.Lat, Lon = r.Lon, SpeedKmh = r.SpeedKmh, Alt = r.AltM });
            return list;
        }
    }
}
