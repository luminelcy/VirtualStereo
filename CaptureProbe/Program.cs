// VirtualStereo 捕获链路自测台（开发工具，不随 mod 发布）。
//
//   CaptureProbe --selftest          自己放正弦音 + 捕获自己进程树 + 静音实验（关键实验）
//   CaptureProbe --name chrome       捕获名字含 chrome 的进程树（外部声源联调）
//   CaptureProbe --pid 1234          捕获指定 pid 的进程树
//   CaptureProbe --exclude-self      捕获除自己之外的全部（相当于整机回环，用于摸设备格式）
//
// --selftest 回答的问题：会话静音（ISimpleAudioVolume::SetMute）之后，
// 进程回环捕获到的数据是否也变静音？
//   RMS 不掉 → 抽取点在静音之前 → "捕获 + 静音原声"方案成立（mod 的默认路径）
//   RMS 归零 → 抽取点在静音之后 → 静音会把捕获一起杀掉，得走"改路由到虚拟设备"路线
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using VirtualStereo.Capture;

namespace CaptureProbe
{
    internal static class Program
    {
        private static int _pid = -1;
        private static bool _includeTree = true;
        private static string _name;
        private static bool _selftest;
        private static bool _volLaw;
        private static bool _mixFormat;
        private static bool _sta;
        private static int _seconds = 10;
        private static bool _muteOn;

        private static int Main(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--selftest": _selftest = true; break;
                    case "--vol-law": _volLaw = true; break;
                    case "--mix-format": _mixFormat = true; break;
                    case "--sta": _sta = true; break;
                    case "--pid": _pid = int.Parse(args[++i]); break;
                    case "--name": _name = args[++i]; break;
                    case "--exclude-self": _pid = (int)NativeMethods.GetCurrentProcessId(); _includeTree = false; break;
                    case "--seconds": _seconds = int.Parse(args[++i]); break;
                    case "--mute-on": _muteOn = true; break;
                    default:
                        Console.WriteLine("未知参数: " + args[i]);
                        return 2;
                }
            }

