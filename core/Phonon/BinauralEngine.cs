// 双耳引擎（Windows / phonon.dll）：两个虚拟声源（L/R）各配一个 binaural effect。
// 处理时两个声源**串行复用同一对输入/输出缓冲**，只有 effect 是每源一份。
// 线程：Init/LoadSofa/SetInterpolation 在控制线程；Process 在音频线程（不分配托管堆）。
//
// 说明：原 Windows 源码已丢失（从未入库），这份是照 macOS 版
// （VirtualStereo_forMac/core/Phonon/BinauralEngine.cs）与 README 的线索隔离定义重建的：
//   FullHrtf —— 直接走 Steam Audio 完整 HRTF（行为与原版应当一致）；
//   ItdIld   —— 去掉耳廓频谱，只留 ITD + ILD：
//               ITD 用该方向 HRTF 的 peakDelays 驱动线性插值分数延迟（SDK 输出，见 phonon.h），
//               ILD 用横向分量等功率 panning。延迟/增益都做了一阶平滑，避免转头时拉链噪声。
using System;
using System.Runtime.InteropServices;

namespace VirtualStereo.Phonon
{
    public static class BinauralEngine
    {
        /// <summary>虚拟声源数（左/右各一）。</summary>
        public const int SourceCount = 2;

        public static bool Initialized { get; private set; }
        public static string LastError { get; private set; } = "";

        private static IntPtr _context;
        private static IntPtr _hrtf;
        private static readonly IntPtr[] _effects = new IntPtr[SourceCount];

        private static int _rate, _frames;
        private static SaiInterpolation _interp = SaiInterpolation.Bilinear;
        private static readonly SaiVector3Native[] _dirs = new SaiVector3Native[SourceCount];

        // 逐源串行处理，所以只有一份输入 / 一份输出缓冲
        private static SaiAudioBuffer _in, _out;
        private static IntPtr _inData, _inPtr; // 采样块 / float**
        private static float[] _tmp0, _tmp1;   // 输出两通道的暂存

        // ItdIld 模式：peakDelays 输出缓冲（float[2]，单位秒）
        private static IntPtr _peakPtr;
        private static readonly float[] _peak = new float[2];

        // ItdIld 模式：每个声源一条历史环（线性插值分数延迟用，ITD 最大约 0.7ms，
        // 48kHz 下不到 40 采样，256 足够）
        private const int HistBits = 8;
        private const int HistLen = 1 << HistBits;
        private const int HistMask = HistLen - 1;
        private const float DelaySmooth = 0.02f; // 每采样一阶平滑系数
        private const float GainSmooth = 0.01f;
        private static readonly float[][] _hist = new float[SourceCount][];
        private static readonly int[] _histW = new int[SourceCount];
        private static readonly float[] _delaySm = new float[SourceCount * 2]; // [源*2+耳]，单位采样
        private static readonly float[] _gainSm = new float[SourceCount * 2];
        private static readonly bool[] _dspStarted = new bool[SourceCount * 2];

        /// <summary>设置左右两个虚拟声源的方向（头坐标系单位向量）。</summary>
        public static void SetDirections(SaiVector3 left, SaiVector3 right)
        {
            _dirs[0] = ToNative(left);
            _dirs[1] = ToNative(right);
        }

        /// <summary>初始化上下文 / HRTF / 两个双耳 effect。已初始化则直接返回 true。</summary>
        public static bool Init(int sampleRate, int frameSize)
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

                _tmp0 = new float[frameSize];
                _tmp1 = new float[frameSize];
                _peakPtr = Marshal.AllocHGlobal(2 * sizeof(float));
                for (int s = 0; s < SourceCount; s++)
                {
                    _hist[s] = new float[HistLen];
                    _histW[s] = 0;
                }
                ResetDspState();

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

        /// <summary>0 = 最近邻 / 1 = 双线性。</summary>
        public static void SetInterpolation(int mode)
        {
            _interp = mode == PhononNative.InterpolationNearest
                ? SaiInterpolation.Nearest
                : SaiInterpolation.Bilinear;
        }

