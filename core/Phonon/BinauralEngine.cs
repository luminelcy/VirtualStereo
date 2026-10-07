// 双耳引擎（Windows / phonon.dll）：支持 **N 个虚拟声源**（现在是三对＝6 个）。
// 每个声源一个 binaural effect（各自保留 HRTF 滤波状态，方向固定时互不污染）；
// 处理时逐源**串行复用同一对输入/输出缓冲**——缓冲只有一份，只有 effect 是每源一份。
//
// 线程：Init/LoadSofa/SetInterpolation 在控制线程，Process/SetDirections 在音频线程，
// Steam Audio 的 context/effect 不是线程安全的，用一把锁把两边隔开（Monitor 可重入，
// Fail→Shutdown 的嵌套调用不会死锁）。
using System;
using System.Runtime.InteropServices;

namespace VirtualStereo.Phonon
{
    public static class BinauralEngine
    {
        /// <summary>虚拟声源数上限（三对＝6，留余量；开多屏时按"主三对+其余各一对"估）。</summary>
        public const int MaxSources = 8;

        public static bool Initialized { get; private set; }
        public static string LastError { get; private set; } = "";

        /// <summary>空间化程度：1 = 完全 HRTF（点声源），调低会把未空间化成分混进来，
        /// 声像变宽、更像"面声源"，转头时移动幅度也更小（IPLBinauralEffectParams.spatialBlend）。</summary>
        public static float SpatialBlend { get; set; } = 0.9f;

        private static readonly object _gate = new object();

        private static IntPtr _context;
        private static IntPtr _hrtf;
        private static int _rate, _frames;
        private static SaiInterpolation _interp = SaiInterpolation.Bilinear;

        private static readonly IntPtr[] _effects = new IntPtr[MaxSources];
        private static readonly SaiVector3Native[] _dirs = new SaiVector3Native[MaxSources];

        // 逐源串行处理，所以只有一份输入 / 一份输出缓冲
        private static SaiAudioBuffer _in, _out;
        private static IntPtr _inData, _inPtr;
        private static float[] _tmpL, _tmpR;

        /// <summary>设置各虚拟声源方向（头坐标系单位向量），只取前 count 个。</summary>
        public static void SetDirections(SaiVector3[] dirs, int count)
        {
            lock (_gate)
            {
                if (dirs == null) return;
                if (count > MaxSources) count = MaxSources;
                for (int i = 0; i < count; i++) _dirs[i] = SaiVector3Native.From(dirs[i]);
            }
        }

        /// <summary>初始化上下文 / HRTF / MaxSources 个双耳 effect。已初始化则直接返回 true。</summary>
        public static bool Init(int sampleRate, int frameSize)
        {
            lock (_gate)
            {
                if (Initialized) return true;
                if (sampleRate <= 0 || frameSize <= 0)
                {
                    LastError = "双耳引擎参数非法";
                    return false;
                }

                try
                {
                    var ctxSettings = new SaiContextSettings
                    {
                        Version = PhononNative.SteamAudioVersion,
                        SimdLevel = 0, // IPL_SIMDLEVEL_SSE2
                    };
                    int err = PhononNative.iplContextCreate(ref ctxSettings, out _context);
                    if (err != PhononNative.StatusSuccess)
                        return Fail("iplContextCreate", err);

                    _rate = sampleRate;
                    _frames = frameSize;
                    var audio = new SaiAudioSettings { SamplingRate = sampleRate, FrameSize = frameSize };

                    if (!CreateHrtf(null, ref audio)) return false;
                    if (!CreateEffects(ref audio)) return false;

                    _inData = Marshal.AllocHGlobal(frameSize * sizeof(float));
                    _inPtr = Marshal.AllocHGlobal(IntPtr.Size);
                    Marshal.WriteIntPtr(_inPtr, _inData);
                    _in = new SaiAudioBuffer { NumChannels = 1, NumSamples = frameSize, Data = _inPtr };

                    err = PhononNative.iplAudioBufferAllocate(_context, 2, frameSize, out _out);
                    if (err != PhononNative.StatusSuccess)
                        return Fail("iplAudioBufferAllocate", err);

                    _tmpL = new float[frameSize];
                    _tmpR = new float[frameSize];

                    Initialized = true;
                    LastError = "";
                    return true;
                }
                catch (Exception e)
                {
                    LastError = "双耳引擎初始化异常: " + e.Message;
                    Shutdown();
                    return false;
                }
            }
        }

        /// <summary>0 = 最近邻 / 1 = 双线性。</summary>
        public static void SetInterpolation(int mode)
        {
            lock (_gate)
            {
                _interp = mode == PhononNative.InterpolationNearest
                    ? SaiInterpolation.Nearest
                    : SaiInterpolation.Bilinear;
            }
        }

        /// <summary>载入 SOFA 自定义 HRTF（null/空 = 回到内置 HRTF）。失败不影响当前 HRTF。</summary>
        public static bool LoadSofa(string path)
        {
            lock (_gate)
            {
                if (!Initialized)
                {
                    LastError = "双耳引擎尚未初始化";
                    return false;
                }

                var audio = new SaiAudioSettings { SamplingRate = _rate, FrameSize = _frames };
                if (!CreateHrtf(string.IsNullOrEmpty(path) ? null : path, ref audio)) return false;

                // effect 创建时绑了 HRTF，换 HRTF 后一并重建
                ReleaseEffects();
                if (!CreateEffects(ref audio)) return false;

                LastError = "";
                return true;
            }
        }

