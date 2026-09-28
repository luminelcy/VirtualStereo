// 桌面应用状态与音频处理循环：
//   捕获环 → 前置增益 → [校准扫频注入] → 指向性/距离 → 直通/双耳/ITD+ILD
//   → 两耳 tap → 校准 EQ → 后处理 PEQ → 后置增益 → WASAPI 播放
// UI 线程只改字段；音频线程每块读取（字段竞争为良性，参数变更对齐到块边界）。
using System;
using VirtualStereo.Capture;
using VirtualStereo.Dsp;
using VirtualStereo.Phonon;

namespace VirtualStereo.Desktop
{
    internal sealed class DesktopApp : IDisposable
    {
        public enum Mode { Passthrough = 0, FullHrtf = 1, ItdIld = 2 }

        // ── 参数（UI 写 / 音频线程读）──
        public volatile int CurrentMode = (int)Mode.FullHrtf;
        public volatile float PreGainDb = -6f;   // 模拟之前（输入电平）
        public volatile float PostGainDb = 0f;   // 模拟之后（输出电平）
        public volatile float AzL = -30f, AzR = 30f, ElL = 0f, ElR = 0f;
        public volatile float DistL = 2f, DistR = 2f; // 音箱距离（米；2m 为参考，反比衰减）
        // 音箱朝向（每源独立；mode: 0=朝向听者 1=固定朝前 2=手动）
        // 默认 固定朝前：朝向听者时 θ=0 增益恒 1，指向性听不出效果
        public volatile int AimModeL = 1, AimModeR = 1;
        public volatile float AimAzL = 0f, AimElL = 0f, AimAzR = 0f, AimElR = 0f;
        public volatile int Interpolation = 1; // 0 最近邻 / 1 双线性
        public volatile bool SilenceOriginal = true;

        // 音箱指向性（分频模拟频率相关指向性；配置在"音箱设置"面板改）
        // 注意：滤波状态每声源一份——共用会互相污染滤波器记忆（调制失真）
        public readonly DirectivityProcessor Directivity = new DirectivityProcessor();
        private readonly DirectivityState _dirStateL = new DirectivityState();
        private readonly DirectivityState _dirStateR = new DirectivityState();

        // 人头两耳监视/录制（tap 点：空间模拟之后、校准 EQ 与后置增益之前——
        // 始终是裸双耳信号；校准 EQ 只作用于最后的耳机回放）
        public readonly EarMonitor Monitor = new EarMonitor();

        // 校准：扫频测量两耳频响 → 输出侧反相 EQ 拉平（见 Calibration.cs）
        public readonly Calibration Cal = new Calibration();

        // 后处理 PEQ：校准之后、播放之前的输出调音（见 PostEq.cs）
        public readonly PostEq Post = new PostEq();

        public int CaptureRate => _capture != null && _capture.SampleRate > 0 ? _capture.SampleRate : 48000;
        public int OutputRate => _outputRate;

        // ── 状态读数（音频线程写 / UI 读）──
        public volatile float OutPeak;
        public volatile float OutRms;
        public float CaptureRms => _capture?.Rms ?? 0f;
        public float CaptureL => _capture?.RmsL ?? 0f;
        public float CaptureR => _capture?.RmsR ?? 0f;
        public bool Capturing => _capture != null;
        public string PlayerError => _player?.LastError;
        public string SessionInfo => _capture?.SessionInfo ?? "";

        private ProcessLoopbackCapture _capture;
        private WasapiPlayer _player;
        private readonly object _dspLock = new object();
        private int _outputRate = 48000; // 播放侧采样率（无捕获时校准也走它）

        private const int Chunk = 256;
        private readonly float[] _monoL = new float[Chunk];
        private readonly float[] _monoR = new float[Chunk];
        private readonly float[] _stereo = new float[Chunk * 2];
        private int _stagePos = Chunk; // == Chunk 表示暂存区已耗尽
        private long _nextSilenceAt;

