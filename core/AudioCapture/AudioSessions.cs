// 音频会话查询：枚举默认渲染设备上的会话（pid/音量/静音/是否正在播放）。
// 用途：进程选择器标注"正在发声"的程序；只读，不改任何状态。
using System;
using System.Collections.Generic;
using System.Text;

namespace VirtualStereo.Capture
{
    public struct AudioSession
    {
        public int Pid;
        public float Volume;
        public bool Muted;
        public bool Active; // 会话正在出声（AudioSessionState.Active）
    }

    public static class AudioSessions
    {
        /// <summary>查询当前全部音频会话（MTA 线程执行；失败返回空表）。</summary>
        public static List<AudioSession> Query()
        {
            var list = new List<AudioSession>();
            try
            {
                Mta.Run(() =>
                {
                    IMMDevice device = AudioEndpoints.DefaultRenderDevice();
                    Guid iid = AudioEndpoints.IidAudioSessionManager2;
                    int hr = device.Activate(ref iid, NativeConst.CLSCTX_ALL, IntPtr.Zero, out object mgrObj);
                    if (hr != HResult.S_OK) return;
                    var mgr = (IAudioSessionManager2)mgrObj;
                    if (mgr.GetSessionEnumerator(out var enumerator) != HResult.S_OK) return;
                    if (enumerator.GetCount(out int n) != HResult.S_OK) return;
                    for (int i = 0; i < n; i++)
                    {
                        if (enumerator.GetSession(i, out var ctl) != HResult.S_OK || ctl == null) continue;
                        if (!(ctl is IAudioSessionControl2 ctl2)) continue;
                        if (ctl2.GetProcessId(out uint pid) != HResult.S_OK) continue;

                        float vol = 1f;
                        bool muted = false;
                        if (ctl is ISimpleAudioVolume v)
                        {
                            v.GetMasterVolume(out vol);
                            bool m = false;
                            if (v.GetMute(out m) == HResult.S_OK) muted = m;
                        }
                        ctl.GetState(out int state); // 0=Inactive 1=Active 2=Expired
                        list.Add(new AudioSession
                        {
                            Pid = (int)pid,
                            Volume = vol,
                            Muted = muted,
                            Active = state == 1,
                        });
                    }
                });
            }
            catch { }
            return list;
        }

        /// <summary>正在发声的 pid 集合（查询失败返回空）。</summary>
        public static HashSet<int> ActivePids()
        {
            var set = new HashSet<int>();
            foreach (var s in Query())
                if (s.Active) set.Add(s.Pid);
            return set;
        }

        /// <summary>人类可读的会话清单（诊断用）。</summary>
        public static string Describe()
        {
            var sb = new StringBuilder();
            foreach (var s in Query())
                sb.Append($"[pid={s.Pid} vol={s.Volume:F2}{(s.Muted ? " 静音" : "")}{(s.Active ? " 播放中" : "")}] ");
            return sb.Length > 0 ? sb.ToString() : "(无会话)";
        }
    }
}
