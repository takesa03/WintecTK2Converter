using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Tk2Gui
{
    // =====================================================================
    //  音声の出力 (Windows 標準の winmm waveOut を直接使う。追加のライブラリは不要)
    //  48 kHz ステレオ 16 bit。小さなバッファ (既定 40 ms × 4 枚) を順に満たして送る。
    //  中身は fill(buf, frames) で毎回作ってもらう (無音にしたいときは 0 で埋める)
    // =====================================================================
    public sealed class WaveOutPlayer : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        struct WAVEFORMATEX
        {
            public ushort wFormatTag, nChannels;
            public uint nSamplesPerSec, nAvgBytesPerSec;
            public ushort nBlockAlign, wBitsPerSample, cbSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct WAVEHDR
        {
            public IntPtr lpData;
            public uint dwBufferLength, dwBytesRecorded;
            public IntPtr dwUser;
            public uint dwFlags, dwLoops;
            public IntPtr lpNext, reserved;
        }

        [DllImport("winmm.dll")] static extern int waveOutOpen(out IntPtr hwo, int uDeviceID, ref WAVEFORMATEX fmt, IntPtr cb, IntPtr inst, int flags);
        [DllImport("winmm.dll")] static extern int waveOutPrepareHeader(IntPtr hwo, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutUnprepareHeader(IntPtr hwo, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutWrite(IntPtr hwo, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutReset(IntPtr hwo);
        [DllImport("winmm.dll")] static extern int waveOutClose(IntPtr hwo);

        const int WHDR_DONE = 1;
        const int WAVE_MAPPER = -1;

        readonly int _rate, _frames;
        readonly Action<short[], int> _fill;
        IntPtr _hwo;
        readonly IntPtr[] _hdr, _data;
        readonly bool[] _busy;
        readonly short[] _buf;
        readonly Thread _thread;
        volatile bool _stop;
        static readonly int HdrSize = Marshal.SizeOf(typeof(WAVEHDR));
        static readonly int FlagsOfs = (int)Marshal.OffsetOf(typeof(WAVEHDR), "dwFlags");

        /// <summary>出力の遅れ (送ってから聞こえるまで) の目安 [s]</summary>
        public double Latency { get { return (double)_frames * _hdr.Length / _rate; } }

        public WaveOutPlayer(int rate, Action<short[], int> fill, int bufferMs = 40, int buffers = 4)
        {
            _rate = rate; _fill = fill;
            _frames = rate * bufferMs / 1000;
            WAVEFORMATEX f = new WAVEFORMATEX
            {
                wFormatTag = 1, nChannels = 2, nSamplesPerSec = (uint)rate, wBitsPerSample = 16,
                nBlockAlign = 4, nAvgBytesPerSec = (uint)rate * 4, cbSize = 0
            };
            int r = waveOutOpen(out _hwo, WAVE_MAPPER, ref f, IntPtr.Zero, IntPtr.Zero, 0);
            if (r != 0) throw new InvalidOperationException("音声の出力を開けません (waveOutOpen " + r + ")");
            _hdr = new IntPtr[buffers]; _data = new IntPtr[buffers]; _busy = new bool[buffers];
            _buf = new short[_frames * 2];
            for (int i = 0; i < buffers; i++)
            {
                _data[i] = Marshal.AllocHGlobal(_frames * 4);
                _hdr[i] = Marshal.AllocHGlobal(HdrSize);
                WAVEHDR h = new WAVEHDR { lpData = _data[i], dwBufferLength = (uint)(_frames * 4) };
                Marshal.StructureToPtr(h, _hdr[i], false);
                waveOutPrepareHeader(_hwo, _hdr[i], HdrSize);
            }
            _thread = new Thread(Loop) { IsBackground = true, Name = "WaveOut", Priority = ThreadPriority.AboveNormal };
            _thread.Start();
        }

        void Loop()
        {
            while (!_stop)
            {
                bool wrote = false;
                for (int i = 0; i < _hdr.Length && !_stop; i++)
                {
                    if (_busy[i] && (Marshal.ReadInt32(_hdr[i], FlagsOfs) & WHDR_DONE) == 0) continue;
                    try { _fill(_buf, _frames); } catch (Exception) { Array.Clear(_buf, 0, _buf.Length); }
                    Marshal.Copy(_buf, 0, _data[i], _buf.Length);
                    waveOutWrite(_hwo, _hdr[i], HdrSize);
                    _busy[i] = true;
                    wrote = true;
                }
                if (!wrote) Thread.Sleep(4);
            }
        }

        public void Dispose()
        {
            if (_stop) return;
            _stop = true;
            _thread.Join(500);
            waveOutReset(_hwo);
            for (int i = 0; i < _hdr.Length; i++)
            {
                waveOutUnprepareHeader(_hwo, _hdr[i], HdrSize);
                Marshal.FreeHGlobal(_hdr[i]); Marshal.FreeHGlobal(_data[i]);
            }
            waveOutClose(_hwo);
        }
    }
}
