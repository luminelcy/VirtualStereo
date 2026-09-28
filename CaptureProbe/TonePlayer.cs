// waveOut 发声器（纯 P/Invoke）：给 CaptureProbe 自测用——
// 自己放正弦音 + 捕获自己进程树，即可端到端验证捕获链路与"静音是否杀捕获"。
using System;
using System.Runtime.InteropServices;

namespace CaptureProbe
{
    internal sealed class TonePlayer : IDisposable
    {
        private const int WAVE_MAPPER = -1;
        private const int WAVE_FORMAT_IEEE_FLOAT = 3;
        private const int CALLBACK_NULL = 0;

        [StructLayout(LayoutKind.Sequential)]
        private struct WaveFormat
        {
            public ushort FormatTag;
            public ushort Channels;
            public uint SamplesPerSec;
            public uint AvgBytesPerSec;
            public ushort BlockAlign;
            public ushort BitsPerSample;
            public ushort Size;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WaveHeader
        {
            public IntPtr Data;
            public uint BufferLength;
            public uint BytesRecorded;
            public IntPtr User;
            public uint Flags;
            public uint Loops;
            public IntPtr Next;
            public IntPtr Reserved;
        }

        [DllImport("winmm.dll")]
        private static extern int waveOutOpen(out IntPtr hwo, int deviceId, ref WaveFormat fmt, IntPtr callback, IntPtr instance, uint flags);

        [DllImport("winmm.dll")]
        private static extern int waveOutPrepareHeader(IntPtr hwo, ref WaveHeader header, uint size);

        [DllImport("winmm.dll")]
        private static extern int waveOutWrite(IntPtr hwo, ref WaveHeader header, uint size);

        [DllImport("winmm.dll")]
        private static extern int waveOutUnprepareHeader(IntPtr hwo, ref WaveHeader header, uint size);

        [DllImport("winmm.dll")]
        private static extern int waveOutGetPosition(IntPtr hwo, ref MmTime time, uint size);

        [DllImport("winmm.dll")]
        private static extern int waveOutClose(IntPtr hwo);

        [StructLayout(LayoutKind.Sequential)]
        private struct MmTime
        {
            public uint Type; // 2 = TIME_SAMPLES
            public uint Samples;
            public uint Pad1;
            public uint Pad2;
        }

        /// <summary>当前播放位置（采样数），用于确认发声器还活着。</summary>
        public long PositionSamples
        {
            get
            {
                if (_hwo == IntPtr.Zero) return -1;
                var t = new MmTime { Type = 2 };
                if (waveOutGetPosition(_hwo, ref t, (uint)Marshal.SizeOf<MmTime>()) != 0) return -1;
                return t.Samples;
            }
        }

        private IntPtr _hwo;
        private GCHandle _bufHandle;
        private WaveHeader _header;
        private bool _headerPrepared;

        public TonePlayer(int sampleRate, double freq, double seconds)
        {
            var fmt = new WaveFormat
            {
                FormatTag = WAVE_FORMAT_IEEE_FLOAT,
                Channels = 2,
                SamplesPerSec = (uint)sampleRate,
                BitsPerSample = 32,
                BlockAlign = 8,
                AvgBytesPerSec = (uint)(sampleRate * 8),
                Size = 0,
            };
            int hr = waveOutOpen(out _hwo, WAVE_MAPPER, ref fmt, IntPtr.Zero, IntPtr.Zero, CALLBACK_NULL);
            if (hr != 0)
                throw new InvalidOperationException($"waveOutOpen 失败: {hr}");

            int frames = (int)(sampleRate * seconds);
            var buf = new float[frames * 2];
            for (int i = 0; i < frames; i++)
            {
                float v = (float)(0.25 * Math.Sin(2 * Math.PI * freq * i / sampleRate));
                buf[i * 2] = v;
                buf[i * 2 + 1] = v;
            }
            _bufHandle = GCHandle.Alloc(buf, GCHandleType.Pinned);
            _header = new WaveHeader
            {
                Data = _bufHandle.AddrOfPinnedObject(),
                BufferLength = (uint)(buf.Length * sizeof(float)),
            };
            hr = waveOutPrepareHeader(_hwo, ref _header, (uint)Marshal.SizeOf<WaveHeader>());
            if (hr != 0)
                throw new InvalidOperationException($"waveOutPrepareHeader 失败: {hr}");
            _headerPrepared = true;
            hr = waveOutWrite(_hwo, ref _header, (uint)Marshal.SizeOf<WaveHeader>());
            if (hr != 0)
                throw new InvalidOperationException($"waveOutWrite 失败: {hr}");
        }

        public void Dispose()
        {
            if (_hwo != IntPtr.Zero)
            {
                if (_headerPrepared)
                {
                    try { waveOutUnprepareHeader(_hwo, ref _header, (uint)Marshal.SizeOf<WaveHeader>()); } catch { }
                }
                try { waveOutClose(_hwo); } catch { }
                _hwo = IntPtr.Zero;
            }
            if (_bufHandle.IsAllocated)
                _bufHandle.Free();
        }
    }
}