        public string StartCapture(int pid)
        {
            Monitor.StopRecording(); // 捕获切换会打断录制，先收尾保存
            StopCapture();
            try
            {
                _capture = ProcessLoopbackCapture.Start(pid, includeTree: true);
                _outputRate = _capture.SampleRate > 0 ? _capture.SampleRate : 48000;
                if (!BinauralEngine.Initialized)
                {
                    if (!BinauralEngine.Init(_outputRate, Chunk))
                    {
                        StopCapture();
                        return "双耳引擎初始化失败（phonon.dll 在程序目录吗？）";
                    }
                    BinauralEngine.SetInterpolation(Interpolation);
                }
                _player = new WasapiPlayer(_outputRate, FillInterleaved);
                _stagePos = Chunk;
                _nextSilenceAt = 0;
                return $"捕获中: pid={pid}  {_outputRate}Hz  播放缓冲 {_player.BufferFrames} 帧";
            }
            catch (Exception e)
            {
                StopCapture();
                return "启动失败: " + e.Message;
            }
        }

        public void StopCapture()
        {
            Cal.Cancel(); // 输出链路拆了，扫频无处可走
            Monitor.StopRecording();
            try { _player?.Dispose(); } catch { }
            try { _capture?.Dispose(); } catch { } // 恢复被压过的会话音量
            _player = null;
            _capture = null;
        }

        /// <summary>只把输出链路拉起来（校准用：扫频走的就是这条链，无需音源）。</summary>
        public string EnsureOutput()
        {
            if (_player != null) return null;
            try
            {
                const int rate = 48000;
                if (!BinauralEngine.Initialized)
                {
                    if (!BinauralEngine.Init(rate, Chunk))
                        return "双耳引擎初始化失败（phonon.dll 在程序目录吗？）";
                    BinauralEngine.SetInterpolation(Interpolation);
                }
                _outputRate = rate;
                _player = new WasapiPlayer(rate, FillInterleaved);
                _stagePos = Chunk;
                return null;
            }
            catch (Exception e)
            {
                return "输出启动失败: " + e.Message;
            }
        }

        /// <summary>开始校准扫频。返回 null=成功 / 错误信息。</summary>
        public string StartCalibration()
        {
            string err = EnsureOutput();
            if (err != null) return err;
            return Cal.Start(_outputRate);
        }

        public void CancelCalibration() => Cal.Cancel();

        public void ApplyInterpolation()
        {
            BinauralEngine.SetInterpolation(Interpolation);
        }

        public bool LoadSofa(string path) => BinauralEngine.LoadSofa(path == null ? null : path);

        /// <summary>WASAPI 拉数据回调（音频线程）：按块填充交错立体声。</summary>
        private void FillInterleaved(float[] buffer, int frames)
        {
            // ε 音量压原声（消双响）：会话是动态出现的，周期补压
            if (SilenceOriginal && _capture != null && Environment.TickCount64 >= _nextSilenceAt)
            {
                _nextSilenceAt = Environment.TickCount64 + 2000;
                _capture.RefreshSilence(0.001f);
            }

            int done = 0;
            while (done < frames)
            {
                if (_stagePos >= Chunk) FillStage();
                int n = Math.Min(frames - done, Chunk - _stagePos);
                Array.Copy(_stereo, _stagePos * 2, buffer, done * 2, n * 2);
                done += n;
                _stagePos += n;
            }
        }

