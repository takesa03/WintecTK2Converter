using System;
using System.Collections.Generic;
using System.Threading;

namespace Tk2Gui
{
    // ============================================================
    //  重ねる表示のフレームを、複数のスレッドで先に描いておき、番号の順に取り出す
    //  (表示を焼き込んだ切り出しで、ffmpeg へ順に流すため)。
    //  描画器 (OverlayRenderer) は WPF の部品なので、スレッドごとに STA で作る
    // ============================================================
    public sealed class OverlayFrameSource : IDisposable
    {
        readonly byte[][] _ready;
        readonly object _gate = new object();
        readonly CancellationTokenSource _stop;
        readonly List<Thread> _threads = new List<Thread>();
        readonly int _ahead;
        int _taken;
        Exception _err;

        public int Total { get; private set; }

        /// <param name="makeRenderer">描画器を作る (各スレッドの中で呼ぶ)</param>
        /// <param name="stateAt">フレーム番号 → 表示の状態 (描画器の TrailFrames を使えるように描画器も渡す)</param>
        public OverlayFrameSource(Func<OverlayRenderer> makeRenderer, Func<OverlayRenderer, int, OverlayState> stateAt, int total, CancellationToken cancel)
        {
            Total = total;
            _ready = new byte[total][];
            _stop = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            // ffmpeg の H.264 圧縮も CPU を使うので、描画は論理コアの半分 (最低 1)
            int workers = Math.Max(1, Environment.ProcessorCount / 2);
            _ahead = workers * 3;
            for (int k = 0; k < workers; k++)
            {
                int id = k;
                Thread th = new Thread(() =>
                {
                    try
                    {
                        OverlayRenderer r = makeRenderer();
                        for (int n = id; n < total; n += workers)
                        {
                            lock (_gate)
                                while (n - _taken >= _ahead && !_stop.IsCancellationRequested) Monitor.Wait(_gate, 200);
                            _stop.Token.ThrowIfCancellationRequested();
                            byte[] px = (byte[])r.RenderBgra(stateAt(r, n)).Clone();   // 描画器の配列は次のフレームで上書きされる
                            lock (_gate) { _ready[n] = px; Monitor.PulseAll(_gate); }
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { lock (_gate) { if (_err == null) _err = ex; } _stop.Cancel(); }
                    finally { lock (_gate) Monitor.PulseAll(_gate); }
                });
                th.SetApartmentState(ApartmentState.STA);
                th.IsBackground = true;
                _threads.Add(th);
            }
            foreach (Thread th in _threads) th.Start();
        }

        /// <summary>n 番目のフレーム (BGRA, 乗算なし)。番号の順に 1 回ずつ呼ぶこと</summary>
        public byte[] Get(int n)
        {
            lock (_gate)
            {
                byte[] px;
                while ((px = _ready[n]) == null)
                {
                    if (_err != null) throw _err;
                    _stop.Token.ThrowIfCancellationRequested();
                    Monitor.Wait(_gate, 200);
                }
                _ready[n] = null;
                _taken = n + 1;
                Monitor.PulseAll(_gate);
                return px;
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            lock (_gate) Monitor.PulseAll(_gate);
            foreach (Thread th in _threads) th.Join();
        }
    }
}