        /// <summary>
        /// N 路双耳渲染：spk[0..count) 各按自己的方向做 HRTF，相加写进交织的
        /// stereo[frames*2]（L,R,L,R…）。未初始化时退化为主声道直出（不丢声）。
        /// </summary>
        public static void Process(float[][] spk, int count, float[] stereo, int frames)
        {
            lock (_gate)
            {
                if (count < 0) count = 0;
                if (count > MaxSources) count = MaxSources;
                Array.Clear(stereo, 0, frames * 2);

                if (!Initialized || frames <= 0 || frames > _frames)
                {
                    // 兜底：不丢声，直接两边都放
                    for (int s = 0; s < count; s++)
                    {
                        float[] b = spk[s];
                        if (b == null) continue;
                        for (int i = 0; i < frames; i++)
                        {
                            stereo[i * 2] += b[i];
                            stereo[i * 2 + 1] += b[i];
                        }
                    }
                    return;
                }

                for (int s = 0; s < count; s++)
                {
                    float[] b = spk[s];
                    if (b == null) continue;

                    Marshal.Copy(b, 0, _inData, frames);
                    _in.NumSamples = frames;
                    var param = new SaiBinauralEffectParams
                    {
                        Direction = _dirs[s],
                        Interpolation = (int)_interp,
                        SpatialBlend = BinauralEngine.SpatialBlend,
                        Hrtf = _hrtf,
                        PeakDelays = IntPtr.Zero,
                    };
                    PhononNative.iplBinauralEffectApply(_effects[s], ref param, ref _in, ref _out);

                    IntPtr l0 = Marshal.ReadIntPtr(_out.Data, 0);
                    IntPtr l1 = Marshal.ReadIntPtr(_out.Data, IntPtr.Size);
                    Marshal.Copy(l0, _tmpL, 0, frames);
                    Marshal.Copy(l1, _tmpR, 0, frames);
                    for (int i = 0; i < frames; i++)
                    {
                        stereo[i * 2] += _tmpL[i];
                        stereo[i * 2 + 1] += _tmpR[i];
                    }
                }
            }
        }

        /// <summary>释放全部原生资源。</summary>
        public static void Shutdown()
        {
            lock (_gate)
            {
                try
                {
                    ReleaseEffects();
                    if (_context != IntPtr.Zero && _out.Data != IntPtr.Zero)
                        PhononNative.iplAudioBufferFree(_context, ref _out);
                }
                catch { }

                _out = default;
                _in = default;
                if (_inData != IntPtr.Zero) { Marshal.FreeHGlobal(_inData); _inData = IntPtr.Zero; }
                if (_inPtr != IntPtr.Zero) { Marshal.FreeHGlobal(_inPtr); _inPtr = IntPtr.Zero; }

                if (_hrtf != IntPtr.Zero) PhononNative.iplHRTFRelease(ref _hrtf);
                if (_context != IntPtr.Zero) PhononNative.iplContextRelease(ref _context);
                Initialized = false;
            }
        }

        // ---------------------------------------------------------------- 内部（调用方已持锁）

        private static bool CreateEffects(ref SaiAudioSettings audio)
        {
            var effSettings = new SaiBinauralEffectSettings { Hrtf = _hrtf };
            for (int i = 0; i < MaxSources; i++)
            {
                int err = PhononNative.iplBinauralEffectCreate(_context, ref audio, ref effSettings, out _effects[i]);
                if (err != PhononNative.StatusSuccess)
                    return Fail($"iplBinauralEffectCreate(#{i})", err);
            }
            return true;
        }

        private static void ReleaseEffects()
        {
            for (int i = 0; i < MaxSources; i++)
                if (_effects[i] != IntPtr.Zero)
                    PhononNative.iplBinauralEffectRelease(ref _effects[i]);
        }

        /// <summary>创建 HRTF（null = 内置；否则从 SOFA 文件）。成功后才替换旧 HRTF。</summary>
        private static bool CreateHrtf(string sofaPath, ref SaiAudioSettings audio)
        {
            bool sofa = !string.IsNullOrEmpty(sofaPath);
            var settings = new SaiHrtfSettings
            {
                Type = (int)(sofa ? SaiHrtfType.Sofa : SaiHrtfType.Default),
                Volume = 1f,
                NormType = (int)SaiHrtfNormType.None,
            };

            IntPtr namePtr = IntPtr.Zero;
            IntPtr newHrtf = IntPtr.Zero;
            try
            {
                if (sofa) namePtr = Marshal.StringToHGlobalAnsi(sofaPath);
                settings.SofaFileName = namePtr;

                int err = PhononNative.iplHRTFCreate(_context, ref audio, ref settings, out newHrtf);
                if (err != PhononNative.StatusSuccess)
                {
                    LastError = sofa
                        ? $"SOFA 载入失败 (0x{err:X8}): {sofaPath}"
                        : $"内置 HRTF 创建失败 (0x{err:X8})";
                    return false;
                }
            }
            finally
            {
                if (namePtr != IntPtr.Zero) Marshal.FreeHGlobal(namePtr);
            }

            if (_hrtf != IntPtr.Zero) PhononNative.iplHRTFRelease(ref _hrtf);
            _hrtf = newHrtf;
            return true;
        }

        private static bool Fail(string what, int err)
        {
            LastError = $"{what} 失败 (0x{err:X8})";
            Shutdown();
            return false;
        }
    }
}
