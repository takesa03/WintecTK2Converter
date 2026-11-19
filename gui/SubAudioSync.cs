using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace Tk2Gui
{
    // =====================================================================
    //  サブ動画 (別のカメラ) とメインの動画の時刻のずれを、音声で求める。設計は docs/subvideo_design.md の 5 章
    //  車内のエンジン音・ロードノイズは両方のマイクに入るので、音の大きさの変化 (包絡) の形を比べる:
    //    8 kHz モノラル → 差分の絶対値 (低い音を弱める) → 10 ms ごとの平均 → log → 1 秒の移動平均を引く
    //  メインの今の位置の前後 (既定 ±60 秒) を、サブの「今のずれ」の前後 (既定 ±5 分) の中で探す (FFT の相互相関)。
    //  相関はサブの側の窓のエネルギーで割り、相関係数に近い値 (r) にする。候補は 0.5 秒以上離れた山を 3 つまで
    // =====================================================================
    public static class SubAudioSync
    {
        public const int Rate = 100;            // 包絡の 1 秒あたりの点数 (10 ms)
        const int AudioRate = 8000;

        public sealed class Candidate
        {
            public double Offset;               // サブの時刻 = メインの時刻 + Offset
            public double R;                    // 相関 (0〜1 くらい。高いほど形が合う)
            public double Z;                    // 際立ち (山の高さ ÷ 全体のばらつき。10 以上ならまず確か)
            public override string ToString()
            {
                return string.Format(CultureInfo.InvariantCulture, "{0:+0.000;-0.000} 秒   相関 {1:0.00}  際立ち {2:0.0}", Offset, R, Z);
            }
        }

        /// <summary>
        /// メインの mainT (秒) の前後 win 秒の音声を、サブの (mainT + guess) の前後 range 秒の中で探す。
        /// 見つからなければ空のリスト
        /// </summary>
        public static List<Candidate> Find(string ffmpeg, string mainPath, double mainDur, double mainT,
                                           string subPath, double subDur, double guess, double win, double range,
                                           CancellationToken ct)
        {
            // メインの窓
            double m0 = Math.Max(0, mainT - win), m1 = Math.Min(mainDur, mainT + win);
            if (m1 - m0 < 2 * win) { m0 = Math.Max(0, m1 - 2 * win); m1 = Math.Min(mainDur, m0 + 2 * win); }
            // サブの探す範囲 (メインの窓がどこにずれても入るように)
            double s0 = Math.Max(0, m0 + guess - range), s1 = Math.Min(subDur, m1 + guess + range);
            if (m1 - m0 < 5 || s1 - s0 < m1 - m0) return new List<Candidate>();

            double[] m = Envelope(ffmpeg, mainPath, m0, m1 - m0, ct);
            ct.ThrowIfCancellationRequested();
            double[] s = Envelope(ffmpeg, subPath, s0, s1 - s0, ct);
            ct.ThrowIfCancellationRequested();
            if (m.Length < Rate * 5 || s.Length < m.Length) return new List<Candidate>();

            double[] c = Correlate(m, s);       // c[k] = Σ m[i] s[i + k]  (k = 0 .. s.Length - m.Length)
            int nk = s.Length - m.Length + 1;

            // サブの側の窓のエネルギーで割る (音の大きい所ばかり選ばないように)
            double mm = Math.Sqrt(m.Sum(x => x * x));
            double[] cum = new double[s.Length + 1];
            for (int i = 0; i < s.Length; i++) cum[i + 1] = cum[i] + s[i] * s[i];
            double[] r = new double[nk];
            for (int k = 0; k < nk; k++)
            {
                double e = Math.Sqrt(Math.Max(0, cum[k + m.Length] - cum[k]));
                r[k] = e > 1e-9 && mm > 1e-9 ? c[k] / (mm * e) : 0;
            }
            double mean = r.Average(), sd = Math.Sqrt(r.Select(x => (x - mean) * (x - mean)).Average());

            List<Candidate> list = new List<Candidate>();
            bool[] used = new bool[nk];
            int sep = Rate / 2;                 // 0.5 秒以上離れた山
            for (int n = 0; n < 3; n++)
            {
                int best = -1;
                for (int k = 0; k < nk; k++) if (!used[k] && (best < 0 || r[k] > r[best])) best = k;
                if (best < 0 || r[best] <= 0) break;
                for (int k = Math.Max(0, best - sep); k <= Math.Min(nk - 1, best + sep); k++) used[k] = true;
                // 山の頂点を放物線で 10 ms より細かく
                double frac = 0;
                if (best > 0 && best < nk - 1)
                {
                    double a = r[best - 1], b = r[best], d = r[best + 1], den = a - 2 * b + d;
                    if (Math.Abs(den) > 1e-12) frac = Math.Max(-0.5, Math.Min(0.5, 0.5 * (a - d) / den));
                }
                double lag = (best + frac) / Rate;
                list.Add(new Candidate { Offset = s0 + lag - m0, R = r[best], Z = sd > 1e-12 ? (r[best] - mean) / sd : 0 });
            }
            return list;
        }

        /// <summary>start 秒から dur 秒の音声の包絡 (Rate 点 / 秒)。音声が無ければ 0 の列</summary>
        public static double[] Envelope(string ffmpeg, string path, double start, double dur, CancellationToken ct)
        {
            int n = Math.Max(0, (int)Math.Floor(dur * Rate));
            double[] env = new double[n];
            int per = AudioRate / Rate;
            string args = string.Format(CultureInfo.InvariantCulture,
                "-hide_banner -loglevel error -ss {0} -i {1} -t {2} -vn -sn -ac 1 -ar {3} -f s16le -",
                FfVideo.S(start), FfVideo.Q(path), FfVideo.S(dur), AudioRate);
            ProcessStartInfo psi = new ProcessStartInfo(ffmpeg, args)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            using (Process p = Process.Start(psi))
            {
                p.ErrorDataReceived += (s, e) => { };
                p.BeginErrorReadLine();
                Stream st = p.StandardOutput.BaseStream;
                byte[] tmp = new byte[65536];
                int carry = -1, prev = 0, cnt = 0, idx = 0;
                double acc = 0;
                bool first = true;
                while (idx < n)
                {
                    if (ct.IsCancellationRequested) { try { p.Kill(); } catch (Exception) { } break; }
                    int got = st.Read(tmp, 0, tmp.Length);
                    if (got <= 0) break;
                    int i = 0;
                    while (i < got && idx < n)
                    {
                        int v;
                        if (carry >= 0) { v = (short)(carry | (tmp[i] << 8)); carry = -1; i++; }
                        else if (i + 1 < got) { v = (short)(tmp[i] | (tmp[i + 1] << 8)); i += 2; }
                        else { carry = tmp[i]; i++; continue; }
                        if (!first) acc += Math.Abs(v - prev);
                        first = false; prev = v;
                        if (++cnt == per) { env[idx++] = Math.Log(1 + acc / per); acc = 0; cnt = 0; }
                    }
                }
                try { if (!p.HasExited) p.Kill(); } catch (Exception) { }
                p.WaitForExit(3000);
            }
            // 1 秒の移動平均を引く (音の大きさの、ゆっくりした変化を除く)
            int h = Rate / 2;
            double[] cum = new double[n + 1];
            for (int i = 0; i < n; i++) cum[i + 1] = cum[i] + env[i];
            double[] o = new double[n];
            for (int i = 0; i < n; i++)
            {
                int a = Math.Max(0, i - h), b = Math.Min(n, i + h + 1);
                o[i] = env[i] - (cum[b] - cum[a]) / (b - a);
            }
            return o;
        }

        /// <summary>c[k] = Σ m[i] s[i + k] (k = 0 .. s.Length - m.Length)。FFT で</summary>
        static double[] Correlate(double[] m, double[] s)
        {
            int len = 1;
            while (len < s.Length + m.Length) len <<= 1;
            double[] ar = new double[len], ai = new double[len], br = new double[len], bi = new double[len];
            Array.Copy(s, ar, s.Length);
            Array.Copy(m, br, m.Length);
            EngineAnalyzer.Fft(ar, ai);
            EngineAnalyzer.Fft(br, bi);
            // S · conj(M)
            for (int i = 0; i < len; i++)
            {
                double re = ar[i] * br[i] + ai[i] * bi[i], im = ai[i] * br[i] - ar[i] * bi[i];
                ar[i] = re; ai[i] = -im;        // 逆変換は 共役 → FFT → 共役
            }
            EngineAnalyzer.Fft(ar, ai);
            int nk = s.Length - m.Length + 1;
            double[] c = new double[nk];
            for (int k = 0; k < nk; k++) c[k] = ar[k] / len;
            return c;
        }
    }
}
