using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace Tk2Gui
{
    // ============================================================
    //  走行動画の音声から、エンジンの回転数とアクセルの ON / OFF を求める
    //  (レーシングカート KT100 のような、変速機・遠心クラッチの無い直結の 2 スト単気筒を想定)
    //
    //  ・音声 (モノラル 16 kHz) を 0.05 秒ごとに FFT (4096 点, ハン窓)
    //  ・帯域ごとに時間方向の中央値を引いた対数スペクトルで、基本周波数 f0 の候補ごとに
    //    「倍音 (1〜12 倍) の平均 − 倍音の間の谷の平均」= 倍音らしさ を求める (半分・倍の取り違えに強い)
    //  ・直結なので f0 = 係数 × 速度。係数は、速度 20 km/h 以上のコマで倍音らしさの合計が最大になる値
    //  ・音から求める f0 は、係数 × 速度 の ±30 % の中で最も倍音らしい所 (後輪の空転・スライドでずれる)
    //  ・回転数 = f0 × 60 (1 回転に 1 回爆発する 2 スト単気筒)
    //  ・アクセル: 倍音らしさ (0.5 秒でならす) が、GPS で加速中と減速中のコマの平均の中間より上なら ON
    // ============================================================
    public static class EngineAnalyzer
    {
        public const int Rate = 16000, N = 4096, Hop = 800;     // Hop / Rate = 0.05 秒
        public const double Dt = (double)Hop / Rate;
        public const double CandMin = 30, CandMax = 260, CandStep = 0.5;
        const int K = 12;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>解析の途中結果 (確認の図を描くのに使う)</summary>
        public sealed class Work
        {
            public float[][] Spec;        // [コマ][ビン] 対数スペクトル (中央値を引いたもの, 0〜1200 Hz)
            public double BinHz;
            public EngineStore.Segment Seg;
            public double HzPerKmh;
            public double[] Speed;        // コマごとの GPS 速度 [km/h]
        }

        /// <summary>
        /// 動画 video (0 秒 = ログの t0) の、ログの [from, to] の範囲を解析する。speedAt はログの時刻 → km/h
        /// </summary>
        public static Work Analyze(string ffmpeg, string video, double t0, double from, double to, Func<double, double> speedAt,
                                   Action<string, double> progress, CancellationToken ct)
        {
            return Analyze(ffmpeg, video, t0, from, to, speedAt, null, progress, ct);
        }

        /// <summary>
        /// car が変速機付き (IsGeared) なら、速度に縛らずに音の線を時間に沿ってたどる (ギア・ブリッピング・空転でずれるため)。
        /// 回転数 = 音の基本周波数 × 60 / (1 回転の爆発の数)。アクセルは求めない (車内の音では当てにならない)
        /// </summary>
        public static Work Analyze(string ffmpeg, string video, double t0, double from, double to, Func<double, double> speedAt,
                                   EngineStore.FileData car, Action<string, double> progress, CancellationToken ct)
        {
            bool geared = car != null && car.IsGeared;
            double ppr = geared ? car.PulsesPerRev : 1;
            double cMin = geared ? Math.Max(15, 700 * ppr / 60) : CandMin;
            double cMax = geared ? Math.Max(car.TachoMaxOrDefault, 8000) * 1.08 * ppr / 60 : CandMax;
            double a = Math.Max(0, from - t0), len = to - t0 - a;
            if (len < 5) throw new InvalidOperationException("動画とセッションの重なりが短すぎます");
            progress("音声を読み込み中...", 0);
            short[] pcm = ReadMono(ffmpeg, video, a, len, ct);
            int frames = Math.Max(0, (pcm.Length - N) / Hop + 1);
            if (frames < 20) throw new InvalidOperationException("音声がありません");
            double binHz = (double)Rate / N;
            // 倍音を見る上限 (カートは従来どおり 2000 Hz、変速機付きは候補の上限 × 12.5 倍まで)。表示は 1200 Hz まで
            int nb = (int)((geared ? Math.Min(Rate / 2 - 100, cMax * (K + 0.5) + 20) : 2000) / binHz);

            // ---- スペクトル ----
            float[][] L = new float[frames][];
            double[] win = new double[N];
            for (int i = 0; i < N; i++) win[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (N - 1));
            double[] re = new double[N], im = new double[N];
            for (int f = 0; f < frames; f++)
            {
                if ((f & 255) == 0) { ct.ThrowIfCancellationRequested(); progress("周波数を解析中...", 0.1 + 0.4 * f / frames); }
                int o = f * Hop;
                for (int i = 0; i < N; i++) { re[i] = pcm[o + i] / 32768.0 * win[i]; im[i] = 0; }
                Fft(re, im);
                float[] row = new float[nb];
                for (int b = 0; b < nb; b++) row[b] = (float)Math.Log(Math.Sqrt(re[b] * re[b] + im[b] * im[b]) + 1e-6);
                L[f] = row;
            }
            // 帯域ごとに時間方向の中央値を引く (風の音などの定常な雑音を消す)
            float[] col = new float[frames];
            if (!geared)
                for (int b = 0; b < nb; b++)
                {
                    for (int f = 0; f < frames; f++) col[f] = L[f][b];
                    Array.Sort(col);
                    float med = col[frames / 2];
                    for (int f = 0; f < frames; f++) L[f][b] = Math.Max(0, L[f][b] - med);
                }
            else
            {
                // 変速機付き: 中央値は前後 30 秒の中で取る (止まっている長い時間の静かな音で引くと、走っている間の騒音・半分の高さの音が残るため)。
                // 5 秒ごとの区切りで、前後 30 秒を 0.5 秒おきに見た中央値
                int blk = 100, half = 600, stepS = 10;
                int nblk = (frames + blk - 1) / blk;
                float[][] med = new float[nblk][];
                List<float> buf = new List<float>();
                for (int k = 0; k < nblk; k++)
                {
                    int c0 = k * blk + blk / 2, a0 = Math.Max(0, c0 - half), a1 = Math.Min(frames - 1, c0 + half);
                    float[] m = new float[nb];
                    for (int b = 0; b < nb; b++)
                    {
                        buf.Clear();
                        for (int f = a0; f <= a1; f += stepS) buf.Add(L[f][b]);
                        buf.Sort();
                        m[b] = buf[buf.Count / 2];
                    }
                    med[k] = m;
                }
                for (int f = 0; f < frames; f++)
                {
                    float[] m = med[f / blk];
                    for (int b = 0; b < nb; b++) L[f][b] = Math.Max(0, L[f][b] - m[b]);
                }
            }

            // ---- 倍音らしさ ----
            int nc = (int)((cMax - cMin) / CandStep) + 1;
            int[,] hi = new int[nc, K], mi = new int[nc, K];
            for (int c = 0; c < nc; c++)
                for (int k = 0; k < K; k++)
                {
                    double f0 = cMin + c * CandStep;
                    hi[c, k] = Math.Min(nb - 1, (int)Math.Round((k + 1) * f0 / binHz));
                    mi[c, k] = Math.Min(nb - 1, (int)Math.Round((k + 0.5) * f0 / binHz));
                }
            // 倍音の重み: カートは全部同じ。変速機付き (車内のマイク) は高い倍音が弱いので、低い倍音ほど重く (1 / k)。
            // 本当の音の 1/2・1/4 の高さの候補は、自分の基本音の所が空なので点が下がる
            double[] wk = new double[K];
            for (int k = 0; k < K; k++) wk[k] = geared ? GearedWeight(k + 1) : 1;
            double wsum = wk.Sum();
            float[][] H = new float[frames][];
            for (int f = 0; f < frames; f++)
            {
                if ((f & 255) == 0) { ct.ThrowIfCancellationRequested(); progress("倍音を探しています...", 0.5 + 0.3 * f / frames); }
                float[] row = L[f], h = new float[nc];
                for (int c = 0; c < nc; c++)
                {
                    double s = 0;
                    for (int k = 0; k < K; k++) s += wk[k] * (row[hi[c, k]] - row[mi[c, k]]);
                    h[c] = (float)(s / wsum);
                }
                H[f] = h;
            }

            // ---- 速度と係数 ----
            double[] v = new double[frames];
            for (int f = 0; f < frames; f++) v[f] = speedAt(t0 + a + (f * Hop + N / 2.0) / Rate);
            if (geared) return TrackGeared(L, H, v, car, nb, binHz, cMin, ppr, video, t0 + a + N / 2.0 / Rate, progress, ct);
            progress("速度との比を求めています...", 0.85);
            double bestK = double.NaN, bestS = double.NegativeInfinity;
            for (double kk = 1.0; kk <= 6.0; kk += 0.005)
            {
                double s = 0; int n = 0;
                for (int f = 0; f < frames; f++)
                {
                    if (v[f] < 20) continue;
                    double c = (kk * v[f] - CandMin) / CandStep;
                    if (c < 0 || c > nc - 1) continue;
                    s += H[f][(int)Math.Round(c)]; n++;
                }
                if (n > 20 && s > bestS) { bestS = s; bestK = kk; }
            }
            if (double.IsNaN(bestK)) throw new InvalidOperationException("20 km/h 以上で走っている所が音声の範囲にありません");

            // ---- 音から求める回転数 ----
            float[] rpm = new float[frames], hnr = new float[frames];
            for (int f = 0; f < frames; f++)
            {
                double c0 = bestK * v[f];
                if (v[f] < 8) { rpm[f] = float.NaN; hnr[f] = float.NaN; continue; }
                int lo = Math.Max(0, (int)Math.Floor((c0 * 0.7 - CandMin) / CandStep));
                int up = Math.Min(nc - 1, (int)Math.Ceiling((c0 * 1.3 - CandMin) / CandStep));
                if (up <= lo) { rpm[f] = float.NaN; hnr[f] = float.NaN; continue; }
                int arg = lo;
                for (int c = lo; c <= up; c++) if (H[f][c] > H[f][arg]) arg = c;
                rpm[f] = (float)((CandMin + arg * CandStep) * 60);
                hnr[f] = H[f][arg];
            }
            // 5 コマ (0.25 秒) の中央値でならす
            float[] rs = (float[])rpm.Clone();
            for (int f = 0; f < frames; f++)
            {
                List<float> w = new List<float>();
                for (int j = Math.Max(0, f - 2); j <= Math.Min(frames - 1, f + 2); j++) if (!float.IsNaN(rpm[j])) w.Add(rpm[j]);
                if (w.Count >= 3 && !float.IsNaN(rpm[f])) { w.Sort(); rs[f] = w[w.Count / 2]; }
            }
            // 倍音らしさが弱いコマ (音が取れていない) は回転数を出さない
            for (int f = 0; f < frames; f++) if (!(hnr[f] >= 0.25)) rs[f] = float.NaN;

            // ---- アクセル ----
            float[] hs = Smooth(hnr, 10);
            double[] acc = new double[frames];
            for (int f = 0; f < frames; f++)
            {
                double t = t0 + a + (f * Hop + N / 2.0) / Rate;
                acc[f] = (speedAt(t + 0.25) - speedAt(t - 0.25)) / 0.5 / 3.6;
            }
            List<double> on = new List<double>(), off = new List<double>();
            for (int f = 0; f < frames; f++)
            {
                if (v[f] < 25 || float.IsNaN(hs[f])) continue;
                if (acc[f] > 0.5) on.Add(hs[f]); else if (acc[f] < -1) off.Add(hs[f]);
            }
            double thr = on.Count > 20 && off.Count > 20 ? (on.Average() + off.Average()) / 2
                       : hs.Where(x => !float.IsNaN(x)).DefaultIfEmpty(0).Average();
            sbyte[] thrl = new sbyte[frames];
            for (int f = 0; f < frames; f++) thrl[f] = (sbyte)(float.IsNaN(hs[f]) ? -1 : hs[f] > thr ? 1 : 0);

            progress("完了", 1);
            EngineStore.Segment seg = new EngineStore.Segment
            {
                Video = video, T0 = t0 + a + N / 2.0 / Rate, Dt = Dt, HzPerKmh = Math.Round(bestK, 4),
                Threshold = Math.Round(thr, 4),
                Rpm = rs.Select(x => float.IsNaN(x) ? 0 : (int)Math.Round(x)).ToArray(),
                Throttle = thrl.Select(x => (int)x).ToArray(),
                Tone = hs.Select(x => float.IsNaN(x) ? 0 : Math.Round(x, 3)).ToArray(),
                OnMean = on.Count > 0 ? Math.Round(on.Average(), 3) : 0, OffMean = off.Count > 0 ? Math.Round(off.Average(), 3) : 0,
            };
            // 表示用に 1200 Hz まで
            int nshow = Math.Min(nb, (int)(1200 / binHz));
            float[][] spec = new float[frames][];
            for (int f = 0; f < frames; f++) { spec[f] = new float[nshow]; Array.Copy(L[f], spec[f], nshow); }
            return new Work { Spec = spec, BinHz = binHz, Seg = seg, HzPerKmh = bestK, Speed = v };
        }

        /// <summary>
        /// 変速機付き: 倍音らしさの高い所を、1 コマ (0.05 秒) で ±Jump の範囲の動きに限ってたどる (動的計画法)。
        /// 跳ぶほど減点 (Lambda × 変化の割合)。音が途切れたあとに離れた所へ移れるよう、どこからでも Reset の減点で始め直せる
        /// </summary>
        public static double Jump = 0.10, Lambda = 3.0, Reset = 1.5;
        static Work TrackGeared(float[][] L, float[][] H, double[] v, EngineStore.FileData car, int nb, double binHz, double cMin, double ppr,
                                string video, double segT0, Action<string, double> progress, CancellationToken ct)
        {
            int frames = H.Length, nc = H[0].Length;
            int[] path;
            if (car.MaxGear > 0 || car.GearRatios == null || car.GearRatios.Length < 2)
                path = Dp(Prior(H, v, car, car.GearRatios != null ? car.TopGear : 0, cMin, ppr), cMin, 0.8, 0.12, progress, ct);
            else
            {
                // 使うギアが「自動」: 1 回目は縛らずにたどり、音がはっきりしている加速中のコマで使っているギアを数える。
                // 一番上のギアが決まったら、それで縛って 2 回目をたどる (6 速の線は 40 km/h で 1,300 rpm と低く、騒音の線と重なるため)
                int[] p1 = Dp(H, cMin, 0.8, 0.06, progress, ct);
                int top = EstimateTopGear(H, v, p1, car, cMin, ppr);
                car.MaxGear = top;
                path = Dp(Prior(H, v, car, top, cMin, ppr), cMin, 0.86, 0.09, progress, ct);
            }

            float[] rpm = new float[frames], hnr = new float[frames];
            for (int f = 0; f < frames; f++)
            {
                rpm[f] = (float)((cMin + path[f] * CandStep) * 60 / ppr);
                hnr[f] = H[f][path[f]];
            }
            float[] rs = (float[])rpm.Clone();
            for (int f = 0; f < frames; f++)
            {
                List<float> w = new List<float>();
                for (int j = Math.Max(0, f - 2); j <= Math.Min(frames - 1, f + 2); j++) w.Add(rpm[j]);
                w.Sort(); rs[f] = w[w.Count / 2];
            }
            float[] hs = Smooth(hnr, 10);
            // 倍音らしさが弱いコマ (音が取れていない) は回転数を出さない
            for (int f = 0; f < frames; f++) if (!(hs[f] >= GearedGate)) rs[f] = float.NaN;

            progress("完了", 1);
            EngineStore.Segment seg = new EngineStore.Segment
            {
                Video = video, T0 = segT0, Dt = Dt, HzPerKmh = 0, Geared = true,
                Rpm = rs.Select(x => float.IsNaN(x) ? 0 : (int)Math.Round(x)).ToArray(),
                Throttle = Enumerable.Repeat(-1, frames).ToArray(),
                Tone = hs.Select(x => float.IsNaN(x) ? 0 : Math.Round(x, 3)).ToArray(),
            };
            int nshow = Math.Min(nb, (int)(1200 / binHz));
            float[][] spec = new float[frames][];
            for (int f = 0; f < frames; f++) { spec[f] = new float[nshow]; Array.Copy(L[f], spec[f], nshow); }
            return new Work { Spec = spec, BinHz = binHz, Seg = seg, HzPerKmh = 0, Speed = v };
        }
        /// <summary>
        /// 加速している間はクラッチがつながっているので、回転数は使うギアのどれかで速度から計算した値の −15 %〜+35 % (空転) の中にある。
        /// その外の候補を減点した H の写しを返す (車内の低い騒音の線や、本当の音の半分の高さの線に移るのを防ぐ)。top が 0 なら H のまま
        /// </summary>
        static float[][] Prior(float[][] H, double[] v, EngineStore.FileData car, int top, double cMin, double ppr)
        {
            if (top <= 0 || car.GearRatios == null) return H;
            int frames = H.Length, nc = H[0].Length;
            double[] e = new double[top];
            for (int g = 1; g <= top; g++) e[g - 1] = car.GearRpmPerKmh(g) * ppr / 60;   // Hz / (km/h)
            float[][] R = (float[][])H.Clone();
            for (int f = 0; f < frames; f++)
            {
                int f0 = Math.Max(0, f - 6), f1 = Math.Min(frames - 1, f + 6);
                if (v[f] < 10 || v[f1] - v[f0] < 0.8) continue;
                float[] row = (float[])H[f].Clone();
                for (int c = 0; c < nc; c++)
                {
                    double fc = cMin + c * CandStep, d = double.PositiveInfinity;
                    for (int g = 0; g < top; g++)
                    {
                        double x = Math.Log(fc / (e[g] * v[f]));
                        double out_ = x < Math.Log(0.85) ? Math.Log(0.85) - x : x > Math.Log(1.35) ? x - Math.Log(1.35) : 0;
                        if (out_ < d) d = out_;
                    }
                    if (d > 0) row[c] -= (float)(PriorPenalty * Math.Min(1, d / 0.3));
                }
                R[f] = row;
            }
            return R;
        }

        /// <summary>
        /// 1 回目にたどった線から、使っている一番上のギアを決める。速度 25 km/h 以上で加速中、音がはっきりしている (倍音らしさが
        /// そのようなコマの上位 50 %) コマを、いちばん近いギアに振り分け、2 秒以上かつ 5 % 以上あるギアのうち一番上
        /// </summary>
        public static int EstimateTopGear(float[][] H, double[] v, int[] path, EngineStore.FileData car, double cMin, double ppr)
        {
            int frames = H.Length, n = car.GearRatios.Length;
            List<int> idx = new List<int>();
            for (int f = 0; f < frames; f++)
            {
                int f0 = Math.Max(0, f - 6), f1 = Math.Min(frames - 1, f + 6);
                if (v[f] >= 25 && v[f1] - v[f0] >= 0.8) idx.Add(f);
            }
            if (idx.Count < 40) return n;
            float[] q = idx.Select(f => H[f][path[f]]).OrderBy(x => x).ToArray();
            float thr = q[q.Length / 2];
            int[] cnt = new int[n + 1]; int tot = 0;
            int saved = car.MaxGear; car.MaxGear = 0;
            foreach (int f in idx)
            {
                if (H[f][path[f]] < thr) continue;
                double rpm = (cMin + path[f] * CandStep) * 60 / ppr;
                cnt[car.GearFor(rpm, v[f])]++; tot++;
            }
            car.MaxGear = saved;
            int top = 1;
            for (int g = 1; g <= n; g++) if (cnt[g] >= 40 && cnt[g] >= tot * 0.05) top = g;
            LastGearCounts = cnt;
            return top;
        }
        public static int[] LastGearCounts;

        /// <summary>動的計画法で音の線をたどり、コマごとの候補の番号を返す</summary>
        static int[] Dp(float[][] H, double cMin, double p0, double pw, Action<string, double> progress, CancellationToken ct)
        {
            int frames = H.Length, nc = H[0].Length;
            float[] prev = new float[nc], cur = new float[nc];
            short[][] from = new short[frames][];
            Array.Copy(H[0], prev, nc);
            from[0] = new short[nc];
            for (int f = 1; f < frames; f++)
            {
                if ((f & 255) == 0) { ct.ThrowIfCancellationRequested(); progress("音の線をたどっています...", p0 + pw * f / frames); }
                int pbest = 0;
                for (int c = 1; c < nc; c++) if (prev[c] > prev[pbest]) pbest = c;
                float resetVal = (float)(prev[pbest] - Reset);
                short[] fr = new short[nc];
                float[] h = H[f];
                for (int c = 0; c < nc; c++)
                {
                    double fc = cMin + c * CandStep;
                    int w = Math.Max(1, (int)(Jump * fc / CandStep));
                    double pen = Lambda * CandStep / fc;
                    int lo = Math.Max(0, c - w), up = Math.Min(nc - 1, c + w);
                    float best = resetVal; int arg = pbest;
                    for (int c2 = lo; c2 <= up; c2++)
                    {
                        float s = (float)(prev[c2] - pen * Math.Abs(c - c2));
                        if (s > best) { best = s; arg = c2; }
                    }
                    cur[c] = best + h[c];
                    fr[c] = (short)arg;
                }
                from[f] = fr;
                // 値が大きくなりすぎないよう、最大値を 0 にそろえる
                float mx = cur.Max();
                for (int c = 0; c < nc; c++) prev[c] = cur[c] - mx;
            }
            int[] path = new int[frames];
            int last = 0;
            for (int c = 1; c < nc; c++) if (prev[c] > prev[last]) last = c;
            path[frames - 1] = last;
            for (int f = frames - 1; f > 0; f--) path[f - 1] = from[f][path[f]];
            return path;
        }

        public static double GearedGate = 0.25, PriorPenalty = 3.0;
        public static Func<int, double> GearedWeight = k => 1.0 / k;

        static float[] Smooth(float[] x, int n)
        {
            float[] r = new float[x.Length];
            for (int i = 0; i < x.Length; i++)
            {
                double s = 0; int c = 0;
                for (int j = Math.Max(0, i - n / 2); j <= Math.Min(x.Length - 1, i + n / 2); j++) if (!float.IsNaN(x[j])) { s += x[j]; c++; }
                r[i] = c > n / 2 && !float.IsNaN(x[i]) ? (float)(s / c) : float.NaN;
            }
            return r;
        }

        static short[] ReadMono(string ffmpeg, string path, double ss, double len, CancellationToken ct)
        {
            string args = string.Format(Inv, "-hide_banner -loglevel error -ss {0} -i \"{1}\" -t {2} -vn -ac 1 -ar {3} -f s16le -",
                ss.ToString("0.###", Inv), path, len.ToString("0.###", Inv), Rate);
            ProcessStartInfo psi = new ProcessStartInfo(ffmpeg, args)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            using (Process p = Process.Start(psi))
            {
                p.ErrorDataReceived += (s, e) => { };
                p.BeginErrorReadLine();
                MemoryStream ms = new MemoryStream();
                byte[] buf = new byte[1 << 16];
                int n;
                Stream st = p.StandardOutput.BaseStream;
                while ((n = st.Read(buf, 0, buf.Length)) > 0)
                {
                    if (ct.IsCancellationRequested) { try { p.Kill(); } catch (Exception) { } ct.ThrowIfCancellationRequested(); }
                    ms.Write(buf, 0, n);
                }
                p.WaitForExit();
                byte[] b = ms.ToArray();
                short[] r = new short[b.Length / 2];
                Buffer.BlockCopy(b, 0, r, 0, r.Length * 2);
                return r;
            }
        }

        /// <summary>基数 2 の FFT (その場で)</summary>
        static void Fft(double[] re, double[] im)
        {
            int n = re.Length;
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j) { double t = re[i]; re[i] = re[j]; re[j] = t; t = im[i]; im[i] = im[j]; im[j] = t; }
            }
            for (int len = 2; len <= n; len <<= 1)
            {
                double ang = -2 * Math.PI / len, wr = Math.Cos(ang), wi = Math.Sin(ang);
                for (int i = 0; i < n; i += len)
                {
                    double cr = 1, ci = 0;
                    for (int j = 0; j < len / 2; j++)
                    {
                        int a = i + j, b = a + len / 2;
                        double xr = re[b] * cr - im[b] * ci, xi = re[b] * ci + im[b] * cr;
                        re[b] = re[a] - xr; im[b] = im[a] - xi;
                        re[a] += xr; im[a] += xi;
                        double t = cr * wr - ci * wi; ci = cr * wi + ci * wr; cr = t;
                    }
                }
            }
        }
    }

    // ============================================================
    //  解析結果の保存: ログ名.engine.json (ログの時刻で持つので、同じ時間を撮った別の動画でも使える)
    // ============================================================
    public static class EngineStore
    {
        public sealed class Segment
        {
            [JsonPropertyName("video")] public string Video { get; set; }         // 解析した動画
            [JsonPropertyName("t0")] public double T0 { get; set; }               // 最初のコマのログの時刻
            [JsonPropertyName("dt")] public double Dt { get; set; }               // コマの間隔 [s]
            [JsonPropertyName("hzPerKmh")] public double HzPerKmh { get; set; }   // この動画で求めた 基本周波数 / 速度
            [JsonPropertyName("threshold")] public double Threshold { get; set; } // アクセル ON とみなす倍音らしさ
            [JsonPropertyName("onMean")] public double OnMean { get; set; }       // GPS で加速中のコマの倍音らしさの平均
            [JsonPropertyName("offMean")] public double OffMean { get; set; }     // 減速中の平均
            [JsonPropertyName("rpm")] public int[] Rpm { get; set; }              // 音から求めた回転数 (0 = 取れていない)
            [JsonPropertyName("throttle")] public int[] Throttle { get; set; }    // 1 = ON, 0 = OFF, -1 = 不明 (止まっている・音が無い)
            [JsonPropertyName("tone")] public double[] Tone { get; set; }         // 倍音らしさ (0.5 秒でならしたもの)
            [JsonPropertyName("geared")] public bool Geared { get; set; }         // 変速機付きとして解析した (音の線をたどった)

            [JsonIgnore] public double EndT { get { return T0 + Dt * (Rpm != null ? Rpm.Length : 0); } }
        }

        public sealed class FileData
        {
            [JsonPropertyName("format")] public string Format { get; set; } = "WintecTK2Engine";
            [JsonPropertyName("version")] public int Version { get; set; } = 1;
            [JsonPropertyName("log")] public string Log { get; set; }
            [JsonPropertyName("engine")] public string Engine { get; set; } = "2-stroke single (1 firing / rev)";
            [JsonPropertyName("rpmPerKmh")] public double RpmPerKmh { get; set; }  // 回転数 = これ × 速度 (直結)
            [JsonPropertyName("manual")] public bool Manual { get; set; }           // 係数を手で直した
            [JsonPropertyName("segments")] public List<Segment> Segments { get; set; } = new List<Segment>();

            // ---- 変速機付きの車 (vehicle = "geared") ----
            [JsonPropertyName("vehicle")] public string Vehicle { get; set; } = "kart";   // kart = 直結のカート / geared = 変速機付き
            [JsonPropertyName("cylinders")] public int Cylinders { get; set; } = 4;       // 4 スト: 1 回転の爆発 = 気筒数 / 2
            [JsonPropertyName("gearRatios")] public double[] GearRatios { get; set; }     // 1 速から順に
            [JsonPropertyName("finalRatio")] public double FinalRatio { get; set; }
            [JsonPropertyName("tireSize")] public string TireSize { get; set; }           // 215/45R17 の形
            [JsonPropertyName("tireCircM")] public double TireCircM { get; set; }         // 走っているときのタイヤ 1 周 [m] (0 = サイズから)
            [JsonPropertyName("maxGear")] public int MaxGear { get; set; }                // 使う一番上のギア (0 = 全部。ジムカーナは 2 など)
            [JsonPropertyName("tachoMax")] public int TachoMax { get; set; }              // 回転計の目盛り (0 = レイアウトの ini の値)
            [JsonPropertyName("tachoYellow")] public int TachoYellow { get; set; }
            [JsonPropertyName("tachoRed")] public int TachoRed { get; set; }

            [JsonIgnore] public bool IsGeared { get { return Vehicle == "geared"; } }
            [JsonIgnore] public double PulsesPerRev { get { return IsGeared ? Math.Max(1, Cylinders) / 2.0 : 1; } }
            [JsonIgnore] public int TopGear { get { int n = GearRatios != null ? GearRatios.Length : 0; return MaxGear > 0 && MaxGear < n ? MaxGear : n; } }
            [JsonIgnore] public int TachoMaxOrDefault { get { return TachoMax > 0 ? TachoMax : 9000; } }

            /// <summary>DC5 インテグラ Type R の値で埋める (ユーザーの車)</summary>
            public void SetGearedDefaults()
            {
                Vehicle = "geared";
                if (Cylinders <= 0) Cylinders = 4;
                if (GearRatios == null || GearRatios.Length == 0) GearRatios = new[] { 3.266, 2.130, 1.517, 1.212, 0.972, 0.780 };
                if (!(FinalRatio > 0)) FinalRatio = 4.764;
                if (string.IsNullOrEmpty(TireSize)) TireSize = "215/45R17";
                if (TachoMax <= 0) { TachoMax = 9000; TachoYellow = 7500; TachoRed = 8200; }
            }

            /// <summary>走っているときのタイヤ 1 周 [m]。サイズからのときは表記の 97 % (荷重でつぶれる分)</summary>
            [JsonIgnore]
            public double TireCirc
            {
                get
                {
                    if (TireCircM > 0) return TireCircM;
                    double d = TireDiameterMm(TireSize);
                    return d > 0 ? Math.PI * d / 1000 * 0.97 : 1.9;
                }
            }

            /// <summary>"215/45R17" → 外径 [mm]</summary>
            public static double TireDiameterMm(string s)
            {
                if (string.IsNullOrWhiteSpace(s)) return 0;
                var m = System.Text.RegularExpressions.Regex.Match(s, @"(\d+)\s*/\s*(\d+)\s*[A-Za-z\- ]*?(\d+(?:\.\d+)?)\s*$");
                if (!m.Success) return 0;
                double w = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), ar = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
                       rim = double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
                return rim * 25.4 + 2 * w * ar / 100;
            }

            /// <summary>g 速 (1 から) の 回転数 / 速度 [rpm / (km/h)]</summary>
            public double GearRpmPerKmh(int g)
            {
                if (GearRatios == null || g < 1 || g > GearRatios.Length) return double.NaN;
                return 1000.0 / 60 / TireCirc * GearRatios[g - 1] * FinalRatio;
            }

            /// <summary>
            /// 音の回転数と速度から、いちばん近いギア (0 = 分からない)。空転で回転数は上に出るので、上へのずれは軽く見る
            /// (2 速の空転を 1 速と取り違えにくくする)。どのギアからも 40 % 以上離れていればクラッチを切っているなどとみなして 0。
            /// アイドルに近い回転数 (ClutchRpm 未満) も 0: サイドターンなどでクラッチを切ると 1,000〜1,500 rpm に下がり、
            /// 低い速度で割ると 2 速の比にたまたま近くなるため
            /// </summary>
            public int GearFor(double rpm, double kmh)
            {
                if (!IsGeared || GearRatios == null || double.IsNaN(rpm) || rpm < ClutchRpm || kmh < 8) return 0;
                double r = rpm / kmh, best = double.PositiveInfinity; int bg = 0;
                for (int g = 1; g <= TopGear; g++)
                {
                    double e = GearRpmPerKmh(g), d = Math.Log(r / e);
                    double cost = d > 0 ? d * 0.6 : -d;
                    if (cost < best) { best = cost; bg = g; }
                }
                return best < Math.Log(1.4) ? bg : 0;
            }

            /// <summary>これより低い回転数はクラッチを切っている (アイドル付近) とみなし、ギアを決めない [rpm]</summary>
            public static double ClutchRpm = 2000;
            /// <summary>ギアが決まらない所は、この秒数以内に次に決まるギアを先に出す (サイドターン中は次の 1 速に入れて待っている)</summary>
            public static double GearFillAheadSec = 4;

            /// <summary>
            /// 時刻 t のギア。前後 0.5 秒 (0.1 秒ごと) の多数決でちらつきを抑える。speedAt はログの時刻 → km/h。
            /// 決まらないときは GearFillAheadSec 秒先までで次に決まるギア (filled = true)。それも無ければ 0
            /// </summary>
            public int GearAt(double t, Func<double, double> speedAt)
            {
                bool filled;
                return GearAt(t, speedAt, out filled);
            }

            public int GearAt(double t, Func<double, double> speedAt, out bool filled)
            {
                filled = false;
                int g = GearVote(t, speedAt);
                if (g > 0) return g;
                for (double dt = 0.2; dt <= GearFillAheadSec + 1e-9; dt += 0.2)
                {
                    g = GearVote(t + dt, speedAt);
                    if (g > 0) { filled = true; return g; }
                }
                return 0;
            }

            int GearVote(double t, Func<double, double> speedAt)
            {
                if (!IsGeared) return 0;
                int[] cnt = new int[(GearRatios != null ? GearRatios.Length : 0) + 1];
                for (int j = -5; j <= 5; j++)
                {
                    double tt = t + j * 0.1;
                    cnt[GearFor(AudioRpmAt(tt), speedAt(tt))]++;
                }
                int bg = 0, bc = 3;    // 11 点のうち 4 点以上で決まったギア
                for (int g = 1; g < cnt.Length; g++) if (cnt[g] > bc) { bc = cnt[g]; bg = g; }
                return bg;
            }

            /// <summary>ログの時刻 t のアクセル (1 / 0 / -1)</summary>
            public int ThrottleAt(double t)
            {
                foreach (Segment s in Segments)
                {
                    if (t < s.T0 || t >= s.EndT || s.Throttle == null) continue;
                    int i = (int)((t - s.T0) / s.Dt);
                    if (i >= 0 && i < s.Throttle.Length) return s.Throttle[i];
                }
                return -1;
            }

            /// <summary>ログの時刻 t の、音から求めた回転数 (無ければ NaN)</summary>
            public double AudioRpmAt(double t)
            {
                foreach (Segment s in Segments)
                {
                    if (t < s.T0 || t >= s.EndT) continue;
                    double x = (t - s.T0) / s.Dt;
                    int i = (int)x;
                    if (i < 0 || i >= s.Rpm.Length || s.Rpm[i] <= 0) continue;
                    // 変速機付きは回転計に直接出すので、コマの間をつないで滑らかにする
                    if (s.Geared && i + 1 < s.Rpm.Length && s.Rpm[i + 1] > 0) return s.Rpm[i] + (s.Rpm[i + 1] - s.Rpm[i]) * (x - i);
                    return s.Rpm[i];
                }
                return double.NaN;
            }
        }

        static readonly JsonSerializerOptions Opt = new JsonSerializerOptions { WriteIndented = false };

        public static string SidecarPath(string log) { return log + ".engine.json"; }

        /// <summary>車の種類と設定だけを写す</summary>
        public static void CopyCar(FileData src, FileData dst)
        {
            dst.Vehicle = src.Vehicle; dst.Cylinders = src.Cylinders;
            dst.GearRatios = src.GearRatios == null ? null : (double[])src.GearRatios.Clone();
            dst.FinalRatio = src.FinalRatio; dst.TireSize = src.TireSize; dst.TireCircM = src.TireCircM;
            dst.MaxGear = src.MaxGear; dst.TachoMax = src.TachoMax; dst.TachoYellow = src.TachoYellow; dst.TachoRed = src.TachoRed;
        }

        public static FileData Load(string log)
        {
            try
            {
                string p = SidecarPath(log);
                if (log != null && File.Exists(p)) return JsonSerializer.Deserialize<FileData>(File.ReadAllText(p), Opt);
            }
            catch (Exception) { }
            return null;
        }

        public static void Save(string log, FileData d)
        {
            d.Log = Path.GetFileName(log);
            File.WriteAllText(SidecarPath(log), JsonSerializer.Serialize(d, Opt));
        }

        /// <summary>解析結果を足す (同じ動画の結果は置き換え)。係数は手で直していなければ、全部の結果の重み付き平均</summary>
        public static FileData Put(string log, Segment seg)
        {
            return Put(log, seg, null);
        }

        /// <summary>car があれば、車の種類と設定 (ギア比など) をそれに合わせてから足す</summary>
        public static FileData Put(string log, Segment seg, FileData car)
        {
            FileData d = Load(log) ?? new FileData();
            if (car != null) CopyCar(car, d);
            d.Segments.RemoveAll(s => string.Equals(s.Video, seg.Video, StringComparison.OrdinalIgnoreCase)
                                      || (s.T0 < seg.EndT && s.EndT > seg.T0));
            d.Segments.Add(seg);
            d.Segments.Sort((x, y) => x.T0.CompareTo(y.T0));
            if (!d.Manual && !d.IsGeared)
            {
                double w = d.Segments.Sum(s => s.EndT - s.T0);
                d.RpmPerKmh = Math.Round(d.Segments.Sum(s => s.HzPerKmh * 60 * (s.EndT - s.T0)) / w, 2);
            }
            Save(log, d);
            return d;
        }
    }
}