        /// <summary>载入 SOFA 自定义 HRTF（null/空 = 回到内置 HRTF）。失败不影响当前 HRTF。</summary>
        public static bool LoadSofa(string path)
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

            ResetDspState();
            LastError = "";
            return true;
        }

        /// <summary>
        /// 双耳渲染：左侧声源 left[] 与右侧声源 right[] 各按自己的方向渲染，
        /// 相加写进交织的 stereo[frames*2]（L,R,L,R…）。
        /// 未初始化（或帧数超出初始化值）时退化为主声道直出，不丢声。
        /// </summary>
        public static void Process(float[] left, float[] right, float[] stereo, int frames, SaiMode mode)
        {
            Array.Clear(stereo, 0, frames * 2);

            if (!Initialized || frames <= 0 || frames > _frames)
            {
                Fallback(left, stereo, frames);
                Fallback(right, stereo, frames);
                return;
            }

            ProcessSource(0, left, stereo, frames, mode);
            ProcessSource(1, right, stereo, frames, mode);
        }

        /// <summary>释放全部原生资源。</summary>
        public static void Shutdown()
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
            if (_peakPtr != IntPtr.Zero) { Marshal.FreeHGlobal(_peakPtr); _peakPtr = IntPtr.Zero; }

            if (_hrtf != IntPtr.Zero) PhononNative.iplHRTFRelease(ref _hrtf);
            if (_context != IntPtr.Zero) PhononNative.iplContextRelease(ref _context);

