using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Tk2Gui
{
    // =====================================================================
    //  動画素材の書き出し: ProRes 4444 (透明付き .mov, ffmpeg) または透明 PNG 連番。
    //  描画は専用の STA スレッドで行い、画素を ffmpeg の標準入力へ流す (-f rawvideo -pix_fmt bgra)。
    // =====================================================================

    public sealed class FrameRate
    {
        public string Label; public int Num, Den;
        public double Fps { get { return (double)Num / Den; } }
        public string Arg { get { return Num.ToString(CultureInfo.InvariantCulture) + "/" + Den.ToString(CultureInfo.InvariantCulture); } }
        public override string ToString() { return Label; }
        public static readonly FrameRate[] All =
        {
            new FrameRate { Label = "23.976", Num = 24000, Den = 1001 },
            new FrameRate { Label = "24", Num = 24, Den = 1 },
            new FrameRate { Label = "25", Num = 25, Den = 1 },
            new FrameRate { Label = "29.97", Num = 30000, Den = 1001 },
            new FrameRate { Label = "30", Num = 30, Den = 1 },
            new FrameRate { Label = "50", Num = 50, Den = 1 },
            new FrameRate { Label = "59.94", Num = 60000, Den = 1001 },
            new FrameRate { Label = "60", Num = 60, Den = 1 },
        };
    }

    public static class OverlayEncoder
    {
        /// <summary>ffmpeg.exe を探す: 指定のパス → PATH → winget の入れ場所。見つからなければ null</summary>
        public static string FindFfmpeg(string configured)
        {
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string d in path.Split(';'))
            {
                try
                {
                    string p = Path.Combine(d.Trim().Trim('"'), "ffmpeg.exe");
                    if (d.Trim().Length > 0 && File.Exists(p)) return p;
                }
                catch (Exception) { }
            }
            try
            {
                string pk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Packages");
                if (Directory.Exists(pk))
                    foreach (string d in Directory.GetDirectories(pk, "Gyan.FFmpeg*"))
                    {
                        string f = Directory.GetFiles(d, "ffmpeg.exe", SearchOption.AllDirectories).FirstOrDefault();
                        if (f != null) return f;
                    }
                string links = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links", "ffmpeg.exe");
                if (File.Exists(links)) return links;
            }
            catch (Exception) { }
            return null;
        }

        /// <summary>
        /// 書き出す。progress(書いたフレーム数, 全フレーム数)。cancel で中断 (書きかけのファイルは消す)。
        /// png = true なら outPath はフォルダ (frame_000000.png ...)。
        /// renderer は makeRenderer で、このスレッド (STA) の中で作る。
        /// </summary>
        public static void Export(Func<OverlayRenderer> makeRenderer, OverlayData data, FrameRate fr, string outPath, bool png, bool anim, string ffmpeg,
                                  Action<int, int> progress, CancellationToken cancel)
        {
            // 描画は CPU で行うので、複数の STA スレッドに分ける (スレッドごとに描画器を作る)。
            // 論理コアの数だけスレッドを使う (ユーザーの希望)
            int workers = Math.Max(1, Environment.ProcessorCount);
            double fps = fr.Fps;
            int total = (int)Math.Floor((data.EndT - data.StartT) * fps) + 1;
            if (anim && !png)
            {
                ExportSegmented(makeRenderer, data, fr, outPath, ffmpeg, total, workers, progress, cancel);
                return;
            }
            int ahead = workers * 3;                         // 書き込み待ちのフレームの上限 (メモリを使いすぎない)
            byte[][] ready = new byte[total][];
            int written = 0;                                 // ffmpeg へ書き終えた数 (PNG では保存し終えた数)
            int done = 0;
            object gate = new object();
            Exception err = null;
            CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            int w0 = 0, h0 = 0;
            ManualResetEventSlim sized = new ManualResetEventSlim(false);

            List<Thread> threads = new List<Thread>();
            for (int k = 0; k < workers; k++)
            {
                int id = k;
                Thread th = new Thread(() =>
                {
                    try
                    {
                        OverlayRenderer r = makeRenderer();
                        if (id == 0) { w0 = r.W; h0 = r.H; sized.Set(); }
                        int trail = r.TrailFrames;
                        for (int n = id; n < total; n += workers)
                        {
                            lock (gate)
                                while (!png && n - written >= ahead && !stop.IsCancellationRequested) Monitor.Wait(gate, 200);
                            stop.Token.ThrowIfCancellationRequested();
                            OverlayState st = data.At(data.StartT + n / fps, fps, trail);
                            if (png)
                            {
                                r.SavePng(st, Path.Combine(outPath, "frame_" + n.ToString("000000", CultureInfo.InvariantCulture) + ".png"));
                                int d = Interlocked.Increment(ref done);
                                if (progress != null && (d % 10 == 0 || d == total)) progress(d, total);
                            }
                            else
                            {
                                byte[] px = (byte[])r.RenderBgra(st).Clone();   // 描画器の配列は次のフレームで上書きされる
                                lock (gate) { ready[n] = px; Monitor.PulseAll(gate); }
                            }
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { lock (gate) { if (err == null) err = ex; } stop.Cancel(); }
                    finally { sized.Set(); lock (gate) Monitor.PulseAll(gate); }
                });
                th.SetApartmentState(ApartmentState.STA);
                th.IsBackground = true;
                threads.Add(th);
            }

            if (png) Directory.CreateDirectory(outPath);
            foreach (Thread th in threads) th.Start();
            try
            {
                if (!png)
                {
                    sized.Wait();
                    if (err != null) throw err;
                    WriteMov(w0, h0, fr, outPath, anim, ffmpeg, total, progress, stop.Token, n =>
                    {
                        byte[] px;
                        lock (gate)
                        {
                            while ((px = ready[n]) == null)
                            {
                                if (err != null) throw err;
                                stop.Token.ThrowIfCancellationRequested();
                                Monitor.Wait(gate, 200);
                            }
                            ready[n] = null;
                            written = n + 1;
                            Monitor.PulseAll(gate);
                        }
                        return px;
                    });
                }
            }
            catch (Exception) { stop.Cancel(); throw; }
            finally { foreach (Thread th in threads) th.Join(); }
            if (err != null) throw err;
            cancel.ThrowIfCancellationRequested();
        }

        /// <summary>
        /// QuickTime Animation 用: ffmpeg の qtrle は 1 スレッドでしか圧縮しないので、
        /// 時間で区切って区切りごとに別の ffmpeg で同時に圧縮し、最後に再圧縮なしでつなぐ。
        /// 区切りごとのスレッドが、自分の描画器で描いて自分の ffmpeg へ流す
        /// </summary>
        static void ExportSegmented(Func<OverlayRenderer> makeRenderer, OverlayData data, FrameRate fr, string outPath, string ffmpeg,
                                    int total, int segs, Action<int, int> progress, CancellationToken cancel)
        {
            if (ffmpeg == null) throw new InvalidOperationException("ffmpeg が見つかりません。PNG 連番で書き出すか、ffmpeg.exe の場所を指定してください。");
            double fps = fr.Fps;
            segs = Math.Max(1, Math.Min(segs, total / 30));        // 1 区切りは最低 30 フレーム
            string tmpDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outPath)), "." + Path.GetFileNameWithoutExtension(outPath) + "_parts");
            Directory.CreateDirectory(tmpDir);
            string[] parts = new string[segs];
            int done = 0;
            Exception err = null;
            CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            List<Thread> threads = new List<Thread>();
            for (int k = 0; k < segs; k++)
            {
                int a = (int)((long)total * k / segs), b = (int)((long)total * (k + 1) / segs);
                string part = Path.Combine(tmpDir, "part_" + k.ToString("000", CultureInfo.InvariantCulture) + ".mov");
                parts[k] = part;
                Thread th = new Thread(() =>
                {
                    try
                    {
                        OverlayRenderer r = makeRenderer();
                        int trail = r.TrailFrames;
                        WriteMov(r.W, r.H, fr, part, true, ffmpeg, b - a, null, stop.Token, i =>
                        {
                            byte[] px = r.RenderBgra(data.At(data.StartT + (a + i) / fps, fps, trail));
                            int d = Interlocked.Increment(ref done);
                            if (progress != null && (d % 10 == 0 || d == total)) progress(d, total);
                            return px;
                        });
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { lock (parts) { if (err == null) err = ex; } stop.Cancel(); }
                });
                th.SetApartmentState(ApartmentState.STA);
                th.IsBackground = true;
                threads.Add(th);
            }
            try
            {
                foreach (Thread th in threads) th.Start();
                foreach (Thread th in threads) th.Join();
                if (err != null) throw err;
                cancel.ThrowIfCancellationRequested();

                // 再圧縮なしでつなぐ
                string list = Path.Combine(tmpDir, "list.txt");
                File.WriteAllLines(list, parts.Select(p => "file '" + p.Replace("'", "'\\''") + "'"), new UTF8Encoding(false));
                ProcessStartInfo psi = new ProcessStartInfo(ffmpeg,
                    "-hide_banner -loglevel error -y -f concat -safe 0 -i \"" + list + "\" -c copy \"" + outPath + "\"")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
                using (Process p = Process.Start(psi))
                {
                    string e = p.StandardError.ReadToEnd();
                    p.WaitForExit();
                    if (p.ExitCode != 0)
                    {
                        try { if (File.Exists(outPath)) File.Delete(outPath); } catch (Exception) { }
                        throw new IOException("ffmpeg で区切りをつなげませんでした (終了コード " + p.ExitCode + ")\n" + e);
                    }
                }
            }
            finally
            {
                stop.Cancel();
                foreach (Thread th in threads) if (th.IsAlive) th.Join();
                try { Directory.Delete(tmpDir, true); } catch (Exception) { }
            }
        }

        static void WriteMov(int w, int h, FrameRate fr, string outPath, bool anim, string ffmpeg, int total,
                             Action<int, int> progress, CancellationToken cancel, Func<int, byte[]> frame)
        {
            if (ffmpeg == null) throw new InvalidOperationException("ffmpeg が見つかりません。PNG 連番で書き出すか、ffmpeg.exe の場所を指定してください。");
            // anim: QuickTime Animation (qtrle, 可逆)。同じ色の続く部分をまとめるので、透明の多いオーバーレイは ProRes の 1/8 ほどになる
            string codec = anim ? "-c:v qtrle -pix_fmt argb"
                                : "-c:v prores_ks -profile:v 4444 -pix_fmt yuva444p10le -alpha_bits 16 -vendor apl0";
            string args = string.Format(CultureInfo.InvariantCulture,
                "-hide_banner -loglevel error -y -f rawvideo -pix_fmt bgra -s {0}x{1} -framerate {2} -i - {3} \"{4}\"",
                w, h, fr.Arg, codec, outPath);
            ProcessStartInfo psi = new ProcessStartInfo(ffmpeg, args)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardError = true
            };
            StringBuilder errText = new StringBuilder();
            using (Process p = Process.Start(psi))
            {
                p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (errText) errText.AppendLine(e.Data); };
                p.BeginErrorReadLine();
                Stream stdin = p.StandardInput.BaseStream;
                bool ok = false;
                try
                {
                    for (int n = 0; n < total; n++)
                    {
                        cancel.ThrowIfCancellationRequested();
                        byte[] px = frame(n);
                        stdin.Write(px, 0, px.Length);
                        if (progress != null && (n % 10 == 0 || n == total - 1)) progress(n + 1, total);
                    }
                    ok = true;
                }
                catch (IOException ex)
                {
                    throw new IOException("ffmpeg への書き込みに失敗しました: " + ex.Message + "\n" + errText, ex);
                }
                finally
                {
                    try { stdin.Close(); } catch (Exception) { }
                    if (!ok) { try { if (!p.HasExited) p.Kill(); } catch (Exception) { } }
                    p.WaitForExit();
                    if (!ok) { try { if (File.Exists(outPath)) File.Delete(outPath); } catch (Exception) { } }
                }
                if (p.ExitCode != 0) throw new IOException("ffmpeg が失敗しました (終了コード " + p.ExitCode + ")\n" + errText);
            }
        }
    }
}