        private void FillStage()
        {
            var cap = _capture;
            int rate = cap != null && cap.SampleRate > 0 ? cap.SampleRate : _outputRate;

            if (cap != null)
            {
                bool buffered = cap.RingL.Available >= rate / 8 && cap.RingR.Available >= rate / 8;
                if (buffered)
                {
                    cap.RingL.Read(_monoL, Chunk);
                    cap.RingR.Read(_monoR, Chunk);
                }
                else
                {
                    Array.Clear(_monoL, 0, Chunk);
                    Array.Clear(_monoR, 0, Chunk);
                }
            }
            else
            {
                // 无捕获（纯校准）：链路照跑，输入静音
                Array.Clear(_monoL, 0, Chunk);
                Array.Clear(_monoR, 0, Chunk);
            }

            float g = (float)Math.Pow(10.0, PreGainDb / 20.0);
            for (int i = 0; i < Chunk; i++) { _monoL[i] *= g; _monoR[i] *= g; }

            // 校准扫频：替换两路音箱信号（扫频期间听不到源内容）
            Cal.Generate(_monoL, _monoR, Chunk, rate);

            // 音箱指向性（分频）：前置增益之后、空间模拟之前（朝向每源独立）
            if (Directivity.Enabled)
            {
                Directivity.Process(_dirStateL, _monoL, Chunk, rate, AzL, ElL, AimModeL, AimAzL, AimElL);
                Directivity.Process(_dirStateR, _monoR, Chunk, rate, AzR, ElR, AimModeR, AimAzR, AimElR);
            }

            // 音箱距离衰减：声学反比（2m 参考，距离每翻倍 -6dB）
            float dl = 2f / Math.Max(0.3f, DistL);
            float dr = 2f / Math.Max(0.3f, DistR);
            if (dl != 1f)
                for (int i = 0; i < Chunk; i++) _monoL[i] *= dl;
            if (dr != 1f)
                for (int i = 0; i < Chunk; i++) _monoR[i] *= dr;

            lock (_dspLock)
            {
                if (CurrentMode == (int)Mode.Passthrough)
                {
                    for (int i = 0; i < Chunk; i++)
                    {
                        _stereo[i * 2] = _monoL[i];
                        _stereo[i * 2 + 1] = _monoR[i];
                    }
                }
                else
                {
                    // 方向由当前角度实时换算（头坐标系：X右 Y上 Z前）
                    BinauralEngine.SetDirections(AngToDir(AzL, ElL), AngToDir(AzR, ElR));
                    BinauralEngine.Process(_monoL, _monoR, _stereo, Chunk,
                        CurrentMode == (int)Mode.FullHrtf ? SaiMode.FullHrtf : SaiMode.ItdIld);
                }
            }

            // 人头两耳 tap：空间模拟之后、校准 EQ 与后置增益之前
            // （裸双耳信号：监视/录制/校准测量都取这里）
            Monitor.Push(_stereo, Chunk, rate);
            Cal.OnEars(_stereo, Chunk);

            // 校准 EQ：输出侧把两耳频响拉平（校准模式开时生效）
            Cal.Process(_stereo, Chunk);

            // 后处理 PEQ：输出调音（校准之后、播放之前；典型=低架补回被拉平的低音）
            if (Post.Enabled) Post.Process(_stereo, Chunk, rate);

            // 后置增益：模拟之后、出声之前（输出电平）
            float pg = (float)Math.Pow(10.0, PostGainDb / 20.0);
            if (pg != 1f)
            {
                for (int i = 0; i < Chunk * 2; i++) _stereo[i] *= pg;
            }

            // 输出电平统计
            float peak = 0f;
            double sq = 0;
            for (int i = 0; i < Chunk * 2; i++)
            {
                float v = _stereo[i];
                float a = v < 0 ? -v : v;
                if (a > peak) peak = a;
                sq += v * v;
            }
            OutPeak = peak;
            OutRms = (float)Math.Sqrt(sq / (Chunk * 2));
            _stagePos = 0;
        }

        private static SaiVector3 AngToDir(float az, float el)
        {
            double a = az * Math.PI / 180.0;
            double e = el * Math.PI / 180.0;
            double ce = Math.Cos(e);
            return new SaiVector3(
                (float)(Math.Sin(a) * ce),
                (float)Math.Sin(e),
                (float)(Math.Cos(a) * ce));
        }

        public void Dispose() => StopCapture();
    }
}
