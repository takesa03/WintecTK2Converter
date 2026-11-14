using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Tk2Gui
{
    /// <summary>
    /// 「CSV に保存」で出力したラップ CSV を読み込んで、解析結果 (読み取り専用の表示用) に戻す。
    /// ログやコース定義がなくても、保存したときの表をそのまま見られる。開始時刻は秒まで、ライン通過の時刻などの詳細は復元できない。
    /// </summary>
    public static class LapCsv
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static LapAnalysis Read(string path)
        {
            List<List<string>> rows = Parse(File.ReadAllText(path, Encoding.UTF8));
            rows.RemoveAll(r => r.Count == 0 || (r.Count == 1 && r[0].Trim().Length == 0));
            if (rows.Count < 2) throw new InvalidDataException("データがありません");
            List<string> h = rows[0].Select(x => x.Trim().TrimStart('\uFEFF')).ToList();
            int iStart = h.IndexOf("StartTime"), iLapSec = h.IndexOf("LapSec");
            if (h.Count == 0 || h[0] != "Session" || iStart < 0 || iLapSec <= iStart)
                throw new InvalidDataException("このアプリで保存したラップ CSV ではありません");
            int M = iLapSec - iStart - 1;
            int iSession = h.IndexOf("Session"), iDriver = h.IndexOf("Driver"), iFile = h.IndexOf("File"), iLap = h.IndexOf("Lap"),
                iType = h.IndexOf("Type"), iValid = h.IndexOf("Valid"), iLapTime = h.IndexOf("LapTime"),
                iMax = h.IndexOf("MaxKmh"), iBestLap = h.IndexOf("BestLap"), iBestSec = h.IndexOf("BestSectors"), iBestMax = h.IndexOf("BestMaxKmh"), iNote = h.IndexOf("Note");
            int iSMax = h.IndexOf(h[iStart + 1] + "MaxKmh");

            LapAnalysis res = new LapAnalysis { SectorNames = h.Skip(iStart + 1).Take(M).ToArray(), BestSector = new double?[M] };
            Dictionary<int, SessionRecord> sessions = new Dictionary<int, SessionRecord>();
            foreach (List<string> r in rows.Skip(1))
            {
                int no;
                if (!int.TryParse(Get(r, iSession), NumberStyles.Integer, Inv, out no)) continue;
                SessionRecord s;
                if (!sessions.TryGetValue(no, out s))
                {
                    s = new SessionRecord { No = no, Driver = Get(r, iDriver), FileName = Get(r, iFile), BestSector = new double?[M] };
                    sessions[no] = s;
                    res.Sessions.Add(s);
                }
                string type = Get(r, iType);
                LapRecord l = new LapRecord
                {
                    Session = no, Valid = Get(r, iValid) != "0", Note = Get(r, iNote), FileName = Get(r, iFile),
                    Sector = new double?[M], SectorMaxKmh = new double[M], SectorMinKmh = new double[M],
                    IsBestSector = new bool[M], IsSessionBestSector = new bool[M],
                    Type = type == "OUT" ? LapType.Out : type == "PARTIAL" ? LapType.Partial : type == "INCOMPLETE" ? LapType.Incomplete : LapType.Normal,
                    IsIn = type == "IN",
                    // ジムカーナの走行 (RUN = 完走, DNF, MISS = ミスコースの疑い)
                    IsGymRun = type == "RUN" || type == "DNF" || type == "MISS",
                    Dnf = type == "DNF", MissCourse = type == "MISS"
                };
                if (l.IsGymRun)
                {
                    res.IsGymkhana = true;
                    l.Memo = Get(r, h.IndexOf("Memo"));
                    string drv = Get(r, iDriver);
                    if (drv != (s.Driver ?? "")) l.RunDriver = drv;     // セッションの最初の走行と違うドライバー
                }
                int lapNo;
                if (int.TryParse(Get(r, iLap), NumberStyles.Integer, Inv, out lapNo)) l.LapNo = lapNo;
                TimeSpan ts;
                if (TimeSpan.TryParse(Get(r, iStart), Inv, out ts)) l.StartT = ts.TotalSeconds - 9 * 3600;   // 表示は日本時間 (+9h) なので、戻す
                for (int k = 0; k < M; k++)
                {
                    double v;
                    if (Num(Get(r, iStart + 1 + k), out v)) l.Sector[k] = v;
                    if (iSMax >= 0 && Num(Get(r, iSMax + k), out v)) l.SectorMaxKmh[k] = v;
                }
                double lapSec;
                if (Num(Get(r, iLapSec), out lapSec))
                {
                    if (l.Type == LapType.Normal && !string.IsNullOrEmpty(Get(r, iLapTime))) l.LapTime = lapSec; else l.PartialTime = lapSec;
                }
                l.EndT = l.StartT + (l.LapTime ?? l.PartialTime ?? 0);
                double mx;
                if (Num(Get(r, iMax), out mx)) l.MaxKmh = mx;
                string bl = Get(r, iBestLap), bm = Get(r, iBestMax);
                l.IsBestLap = bl == "*"; l.IsSessionBestLap = bl == "s";
                l.IsBestMax = bm == "*"; l.IsSessionBestMax = bm == "s";
                foreach (string tok in Get(r, iBestSec).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string mark = tok.Substring(tok.Length - 1), name = tok.Substring(0, tok.Length - 1);
                    int k = Array.IndexOf(res.SectorNames, name);
                    if (k < 0) continue;
                    if (mark == "*") l.IsBestSector[k] = true; else if (mark == "s") l.IsSessionBestSector[k] = true;
                }
                s.Laps.Add(l);
            }
            if (res.Sessions.Count == 0) throw new InvalidDataException("周回のデータがありません");

            // セッションと全体のベスト (マークの付いた周回・区間から求める)
            foreach (SessionRecord s in res.Sessions)
            {
                s.StartT = s.Laps.Min(l => l.StartT);
                s.EndT = s.Laps.Max(l => l.EndT);
                foreach (LapRecord l in s.Laps)
                {
                    if ((l.IsBestLap || l.IsSessionBestLap) && l.LapTime.HasValue && (!s.BestLap.HasValue || l.LapTime.Value < s.BestLap.Value)) { s.BestLap = l.LapTime; s.BestLapNo = l.LapNo; }
                    if ((l.IsBestMax || l.IsSessionBestMax) && (!s.BestMaxKmh.HasValue || l.MaxKmh > s.BestMaxKmh.Value)) s.BestMaxKmh = l.MaxKmh;
                    for (int k = 0; k < M; k++)
                        if ((l.IsBestSector[k] || l.IsSessionBestSector[k]) && l.Sector[k].HasValue && (!s.BestSector[k].HasValue || l.Sector[k].Value < s.BestSector[k].Value)) s.BestSector[k] = l.Sector[k];
                }
            }
            foreach (SessionRecord s in res.Sessions)
                foreach (LapRecord l in s.Laps)
                {
                    if (l.IsBestLap && l.LapTime.HasValue) res.BestLap = l.LapTime;
                    if (l.IsBestMax) res.BestMaxKmh = l.MaxKmh;
                    for (int k = 0; k < M; k++) if (l.IsBestSector[k] && l.Sector[k].HasValue) res.BestSector[k] = l.Sector[k];
                }
            if (res.IsGymkhana)
            {
                foreach (SessionRecord s in res.Sessions)
                    foreach (LapRecord l in s.Laps)
                        if (l.LapTime.HasValue && res.BestLap.HasValue) l.Gap = l.LapTime.Value - res.BestLap.Value;
                if (M >= 2 && res.BestSector.All(b => b.HasValue)) res.TheoreticalBest = res.BestSector.Sum(b => b.Value);
            }
            return res;
        }

        static string Get(List<string> r, int i) { return i >= 0 && i < r.Count ? r[i].Trim() : ""; }

        static bool Num(string s, out double v)
        {
            return double.TryParse(s, NumberStyles.Float, Inv, out v);
        }

        // 引用符つきの項目 (カンマ・改行・"" を含む) に対応した簡単な CSV 読み取り
        static List<List<string>> Parse(string text)
        {
            List<List<string>> rows = new List<List<string>>();
            List<string> row = new List<string>();
            StringBuilder cur = new StringBuilder();
            bool q = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (q)
                {
                    if (c == '"') { if (i + 1 < text.Length && text[i + 1] == '"') { cur.Append('"'); i++; } else q = false; }
                    else cur.Append(c);
                }
                else if (c == '"') q = true;
                else if (c == ',') { row.Add(cur.ToString()); cur.Clear(); }
                else if (c == '\r') { }
                else if (c == '\n') { row.Add(cur.ToString()); cur.Clear(); rows.Add(row); row = new List<string>(); }
                else cur.Append(c);
            }
            if (cur.Length > 0 || row.Count > 0) { row.Add(cur.ToString()); rows.Add(row); }
            return rows;
        }
    }
}
