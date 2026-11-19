using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;

namespace Tk2Gui
{
    // =====================================================================
    //  動画素材の書き出し (透明) にサブ動画を入れる (docs/subvideo_design.md の 7.3)。
    //  ffmpeg でサブを「トリミング → 枠の大きさ → 出力の fps」にして順に読み、
    //  フレーム番号で取り出せるようにしておく。表示の画像の下に Compose で貼る
    // =====================================================================
    public sealed class OverlaySubFeed : IDisposable
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        readonly SubClip _c;
        readonly Dictionary<int, byte[]> _buf = new Dictionary<int, byte[]>();
        readonly object _gate = new object();
        readonly Process _p;
        readonly Thread _th;
        readonly int _startN;
        const int Cap = 48;                 // 先に読んでおくフレームの上限
        int _next;                          // 次に読み出すフレーム番号
        bool _ended, _disposed;
        Exception _err;

        /// <param name="c">位置・大きさ・トリミング (SubClip.Make で出力の画素に直したもの。Start = 書き出しの 0 フレームに当たるサブの秒)</param>
        /// <param name="firstN">このフィードで最初に使うフレーム番号</param>
        public OverlaySubFeed(string ffmpeg, SubClip c, FrameRate fr, int firstN)
        {
            _c = c;
            double fps = fr.Fps;
            int n0 = firstN;
            double seek = c.Start + n0 / fps;
            if (seek < 0) { n0 += (int)Math.Ceiling(-seek * fps - 1e-9); seek = Math.Max(0, c.Start + n0 / fps); }
            _startN = n0; _next = n0;
            string vf = string.Format(Inv, "crop={0}:{1}:{2}:{3},scale={4}:{5}:flags=bicubic,fps={6}", c.CW, c.CH, c.CX, c.CY, c.DW, c.DH, fr.Arg);
            string args = string.Format(Inv, "-hide_banner -loglevel error -ss {0:0.######} -i \"{1}\" -an -sn -vf {2} -f rawvideo -pix_fmt bgra -", seek, c.Path, vf);
            _p = new Process
            {
                StartInfo = new ProcessStartInfo(ffmpeg, args)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true
                }
            };
            _p.Start();
            _p.ErrorDataReceived += (s, e) => { };
            _p.BeginErrorReadLine();
            _th = new Thread(Run) { IsBackground = true };
            _th.Start();
        }

        void Run()
        {
            int size = _c.DW * _c.DH * 4;
            try
            {
                Stream s = _p.StandardOutput.BaseStream;
                while (true)
                {
                    lock (_gate)
                        while (_buf.Count >= Cap && !_disposed) Monitor.Wait(_gate, 200);
                    if (_disposed) return;
                    byte[] px = new byte[size];
                    int got = 0;
                    while (got < size)
                    {
                        int r = s.Read(px, got, size - got);
                        if (r <= 0) break;
                        got += r;
                    }
                    if (got < size) break;
                    lock (_gate) { _buf[_next++] = px; Monitor.PulseAll(_gate); }
                }
            }
            catch (Exception ex) { lock (_gate) { if (!_disposed) _err = ex; } }
            finally { lock (_gate) { _ended = true; Monitor.PulseAll(_gate); } }
        }

        /// <summary>フレーム n のサブの画素 (DW x DH の BGRA)。映っていなければ null。どの番号も 1 回だけ呼ぶこと</summary>
        public byte[] Get(int n, CancellationToken ct)
        {
            if (n < _startN) return null;
            lock (_gate)
            {
                while (true)
                {
                    byte[] px;
                    if (_buf.TryGetValue(n, out px)) { _buf.Remove(n); Monitor.PulseAll(_gate); return px; }
                    if (_err != null) throw new IOException("サブ動画を読めません: " + _err.Message, _err);
                    if (_ended && n >= _next) return null;      // サブの終わりより後
                    ct.ThrowIfCancellationRequested();
                    Monitor.Wait(_gate, 200);
                }
            }
        }

        /// <summary>表示の画像 hud (W x H の BGRA、乗算なし) の下にサブを貼る (表示が上)</summary>
        public void Compose(byte[] hud, int W, int H, byte[] sub)
        {
            if (sub == null) return;
            SubClip c = _c;
            double op = Math.Max(0, Math.Min(1, c.Opacity));
            // 枠線 (サブの外側)
            if (c.Border > 0)
            {
                System.Windows.Media.Color bc = ParseColor(c.BorderColor);
                int b = c.Border;
                for (int y = c.Y - b; y < c.Y + c.DH + b; y++)
                    for (int x = c.X - b; x < c.X + c.DW + b; x++)
                    {
                        if (x >= c.X && x < c.X + c.DW && y >= c.Y && y < c.Y + c.DH) continue;
                        if (x < 0 || y < 0 || x >= W || y >= H) continue;
                        Under(hud, (y * W + x) * 4, bc.B, bc.G, bc.R, bc.A / 255.0);
                    }
            }
            for (int y = 0; y < c.DH; y++)
            {
                int oy = c.Y + y;
                if (oy < 0 || oy >= H) continue;
                for (int x = 0; x < c.DW; x++)
                {
                    int ox = c.X + x;
                    if (ox < 0 || ox >= W) continue;
                    int si = (y * c.DW + x) * 4;
                    Under(hud, (oy * W + ox) * 4, sub[si], sub[si + 1], sub[si + 2], op);
                }
            }
        }

        /// <summary>画素 i の下に色 (b,g,r, 不透明度 sa) を敷く</summary>
        static void Under(byte[] px, int i, byte b, byte g, byte r, double sa)
        {
            double ha = px[i + 3] / 255.0;
            double oa = ha + sa * (1 - ha);
            if (oa <= 0) return;
            double k = sa * (1 - ha);
            px[i] = (byte)Math.Round((px[i] * ha + b * k) / oa);
            px[i + 1] = (byte)Math.Round((px[i + 1] * ha + g * k) / oa);
            px[i + 2] = (byte)Math.Round((px[i + 2] * ha + r * k) / oa);
            px[i + 3] = (byte)Math.Round(oa * 255);
        }

        static System.Windows.Media.Color ParseColor(string s)
        {
            // "0xRRGGBB@a"
            try
            {
                string hex = s.Substring(2, 6);
                double a = s.Length > 9 ? double.Parse(s.Substring(9), Inv) : 1;
                return System.Windows.Media.Color.FromArgb((byte)Math.Round(a * 255),
                    Convert.ToByte(hex.Substring(0, 2), 16), Convert.ToByte(hex.Substring(2, 2), 16), Convert.ToByte(hex.Substring(4, 2), 16));
            }
            catch (Exception) { return System.Windows.Media.Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF); }
        }

        public void Dispose()
        {
            lock (_gate) { _disposed = true; Monitor.PulseAll(_gate); }
            try { if (!_p.HasExited) _p.Kill(); } catch (Exception) { }
            _th.Join(2000);
            _p.Dispose();
        }
    }
}
