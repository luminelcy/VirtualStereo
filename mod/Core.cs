// VirtualStereo —— 观影派对虚拟立体声：
// 把 Vuplex WebView 桥进程的视频音频（WASAPI 进程树回环捕获）送进 Unity，
// 用摆在观影屏幕两侧的一对 AudioSource 做 3D 定位，声音"从屏幕/虚拟音箱发出"。
//
// 链路：桥进程树音频 → WASAPI 进程回环 → L/R 环形缓冲 → Unity AudioSource（空间化）
// 双响消除：ε 音量压原声 + 数字补偿增益（实测 SetMute 会把捕获一起清零，不能用静音）。
//
// F9 = 总开关（关闭时恢复原声直出）。日志含 RMS 巡检，用于观察静音实验结果。
using System;
using System.Collections.Generic;
using MelonLoader;
using UnityEngine;
using VirtualStereo.Capture;

[assembly: MelonInfo(typeof(VirtualStereo.Core), "VirtualStereo", "0.1.0", "kasa", null)]
[assembly: MelonGame("gogh Japan", "gogh")]
[assembly: MelonPriority(300)]

namespace VirtualStereo
{
    public class Core : MelonMod
    {
        private static bool _enabled = true;
        private static bool _silenced;   // 原声是否已被压到 ε（只有双耳引擎就绪后才做）
        private static bool _inputBroken;
        private static ProcessLoopbackCapture _capture;
        private static int _bridgePid = -1;
        private static long _nextBridgeScanAt;
        private static int _consecutiveCaptureFails;

        public override void OnInitializeMelon()
        {
            // core 的日志出口接上 MelonLoader（core 不能依赖 MelonLoader，由壳注入）
            VsLog.OnInfo = m => MelonLogger.Msg(m);
            VsLog.OnError = m => MelonLogger.Error(m);

            // MelonLogger.Msg("[VirtualStereo] 已加载：F9 = 虚拟立体声开关");
        }

        public override void OnUpdate()
        {
            if (!_inputBroken)
            {
                try
                {
                    if (Input.GetKeyDown(KeyCode.F9))
                        Toggle();
#if VS_DEV
                    if (Input.GetKeyDown(KeyCode.F10))
                        DebugMenu.Toggle();
#endif
                }
                catch (Exception e)
                {
                    _inputBroken = true;
                    MelonLogger.Error("[VirtualStereo] 读按键失败，F9/F10 失效: " + e);
                }
            }

            if (!_enabled) return;

            long now = Environment.TickCount64;

            // 双耳引擎刚就绪就立刻压原声（晚了会有一小段原声+双耳声叠加）
            if (!_silenced && _capture != null && AudioEngine.HrtfReady)
            {
                _silenced = true;
                try { _capture.RefreshSilence(0.001f); } catch { }
            }

            if (now >= _nextBridgeScanAt)
            {
                _nextBridgeScanAt = now + 2000;
                ScanBridge();
            }

            if (_capture != null)
            {
                if (!_capture.Running)
                {
                    MelonLogger.Warning("[VirtualStereo] 捕获线程退出: " + (_capture.LastError ?? "未知原因"));
                    ReleaseCapture();
                }
                else
                {
                    AudioEngine.Tick();
                }
            }
        }

        private static void Toggle()
        {
            _enabled = !_enabled;
            MelonLogger.Msg("[VirtualStereo] F9: " + (_enabled ? "开" : "关（已恢复原声直出）"));
            if (!_enabled)
            {
                ReleaseCapture();
                _nextBridgeScanAt = 0;
            }
        }

        /// <summary>找 Vuplex WebView 桥进程（gogh.exe 的子进程），变化时重启捕获。</summary>
        private static void ScanBridge()
        {
            int myPid = (int)NativeMethods.GetCurrentProcessId();
            int bridge = ProcessTree.FindChildByName(myPid, "Vuplex");
            if (bridge < 0)
            {
                if (_bridgePid > 0)
                {
                    // MelonLogger.Msg("[VirtualStereo] 桥进程消失，暂停捕获");
                    ReleaseCapture();
                    _bridgePid = -1;
                }
                return;
            }
            if (bridge == _bridgePid && _capture != null)
            {
                // 只有双耳引擎真的就绪了才去压原声——否则游戏会静音又没人补声。
                // 视频声音的会话是播视频时才出现的，所以周期性补压 ε 并同步补偿增益。
                if (AudioEngine.HrtfReady)
                {
                    _capture.RefreshSilence(0.001f);
                    _silenced = true;
                }
                return;
            }

            _bridgePid = bridge;
            StartCapture(bridge);
        }

        private static void StartCapture(int bridgePid)
        {
            ReleaseCapture();
            try
            {
                _capture = ProcessLoopbackCapture.Start(bridgePid, includeTree: true);
                AudioEngine.AttachCapture(_capture);
                _consecutiveCaptureFails = 0;
                // MelonLogger.Msg(
                    // $"[VirtualStereo] 捕获已启动: bridge pid={bridgePid}, {_capture.SampleRate}Hz x {_capture.CaptureChannels}ch");
                _silenced = false; // 等双耳引擎就绪（OnUpdate 里立刻补压，不等下一次扫描）
            }
            catch (Exception e)
            {
                _consecutiveCaptureFails++;
                MelonLogger.Error($"[VirtualStereo] 捕获启动失败(第 {_consecutiveCaptureFails} 次): {e.Message}");
                _capture = null;
                // 简单退避：失败后放慢重扫
                _nextBridgeScanAt = Environment.TickCount64 + Math.Min(30000, 2000 * _consecutiveCaptureFails);
            }
        }

        private static void ReleaseCapture()
        {
            if (_capture != null)
            {
                AudioEngine.DetachCapture();
                try { _capture.Dispose(); } catch { }
                _capture = null;
            }
            _silenced = false;
        }

        public override void OnGUI()
        {
#if VS_DEV
            try
            {
                DebugMenu.Draw();
            }
            catch (Exception e)
            {
                // IMGUI 出错不能刷屏，报一次关掉
                MelonLogger.Error("[VirtualStereo] 调试菜单渲染失败: " + e);
                DebugMenu.Visible = false;
            }
#endif
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            // 屏幕 transform 随场景失效，AudioEngine 会自动重找；捕获不受场景影响
            _nextBridgeScanAt = 0;
        }

        public override void OnApplicationQuit()
        {
            ReleaseCapture();
        }

        public override void OnDeinitializeMelon()
        {
            ReleaseCapture();
        }
    }
}
