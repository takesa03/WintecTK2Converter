using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace Tk2Gui
{
    // =====================================================================
    //  データをまとめる (docs/data_library_design.md 6 章)
    //  コースフォルダ (場所とレイアウト) の下に、ログごとのログフォルダを作り、
    //  ログ・付属情報 (log.json / rpm.json / laps.json)・FIT (heart\)・動画 (videos\、切り出しは videos\clips\) を置く。
    //  中のパスは相対なので、まとめた後はフォルダごと移しても使える。
    //  元のファイル (ログ・古い付属ファイル・動画・FIT) は消さない。動画を「移動」にしたときだけ、全部うまくいった後で元の動画を消す。
    // =====================================================================

    public enum PackVideoMode { Copy, Move, Reference }

    public sealed class PackItem
    {
        public string Src, Dst;
        public long Bytes;
        public bool IsVideo;
    }

    public sealed class PackLog
    {
        public string Src;          // 元のログ
        public string Dir;          // まとめ先のログフォルダ
        public string Dst;          // まとめ先のログ
        public LogData Data;        // 元の付属情報 (パスは絶対)
        public List<EngineStore.Segment> Segments = new List<EngineStore.Segment>();
        public List<PackItem> Items = new List<PackItem>();
        public Dictionary<string, string> Map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // 元のパス → まとめ先
        public List<string> Missing = new List<string>();   // 付属情報にあるのに見つからないファイル
        public int Sessions;
    }

    public sealed class PackPlan
    {
        public string CourseDir;
        public string CourseSrc;            // 今のコース定義のファイル
        public string CourseDst;            // まとめ先のコース定義 (同じ id があればそれ)
        public bool CourseExists;           // まとめ先に同じ id のコース定義が既にある
        public CourseDefinition Def;
        public LapAnalysis Res;
        public PackVideoMode Mode;
        public List<PackLog> Logs = new List<PackLog>();
        public long CopyBytes;              // コピーする量
        public long FreeBytes = -1;         // まとめ先の空き容量 (分からなければ −1)
    }

    public static class LibraryPacker
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static string SafeName(string n)
        {
            n = n ?? "";
            foreach (char c in Path.GetInvalidFileNameChars()) n = n.Replace(c, '_');
            n = n.Trim();
            return n.Length == 0 ? "course" : n;
        }

        /// <summary>ログフォルダの名前の既定: 日付 (日本時間、最初のセッション) _ ログの名前</summary>
        public static string LogFolderName(string log, LapAnalysis res)
        {
            string full = Path.GetFullPath(log);
            double t = res == null ? double.NaN : res.Sessions.Where(s => s.FilePath != null && string.Equals(Path.GetFullPath(s.FilePath), full, StringComparison.OrdinalIgnoreCase))
                                                    .Select(s => s.StartT).DefaultIfEmpty(double.NaN).Min();
            DateTime d = double.IsNaN(t) ? File.GetLastWriteTime(log) : new DateTime(2000, 1, 1).AddSeconds(t).AddHours(9);
            return d.ToString("yyyy-MM-dd", Inv) + "_" + SafeName(Path.GetFileNameWithoutExtension(log));
        }

        /// <summary>何をどこへ置くかを決める (まだ何も書かない)</summary>
        public static PackPlan Plan(string courseDir, CourseDefinition def, string courseSrc, LapAnalysis res, IEnumerable<string> logs, PackVideoMode mode)
        {
            PackPlan p = new PackPlan { CourseDir = Path.GetFullPath(courseDir), CourseSrc = courseSrc, Def = def, Res = res, Mode = mode };
            if (string.IsNullOrEmpty(def.Id)) throw new InvalidOperationException("コース定義に ID がありません。コース定義を保存してから、開き直してください。");
            // コース定義: まとめ先に同じ id があればそれを使う。無ければ今のファイルの名前で置く (同じ名前で別の id があれば名前を変える)
            if (Directory.Exists(p.CourseDir))
                foreach (string f in Directory.GetFiles(p.CourseDir, "*.course.json"))
                    if (CourseDefinition.ReadId(f) == def.Id) { p.CourseDst = f; p.CourseExists = true; break; }
            if (p.CourseDst == null)
            {
                string name = courseSrc != null ? Path.GetFileName(courseSrc) : SafeName(def.Name) + ".course.json";
                p.CourseDst = Unique(Path.Combine(p.CourseDir, name), null);
            }
            HashSet<string> taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string log in logs)
            {
                PackLog l = new PackLog { Src = Path.GetFullPath(log) };
                l.Dir = Path.Combine(p.CourseDir, LogFolderName(log, res));
                l.Dst = Path.Combine(l.Dir, Path.GetFileName(log));
                if (string.Equals(l.Dst, l.Src, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("まとめ先が元のログと同じ場所です: " + l.Dst);
                l.Data = LogFile.Load(log);
                EngineStore.FileData eng = EngineStore.Load(log);
                if (eng != null && eng.Segments != null) l.Segments = eng.Segments;
                l.Sessions = res == null ? 0 : res.Sessions.Count(s => s.FilePath != null && string.Equals(Path.GetFullPath(s.FilePath), l.Src, StringComparison.OrdinalIgnoreCase));
                Add(l, l.Src, l.Dst, false, taken);
                if (l.Data.Heart != null)
                    foreach (HeartStore.Entry h in l.Data.Heart)
                        if (!string.IsNullOrEmpty(h.Fit)) Add(l, h.Fit, Path.Combine(l.Dir, HeartStore.Folder, Path.GetFileName(h.Fit)), false, taken);
                if (l.Data.Videos != null && mode != PackVideoMode.Reference)
                    foreach (VideoStore.Entry v in l.Data.Videos)
                    {
                        if (string.IsNullOrEmpty(v.Path)) continue;
                        string sub = string.IsNullOrEmpty(v.ClipOf) ? "videos" : Path.Combine("videos", "clips");
                        Add(l, v.Path, Path.Combine(l.Dir, sub, Path.GetFileName(v.Path)), true, taken);
                    }
                p.Logs.Add(l);
            }
            string dstRoot = Path.GetPathRoot(p.CourseDir);
            foreach (PackItem it in p.Logs.SelectMany(l => l.Items))
            {
                bool sameVolume = string.Equals(Path.GetPathRoot(it.Src), dstRoot, StringComparison.OrdinalIgnoreCase);
                if (!(it.IsVideo && mode == PackVideoMode.Move && sameVolume)) p.CopyBytes += it.Bytes;
            }
            try { p.FreeBytes = new DriveInfo(dstRoot).AvailableFreeSpace; } catch (Exception) { p.FreeBytes = -1; }
            return p;
        }

        static void Add(PackLog l, string src, string dst, bool video, HashSet<string> taken)
        {
            string full = Path.GetFullPath(src);
            if (l.Map.ContainsKey(full)) return;
            if (!File.Exists(full)) { l.Missing.Add(full); return; }
            dst = Unique(dst, taken);
            taken.Add(dst);
            l.Map[full] = dst;
            l.Items.Add(new PackItem { Src = full, Dst = dst, Bytes = new FileInfo(full).Length, IsVideo = video });
        }

        /// <summary>同じ名前が既に決まっていれば「名前 (2).拡張子」にする (まとめ先に既にあるファイルは上書きする)</summary>
        static string Unique(string path, HashSet<string> taken)
        {
            if (taken == null || !taken.Contains(path)) return path;
            string dir = Path.GetDirectoryName(path), stem = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
            for (int i = 2; ; i++)
            {
                string q = Path.Combine(dir, stem + " (" + i + ")" + ext);
                if (!taken.Contains(q)) return q;
            }
        }

        public static string Size(long b)
        {
            if (b < 0) return "不明";
            if (b >= 1L << 30) return (b / (double)(1L << 30)).ToString("0.00", Inv) + " GB";
            if (b >= 1L << 20) return (b / (double)(1L << 20)).ToString("0.0", Inv) + " MB";
            return (b / 1024.0).ToString("0", Inv) + " KB";
        }

        /// <summary>計画の説明 (画面に出す)</summary>
        public static List<string> Describe(PackPlan p)
        {
            List<string> o = new List<string>();
            o.Add("コースフォルダ: " + p.CourseDir);
            o.Add("コース定義: " + Path.GetFileName(p.CourseDst) + (p.CourseExists ? " (同じ ID のものが既にあるので、それを使います)" : " (コピーします)"));
            foreach (PackLog l in p.Logs)
            {
                o.Add("");
                o.Add("ログ: " + Path.GetFileName(l.Src) + " → " + Path.GetFileName(l.Dir) + "\\  (セッション " + l.Sessions + ")");
                int fits = l.Items.Count(i => !i.IsVideo && i.Src != l.Src);
                int vids = l.Items.Count(i => i.IsVideo);
                o.Add(string.Format(Inv, "  心拍の FIT {0} 個 / 動画 {1} 本{2}", fits, p.Mode == PackVideoMode.Reference ? (l.Data.Videos == null ? 0 : l.Data.Videos.Count) : vids,
                    p.Mode == PackVideoMode.Reference ? " (元の場所を参照)" : p.Mode == PackVideoMode.Move ? " (移動)" : " (コピー)"));
                foreach (PackItem it in l.Items.Where(i => i.IsVideo)) o.Add("    " + Path.GetFileName(it.Src) + "  " + Size(it.Bytes));
                foreach (string m in l.Missing) o.Add("  見つからないファイル (まとめません): " + m);
                if (File.Exists(l.Dst)) o.Add("  まとめ先に同じログが既にあります。上書きします (付属情報は今の内容で書き直します)");
            }
            o.Add("");
            o.Add("コピーする量: " + Size(p.CopyBytes) + " / まとめ先の空き: " + Size(p.FreeBytes));
            return o;
        }

        /// <summary>
        /// まとめる。progress は 0〜1。中止・失敗したら、この回に新しく作ったファイルを消す (元のファイルには触れない)。
        /// 結果の確かめ (まとめ先を読み直して数が合うか) を返す
        /// </summary>
        public static List<string> Run(PackPlan p, Action<double, string> progress, CancellationToken ct)
        {
            if (p.FreeBytes >= 0 && p.CopyBytes > p.FreeBytes)
                throw new IOException("まとめ先の空き容量が足りません (必要 " + Size(p.CopyBytes) + " / 空き " + Size(p.FreeBytes) + ")");
            List<string> created = new List<string>();      // この回に新しく作ったファイル (中止したら消す)
            List<string> createdDirs = new List<string>();
            List<string> report = new List<string>();
            List<Tuple<string, string>> movedBack = new List<Tuple<string, string>>();   // 同じドライブで移した動画 (まとめ先, 元)
            long total = Math.Max(1, p.CopyBytes), done = 0;
            try
            {
                MakeDir(p.CourseDir, createdDirs);
                if (!p.CourseExists)
                {
                    if (p.CourseSrc != null && File.Exists(p.CourseSrc))
                    {
                        CopyFile(p.CourseSrc, p.CourseDst, created, ct, null);
                        string kml = KmlOf(p.CourseSrc);
                        if (File.Exists(kml) && !File.Exists(KmlOf(p.CourseDst))) CopyFile(kml, KmlOf(p.CourseDst), created, ct, null);
                    }
                    else
                    {
                        if (!File.Exists(p.CourseDst)) created.Add(p.CourseDst);
                        p.Def.Save(p.CourseDst);
                    }
                    report.Add("コース定義を置きました: " + p.CourseDst);
                }
                string dstRoot = Path.GetPathRoot(p.CourseDir);
                List<PackItem> toDelete = new List<PackItem>();
                foreach (PackLog l in p.Logs)
                {
                    MakeDir(l.Dir, createdDirs);
                    foreach (PackItem it in l.Items)
                    {
                        ct.ThrowIfCancellationRequested();
                        MakeDir(Path.GetDirectoryName(it.Dst), createdDirs);
                        bool move = it.IsVideo && p.Mode == PackVideoMode.Move;
                        if (move && string.Equals(Path.GetPathRoot(it.Src), dstRoot, StringComparison.OrdinalIgnoreCase))
                        {
                            // 同じドライブの移動は一瞬。中止したら戻せるように、移した記録を残す
                            progress(done / (double)total, "移動: " + Path.GetFileName(it.Src));
                            if (File.Exists(it.Dst)) File.Delete(it.Dst);
                            File.Move(it.Src, it.Dst);
                            movedBack.Add(Tuple.Create(it.Dst, it.Src));
                            continue;
                        }
                        long baseDone = done;
                        string label = (move ? "移動 (コピー): " : "コピー: ") + Path.GetFileName(it.Src);
                        CopyFile(it.Src, it.Dst, created, ct, b => progress((baseDone + b) / (double)total, label));
                        done += it.Bytes;
                        if (move) toDelete.Add(it);
                    }
                    WriteSidecars(p, l, created);
                    report.AddRange(Verify(p, l));
                }
                // 全部うまくいったので、別のドライブへ「移動」した動画の元を消す
                foreach (PackItem it in toDelete)
                {
                    try { File.Delete(it.Src); } catch (Exception ex) { report.Add("元の動画を消せません (コピーは済み): " + it.Src + " (" + ex.Message + ")"); }
                }
                movedBack.Clear();
                progress(1, "まとめました");
                return report;
            }
            catch (Exception)
            {
                // 途中で止まったら、この回に作ったファイルを消し、同じドライブで移した動画は元へ戻す
                foreach (string f in created) { try { if (File.Exists(f)) File.Delete(f); } catch (Exception) { } }
                foreach (Tuple<string, string> m in movedBack) { try { if (File.Exists(m.Item1) && !File.Exists(m.Item2)) File.Move(m.Item1, m.Item2); } catch (Exception) { } }
                movedBack.Clear();
                createdDirs.Reverse();
                foreach (string d in createdDirs) { try { if (Directory.Exists(d) && !Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); } catch (Exception) { } }
                throw;
            }
        }

        static string KmlOf(string course)
        {
            return course.EndsWith(".course.json", StringComparison.OrdinalIgnoreCase)
                ? course.Substring(0, course.Length - ".course.json".Length) + ".kml" : Path.ChangeExtension(course, ".kml");
        }

        static void MakeDir(string d, List<string> createdDirs)
        {
            if (Directory.Exists(d)) return;
            string parent = Path.GetDirectoryName(d);
            if (parent != null) MakeDir(parent, createdDirs);
            Directory.CreateDirectory(d);
            createdDirs.Add(d);
        }

        /// <summary>コピーする (一時ファイルに書いてから置き換え。更新日時も写す。laps.json の指紋がログの更新日時を使うため)</summary>
        static void CopyFile(string src, string dst, List<string> created, CancellationToken ct, Action<long> prog)
        {
            if (string.Equals(Path.GetFullPath(src), Path.GetFullPath(dst), StringComparison.OrdinalIgnoreCase)) return;
            string tmp = dst + ".part";
            created.Add(tmp);
            if (!File.Exists(dst)) created.Add(dst);
            byte[] buf = new byte[4 << 20];
            using (FileStream i = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan))
            using (FileStream o = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            {
                long n = 0; int r;
                while ((r = i.Read(buf, 0, buf.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    o.Write(buf, 0, r);
                    n += r;
                    if (prog != null) prog(n);
                }
            }
            File.SetLastWriteTimeUtc(tmp, File.GetLastWriteTimeUtc(src));
            File.Move(tmp, dst, true);
        }

        static string MapPath(PackLog l, string path, PackVideoMode mode)
        {
            if (string.IsNullOrEmpty(path)) return path;
            string d;
            return l.Map.TryGetValue(Path.GetFullPath(path), out d) ? d : path;   // まとめなかったもの (参照・見つからない) は元のパスのまま
        }

        /// <summary>まとめ先の log.json・rpm.json・laps.json を書く (パスはまとめ先のものに直す)</summary>
        static void WriteSidecars(PackPlan p, PackLog l, List<string> created)
        {
            LogData d = l.Data;
            string dstLog = LogFile.PathOf(l.Dst), dstRpm = EngineStore.SegmentsPath(l.Dst), dstLaps = LapsStore.PathOf(l.Dst);
            foreach (string f in new[] { dstLog, dstRpm, dstLaps }) if (!File.Exists(f)) created.Add(f);
            // 元の付属情報を書き換えないよう、写しを作ってから直す
            LogData c = LogFile.Clone(d);
            c.Imported = null;
            c.Courses = new List<string> { p.CourseDst };
            if (c.Videos != null) foreach (VideoStore.Entry v in c.Videos) { v.Path = MapPath(l, v.Path, p.Mode); v.ClipOf = MapPath(l, v.ClipOf, p.Mode); }
            if (c.Heart != null) foreach (HeartStore.Entry h in c.Heart) h.Fit = MapPath(l, h.Fit, p.Mode);
            if (c.Engine != null) c.Engine.Segments = null;
            LogFile.Save(l.Dst, c);
            List<EngineStore.Segment> segs = l.Segments.Select(s => EngineStore.CloneSegment(s)).ToList();
            foreach (EngineStore.Segment s in segs) s.Video = MapPath(l, s.Video, p.Mode);
            EngineStore.WriteSegments(l.Dst, segs);
            List<string> notes = new List<string>();
            LapsStore.SaveTo(p.Def, p.CourseDst, p.Res, l.Src, l.Dst, notes);
            if (notes.Count > 0) throw new IOException(string.Join(" / ", notes));
        }

        /// <summary>まとめ先を読み直して、セッション・動画・心拍・回転数の数が元と同じかを確かめる</summary>
        static List<string> Verify(PackPlan p, PackLog l)
        {
            List<string> o = new List<string>();
            LogData n = LogFile.Load(l.Dst);
            LogFile.TakeNotes(l.Dst);
            LapsFile laps = LapsStore.Load(l.Dst);
            LapsResult r = laps == null ? null : laps.Results.FirstOrDefault(x => x.Course == p.Def.Id);
            int vOld = l.Data.Videos == null ? 0 : l.Data.Videos.Count, vNew = n.Videos == null ? 0 : n.Videos.Count;
            int vOk = n.Videos == null ? 0 : n.Videos.Count(v => File.Exists(v.Path));
            int hOld = l.Data.Heart == null ? 0 : l.Data.Heart.Count, hNew = n.Heart == null ? 0 : n.Heart.Count;
            int hOk = n.Heart == null ? 0 : n.Heart.Count(h => File.Exists(h.Fit));
            EngineStore.FileData eng = EngineStore.Load(l.Dst);
            int eOld = l.Segments.Count, eNew = eng == null || eng.Segments == null ? 0 : eng.Segments.Count;
            int sNew = r == null ? 0 : r.Sessions.Count;
            bool ok = vOld == vNew && vOk == vNew && hOld == hNew && hOk == hNew && eOld == eNew && sNew == l.Sessions && File.Exists(l.Dst);
            o.Add(string.Format(Inv, "{0} {1}: セッション {2}/{3}・動画 {4}/{5} (見つかる {6})・心拍 {7}/{8} (見つかる {9})・回転数 {10}/{11}  (まとめ先/元)",
                ok ? "OK" : "要確認", Path.GetFileName(l.Dir), sNew, l.Sessions, vNew, vOld, vOk, hNew, hOld, hOk, eNew, eOld));
            return o;
        }
    }
}
