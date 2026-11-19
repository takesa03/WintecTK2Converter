using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Tk2Gui
{
    // =====================================================================
    //  走行動画の読み込み (ffmpeg を使う)
    //  ・Probe: 長さ・大きさ・コマの速さ・音声の有無
    //  ・FrameReader: 指定の時刻から映像を縮小して BGRA のコマで読み続ける (別スレッド)
    //  ・ReadAudio: 指定範囲の音声を 48 kHz ステレオ 16 bit で読む
    //  ・ExportClip: 指定範囲を H.264 / AAC の MP4 に切り出す (再エンコードなのでコマ単位で正確)
    //  どの形式の動画でも ffmpeg が読めれば扱える (AV1・HEVC なども)
    // =====================================================================
    public sealed class VideoInfo
    {
        public double Duration;     // [s]
        public int Width, Height;
        public double Fps = 30;
        public bool HasAudio;
    }

    public static class FfVideo
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        public const int AudioRate = 48000;

        public static string Ffmpeg()
        {
            return OverlayEncoder.FindFfmpeg(Settings.Get("Overlay.Ffmpeg"));
        }

        static ProcessStartInfo Psi(string ffmpeg, string args)
        {
            return new ProcessStartInfo(ffmpeg, args)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
                StandardErrorEncoding = Encoding.UTF8
            };
        }

        public static string Q(string path) { return "\"" + path + "\""; }
        public static string S(double v) { return v.ToString("0.###", Inv); }

        /// <summary>ffmpeg -i の表示から、動画の情報を読む</summary>
        public static VideoInfo Probe(string ffmpeg, string path)
        {
            string err;
            using (Process p = Process.Start(Psi(ffmpeg, "-hide_banner -i " + Q(path))))
            {
                Task<string> o = p.StandardOutput.ReadToEndAsync();
                err = p.StandardError.ReadToEnd();
                p.WaitForExit(10000);
                o.Wait(1000);
            }
            VideoInfo vi = new VideoInfo();
            Match m = Regex.Match(err, @"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)");
            if (!m.Success) throw new InvalidDataException("動画として読めません: " + Path.GetFileName(path));
            vi.Duration = int.Parse(m.Groups[1].Value) * 3600 + int.Parse(m.Groups[2].Value) * 60 + double.Parse(m.Groups[3].Value, Inv);
            foreach (string line in err.Split('\n'))
            {
                if (line.Contains("Stream #") && line.Contains("Video:") && vi.Width == 0)
                {
                    Match s = Regex.Match(line, @"\b(\d{2,5})x(\d{2,5})\b");
                    if (s.Success) { vi.Width = int.Parse(s.Groups[1].Value); vi.Height = int.Parse(s.Groups[2].Value); }
                    Match f = Regex.Match(line, @"([\d.]+) fps");
                    if (!f.Success) f = Regex.Match(line, @"([\d.]+) tbr");
                    double fps;
                    if (f.Success && double.TryParse(f.Groups[1].Value, NumberStyles.Float, Inv, out fps) && fps > 1 && fps < 1000) vi.Fps = fps;
                }
                if (line.Contains("Stream #") && line.Contains("Audio:")) vi.HasAudio = true;
            }
            if (vi.Width == 0) throw new InvalidDataException("映像がありません: " + Path.GetFileName(path));
            return vi;
        }

        /// <summary>指定範囲の音声 (48 kHz ステレオ 16 bit のサンプル列、左右交互)。範囲の外や音声が無い所は無音</summary>
        public static short[] ReadAudio(string ffmpeg, string path, double start, double dur, CancellationToken ct)
        {
            int frames = (int)Math.Ceiling(dur * AudioRate);
            short[] buf = new short[frames * 2];
            double ss = Math.Max(0, start);
            int skip = (int)Math.Round((ss - start) * AudioRate);       // 動画の先頭より前の分は無音のまま
            double len = dur - (ss - start);
            if (len <= 0) return buf;
            string args = string.Format(Inv, "-hide_banner -loglevel error -ss {0} -i {1} -t {2} -vn -ac 2 -ar {3} -f s16le -", S(ss), Q(path), S(len), AudioRate);
            using (Process p = Process.Start(Psi(ffmpeg, args)))
            {
                p.ErrorDataReceived += (s, e) => { };
                p.BeginErrorReadLine();
                Stream st = p.StandardOutput.BaseStream;
                byte[] tmp = new byte[65536];
                int pos = skip * 2;              // short 単位
                int carry = -1;
                while (true)
                {
                    if (ct.IsCancellationRequested) { try { p.Kill(); } catch (Exception) { } break; }
                    int n = st.Read(tmp, 0, tmp.Length);
                    if (n <= 0) break;
                    int i = 0;
                    if (carry >= 0 && n > 0) { if (pos < buf.Length) buf[pos] = (short)(carry | (tmp[0] << 8)); pos++; carry = -1; i = 1; }
                    for (; i + 1 < n; i += 2) { if (pos < buf.Length) buf[pos] = (short)(tmp[i] | (tmp[i + 1] << 8)); pos++; }
                    if (i < n) carry = tmp[i];
                }
                p.WaitForExit(5000);
            }
            return buf;
        }

        /// <summary>
        /// 指定範囲を MP4 (H.264 + AAC) に切り出す。再エンコードなので、範囲はコマ単位で正確。
        /// progress には 0〜1 を渡す
        /// </summary>
        public static void ExportClip(string ffmpeg, string path, double start, double dur, string outPath, bool audio,
                                      Action<double> progress, CancellationToken ct)
        {
            ExportClip(ffmpeg, path, start, dur, outPath, audio, null, 0, 0, 0, progress, ct);
        }

        /// <summary>
        /// overlay を渡すと、その表示 (w × h の BGRA、fps コマ/秒、overlay.Total コマ) を映像に重ねて焼き込む。
        /// 表示のフレーム 0 は切り出しの 0 秒 (= 元の動画の start 秒) に当たる
        /// </summary>
        public static void ExportClip(string ffmpeg, string path, double start, double dur, string outPath, bool audio,
                                      OverlayFrameSource overlay, int w, int h, double fps,
                                      Action<double> progress, CancellationToken ct)
        {
            ExportClip(ffmpeg, path, start, dur, outPath, audio, overlay, w, h, fps, null, progress, ct);
        }

        /// <summary>
        /// sub を渡すと、サブ動画 (ペダルなど) をトリミング・縮小して、表示の下 (映像の上) に重ねる。
        /// 入力は 0 = メイン、1 = サブ、2 = 表示 (overlay があるとき、標準入力)。サブの音声は使わない
        /// </summary>
        public static void ExportClip(string ffmpeg, string path, double start, double dur, string outPath, bool audio,
                                      OverlayFrameSource overlay, int w, int h, double fps, SubClip sub,
                                      Action<double> progress, CancellationToken ct)
        {
            double ss = Math.Max(0, start);
            string vin = "", vmap = "-map 0:v:0";
            List<string> fc = new List<string>();
            string cur = "0:v";
            if (sub != null)
            {
                // サブの開始が負 (切り出しの 0 秒にまだ録り始めていない) なら、その分だけ後ろへずらす
                vin += sub.Start >= 0
                    ? string.Format(Inv, "-ss {0} -i {1} ", S(sub.Start), Q(sub.Path))
                    : string.Format(Inv, "-itsoffset {0} -i {1} ", S(-sub.Start), Q(sub.Path));
                string sv = string.Format(Inv, "[1:v]crop={0}:{1}:{2}:{3},scale={4}:{5}:flags=bicubic", sub.CW, sub.CH, sub.CX, sub.CY, sub.DW, sub.DH);
                if (sub.Opacity < 0.999) sv += string.Format(Inv, ",format=yuva420p,colorchannelmixer=aa={0:0.###}", sub.Opacity);
                fc.Add(sv + "[s]");
                string en = string.Format(Inv, "between(t,{0},{1})", S(Math.Max(0, sub.ClipFrom)), S(sub.ClipTo));
                if (sub.Border > 0)
                {
                    fc.Add(string.Format(Inv, "[{0}]drawbox=x={1}:y={2}:w={3}:h={4}:color={5}:t={6}:enable='{7}'[b]",
                        cur, sub.X - sub.Border, sub.Y - sub.Border, sub.DW + 2 * sub.Border, sub.DH + 2 * sub.Border, sub.BorderColor, sub.Border, en));
                    cur = "b";
                }
                fc.Add(string.Format(Inv, "[{0}][s]overlay={1}:{2}:eof_action=pass:enable='{3}'[m]", cur, sub.X, sub.Y, en));
                cur = "m";
            }
            if (overlay != null)
            {
                int oi = sub != null ? 2 : 1;
                vin += string.Format(Inv, "-f rawvideo -pix_fmt bgra -s {0}x{1} -framerate {2} -i pipe:0 ", w, h, fps.ToString("0.######", Inv));
                // 表示は映像と同じ大きさで描くので、そのまま左上に合わせて重ねる
                fc.Add(string.Format(Inv, "[{0}][{1}:v]overlay=0:0:format=auto:eof_action=pass,format=yuv420p[v]", cur, oi));
            }
            else if (sub != null) fc.Add("[" + cur + "]format=yuv420p[v]");
            if (fc.Count > 0) vmap = "-filter_complex \"" + string.Join(";", fc) + "\" -map \"[v]\"";
            string args = string.Format(Inv,
                "-hide_banner -y -ss {0} -i {1} {6}-t {2} {7} {3} -c:v libx264 -preset medium -crf 18 -pix_fmt yuv420p " +
                "{4} -movflags +faststart -progress pipe:1 -nostats {5}",
                S(ss), Q(path), S(dur), audio ? "-map 0:a:0?" : "", audio ? "-c:a aac -b:a 192k" : "-an", Q(outPath), vin, vmap);
            StringBuilder err = new StringBuilder();
            using (Process p = Process.Start(Psi(ffmpeg, args)))
            {
                p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (err) { err.AppendLine(e.Data); if (err.Length > 20000) err.Remove(0, 10000); } };
                p.BeginErrorReadLine();
                Thread feeder = null;
                Exception feedErr = null;
                if (overlay != null)
                {
                    // 表示のフレームを ffmpeg の標準入力へ流す (標準出力の進み具合を読むのと別のスレッドで)
                    feeder = new Thread(() =>
                    {
                        try
                        {
                            Stream sin = p.StandardInput.BaseStream;
                            for (int n = 0; n < overlay.Total; n++)
                            {
                                byte[] px = overlay.Get(n);
                                sin.Write(px, 0, px.Length);
                            }
                        }
                        catch (OperationCanceledException) { }
                        catch (IOException) { }        // ffmpeg が先に終わった (-t に達した)
                        catch (Exception ex) { feedErr = ex; try { p.Kill(); } catch (Exception) { } }
                        finally { try { p.StandardInput.Close(); } catch (Exception) { } }
                    });
                    feeder.IsBackground = true;
                    feeder.Start();
                }
                else
                {
                    try { p.StandardInput.Close(); } catch (Exception) { }
                }
                string line;
                while ((line = p.StandardOutput.ReadLine()) != null)
                {
                    if (ct.IsCancellationRequested) { try { p.Kill(); } catch (Exception) { } break; }
                    if (line.StartsWith("out_time_us=", StringComparison.Ordinal))
                    {
                        long us;
                        if (long.TryParse(line.Substring(12), out us) && progress != null) progress(Math.Min(1, us / 1e6 / dur));
                    }
                }
                p.WaitForExit();
                if (feeder != null) feeder.Join();
                if (ct.IsCancellationRequested)
                {
                    try { File.Delete(outPath); } catch (Exception) { }
                    throw new OperationCanceledException();
                }
                if (feedErr != null)
                {
                    try { File.Delete(outPath); } catch (Exception) { }
                    throw new IOException("表示を描けませんでした: " + feedErr.Message, feedErr);
                }
                if (p.ExitCode != 0)
                {
                    string e2; lock (err) e2 = err.ToString();
                    string[] ls = e2.Trim().Split('\n');
                    throw new IOException("ffmpeg が失敗しました (終了コード " + p.ExitCode + ")\n" + string.Join("\n", ls, Math.Max(0, ls.Length - 4), Math.Min(4, ls.Length)));
                }
            }
        }
    }

    /// <summary>
    /// 映像を縮小した BGRA のコマで読み続ける。Seek で読み始めの時刻を決め、Get(t) で t 以前で最も新しいコマを受け取る。
    /// 読み出しは別スレッド (ffmpeg の標準出力)。先読みは数コマだけ
    /// </summary>
    /// <summary>
    /// 切り出しに焼き込むサブ動画 (FfVideo.ExportClip に渡す)。位置・大きさは出力の画素 (偶数に丸めてある)
    /// </summary>
    public sealed class SubClip
    {
        public string Path;
        public double Start;            // 切り出しの 0 秒に当たるサブの秒 (負 = まだ録り始めていない)
        public double ClipFrom, ClipTo; // サブが映っている切り出しの秒の範囲
        public int CX, CY, CW, CH;      // 元の絵のトリミング (画素)
        public int X, Y, DW, DH;        // 出力の画面での位置と大きさ
        public int Border;              // 枠線の太さ (画素。0 で無し)
        public string BorderColor;      // ffmpeg の色 (0xRRGGBB@a)
        public double Opacity = 1;

        static int Even(double v) { return Math.Max(2, (int)Math.Round(v / 2) * 2); }

        /// <summary>
        /// 出力 W x H、切り出しの長さ dur、切り出しの 0 秒に当たるサブの秒 start から作る。
        /// 枠が show = 0、またはこの切り出しにサブが映らないなら null
        /// </summary>
        public static SubClip Make(string path, VideoInfo vi, double[] crop, SubVideoLayout lay, int W, int H, double start, double dur)
        {
            if (lay == null || !lay.Show || vi == null || vi.Width <= 0) return null;
            double from = -start, to = vi.Duration - start;          // 切り出しの秒で
            if (to <= 0 || from >= dur) return null;
            System.Windows.Rect src = SubVideoLayout.Source(vi.Width, vi.Height, crop);
            System.Windows.Rect dst = lay.Dest(W, H, vi.Width, vi.Height, crop);
            SubClip c = new SubClip { Path = path, Start = start, ClipFrom = from, ClipTo = to, Opacity = lay.Opacity };
            c.CW = Math.Min(Even(src.Width), vi.Width - vi.Width % 2); c.CH = Math.Min(Even(src.Height), vi.Height - vi.Height % 2);
            c.CX = Math.Max(0, Math.Min(vi.Width - c.CW, (int)Math.Round(src.X))); c.CY = Math.Max(0, Math.Min(vi.Height - c.CH, (int)Math.Round(src.Y)));
            c.DW = Even(dst.Width); c.DH = Even(dst.Height);
            c.X = (int)Math.Round(dst.X); c.Y = (int)Math.Round(dst.Y);
            c.Border = lay.Border > 0 ? Math.Max(1, (int)Math.Round(lay.Border * H / 1080.0)) : 0;
            System.Windows.Media.Color bc = lay.BorderColor;
            c.BorderColor = string.Format(Inv, "0x{0:X2}{1:X2}{2:X2}@{3:0.###}", bc.R, bc.G, bc.B, bc.A / 255.0);
            return c;
        }

        static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;
    }

    public sealed class FrameReader : IDisposable
    {
        public sealed class Frame { public double T; public byte[] Px; }

        readonly string _ffmpeg, _path;
        public readonly int W, H;
        public readonly double Fps;
        public readonly double Duration;
        readonly object _lock = new object();
        readonly Queue<Frame> _q = new Queue<Frame>();
        const int MaxQueue = 6;
        Process _proc;
        Thread _thread;
        int _gen;                       // Seek のたびに増やす (古い読み出しのコマを捨てる)
        double _seekT;
        bool _eof;
        double _lastGiven = double.NegativeInfinity;
        readonly Stack<byte[]> _pool = new Stack<byte[]>();

        public FrameReader(string ffmpeg, string path, VideoInfo vi, int maxWidth)
        {
            _ffmpeg = ffmpeg; _path = path; Fps = vi.Fps; Duration = vi.Duration;
            double k = Math.Min(1.0, (double)maxWidth / vi.Width);
            W = Math.Max(2, (int)Math.Round(vi.Width * k / 2) * 2);
            H = Math.Max(2, (int)Math.Round(vi.Height * k / 2) * 2);
        }

        /// <summary>いま読んでいる範囲の先頭 (この時刻から読み直した)</summary>
        public double SeekTime { get { return _seekT; } }

        public void Seek(double t)
        {
            t = Math.Max(0, Math.Min(Duration - 0.05, t));
            Stop();
            int gen;
            lock (_lock) { _gen++; gen = _gen; _q.Clear(); _eof = false; _seekT = t; _lastGiven = double.NegativeInfinity; }
            string args = string.Format(CultureInfo.InvariantCulture,
                "-hide_banner -loglevel error -ss {0} -i {1} -an -sn -vf scale={2}:{3}:flags=bilinear -pix_fmt bgra -f rawvideo -",
                FfVideo.S(t), FfVideo.Q(_path), W, H);
            ProcessStartInfo psi = new ProcessStartInfo(_ffmpeg, args)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            Process p = Process.Start(psi);
            p.ErrorDataReceived += (s, e) => { };
            p.BeginErrorReadLine();
            _proc = p;
            double t0 = t;
            _thread = new Thread(() => Pump(p, gen, t0)) { IsBackground = true, Name = "FrameReader" };
            _thread.Start();
        }

        void Pump(Process p, int gen, double t0)
        {
            int size = W * H * 4;
            Stream st = p.StandardOutput.BaseStream;
            long k = 0;
            try
            {
                while (true)
                {
                    byte[] px;
                    lock (_lock) px = _pool.Count > 0 ? _pool.Pop() : null;
                    if (px == null) px = new byte[size];
                    int got = 0;
                    while (got < size)
                    {
                        int n = st.Read(px, got, size - got);
                        if (n <= 0) break;
                        got += n;
                    }
                    if (got < size) break;
                    Frame f = new Frame { T = t0 + k / Fps, Px = px };
                    k++;
                    lock (_lock)
                    {
                        while (_gen == gen && _q.Count >= MaxQueue) Monitor.Wait(_lock, 200);
                        if (_gen != gen) return;
                        _q.Enqueue(f);
                    }
                }
            }
            catch (Exception) { }
            lock (_lock) { if (_gen == gen) _eof = true; }
        }

        /// <summary>
        /// 時刻 t に見せるコマ (t 以前で最も新しいもの)。まだ無ければ null。
        /// 読み出しより大きく先・後ろに飛んだら、needSeek = true (呼び出し側で Seek する)
        /// </summary>
        public Frame Get(double t, out bool needSeek)
        {
            needSeek = false;
            Frame best = null;
            lock (_lock)
            {
                if (t < _seekT - 0.02) { needSeek = true; return null; }
                while (_q.Count > 0 && _q.Peek().T <= t + 0.5 / Fps)
                {
                    if (best != null) _pool.Push(best.Px);
                    best = _q.Dequeue();
                }
                Monitor.PulseAll(_lock);
                if (best != null) _lastGiven = best.T;
                if (best == null && _q.Count == 0 && !_eof && t - Math.Max(_seekT, _lastGiven) > 1.0) needSeek = true;   // 読み出しが追いつかない
                if (best == null && _q.Count > 0 && _q.Peek().T > t + 1.0) needSeek = true;       // 後ろへ戻った
            }
            return best;
        }

        /// <summary>使い終わったコマのバッファを返す (次の読み出しで使い回す)</summary>
        public void Recycle(Frame f) { if (f != null) lock (_lock) { if (_pool.Count < 4) _pool.Push(f.Px); } }

        void Stop()
        {
            Process p = _proc; _proc = null;
            lock (_lock) { _gen++; Monitor.PulseAll(_lock); }
            if (p != null)
            {
                try { if (!p.HasExited) p.Kill(); } catch (Exception) { }
                try { p.Dispose(); } catch (Exception) { }
            }
        }

        public void Dispose() { Stop(); }
    }
}
