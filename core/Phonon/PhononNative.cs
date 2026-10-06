// Steam Audio（phonon.dll）P/Invoke 绑定：只覆盖 mod 用到的子集。
// 库：Steam Audio 4.8.1 SDK（Apache 2.0）的 windows-x64/phonon.dll，部署到游戏根目录。
// 调用约定：phonon.h 里 IPLCALL 在 _WIN32 下展开为 __stdcall，所以这里用 StdCall。
//
// 说明：Windows 版的这份绑定是照 macOS 版（VirtualStereo_forMac/core/Phonon）与官方
// phonon.h 重建的——原 Windows 源码从未入库（仓库 .gitignore 的 phonon/ 规则把
// core/Phonon 一起忽略了）。结构体布局逐字段对照 phonon.h 4.8.1 核对过。
using System;
using System.Runtime.InteropServices;

namespace VirtualStereo.Phonon
{
    /// <summary>空间化模式。</summary>
    public enum SaiMode
    {
        /// <summary>完整 HRTF：ITD + ILD + 耳廓频谱，全部由 Steam Audio 完成。</summary>
        FullHrtf = 0,

        /// <summary>只留 ITD + ILD（去掉耳廓频谱）：用 HRTF 的 peakDelays 驱动纯分数延迟，
        /// 再叠加等功率幅度 panning。</summary>
        ItdIld = 1,
    }

    /// <summary>头坐标系下的单位方向（X 右 / Y 上 / Z 前）。</summary>
    public struct SaiVector3
    {
        public float X, Y, Z;
        public SaiVector3(float x, float y, float z) { X = x; Y = y; Z = z; }
        public static readonly SaiVector3 Forward = new SaiVector3(0f, 0f, 1f);
    }

    /// <summary>HRTF 插值方式（取值与 IPLHRTFInterpolation 一致）。</summary>
    internal enum SaiInterpolation { Nearest = 0, Bilinear = 1 }

    /// <summary>HRTF 来源类型（IPLHRTFType）。</summary>
    internal enum SaiHrtfType { Default = 0, Sofa = 1 }

    /// <summary>HRTF 音量归一化（IPLHRTFNormType）。</summary>
    internal enum SaiHrtfNormType { None = 0, Rms = 1 }

    /// <summary>IPLVector3（C 布局）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SaiVector3Native
    {
        public float X, Y, Z;
        public SaiVector3Native(float x, float y, float z) { X = x; Y = y; Z = z; }
        public static SaiVector3Native From(SaiVector3 v) => new SaiVector3Native(v.X, v.Y, v.Z);
    }

    /// <summary>IPLAudioSettings：全局采样率与帧大小。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SaiAudioSettings
    {
        public int SamplingRate;
        public int FrameSize;
    }

    /// <summary>IPLAudioBuffer：去交织（data = float**，每通道一个指针）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SaiAudioBuffer
    {
        public int NumChannels;
        public int NumSamples;
        public IntPtr Data; // float**
    }

    /// <summary>IPLHRTFSettings。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SaiHrtfSettings
    {
        public int Type;            // IPLHRTFType
        public IntPtr SofaFileName; // const char*
        public IntPtr SofaData;     // const uint8*
        public int SofaDataSize;
        public float Volume;
        public int NormType;        // IPLHRTFNormType
    }

    /// <summary>IPLBinauralEffectSettings。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SaiBinauralEffectSettings
    {
        public IntPtr Hrtf;
    }

    /// <summary>IPLBinauralEffectParams。PeakDelays 指向调用方分配的两个 float，
    /// SDK 会把左/右耳的峰值延迟（秒）写进去；传 IntPtr.Zero 则不写。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SaiBinauralEffectParams
    {
        public SaiVector3Native Direction;
        public int Interpolation;   // IPLHRTFInterpolation
        public float SpatialBlend;
        public IntPtr Hrtf;
        public IntPtr PeakDelays;   // float*（可为 NULL）
    }

    /// <summary>IPLContextSettings。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SaiContextSettings
    {
        public uint Version;
        public IntPtr LogCallback;
        public IntPtr AllocateCallback;
        public IntPtr FreeCallback;
        public int SimdLevel;       // IPLSIMDLevel
        public int Flags;
    }

    internal static class PhononNative
    {
        /// <summary>STEAMAUDIO_VERSION = (major&lt;&lt;16)|(minor&lt;&lt;8)|patch = 4.8.1。</summary>
        public const uint SteamAudioVersion = (4u << 16) | (8u << 8) | 1u;
        public const int StatusSuccess = 0;

        /// <summary>IPLHRTFInterpolation 取值，供上层直接使用。</summary>
        public const int InterpolationNearest = (int)SaiInterpolation.Nearest;
        public const int InterpolationBilinear = (int)SaiInterpolation.Bilinear;

        /// <summary>phonon.h 的 IPLCALL 在 Windows 上是 __stdcall。</summary>
        private const string Lib = "phonon.dll";
        private const CallingConvention Cc = CallingConvention.StdCall;

        [DllImport(Lib, CallingConvention = Cc)]
        public static extern int iplContextCreate(ref SaiContextSettings settings, out IntPtr context);

        [DllImport(Lib, CallingConvention = Cc)]
        public static extern void iplContextRelease(ref IntPtr context);

        [DllImport(Lib, CallingConvention = Cc)]
        public static extern int iplHRTFCreate(IntPtr context, ref SaiAudioSettings audioSettings,
            ref SaiHrtfSettings hrtfSettings, out IntPtr hrtf);

        [DllImport(Lib, CallingConvention = Cc)]
        public static extern void iplHRTFRelease(ref IntPtr hrtf);

        [DllImport(Lib, CallingConvention = Cc)]
        public static extern int iplAudioBufferAllocate(IntPtr context, int numChannels, int numSamples,
            out SaiAudioBuffer audioBuffer);

        [DllImport(Lib, CallingConvention = Cc)]
        public static extern void iplAudioBufferFree(IntPtr context, ref SaiAudioBuffer audioBuffer);

        [DllImport(Lib, CallingConvention = Cc)]
        public static extern int iplBinauralEffectCreate(IntPtr context, ref SaiAudioSettings audioSettings,
            ref SaiBinauralEffectSettings effectSettings, out IntPtr effect);

        [DllImport(Lib, CallingConvention = Cc)]
        public static extern void iplBinauralEffectRelease(ref IntPtr effect);

        [DllImport(Lib, CallingConvention = Cc)]
        public static extern int iplBinauralEffectApply(IntPtr effect, ref SaiBinauralEffectParams effectParams,
            ref SaiAudioBuffer inBuffer, ref SaiAudioBuffer outBuffer);
    }
}
