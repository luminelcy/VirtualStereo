// WASAPI 进程树回环捕获引擎（纯 C#）。
// 设计目标：从游戏进程内捕获"Vuplex WebView 桥进程树"的音频（视频声音），
// 与游戏自己的音效天然隔离；可选把目标会话静音（消除"原声+复播"双响）。
// 关键未知数：会话静音是否发生在回环抽取点之前（静音后捕获是否变静音）——
// CaptureProbe 的 --selftest 就是测这个的。
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace VirtualStereo.Capture
{
    public sealed class ProcessLoopbackCapture : IDisposable
    {
        public const int RingSeconds = 2;

        public SampleRing RingL { get; } = new SampleRing(1 << 17); // 131072 ≈ 2.7s @ 48k
        public SampleRing RingR { get; } = new SampleRing(1 << 17);

        public int SampleRate { get; private set; }
        public int CaptureChannels { get; private set; }
        public bool Running { get; private set; }
        public string LastError { get; private set; }

        /// <summary>捕获到的信号 RMS（滑动，已含补偿增益），用于日志/实验观察。</summary>
        public float Rms { get; private set; }
        public float RmsL { get; private set; }
        public float RmsR { get; private set; }

        /// <summary>L/R 相关度（≈1 = 双单声道内容，两源会融合成正中；&lt;1 = 真立体声）。</summary>
        public float Correlation { get; private set; }
        public long PacketsReceived { get; private set; }

        /// <summary>数字补偿增益（线性；实测 CEF 流幅度 ∝ v¹，故 = 1/ε），在写环前乘上。</summary>
        public float OutputGain { get; private set; } = 1f;

        /// <summary>最近一次会话清单（诊断用：看有没有漏压的会话导致双响）。</summary>
        public string SessionInfo { get; private set; } = "";

        /// <summary>补偿增益延迟生效时间点（Windows 音量切换有约 1s 斜坡，斜坡期间不能上大增益）。</summary>
        private long _gainEngageAtMs;

        private Thread _thread;
        private IAudioClient _client;
        private IAudioCaptureClient _capture;
        private SessionMuter _muter;
        private int _targetPid;
        private bool _includeTree;

        /// <summary>true 时跳过 AUTOCONVERT，直接用设备混音格式（对比实验用）。</summary>
        public static bool ForceMixFormat;

        public static ProcessLoopbackCapture Start(int targetPid, bool includeTree)
        {
            var c = new ProcessLoopbackCapture { _targetPid = targetPid, _includeTree = includeTree };
            Mta.Run(c.Init); // STA 线程（Unity 主线程）直接调用会在 RCW 挂接时 QI 失败
            return c;
        }

        private void Init()
        {
            _client = AudioEndpoints.ActivateProcessLoopback(_targetPid, _includeTree);

            // 优先请求 48k 立体声 float32（AUTOCONVERTPCM 让引擎转换），失败则退回设备混音格式
            IntPtr pRequested = Marshal.AllocHGlobal(32);
            IntPtr pFormat = IntPtr.Zero;
            int hr;
            try
            {
                var requested = new WaveFormat
                {
                    FormatTag = 3, // WAVE_FORMAT_IEEE_FLOAT
                    Channels = 2,
                    SamplesPerSec = 48000,
                    BitsPerSample = 32,
                    BlockAlign = 8,
                    AvgBytesPerSec = 48000 * 8,
                    Size = 0,
                };
                Marshal.StructureToPtr(requested, pRequested, false);

                hr = ForceMixFormat
                    ? unchecked((int)0x80070057) // E_INVALIDARG：直接走混音格式分支
                    : _client.Initialize(
                        NativeConst.AUDCLNT_SHAREMODE_SHARED,
                        NativeConst.AUDCLNT_STREAMFLAGS_LOOPBACK | NativeConst.AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM,
                        0, 0, pRequested, IntPtr.Zero);
                if (hr == HResult.S_OK)
                {
                    SampleRate = 48000;
                    CaptureChannels = 2;
                }
                else
                {
                    // 退回混音格式
                    if (_client.GetMixFormat(out pFormat) != HResult.S_OK)
                        throw new InvalidOperationException($"GetMixFormat 失败（AUTOCONVERT 也被拒: 0x{hr:X8}）");
                    var fmt = Marshal.PtrToStructure<WaveFormat>(pFormat);
                    SampleRate = (int)fmt.SamplesPerSec;
                    CaptureChannels = fmt.Channels;
                    hr = _client.Initialize(
                        NativeConst.AUDCLNT_SHAREMODE_SHARED,
                        NativeConst.AUDCLNT_STREAMFLAGS_LOOPBACK,
                        200000, 0, pFormat, IntPtr.Zero);
                    if (hr != HResult.S_OK)
                        throw new InvalidOperationException($"IAudioClient.Initialize 失败: 0x{hr:X8}");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(pRequested);
                if (pFormat != IntPtr.Zero) Marshal.FreeCoTaskMem(pFormat); // GetMixFormat 用 CoTaskMem 分配
            }

            Guid iid = AudioEndpoints.IidAudioCaptureClient;
            hr = _client.GetService(ref iid, out object service);
            if (hr != HResult.S_OK)
                throw new InvalidOperationException($"GetService(IAudioCaptureClient) 失败: 0x{hr:X8}");
            _capture = (IAudioCaptureClient)service;

            hr = _client.Start();
            if (hr != HResult.S_OK)
                throw new InvalidOperationException($"IAudioClient.Start 失败: 0x{hr:X8}");

            Running = true;
            _thread = new Thread(CaptureLoop)
            {
                IsBackground = true,
                Name = "VirtualStereo.WasapiCapture",
                Priority = ThreadPriority.AboveNormal,
            };
            _thread.Start();
        }

        /// <summary>当前被压到 ε 的会话数（0 = 没压到任何会话，补偿增益不应生效）。</summary>
        public int SilencedSessions { get; private set; }

        /// <summary>
        /// 消双响（可周期性调用）：把目标进程树的会话音量压到 ε（默认 0.001），并做纯线性补偿。
        /// 实测：CEF 流的幅度 ∝ v¹（补偿 = 1/ε = 1000）；waveOut 流才是 v²——compensationGain
        /// 参数留给探针对不同流类型指定。实测静音（SetMute）会连捕获一起清零，所以用 ε 音量而不是静音。
        ///
        /// 关键时序：视频声音的会话是播视频时才创建的，晚于捕获启动——所以要周期性补压。
        /// 只有真的压到会话才开补偿增益；没压到（n=0）时增益保持 1。
        /// 返回本次压到的会话数。
        /// </summary>
        public int RefreshSilence(float level = 0.001f, float compensationGain = 0f)
        {
            try
            {
                return Mta.Run(() =>
                {
                    _muter ??= new SessionMuter();
                    var pids = ProcessTree.CollectTree(_targetPid);
                    int n = _muter.SetVolumePids(pids, level);
                    SilencedSessions = n;
                    SessionInfo = _muter.DescribeAll();

                    if (n <= 0)
                    {
                        OutputGain = 1f;
                        _gainEngageAtMs = 0;
                    }
                    else
                    {
                        float want = compensationGain > 0f ? compensationGain : 1f / level;
                        if (want > OutputGain)
                        {
                            // 音量斜坡 ~1s：增益上调延迟 2s 生效
                            _gainEngageAtMs = Environment.TickCount64 + 2000;
                            OutputGain = want;
                        }
                        else if (want < OutputGain)
                        {
                            _gainEngageAtMs = 0;
                            OutputGain = want;
                        }
                    }
                    return n;
                });
            }
            catch (Exception e)
            {
                LastError = "RefreshSilence: " + e.Message;
                return -1;
            }
        }

        /// <summary>恢复之前压过音量的会话（关闭虚拟立体声时必须调，否则原声只剩残余）。</summary>
        public void RestoreTargetSessions()
        {
            try
            {
                Mta.Run(() =>
                {
                    _muter?.RestoreAll();
                });
                OutputGain = 1f;
                SilencedSessions = 0;
            }
            catch { }
        }

        private void CaptureLoop()
        {
            float[] chunk = new float[8192];
            float[] left = new float[4096];
            float[] right = new float[4096];
            double rmsAccum = 0, lSq = 0, rSq = 0, lrSum = 0;
            long rmsCount = 0;

            while (Running)
            {
                int hr = _capture.GetNextPacketSize(out uint packetFrames);
                if (hr != HResult.S_OK)
                {
                    LastError = $"GetNextPacketSize: 0x{hr:X8}";
                    break;
                }
                if (packetFrames == 0)
                {
                    Thread.Sleep(5);
                    continue;
                }

                while (packetFrames > 0)
                {
                    hr = _capture.GetBuffer(out IntPtr data, out uint frames, out uint flags, out _, out _);
                    if (hr != HResult.S_OK)
                    {
                        LastError = $"GetBuffer: 0x{hr:X8}";
                        Running = false;
                        return;
                    }

                    int n = (int)frames;
                    int ch = CaptureChannels;
                    if (n > 0)
                    {
                        if (chunk.Length < n * ch)
                            chunk = new float[n * ch];

                        if ((flags & NativeConst.AUDCLNT_BUFFERFLAGS_SILENT) != 0)
                        {
                            Array.Clear(chunk, 0, n * ch);
                        }
                        else
                        {
                            Marshal.Copy(data, chunk, 0, n * ch);
                        }

                        // 取前两个声道拆 L/R；单声道复制两份；乘线性补偿增益（ε 音量回补）。
                        // 不做任何动态处理；±1 硬钳位只是保险丝（增益正常时永不触发）。
                        if (left.Length < n) { left = new float[n]; right = new float[n]; }
                        float gain = Environment.TickCount64 >= _gainEngageAtMs ? OutputGain : 1f;
                        if (ch >= 2)
                        {
                            for (int i = 0; i < n; i++)
                            {
                                float l = chunk[i * ch] * gain;
                                float r = chunk[i * ch + 1] * gain;
                                if (l > 1f) l = 1f; else if (l < -1f) l = -1f;
                                if (r > 1f) r = 1f; else if (r < -1f) r = -1f;
                                left[i] = l;
                                right[i] = r;
                            }
                        }
                        else
                        {
                            for (int i = 0; i < n; i++)
                            {
                                float v = chunk[i] * gain;
                                if (v > 1f) v = 1f; else if (v < -1f) v = -1f;
                                left[i] = v;
                                right[i] = v;
                            }
                        }
                        RingL.Write(left, 0, n);
                        RingR.Write(right, 0, n);

                        for (int i = 0; i < n; i++)
                        {
                            double l = left[i], r = right[i];
                            rmsAccum += l * l;
                            lSq += l * l;
                            rSq += r * r;
                            lrSum += l * r;
                        }
                        rmsCount += n;
                        PacketsReceived += n;
                    }

                    _capture.ReleaseBuffer(frames);
                    hr = _capture.GetNextPacketSize(out packetFrames);
                    if (hr != HResult.S_OK) { packetFrames = 0; break; }
                }

                if (rmsCount > SampleRate / 2)
                {
                    Rms = (float)Math.Sqrt(rmsAccum / Math.Max(1, rmsCount));
                    RmsL = (float)Math.Sqrt(lSq / Math.Max(1, rmsCount));
                    RmsR = (float)Math.Sqrt(rSq / Math.Max(1, rmsCount));
                    double denom = Math.Sqrt(lSq * rSq);
                    Correlation = denom > 1e-12 ? (float)(lrSum / denom) : 1f;
                    rmsAccum = 0;
                    lSq = 0;
                    rSq = 0;
                    lrSum = 0;
                    rmsCount = 0;
                }
            }
        }

        public void Dispose()
        {
            Running = false;
            _thread?.Join(500);
            RestoreTargetSessions();
            try
            {
                Mta.Run(() =>
                {
                    try { _client?.Stop(); } catch { }
                    try
                    {
                        if (_capture != null && Marshal.IsComObject(_capture)) Marshal.ReleaseComObject(_capture);
                        if (_client != null && Marshal.IsComObject(_client)) Marshal.ReleaseComObject(_client);
                    }
                    catch { }
                    _muter?.Dispose();
                });
            }
            catch { }
            _muter = null;
        }
    }
}
