// WASAPI shared 模式播放输出（桌面端）。请求 48k 立体声 float32（AUTOCONVERTPCM 让引擎转换），
// 轮询线程按可写帧数拉数据：回调 FillInterleaved(frames) 填充交错立体声。
// 复用 core 的 COM 互操作（IMMDevice/IAudioClient），另补 IAudioRenderClient。
using System;
using System.Runtime.InteropServices;
using System.Threading;
using VirtualStereo.Capture;

namespace VirtualStereo.Desktop
{
    [ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioRenderClient
    {
        [PreserveSig] int GetBuffer(uint numFramesRequested, out IntPtr data);
        [PreserveSig] int ReleaseBuffer(uint numFramesWritten, uint flags);
    }

    internal sealed class WasapiPlayer : IDisposable
    {
        /// <summary>填充交错立体声（float32，长度 = frames*2）。写不满的部分保持零。</summary>
        public delegate void FillInterleaved(float[] buffer, int frames);

        public int SampleRate { get; }
        public int BufferFrames { get; private set; }
        public string LastError { get; private set; }

        private IAudioClient _client;
        private IAudioRenderClient _render;
        private Thread _thread;
        private volatile bool _running;
        private readonly FillInterleaved _fill;
        private float[] _buf = new float[8192];

        public WasapiPlayer(int sampleRate, FillInterleaved fill)
        {
            SampleRate = sampleRate;
            _fill = fill;
            Init();
        }

        private void Init()
        {
            Mta.Run(() =>
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
                        100000 /* 10ms：引擎通常分配2×=20ms；每3ms轮询补满，
                                 常驻padding≈BufferFrames——这是延迟的第二块大头，20ms请求会拿40ms */,
                        0, pFmt, IntPtr.Zero);
                    if (hr != HResult.S_OK)
                        throw new InvalidOperationException($"IAudioClient.Initialize 失败: 0x{hr:X8}");
                }
                finally
                {
                    Marshal.FreeHGlobal(pFmt);
                }

                hr = _client.GetBufferSize(out uint bufferFrames);
                if (hr != HResult.S_OK)
                    throw new InvalidOperationException($"GetBufferSize 失败: 0x{hr:X8}");
                BufferFrames = (int)bufferFrames;

                Guid rid = new Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
                hr = _client.GetService(ref rid, out object render);
                if (hr != HResult.S_OK)
                    throw new InvalidOperationException($"GetService(IAudioRenderClient) 失败: 0x{hr:X8}");
                _render = (IAudioRenderClient)render;

                hr = _client.Start();
                if (hr != HResult.S_OK)
                    throw new InvalidOperationException($"IAudioClient.Start 失败: 0x{hr:X8}");
            });

            _running = true;
            _thread = new Thread(RenderLoop)
            {
                IsBackground = true,
                Name = "VirtualStereo.WasapiRender",
                Priority = ThreadPriority.AboveNormal,
            };
            _thread.Start();
        }

        private void RenderLoop()
        {
            while (_running)
            {
                try
                {
                    int hr = _client.GetCurrentPadding(out uint pad);
                    if (hr != HResult.S_OK)
                    {
                        LastError = $"GetCurrentPadding: 0x{hr:X8}";
                        break;
                    }
                    int writable = BufferFrames - (int)pad;
                    if (writable > 0)
                    {
                        if (_buf.Length < writable * 2) _buf = new float[writable * 2];
                        Array.Clear(_buf, 0, writable * 2);
                        _fill(_buf, writable);
                        hr = _render.GetBuffer((uint)writable, out IntPtr data);
                        if (hr != HResult.S_OK)
                        {
                            LastError = $"GetBuffer: 0x{hr:X8}";
                            break;
                        }
                        Marshal.Copy(_buf, 0, data, writable * 2);
                        _render.ReleaseBuffer((uint)writable, 0);
                    }
                    Thread.Sleep(3);
                }
                catch (Exception e)
                {
                    LastError = e.Message;
                    break;
                }
            }
        }

        public void Dispose()
        {
            _running = false;
            _thread?.Join(500);
            try { _client?.Stop(); } catch { }
            try
            {
                if (_render != null && Marshal.IsComObject(_render)) Marshal.ReleaseComObject(_render);
                if (_client != null && Marshal.IsComObject(_client)) Marshal.ReleaseComObject(_client);
            }
            catch { }
        }
    }
}