            for (int s = 0; s < SourceCount; s++) _hist[s] = null;
            ResetDspState();
            Initialized = false;
        }

        // ---------------------------------------------------------------- 内部

        private static void ProcessSource(int s, float[] src, float[] stereo, int frames, SaiMode mode)
        {
            if (src == null) return;

            if (mode == SaiMode.ItdIld)
            {
                ProcessItdIld(s, src, stereo, frames);
                return;
            }

            Marshal.Copy(src, 0, _inData, frames);
            _in.NumSamples = frames;
            var param = new SaiBinauralEffectParams
            {
                Direction = _dirs[s],
                Interpolation = (int)_interp,
                SpatialBlend = 1f,
                Hrtf = _hrtf,
                PeakDelays = IntPtr.Zero,
            };
            PhononNative.iplBinauralEffectApply(_effects[s], ref param, ref _in, ref _out);

            IntPtr l0 = Marshal.ReadIntPtr(_out.Data, 0);
            IntPtr l1 = Marshal.ReadIntPtr(_out.Data, IntPtr.Size);
            Marshal.Copy(l0, _tmp0, 0, frames);
            Marshal.Copy(l1, _tmp1, 0, frames);
            for (int i = 0; i < frames; i++)
            {
                stereo[i * 2] += _tmp0[i];
                stereo[i * 2 + 1] += _tmp1[i];
            }
        }

        /// <summary>ITD + ILD（无耳廓频谱）：peakDelays 分数延迟 + 等功率 panning。</summary>
        private static void ProcessItdIld(int s, float[] src, float[] stereo, int frames)
        {
            // 借 HRTF effect 把该方向的左右耳峰值延迟（秒）取出来
            Marshal.Copy(src, 0, _inData, frames);
            _in.NumSamples = frames;
            var param = new SaiBinauralEffectParams
            {
                Direction = _dirs[s],
                Interpolation = (int)_interp,
                SpatialBlend = 1f,
                Hrtf = _hrtf,
                PeakDelays = _peakPtr,
            };
            PhononNative.iplBinauralEffectApply(_effects[s], ref param, ref _in, ref _out);
            Marshal.Copy(_peakPtr, _peak, 0, 2);

            float dL = _peak[0] * _rate;
            float dR = _peak[1] * _rate;
            if (float.IsNaN(dL) || float.IsInfinity(dL)) dL = 0f;
            if (float.IsNaN(dR) || float.IsInfinity(dR)) dR = 0f;

            // 以较早到达的耳为基准归零，只保留耳间时间差（并保证因果）
            float earliest = Math.Min(dL, dR);
            dL = Clamp(dL - earliest, 0f, HistLen - 2);
            dR = Clamp(dR - earliest, 0f, HistLen - 2);

            // ILD：横向分量等功率幅度 panning（x 右为正）
            float x = Clamp(_dirs[s].X, -1f, 1f);
            float gL = (float)Math.Sqrt((1f - x) * 0.5f);
            float gR = (float)Math.Sqrt((1f + x) * 0.5f);

            int iL = s * 2, iR = s * 2 + 1;
            if (!_dspStarted[iL]) { _delaySm[iL] = dL; _gainSm[iL] = gL; _dspStarted[iL] = true; }
            if (!_dspStarted[iR]) { _delaySm[iR] = dR; _gainSm[iR] = gR; _dspStarted[iR] = true; }

            float[] hist = _hist[s];
            int w = _histW[s];
            float smL = _delaySm[iL], smR = _delaySm[iR];
            float gnL = _gainSm[iL], gnR = _gainSm[iR];

            for (int i = 0; i < frames; i++)
            {
                hist[w] = src[i];

                smL += (dL - smL) * DelaySmooth;
                smR += (dR - smR) * DelaySmooth;
                gnL += (gL - gnL) * GainSmooth;
                gnR += (gR - gnR) * GainSmooth;

                float vL = Tap(hist, w, smL);
                float vR = Tap(hist, w, smR);
                stereo[i * 2] += gnL * vL;
                stereo[i * 2 + 1] += gnR * vR;

                w = (w + 1) & HistMask;
            }

            _histW[s] = w;
            _delaySm[iL] = smL; _delaySm[iR] = smR;
            _gainSm[iL] = gnL; _gainSm[iR] = gnR;
        }

        /// <summary>在历史环上取分数延迟采样（线性插值）。d 单位：采样。</summary>
        private static float Tap(float[] hist, int w, float d)
        {
            float rd = w - d;
            int i0 = (int)Math.Floor(rd);
            float frac = rd - i0;
            float a = hist[i0 & HistMask];
            float b = hist[(i0 - 1) & HistMask];
            return a + (b - a) * frac;
        }

        private static void Fallback(float[] src, float[] stereo, int frames)
        {
            if (src == null) return;
            for (int i = 0; i < frames; i++)
            {
                stereo[i * 2] += src[i];
                stereo[i * 2 + 1] += src[i];
            }
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

        private static bool CreateEffects(ref SaiAudioSettings audio)
        {
            var effSettings = new SaiBinauralEffectSettings { Hrtf = _hrtf };
            for (int i = 0; i < SourceCount; i++)
            {
                int err = PhononNative.iplBinauralEffectCreate(_context, ref audio, ref effSettings, out _effects[i]);
                if (err != PhononNative.StatusSuccess)
                    return Fail($"iplBinauralEffectCreate(#{i})", err);
            }
            return true;
        }

        private static void ReleaseEffects()
        {
            for (int i = 0; i < SourceCount; i++)
                if (_effects[i] != IntPtr.Zero)
                    PhononNative.iplBinauralEffectRelease(ref _effects[i]);
        }

        private static void ResetDspState()
        {
            for (int i = 0; i < SourceCount * 2; i++)
            {
                _delaySm[i] = 0f;
                _gainSm[i] = 0f;
                _dspStarted[i] = false;
            }
        }

        private static SaiVector3Native ToNative(SaiVector3 v)
        {
            float len = (float)Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
            if (len < 1e-6f || float.IsNaN(len)) return SaiVector3Native.From(SaiVector3.Forward);
            return new SaiVector3Native(v.X / len, v.Y / len, v.Z / len);
        }

        private static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);

        private static bool Fail(string what, int err)
        {
            LastError = $"{what} 失败 (0x{err:X8})";
            Shutdown();
            return false;
        }
    }
}