            try
            {
                // --sta：在 STA 线程上跑（Unity 主线程就是 STA——游戏里激活失败的头号嫌疑）
                if (_sta)
                {
                    int rc = -1;
                    Exception err = null;
                    var t = new Thread(() =>
                    {
                        try
                        {
                            Console.WriteLine($"[STA 线程] Apartment={Thread.CurrentThread.GetApartmentState()}");
                            rc = _selftest ? SelfTest() : _volLaw ? VolumeLawTest() : Run();
                        }
                        catch (Exception e) { err = e; }
                    });
                    t.SetApartmentState(ApartmentState.STA);
                    t.Start();
                    t.Join();
                    if (err != null) { Console.Error.WriteLine("[STA 线程异常] " + err); return 1; }
                    return rc;
                }
                if (_selftest) return SelfTest();
                if (_volLaw) return VolumeLawTest();
                return Run();
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("失败: " + e);
                return 1;
            }
        }

        private static int Run()
        {
            int pid = _pid;
            if (!string.IsNullOrEmpty(_name))
            {
                pid = -1;
                foreach (var p in ProcessTree.Snapshot())
                {
                    if (p.Name.IndexOf(_name, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        pid = p.Pid;
                        Console.WriteLine($"匹配进程: pid={p.Pid} parent={p.ParentPid} name={p.Name}");
                        if (_includeTree) break; // include 模式取第一个即可
                    }
                }
                if (pid < 0)
                {
                    Console.Error.WriteLine($"找不到名字含 '{_name}' 的进程");
                    return 1;
                }
            }
            if (pid < 0)
            {
                Console.Error.WriteLine("需要 --pid / --name / --exclude-self / --selftest 之一");
                return 2;
            }

            Console.WriteLine($"捕获启动: pid={pid} includeTree={_includeTree}");
            using var cap = ProcessLoopbackCapture.Start(pid, _includeTree);
            Console.WriteLine($"格式: {cap.SampleRate} Hz, {cap.CaptureChannels} ch (设备混音格式)");

            using var muter = _muteOn ? new SessionMuter() : null;
            bool muted = false;

            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < _seconds)
            {
                Thread.Sleep(500);
                if (_muteOn && !muted && sw.Elapsed.TotalSeconds > 3)
                {
                    var pids = ProcessTree.CollectTree(pid);
                    int n = muter.MutePids(pids);
                    Console.WriteLine($"[{sw.Elapsed.TotalSeconds,6:F1}s] 已静音 {n} 个会话 (pid 树含 {pids.Count} 个进程)");
                    muted = true;
                }
                if (_muteOn && muted && sw.Elapsed.TotalSeconds > 6)
                {
                    muter.RestoreAll();
                    Console.WriteLine($"[{sw.Elapsed.TotalSeconds,6:F1}s] 已恢复静音");
                    muted = false;
                    _muteOn = false;
                }
                Console.WriteLine($"[{sw.Elapsed.TotalSeconds,6:F1}s] RMS={cap.Rms:F5}  样本={cap.PacketsReceived}  L缓冲={cap.RingL.Available}  {cap.LastError}");
            }
            if (muted) muter.RestoreAll();
            return 0;
        }

        /// <summary>
        /// 端到端实验：本进程放正弦音 → 捕获自己进程树 →
        ///   ① ε 音量 + 补偿增益（mod 的消双响策略）是否等价还原；
        ///   ② SetMute 静音对捕获的影响（对照；已知会把捕获一起清零）。
        /// 音量/增益统一由 ProcessLoopbackCapture 自己管理，避免两套恢复逻辑互相覆盖。
        /// </summary>
        private static int SelfTest()
        {
            int myPid = (int)NativeMethods.GetCurrentProcessId();
            Console.WriteLine($"=== selftest: pid={myPid} ===");

            Console.WriteLine("启动 440Hz 正弦音（60 秒缓冲）...");
            using var tone = new TonePlayer(48000, 440, 60);

            // Windows 会持久化应用会话音量/静音——先强制归一，避免上轮残留毁掉本轮基线
            using (var reset = new SessionMuter())
            {
                var selfPids = ProcessTree.CollectTree(myPid);
                int nReset = reset.NormalizePids(selfPids);
                Console.WriteLine($"已归一 {nReset} 个本进程会话（解除持久化静音/音量）");
            }
            Thread.Sleep(500);

            Console.WriteLine("启动进程回环捕获（include 自己的树）...");
            using var cap = ProcessLoopbackCapture.Start(myPid, includeTree: true);
            Console.WriteLine($"捕获格式: {cap.SampleRate} Hz, {cap.CaptureChannels} ch");

            float baseline = Measure(cap, tone, 3.0, "基线（发声中）");
            if (baseline < 1e-4)
            {
                Console.WriteLine("结论: 基线 RMS≈0 —— 捕获链路本身有问题（设备/激活/格式），先别管静音问题");
                return 1;
            }

            // ① ε 音量 + 补偿增益（唯一会话管理者 = cap）
            const float epsilon = 0.001f;
            // waveOut 流的定律是 v²（CEF 流才是 v¹）——探针显式指定平方补偿
            int n = cap.RefreshSilence(epsilon, compensationGain: 1f / (epsilon * epsilon));
            Console.WriteLine($"RefreshSilence({epsilon}) → {n} 个会话, OutputGain={cap.OutputGain}, err={cap.LastError ?? "无"}");
            float epsGain = Measure(cap, tone, 4.0, "ε音量+补偿增益（应≈基线；前 2s 为音量斜坡+增益延迟）");

            cap.RestoreTargetSessions();
            Console.WriteLine($"RestoreTargetSessions → OutputGain={cap.OutputGain}, err={cap.LastError ?? "无"}");
            float restored = Measure(cap, tone, 2.0, "恢复原音量（应≈基线）");

            // ② SetMute 对照（仅静音，不动音量；结束后 RestoreAll 只撤销静音）
            var muter = new SessionMuter();
            var pids = ProcessTree.CollectTree(myPid);
            n = muter.MutePids(pids);
            Console.WriteLine($"已静音 {n} 个会话");
            float muted = Measure(cap, tone, 3.0, "SetMute 静音后（应≈0）");
            muter.RestoreAll();
            muter.Dispose();
            float after = Measure(cap, tone, 2.0, "恢复后（应≈基线）");

            Console.WriteLine();
            Console.WriteLine("=== 结论 ===");
            Console.WriteLine($"基线 RMS         = {baseline:F5}");
            Console.WriteLine($"ε音量+补偿 RMS   = {epsGain:F5}   （目标 ≈ 基线）");
            Console.WriteLine($"恢复音量 RMS     = {restored:F5}");
            Console.WriteLine($"SetMute 静音 RMS = {muted:F5}");
            Console.WriteLine($"最终恢复 RMS     = {after:F5}");

            bool epsOk = Math.Abs(epsGain - baseline) < baseline * 0.15f;
            bool restoreOk = Math.Abs(restored - baseline) < baseline * 0.15f;
            bool muteKills = muted < baseline * 0.05f;
            Console.WriteLine();
            if (epsOk)
                Console.WriteLine($"→ ε 音量 + 补偿增益成立（还原误差 {Math.Abs(epsGain - baseline) / baseline * 100:F1}%）：mod 消双响策略可用。");
            else
                Console.WriteLine($"→ ε 音量 + 补偿增益不成立（还原误差 {Math.Abs(epsGain - baseline) / baseline * 100:F1}%）。");
            if (restoreOk)
                Console.WriteLine("→ 音量恢复有效。");
            else
                Console.WriteLine("→ 警告：音量恢复异常（对照窗口不在基线附近）。");
            if (muteKills)
                Console.WriteLine("→ 确认：SetMute 会把捕获一起清零（抽取点在音量/静音之后），不能用静音消双响。");
            return epsOk && restoreOk ? 0 : 10;
        }

        /// <summary>
        /// 音量-幅度定律实验：会话音量 v 与捕获幅度的关系（v¹ / v² / …）。
        /// selftest 发现 v=0.001 时捕获衰减了 1e-6（像 v²），这里逐点标定。
        /// </summary>
        private static int VolumeLawTest()
        {
            int myPid = (int)NativeMethods.GetCurrentProcessId();
            ProcessLoopbackCapture.ForceMixFormat = _mixFormat;
            Console.WriteLine($"=== vol-law: pid={myPid} mixFormat={_mixFormat} ===");

            using var tone = new TonePlayer(48000, 440, 60);
            using (var reset = new SessionMuter())
                reset.NormalizePids(ProcessTree.CollectTree(myPid));
            Thread.Sleep(500);

            using var cap = ProcessLoopbackCapture.Start(myPid, includeTree: true);
            using var muter = new SessionMuter();
            var pids = ProcessTree.CollectTree(myPid);

            float baseline = Measure(cap, tone, 2.5, "v=1.0 基线");
            if (baseline < 1e-4)
            {
                Console.WriteLine("基线为 0，放弃");
                return 1;
            }
            Console.WriteLine($"{"v",8} {"RMS",12} {"比值",12} {"等效 v^n 的 n",14}");
            foreach (float v in new[] { 0.5f, 0.1f, 0.03f, 0.01f, 0.003f, 0.001f })
            {
                muter.SetVolumePids(pids, v);
                Thread.Sleep(300); // 等音量斜坡
                float rms = Measure(cap, tone, 2.0, $"v={v}");
                double ratio = rms / baseline;
                double n = ratio > 0 ? Math.Log(ratio) / Math.Log(v) : double.NaN;
                Console.WriteLine($"{v,8:F3} {rms,12:F6} {ratio,12:E3} {n,14:F2}");
            }
            muter.RestoreAll();
            muter.NormalizePids(pids);
            Measure(cap, tone, 1.5, "恢复确认");
            Console.WriteLine("完成。若 n≈1 → 幅度正比 v；n≈2 → 正比 v²（补偿增益要用 1/v²）。");
            return 0;
        }

        /// <summary>取窗口内"最后一个"读数（避开过渡样本）；同时打印播放位置与捕获增益。</summary>
        private static float Measure(ProcessLoopbackCapture cap, TonePlayer tone, double seconds, string label)
        {
            Console.WriteLine($"-- {label} --");
            float last = 0;
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < seconds)
            {
                Thread.Sleep(500);
                last = cap.Rms;
                Console.WriteLine(
                    $"   [{sw.Elapsed.TotalSeconds,4:F1}s] RMS={cap.Rms:F5} 样本={cap.PacketsReceived} " +
                    $"增益={cap.OutputGain:F0} 位置={tone.PositionSamples} err={cap.LastError ?? "无"}");
            }
            return last;
        }
    }
}
