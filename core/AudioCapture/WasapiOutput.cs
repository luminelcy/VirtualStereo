// WASAPI shared 模式播放输出（mod 用）：**不走 Unity 的 AudioSource/AudioClip**，
// 由我们自己的 DSP 线程按设备可写帧数取数据。
//
// 为什么要有它：原来的做法是把 DSP 结果写进环形 AudioClip、靠主线程每帧 SetData 喂，
// 于是音频实时性被游戏帧率绑架——帧率一掉，播放头就追上写指针（欠载/切片乱跳），
// 音频结束时更明显。改成自建输出后，节奏由**声卡**决定，与帧率彻底解耦。
//
// 用法（在 DSP 线程里）：
//     if (out.FreeFrames >= Block) { ...算好一块...; out.Write(block, Block); }
// 请求 48k 立体声 float32，AUTOCONVERTPCM 让引擎自己转换。
// 复用 core 的 COM 互操作（IMMDevice/IAudioClient/WaveFormat/HResult/Mta），另补 IAudioRenderClient。
using System;
using System.Runtime.InteropServices;

namespace VirtualStereo.Capture
{
    [ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioRenderClient
    {
        [PreserveSig] int GetBuffer(uint numFramesRequested, out IntPtr data);
        [PreserveSig] int ReleaseBuffer(uint numFramesWritten, uint flags);
    }

    /// <summary>自建播放输出。Open 在调用线程完成（内部走 MTA），之后 Write 由音频线程调用。</summary>
    public sealed class WasapiOutput : IDisposable
    {
        public int SampleRate { get; private set; }
        public int BufferFrames { get; private set; }
        public string LastError { get; private set; }

        /// <summary>写不进去的次数（设备缓冲不够或 GetBuffer 失败）——设备侧欠载。</summary>
        public int Underruns { get; private set; }

        private IAudioClient _client;
        private IAudioRenderClient _render;

        /// <summary>设备缓冲当前还能写多少帧。</summary>
        public int FreeFrames
        {
            get
            {
                if (_client == null) return 0;
                if (_client.GetCurrentPadding(out uint pad) != HResult.S_OK) return 0;
                int free = BufferFrames - (int)pad;
                return free < 0 ? 0 : free;
            }
        }

        /// <summary>设备缓冲里已经排队的帧数（延迟里的那一段）。</summary>
        public int FillFrames
        {
            get
            {
                if (_client == null) return 0;
                if (_client.GetCurrentPadding(out uint pad) != HResult.S_OK) return 0;
                return (int)pad;
            }
        }

        /// <summary>打开默认输出设备（共享模式，10ms 周期请求）。失败抛异常。</summary>
        public static WasapiOutput Open(int sampleRate)
        {
            var o = new WasapiOutput { SampleRate = sampleRate };
            Mta.Run(o.Init);
            return o;
        }

        private void Init()
        {
            IMMDevice device = AudioEndpoints.DefaultRenderDevice();
            Guid iid = AudioEndpoints.IidAudioClient;
            int hr = device.Activate(ref iid, NativeConst.CLSCTX_ALL, IntPtr.Zero, out object obj);
            if (hr != HResult.S_OK)
                throw new InvalidOperationException($"Activate(IAudioClient) 失败: 0x{hr:X8}");
            _client = (IAudioClient)obj;

            var fmt = new WaveFormat
            {
                FormatTag = 3, // WAVE_FORMAT_IEEE_FLOAT
                Channels = 2,
                SamplesPerSec = (uint)SampleRate,
                BitsPerSample = 32,
                BlockAlign = 8,
                AvgBytesPerSec = (uint)(SampleRate * 8),
                Size = 0,
            };
            IntPtr pFmt = Marshal.AllocHGlobal(32);
            try
            {
                Marshal.StructureToPtr(fmt, pFmt, false);
                hr = _client.Initialize(
                    NativeConst.AUDCLNT_SHAREMODE_SHARED,
                    NativeConst.AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM,
                    100000 /* 10ms：引擎通常按 2× 分配 → 设备缓冲约 20ms */,
                    0, pFmt, IntPtr.Zero);
                if (hr != HResult.S_OK)
                    throw new InvalidOperationException($"IAudioClient.Initialize 失败: 0x{hr:X8}");
            }
            finally
            {
                Marshal.FreeHGlobal(pFmt);
            }

            hr = _client.GetBufferSize(out uint frames);
            if (hr != HResult.S_OK)
                throw new InvalidOperationException($"GetBufferSize 失败: 0x{hr:X8}");
            BufferFrames = (int)frames;

            Guid rid = new Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
            hr = _client.GetService(ref rid, out object render);
            if (hr != HResult.S_OK)
                throw new InvalidOperationException($"GetService(IAudioRenderClient) 失败: 0x{hr:X8}");
            _render = (IAudioRenderClient)render;

            hr = _client.Start();
            if (hr != HResult.S_OK)
                throw new InvalidOperationException($"IAudioClient.Start 失败: 0x{hr:X8}");
        }

        /// <summary>把交错立体声从 offsetFrames 起的 frames 帧写进设备。返回 false = 这次没写进去。
        /// 注意设备缓冲可能比我们的 DSP 块还小，所以要能分块写（带 offset）。</summary>
        public bool Write(float[] interleaved, int offsetFrames, int frames)
        {
            if (_render == null || frames <= 0) return false;
            if (offsetFrames < 0) offsetFrames = 0;
            if (offsetFrames + frames > interleaved.Length / 2) return false;
            if (FreeFrames < frames)
            {
                Underruns++;
                return false;
            }

            int hr = _render.GetBuffer((uint)frames, out IntPtr data);
            if (hr != HResult.S_OK)
            {
                Underruns++;
                LastError = $"GetBuffer: 0x{hr:X8}";
                return false;
            }
            Marshal.Copy(interleaved, offsetFrames * 2, data, frames * 2);
            _render.ReleaseBuffer((uint)frames, 0);
            return true;
        }

        public void Dispose()
        {
            try { _client?.Stop(); } catch { }
            try
            {
                if (_render != null && Marshal.IsComObject(_render)) Marshal.ReleaseComObject(_render);
                if (_client != null && Marshal.IsComObject(_client)) Marshal.ReleaseComObject(_client);
            }
            catch { }
            _render = null;
            _client = null;
        }
    }
}
