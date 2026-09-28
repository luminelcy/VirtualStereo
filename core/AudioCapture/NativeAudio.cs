// 纯 C# 音频互操作层（不依赖 Unity / MelonLoader），mod 与 CaptureProbe 共用。
// 目标：WASAPI 进程树回环捕获（Win10 2004+ AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK）
// + 会话静音（ISimpleAudioVolume）+ Toolhelp 进程树枚举。
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace VirtualStereo.Capture
{
    internal static class HResult
    {
        public const int S_OK = 0;
        public const int AUDCLNT_E_DEVICE_INVALIDATED = unchecked((int)0x88890004);
    }

    /// <summary>
    /// 把 WASAPI COM 操作放到 MTA 套间执行。
    /// 实测（CaptureProbe --sta）：激活回调在 MTA 线程返回的 IAudioClient RCW，
    /// 若从 STA 线程（Unity 主线程）调用会在 GetCOMIPFromRCW 处 QI 失败（E_NOINTERFACE）。
    /// MTA 里创建的对象在任意 MTA 线程可直接用，所以统一经此包装。
    /// </summary>
    internal static class Mta
    {
        public static void Run(Action action)
        {
            if (Thread.CurrentThread.GetApartmentState() == ApartmentState.MTA)
            {
                action();
                return;
            }
            Exception error = null;
            var t = new Thread(() =>
            {
                try { action(); }
                catch (Exception e) { error = e; }
            });
            t.SetApartmentState(ApartmentState.MTA);
            t.IsBackground = true;
            t.Start();
            t.Join();
            if (error != null) throw error;
        }

        public static T Run<T>(Func<T> func)
        {
            T result = default;
            Run(() => { result = func(); });
            return result;
        }
    }

    internal static class NativeConst
    {
        public const int ERender = 0;
        public const int EConsole = 0;
        public const uint CLSCTX_ALL = 0x17;
        public const uint AUDCLNT_SHAREMODE_SHARED = 0;
        public const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
        public const uint AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM = 0x80000000;
        public const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;
        public const ushort VT_BLOB = 0x41;
        public const uint AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK = 1;
        public const uint AUDIOCLIENT_PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE = 0;
        public const uint AUDIOCLIENT_PROCESS_LOOPBACK_MODE_EXCLUDE_TARGET_PROCESS_TREE = 1;
        public const uint TH32CS_SNAPPROCESS = 0x2;
    }

    // ────────────────────────────── WASAPI COM ──────────────────────────────

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    internal class MMDeviceEnumeratorCom { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(int access, out IntPtr properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioClient
    {
        [PreserveSig] int Initialize(uint shareMode, uint streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
        [PreserveSig] int GetBufferSize(out uint numBufferFrames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint numPaddingFrames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultDevicePeriod, out long minimumDevicePeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint numFrames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint numFrames);
        [PreserveSig] int GetNextPacketSize(out uint numFramesInNextPacket);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionManager2
    {
        // IAudioSessionManager
        [PreserveSig] int GetAudioSessionControl(ref Guid sessionGuid, uint streamFlags, out IAudioSessionControl sessionControl);
        [PreserveSig] int GetSimpleAudioVolume(ref Guid sessionGuid, uint streamFlags, out ISimpleAudioVolume audioVolume);
        // IAudioSessionManager2
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum);
        [PreserveSig] int RegisterSessionNotification(IntPtr notification);
        [PreserveSig] int UnregisterSessionNotification(IntPtr notification);
        [PreserveSig] int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionId, IntPtr notification);
        [PreserveSig] int UnregisterDuckNotification(IntPtr notification);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int sessionCount);
        [PreserveSig] int GetSession(int sessionCount, out IAudioSessionControl session);
    }

    [ComImport, Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionControl
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string displayName);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string value, IntPtr eventContext);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string iconPath);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string value, IntPtr eventContext);
        [PreserveSig] int GetGroupingParam(out Guid groupingParam);
        [PreserveSig] int SetGroupingParam(ref Guid value, IntPtr eventContext);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr notification);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr notification);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionControl2
    {
        // IAudioSessionControl
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string displayName);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string value, IntPtr eventContext);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string iconPath);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string value, IntPtr eventContext);
        [PreserveSig] int GetGroupingParam(out Guid groupingParam);
        [PreserveSig] int SetGroupingParam(ref Guid value, IntPtr eventContext);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr notification);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr notification);
        // IAudioSessionControl2
        [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string sessionId);
        [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string instanceId);
        [PreserveSig] int GetProcessId(out uint processId);
        [PreserveSig] int IsSystemSoundsSession();
        [PreserveSig] int SetDuckingPreference(int optOut);
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ISimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float level, IntPtr eventContext);
        [PreserveSig] int GetMasterVolume(out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, IntPtr eventContext);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    // ────────────────────────────── 结构体 ──────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    internal struct Blob
    {
        public uint cbSize;
        public uint pad;
        public IntPtr pBlobData;
    }

    // PROPVARIANT：vt(2)+reserved(6) = 8 字节头，union 在偏移 8
    [StructLayout(LayoutKind.Sequential)]
    internal struct PropVariant
    {
        public ushort vt;
        public ushort wReserved1;
        public ushort wReserved2;
        public ushort wReserved3;
        public Blob blob;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AudioClientActivationParams
    {
        public uint ActivationType;
        public uint TargetProcessId;
        public uint ProcessLoopbackMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WaveFormat
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSec;
        public uint AvgBytesPerSec;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort Size;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClass;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }

    // ────────────────────────────── 进程回环激活（Async + 完成回调） ──────────────────────────────
    // 注意：系统并不导出同步版 ActivateAudioInterface（只有 ActivateAudioInterfaceAsync），
    // 且设备 ID 必须用虚拟设备 VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK（audioclientactivationparams.h）。

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IActivateAudioInterfaceAsyncOperation
    {
        [PreserveSig] int GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    // 托管实现侧（CCW）：不能用 ComImport（ComImport 接口不可被托管类实现），
    // 用同 GUID 的普通接口 + ComVisible 让运行时生成 CCW。
    [Guid("41D949AB-9862-444A-80F6-C261334DA5EB"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown), ComVisible(true)]
    internal interface IActivateCompletionHandler
    {
        [PreserveSig] int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation);
    }

    internal sealed class ActivateCompletionHandler : IActivateCompletionHandler
    {
        public int Result = unchecked((int)0x8000FFFF); // E_UNEXPECTED
        public IAudioClient Client;
        public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);

        public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
        {
            try
            {
                int hr = operation.GetActivateResult(out int hrActivate, out object unk);
                Result = hr == HResult.S_OK ? hrActivate : hr;
                if (Result == HResult.S_OK && unk is IAudioClient client)
                    Client = client;
            }
            catch (Exception e)
            {
                Result = unchecked((int)0x80004005);
                LastException = e;
            }
            finally
            {
                Done.Set();
            }
            return HResult.S_OK;
        }

        public Exception LastException;
    }

    // ────────────────────────────── P/Invoke ──────────────────────────────

    internal static class NativeMethods
    {
        [DllImport("MMDevAPI.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        public static extern int ActivateAudioInterfaceAsync(
            [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfaceId,
            ref Guid iid,
            ref PropVariant activationParams,
            [MarshalAs(UnmanagedType.Interface)] IActivateCompletionHandler completionHandler,
            out IActivateAudioInterfaceAsyncOperation operation);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry32 entry);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry32 entry);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentProcessId();
    }

    // ────────────────────────────── 组合工具 ──────────────────────────────

    internal static class AudioEndpoints
    {
        /// <summary>audioclientactivationparams.h: VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK</summary>
        public const string VadProcessLoopback = "VAD\\Process_Loopback";

        public static readonly Guid IidAudioClient = new Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
        public static readonly Guid IidAudioCaptureClient = new Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317");
        public static readonly Guid IidAudioSessionManager2 = new Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");

        public static IMMDevice DefaultRenderDevice()
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            int hr = enumerator.GetDefaultAudioEndpoint(NativeConst.ERender, NativeConst.EConsole, out var device);
            if (hr != HResult.S_OK)
                throw new InvalidOperationException($"GetDefaultAudioEndpoint 失败: 0x{hr:X8}");
            return device;
        }

        /// <summary>激活"按进程回环"捕获客户端。includeTree=true 捕获目标进程树，false 捕获其余全部。</summary>
        public static IAudioClient ActivateProcessLoopback(int targetPid, bool includeTree)
        {
            var activation = new AudioClientActivationParams
            {
                ActivationType = NativeConst.AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK,
                TargetProcessId = (uint)targetPid,
                ProcessLoopbackMode = includeTree
                    ? NativeConst.AUDIOCLIENT_PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE
                    : NativeConst.AUDIOCLIENT_PROCESS_LOOPBACK_MODE_EXCLUDE_TARGET_PROCESS_TREE,
            };
            int size = Marshal.SizeOf<AudioClientActivationParams>();
            IntPtr pActivation = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(activation, pActivation, false);
                var pv = new PropVariant
                {
                    vt = NativeConst.VT_BLOB,
                    blob = new Blob { cbSize = (uint)size, pBlobData = pActivation },
                };
                Guid iid = IidAudioClient;
                var handler = new ActivateCompletionHandler();
                int hr = NativeMethods.ActivateAudioInterfaceAsync(
                    VadProcessLoopback, ref iid, ref pv, handler, out _);
                if (hr != HResult.S_OK)
                    throw new InvalidOperationException($"ActivateAudioInterfaceAsync 失败: 0x{hr:X8}");

                if (!handler.Done.Wait(5000))
                    throw new InvalidOperationException("激活完成回调 5 秒超时");
                if (handler.Result != HResult.S_OK)
                    throw new InvalidOperationException(
                        $"进程回环激活结果失败: 0x{handler.Result:X8}" +
                        (handler.LastException != null ? " / " + handler.LastException.Message : ""));
                if (handler.Client == null)
                    throw new InvalidOperationException("激活回调未返回 IAudioClient");
                return handler.Client;
            }
            finally
            {
                Marshal.FreeHGlobal(pActivation);
            }
        }
    }

    internal static class ProcessTree
    {
        public struct ProcInfo
        {
            public int Pid;
            public int ParentPid;
            public string Name;
        }

        public static List<ProcInfo> Snapshot()
        {
            var list = new List<ProcInfo>();
            IntPtr snap = NativeMethods.CreateToolhelp32Snapshot(NativeConst.TH32CS_SNAPPROCESS, 0);
            if (snap == IntPtr.Zero || snap == new IntPtr(-1))
                return list;
            try
            {
                var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
                if (NativeMethods.Process32FirstW(snap, ref entry))
                {
                    do
                    {
                        list.Add(new ProcInfo
                        {
                            Pid = (int)entry.ProcessId,
                            ParentPid = (int)entry.ParentProcessId,
                            Name = entry.ExeFile ?? "",
                        });
                        entry.Size = (uint)Marshal.SizeOf<ProcessEntry32>();
                    } while (NativeMethods.Process32NextW(snap, ref entry));
                }
            }
            finally
            {
                NativeMethods.CloseHandle(snap);
            }
            return list;
        }

        /// <summary>找 parentPid 的子进程里名字含 namePart（不区分大小写）的，返回第一个。</summary>
        public static int FindChildByName(int parentPid, string namePart)
        {
            foreach (var p in Snapshot())
            {
                if (p.ParentPid == parentPid &&
                    p.Name.IndexOf(namePart, StringComparison.OrdinalIgnoreCase) >= 0)
                    return p.Pid;
            }
            return -1;
        }

        /// <summary>rootPid 及其全部子孙进程的 pid 集合。</summary>
        public static HashSet<int> CollectTree(int rootPid)
        {
            var all = Snapshot();
            var children = new Dictionary<int, List<int>>();
            foreach (var p in all)
            {
                if (!children.TryGetValue(p.ParentPid, out var list))
                    children[p.ParentPid] = list = new List<int>();
                list.Add(p.Pid);
            }
            var result = new HashSet<int>();
            var stack = new Stack<int>();
            stack.Push(rootPid);
            while (stack.Count > 0)
            {
                int pid = stack.Pop();
                if (!result.Add(pid)) continue;
                if (children.TryGetValue(pid, out var kids))
                    foreach (var k in kids) stack.Push(k);
            }
            return result;
        }
    }

    /// <summary>会话音量/静音：枚举默认渲染设备上的音频会话，按 pid 改音量/静音并可恢复。
    /// 实测（CaptureProbe --selftest）：静音发生在进程回环抽取点之前——SetMute 会把
    /// 捕获数据一起清零。因此消双响用"ε 音量 + 数字补偿增益"，不用 SetMute。</summary>
    internal sealed class SessionMuter : IDisposable
    {
        private IMMDevice _device;
        private IAudioSessionManager2 _manager;
        private readonly List<(IAudioSessionControl2 ctl, ISimpleAudioVolume vol, bool muted, float savedVolume)> _changed = new();
        private readonly HashSet<IntPtr> _recorded = new();
        private bool _disposed;

        public SessionMuter()
        {
            Mta.Run(() =>
            {
                _device = AudioEndpoints.DefaultRenderDevice();
                Guid iid = AudioEndpoints.IidAudioSessionManager2;
                int hr = _device.Activate(ref iid, NativeConst.CLSCTX_ALL, IntPtr.Zero, out object mgr);
                if (hr != HResult.S_OK)
                    throw new InvalidOperationException($"Activate(IAudioSessionManager2) 失败: 0x{hr:X8}");
                _manager = (IAudioSessionManager2)mgr;
            });
        }

        /// <summary>把属于 pids 的会话音量压到 level（ε 音量消双响）；返回改到的会话数。
        /// 原音量只记录一次（重复调用不会把 ε 记成"原音量"）。</summary>
        public int SetVolumePids(HashSet<int> pids, float level)
        {
            return Mta.Run(() => ForEachPid(pids, (vol, ctl2) =>
            {
                bool first = _recorded.Add(PointerOf(ctl2));
                vol.GetMasterVolume(out float saved);
                if (vol.SetMasterVolume(level, IntPtr.Zero) == HResult.S_OK)
                {
                    if (first) _changed.Add((ctl2, vol, false, saved));
                    return true;
                }
                return false;
            }));
        }

        /// <summary>强制归一：解除静音并把音量设为 1（不记录；用于清掉 Windows 持久化的应用音量）。</summary>
        public int NormalizePids(HashSet<int> pids)
        {
            return Mta.Run(() => ForEachPid(pids, (vol, ctl2) =>
            {
                vol.SetMute(false, IntPtr.Zero);
                vol.SetMasterVolume(1f, IntPtr.Zero);
                return true;
            }));
        }

        /// <summary>静音所有属于 pids 的会话（实验用；会杀捕获）。返回静音到的会话数。</summary>
        public int MutePids(HashSet<int> pids)
        {
            return Mta.Run(() => ForEachPid(pids, (vol, ctl2) =>
            {
                if (vol.SetMute(true, IntPtr.Zero) == HResult.S_OK)
                {
                    bool first = _recorded.Add(PointerOf(ctl2));
                    if (first) _changed.Add((ctl2, vol, true, 1f));
                    return true;
                }
                return false;
            }));
        }

        /// <summary>列出默认设备上全部会话（pid/音量/静音）——诊断漏压导致双响用。</summary>
        public string DescribeAll()
        {
            return Mta.Run(() =>
            {
                var sb = new System.Text.StringBuilder();
                if (_manager.GetSessionEnumerator(out var enumerator) != HResult.S_OK) return "枚举失败";
                if (enumerator.GetCount(out int n) != HResult.S_OK) return "枚举失败";
                for (int i = 0; i < n; i++)
                {
                    if (enumerator.GetSession(i, out var ctl) != HResult.S_OK || ctl == null) continue;
                    if (!(ctl is IAudioSessionControl2 ctl2)) continue;
                    if (ctl2.GetProcessId(out uint pid) != HResult.S_OK) continue;
                    float vol = -1f;
                    int muted = -1;
                    if (ctl is ISimpleAudioVolume v)
                    {
                        v.GetMasterVolume(out vol);
                        bool m = false;
                        if (v.GetMute(out m) == HResult.S_OK) muted = m ? 1 : 0;
                    }
                    sb.Append($"[pid={pid} vol={vol:F3} mute={muted}] ");
                }
                return sb.Length > 0 ? sb.ToString() : "(无会话)";
            });
        }

        private static IntPtr PointerOf(object comObj)
        {
            try { return Marshal.GetIUnknownForObject(comObj); }
            catch { return IntPtr.Zero; }
        }

        private int ForEachPid(HashSet<int> pids, Func<ISimpleAudioVolume, IAudioSessionControl2, bool> action)
        {
            int count = 0;
            int hr = _manager.GetSessionEnumerator(out var enumerator);
            if (hr != HResult.S_OK) return 0;
            hr = enumerator.GetCount(out int n);
            if (hr != HResult.S_OK) return 0;
            for (int i = 0; i < n; i++)
            {
                if (enumerator.GetSession(i, out var ctl) != HResult.S_OK || ctl == null) continue;
                if (!(ctl is IAudioSessionControl2 ctl2)) continue;
                if (ctl2.GetProcessId(out uint pid) != HResult.S_OK) continue;
                if (!pids.Contains((int)pid)) continue;
                if (!(ctl is ISimpleAudioVolume vol)) continue;
                if (action(vol, ctl2)) count++;
            }
            return count;
        }

        /// <summary>恢复本对象改过的全部会话（音量与静音）。</summary>
        public void RestoreAll()
        {
            Mta.Run(() =>
            {
                foreach (var (_, vol, muted, saved) in _changed)
                {
                    try
                    {
                        if (muted) vol.SetMute(false, IntPtr.Zero);
                        else vol.SetMasterVolume(saved, IntPtr.Zero);
                    }
                    catch { /* 会话可能已消失 */ }
                }
                _changed.Clear();
            });
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Mta.Run(() =>
            {
                RestoreAll();
                if (_device != null && Marshal.IsComObject(_device)) Marshal.ReleaseComObject(_device);
                if (_manager != null && Marshal.IsComObject(_manager)) Marshal.ReleaseComObject(_manager);
            });
        }
    }
}
