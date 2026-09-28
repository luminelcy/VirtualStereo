// Unity 侧：把捕获到的 L/R PCM 送进两个 AudioSource，摆在观影屏幕两侧。
// 数据通路两级：
//   1) OnAudioFilterRead（低延迟，注入组件的 Unity 音频回调）
//   2) AudioClip 流式 SetData（回调不触发时的兜底，由 Core.OnUpdate 泵）
using System;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MelonLoader;
using UnityEngine;
using UnityEngine.SceneManagement;
using VirtualStereo.Capture;
using VirtualStereo.Phonon;
using Object = UnityEngine.Object;

namespace VirtualStereo
{
    internal static class AudioEngine
    {
        // 观影派对屏幕的物品编号（Prefab 名 P_RoomItem_<id>，见 gogh-物品模型库/geometry/room_item_bounds.csv）
        private static readonly string[] ScreenIds = { "02624", "02625", "02626", "02627", "02628" };

        private static ProcessLoopbackCapture _capture;
        private static GameObject _goL, _goR;
        private static AudioSource _srcL, _srcR;
        private static AudioClip _clipL, _clipR;
        private static bool _filterMode;
        private static long _filterFiredAt;
        private static long _startedAt;
        private static int _writePos;
        private static float[] _scratch = new float[4096];
        private static float[] _pumpScratch = new float[8192];
        private static int _filterChannels;
        private static Transform _screen;
        private static long _nextSpatialAt;
        private static long _nextStatAt;
        private static bool _injected;

        // ── 空间化模式（实验）──
        // Speakers  : 双音箱对（Unity 内置 3D panning，对照组）
        // FullHrtf  : 双耳完整 HRTF（Steam Audio）
        // ItdIld    : 双耳时间差+声级差（无耳廓频谱染色）
        public enum SpatialMode { Speakers = 0, FullHrtf = 1, ItdIld = 2 }

        public static SpatialMode Mode { get; private set; } = SpatialMode.Speakers;
        public static int Interpolation { get; private set; } = PhononNative.InterpolationBilinear;

        /// <summary>前置增益（dB，作用在进空间化模拟之前的原始 L/R 上；双耳 HRTF 相干叠加易爆电平，默认 -6dB 留余量）。</summary>
        public static float PreGainDb { get; set; } = -6f;

        /// <summary>前置增益（线性）。用 Math 而不是 Mathf（数学成员按惯例避开 IL2CPP）。</summary>
        public static float PreGainLinear => (float)Math.Pow(10.0, PreGainDb / 20.0);

        /// <summary>最近一次泵写入信号的峰值（模拟之后），供面板观察电平。</summary>
        public static float LastPeak { get; private set; }

        private static GameObject _goB;
        private static AudioSource _srcB;
        private static AudioClip _clipB;
        private static readonly float[] _blockStereo = new float[Block * 2];

        /// <summary>切换模式（重建输出对象；Tick 会自动重建）。返回 false = 双耳引擎不可用。</summary>
        public static bool SetMode(SpatialMode m)
        {
            if (m != SpatialMode.Speakers && !BinauralEngine.Initialized)
            {
                int rate = _capture != null && _capture.SampleRate > 0 ? _capture.SampleRate : 48000;
                if (!BinauralEngine.Init(rate, Block))
                    return false;
            }
            Mode = m;
            StopSources();
            return true;
        }

        public static void SetInterpolation(int interpolation)
        {
            Interpolation = interpolation;
            BinauralEngine.SetInterpolation(interpolation);
        }

        public static bool SourcesAlive => _srcL != null && _srcR != null;
        public static bool FilterMode => _filterMode;

        // ── 调试：手动坐标模式 + 标记球 ──
        private static bool _manualMode;
        private static Vector3 _manualL, _manualR;
        private static bool _markersOn = true; // 默认显示（上一版默认 false 且菜单不同步 → 球从没出现过）
        private static GameObject _markerL, _markerR;
        private static Transform _listener;

        public static bool ManualMode => _manualMode;

        public static void SetManualPositions(Vector3 l, Vector3 r)
        {
            _manualL = l;
            _manualR = r;
            _manualMode = true;
            ApplyManualPositions();
        }

        public static void ClearManual() => _manualMode = false;

        private static void ApplyManualPositions()
        {
            if (_srcL != null) { _srcL.transform.position = _manualL; _srcL.spatialBlend = 1f; }
            if (_srcR != null) { _srcR.transform.position = _manualR; _srcR.spatialBlend = 1f; }
        }

        public static void GetPositions(out Vector3 l, out Vector3 r, out Vector3 listener)
        {
            l = _srcL != null ? _srcL.transform.position : Vector3.zero;
            r = _srcR != null ? _srcR.transform.position : Vector3.zero;
            var lis = Object.FindObjectOfType<AudioListener>();
            listener = lis != null ? lis.transform.position : Vector3.zero;
        }

        public static bool MarkersOn
        {
            get => _markersOn;
            set
            {
                _markersOn = value;
                EnsureMarkers();
                if (_markerL != null) _markerL.SetActive(value);
                if (_markerR != null) _markerR.SetActive(value);
            }
        }

        private static void EnsureMarkers()
        {
            if (!_markersOn || _goL == null || _goR == null) return;
            if (_markerL == null) _markerL = MakeMarker("VS_Marker_L", _goL.transform, new Color(0.25f, 0.55f, 1f));
            if (_markerR == null) _markerR = MakeMarker("VS_Marker_R", _goR.transform, new Color(1f, 0.35f, 0.3f));

            // 朝听者方向偏 10cm，避免半个球埋进屏幕/墙里看不见
            if (_markerL != null) _markerL.transform.localPosition = NudgeTowardListener(_srcL);
            if (_markerR != null) _markerR.transform.localPosition = NudgeTowardListener(_srcR);
        }

        private static Vector3 NudgeTowardListener(AudioSource src)
        {
            if (src == null || _listener == null) return Vector3.zero;
            var from = src.transform.position;
            var to = _listener.position;
            float dx = to.x - from.x, dy = to.y - from.y, dz = to.z - from.z;
            float len = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (len < 1e-3f) return Vector3.zero;
            return new Vector3(dx / len * 0.1f, dy / len * 0.1f, dz / len * 0.1f);
        }

        private static GameObject MakeMarker(string name, Transform parent, Color color)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = name;
            var col = go.GetComponent<Collider>();
            if (col != null) col.enabled = false; // 绝不干扰游戏物理/交互
            go.transform.SetParent(parent, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localScale = new Vector3(0.25f, 0.25f, 0.25f);
            var rend = go.GetComponent<Renderer>();
            if (rend != null)
            {
                var mat = MakeMarkerMaterial(color);
                if (mat != null) rend.material = mat;
            }
            // MelonLogger.Msg($"[VirtualStereo] 标记球已创建: {name}");
            return go;
        }

        /// <summary>
        /// CreatePrimitive 自带的内置 Standard 着色器在 URP 下渲染成洋红（无材质）——
        /// 必须按管线显式找着色器；全找不到就复制一个游戏里的现成材质（保证管线兼容）。
        /// </summary>
        private static Material MakeMarkerMaterial(Color color)
        {
            string[] names =
            {
                "Universal Render Pipeline/Unlit",
                "Universal Render Pipeline/Lit",
                "Universal Render Pipeline/Simple Lit",
                "Unlit/Color",
                "Sprites/Default",
                "Standard",
            };
            foreach (var n in names)
            {
                var sh = Shader.Find(n);
                if (sh == null) continue;
                var m = new Material(sh);
                m.color = color;
                // MelonLogger.Msg($"[VirtualStereo] 标记材质: {n}");
                return m;
            }
            try
            {
                var any = Object.FindObjectOfType<Renderer>();
                if (any != null && any.sharedMaterial != null)
                {
                    var m = new Material(any.sharedMaterial);
                    m.color = color;
                    // MelonLogger.Msg("[VirtualStereo] 标记材质: 复制自场景渲染器");
                    return m;
                }
            }
            catch { }
            MelonLogger.Warning("[VirtualStereo] 标记材质全部失败，球会显示为洋红");
            return null;
        }

        public static void SetInjectionAvailable(bool ok) => _injected = ok;

        public static void AttachCapture(ProcessLoopbackCapture capture)
        {
            _capture = capture;
            _filterFiredAt = 0;
            _startedAt = NowMs;
            // 只走"数据装进 clip"的正路：clip → 空间化 → 输出，全程 Unity 正常管线。
            // （OnAudioFilterRead 覆盖路径会把空间化声像抹掉，弃用——ch=2 时实测声像塌成居中）
            _filterMode = false;
            _writePos = 0;
            _samplesDue = 0;
            _lastPumpAt = 0;
        }

        public static void DetachCapture()
        {
            StopSources();
            _capture = null;
        }

        /// <summary>OnAudioFilterRead 回调（音频线程）：用捕获环内容覆盖输出缓冲。
        /// 按"帧"消费（不是按样本）：Unity 可能给立体声缓冲，用 data.Length 当样本数会
        /// 双倍速抽干环、并把左右声道槽位填进错乱序列（声音怪异的根源之一）。</summary>
        public static void OnFilterRead(int channel, Il2CppStructArray<float> data, int channels)
        {
            _filterFiredAt = NowMs;
            int ch = channels > 0 ? channels : 1;
            if (_filterChannels != ch) _filterChannels = ch;

            int n = data.Length;
            var ring = channel == 0 ? _capture?.RingL : _capture?.RingR;
            if (ring == null)
            {
                for (int i = 0; i < n; i++) data[i] = 0f;
                return;
            }

            // 延迟缓冲：攒够 ~125ms 再开始消费，吸收捕获/播放的时钟抖动；欠载后自动重新蓄水
            int target = _capture.SampleRate > 0 ? _capture.SampleRate / 8 : 6000;
            if (ring.Available < target)
            {
                for (int i = 0; i < n; i++) data[i] = 0f;
                return;
            }

            int frames = n / ch;
            if (_scratch.Length < frames) _scratch = new float[frames];
            ring.Read(_scratch, frames);
            for (int f = 0; f < frames; f++)
            {
                float v = _scratch[f];
                int baseIdx = f * ch;
                for (int c = 0; c < ch; c++) data[baseIdx + c] = v;
            }
        }

        /// <summary>每帧泵：模式切换、SetData 兜底、空间位置刷新、统计。</summary>
        public static void Tick()
        {
            if (_capture == null) return;
            long now = NowMs;

            if (!SourcesAlive)
            {
                if (!CreateSources()) return;
            }

            EnsureMarkers();
            PumpSetData();

            if (now >= _nextSpatialAt)
            {
                _nextSpatialAt = now + (_screen != null ? 500 : 2000);
                UpdateSpatial();
            }

            if (now >= _nextStatAt)
            {
                _nextStatAt = now + 10000;
                var listener = Object.FindObjectOfType<AudioListener>();
                string lp = listener != null ? $"{listener.transform.position.x:F1},{listener.transform.position.y:F1},{listener.transform.position.z:F1}" : "无";
                string lPos = _srcL != null ? $"{_srcL.transform.position.x:F1},{_srcL.transform.position.y:F1},{_srcL.transform.position.z:F1}" : "-";
                string rPos = _srcR != null ? $"{_srcR.transform.position.x:F1},{_srcR.transform.position.y:F1},{_srcR.transform.position.z:F1}" : "-";
                float blendL = _srcL != null ? _srcL.spatialBlend : -1f;
                // MelonLogger.Msg(
                    // $"[VirtualStereo] RMS={_capture.Rms:F4} L={_capture.RmsL:F4} R={_capture.RmsR:F4} " +
                    // $"相关度={_capture.Correlation:F3} 静音会话={_capture.SilencedSessions} 补偿=x{_capture.OutputGain:F0} " +
                    // $"L缓冲={_capture.RingL.Available} R缓冲={_capture.RingR.Available}");
                // MelonLogger.Msg(
                    // $"[VirtualStereo] 空间: 屏幕={(_screen != null ? _screen.name : "无(2D)")} blend={blendL:F2} " +
                    // $"L源={lPos} R源={rPos} 听者={lp}");
                // MelonLogger.Msg($"[VirtualStereo] 会话清单: {_capture.SessionInfo}");
            }
        }

        private static bool CreateSources()
        {
            try
            {
                _goL = new GameObject("VirtualStereo_L");
                _goR = new GameObject("VirtualStereo_R");
                Object.DontDestroyOnLoad(_goL);
                Object.DontDestroyOnLoad(_goR);

                _srcL = _goL.AddComponent<AudioSource>();
                _srcR = _goR.AddComponent<AudioSource>();
                Configure(_srcL);
                Configure(_srcR);

                if (Mode != SpatialMode.Speakers)
                {
                    // 双耳模式：L/R 物体只做"虚拟声源位置"载体（标记球/手动坐标），不发声；
                    // 发声的是挂在听者附近的 2D 立体声源（双耳渲染结果）
                    int rate = _capture != null && _capture.SampleRate > 0 ? _capture.SampleRate : 48000;
                    if (!BinauralEngine.Initialized && !BinauralEngine.Init(rate, Block))
                    {
                        MelonLogger.Error("[VirtualStereo] 双耳引擎不可用，回退双音箱模式");
                        Mode = SpatialMode.Speakers;
                    }
                    else
                    {
                        BinauralEngine.SetInterpolation(Interpolation);
                        _goB = new GameObject("VirtualStereo_Binaural");
                        Object.DontDestroyOnLoad(_goB);
                        _srcB = _goB.AddComponent<AudioSource>();
                        _srcB.playOnAwake = false;
                        _srcB.spatialBlend = 0f;
                        _srcB.dopplerLevel = 0f;
                        _srcB.priority = 48;
                        _srcB.volume = 1f;
                        _clipB = AudioClip.Create("vs_binaural", 96 * Block, 2, rate, false);
                        _writePos = rate / 5;
                        _srcB.clip = _clipB;
                        _srcB.loop = true;
                        _srcB.Play();
                        return true;
                    }
                }

                RecreateClips();
                // MelonLogger.Msg("[VirtualStereo] 音频源已创建（clip 直通管线）");
                return true;
            }
            catch (Exception e)
            {
                MelonLogger.Error("[VirtualStereo] 创建音频源失败: " + e);
                StopSources();
                return false;
            }
        }

        private static void Configure(AudioSource src)
        {
            src.playOnAwake = false;
            src.spatialBlend = 0f;
            src.rolloffMode = AudioRolloffMode.Logarithmic;
            src.minDistance = 2.0f;
            src.maxDistance = 60f;
            src.dopplerLevel = 0f;
            src.spread = 0f;
            src.priority = 64;
            src.volume = 1f;
        }

        private static void RecreateClips()
        {
            int rate = _capture != null && _capture.SampleRate > 0 ? _capture.SampleRate : 48000;
            try
            {
                // stream 必须 false：Unity 6 禁止对"无 PCM 回调的流式 clip"SetData
                // （实测报错 "Cannot set data on streamed samples..."）。非流式 clip +
                // 循环播放 + 运行时 SetData = 环形写法，长度取块长整数倍（绕回精确）。
                _clipL = AudioClip.Create("vs_stream_L", 96 * Block, 1, rate, false);
                _clipR = AudioClip.Create("vs_stream_R", 96 * Block, 1, rate, false);
                // 写指针领先播放头 200ms，避免读写同一段的竞态
                _writePos = rate / 5;
                if (_srcL != null) { _srcL.Stop(); _srcL.clip = _clipL; _srcL.loop = true; _srcL.Play(); }
                if (_srcR != null) { _srcR.Stop(); _srcR.clip = _clipR; _srcR.loop = true; _srcR.Play(); }
            }
            catch (Exception e)
            {
                MelonLogger.Error("[VirtualStereo] 创建音频剪辑失败: " + e);
            }
        }

        private static double _samplesDue;
        private static long _lastPumpAt;
        private const int Block = 1024;
        private static readonly float[] _blockL = new float[Block];
        private static readonly float[] _blockR = new float[Block];

        private static void PumpSetData()
        {
            bool binaural = Mode != SpatialMode.Speakers && _clipB != null;
            if (_capture == null) return;
            if (!binaural && (_clipL == null || _clipR == null)) return;

            int rate = _capture.SampleRate > 0 ? _capture.SampleRate : 48000;

            // 按流逝时间写入（rate×dt 个样本/帧），再以固定 1024 块落盘——
            // clip 长度是块的整数倍，绕回天然精确，SetData 永不越界
            long now = NowMs;
            if (_lastPumpAt == 0) _lastPumpAt = now;
            double dt = (now - _lastPumpAt) / 1000.0;
            _lastPumpAt = now;
            if (dt > 0.25) dt = 0.25;
            if (dt < 0) dt = 0;
            _samplesDue += rate * dt;

            bool buffered = _capture.RingL.Available >= rate / 8 && _capture.RingR.Available >= rate / 8;

            // 写指针自愈：若被播放头追上（卡顿后）或领先过大，重新跳到播放头前 200ms
            var playSrc = binaural ? _srcB : _srcL;
            int clipLen = binaural ? _clipB.samples : _clipL.samples;
            int lead = (_writePos - playSrc.timeSamples + clipLen) % clipLen;
            if (lead < Block || lead > clipLen / 2)
                _writePos = (playSrc.timeSamples + rate / 5) % clipLen;

            if (binaural)
            {
                // 方向按当前虚拟声源位置 × 头部（相机）朝向换算到头坐标系
                BinauralEngine.SetDirections(
                    HeadDir(_srcL != null ? _srcL.transform.position : Vector3.zero),
                    HeadDir(_srcR != null ? _srcR.transform.position : Vector3.zero));
            }

            int blocks = 0;
            float tickPeak = 0f;
            float gain = PreGainLinear;
            while (_samplesDue >= Block && blocks < rate / 4 / Block) // 单帧最多补 250ms
            {
                if (buffered)
                {
                    _capture.RingL.Read(_blockL, Block);
                    _capture.RingR.Read(_blockR, Block);
                }
                else
                {
                    // 没蓄够 ~125ms：写静音（欠账照记，蓄够后追平）
                    Array.Clear(_blockL, 0, Block);
                    Array.Clear(_blockR, 0, Block);
                }

                // 前置增益：进空间化模拟之前的输入衰减
                if (gain != 1f)
                {
                    for (int i = 0; i < Block; i++) { _blockL[i] *= gain; _blockR[i] *= gain; }
                }

                if (binaural)
                {
                    BinauralEngine.Process(_blockL, _blockR, _blockStereo, Block,
                        Mode == SpatialMode.FullHrtf ? SaiMode.FullHrtf : SaiMode.ItdIld);
                    _clipB.SetData(_blockStereo, _writePos);
                    for (int i = 0; i < Block * 2; i++)
                    {
                        float a = _blockStereo[i] < 0 ? -_blockStereo[i] : _blockStereo[i];
                        if (a > tickPeak) tickPeak = a;
                    }
                }
                else
                {
                    _clipL.SetData(_blockL, _writePos);
                    _clipR.SetData(_blockR, _writePos);
                    for (int i = 0; i < Block; i++)
                    {
                        float a = _blockL[i] < 0 ? -_blockL[i] : _blockL[i];
                        if (a > tickPeak) tickPeak = a;
                        a = _blockR[i] < 0 ? -_blockR[i] : _blockR[i];
                        if (a > tickPeak) tickPeak = a;
                    }
                }
                _writePos += Block;
                if (_writePos >= clipLen) _writePos = 0;
                _samplesDue -= Block;
                blocks++;
            }
            LastPeak = tickPeak;
        }

        /// <summary>世界方向 → 头坐标系单位向量（X=右, Y=上, Z=前；双耳引擎约定与 Unity 轴一致）。</summary>
        private static SaiVector3 HeadDir(Vector3 sourcePos)
        {
            if (_listener == null) return new SaiVector3(0, 0, 1);
            var lp = _listener.position;
            float dx = sourcePos.x - lp.x, dy = sourcePos.y - lp.y, dz = sourcePos.z - lp.z;
            var r = _listener.right;
            var u = _listener.up;
            var f = _listener.forward;
            float x = dx * r.x + dy * r.y + dz * r.z;
            float y = dx * u.x + dy * u.y + dz * u.z;
            float z = dx * f.x + dy * f.y + dz * f.z;
            float len = (float)Math.Sqrt(x * x + y * y + z * z);
            if (len < 1e-4f) return new SaiVector3(0, 0, 1);
            return new SaiVector3(x / len, y / len, z / len);
        }

        /// <summary>屏幕宽度（米，prefab 局部尺寸，见 room_item_bounds.csv）。</summary>
        private static float WidthForName(string name)
        {
            if (name.IndexOf("02624", StringComparison.Ordinal) >= 0) return 3.328f; // XL
            if (name.IndexOf("02625", StringComparison.Ordinal) >= 0) return 2.560f; // L
            if (name.IndexOf("02626", StringComparison.Ordinal) >= 0) return 2.000f; // M
            if (name.IndexOf("02627", StringComparison.Ordinal) >= 0) return 1.440f; // S
            if (name.IndexOf("02628", StringComparison.Ordinal) >= 0) return 0.968f; // XS
            return 2.0f;
        }

        /// <summary>手写叉乘（Vector3.Cross 属数学成员，按惯例不碰）。</summary>
        private static Vector3 Cross(Vector3 a, Vector3 b)
        {
            return new Vector3(
                a.y * b.z - a.z * b.y,
                a.z * b.x - a.x * b.z,
                a.x * b.y - a.y * b.x);
        }

        private static void UpdateSpatial()
        {
            try
            {
                if (_listener == null)
                {
                    var al = Object.FindObjectOfType<AudioListener>();
                    _listener = al != null ? al.transform : null;
                }

                if (_manualMode)
                {
                    ApplyManualPositions();
                    return;
                }

                if (_screen == null)
                    _screen = FindScreen();

                if (_screen == null)
                {
                    // 没有屏幕：保持 2D 平铺（听感=现状原声）
                    if (_srcL != null) _srcL.spatialBlend = 0f;
                    if (_srcR != null) _srcR.spatialBlend = 0f;
                    return;
                }

                Transform t = _screen;
                // 物品原点=面板几何中心（room_item_bounds.csv 里 x/z 对称、y=0），
                // 位置/宽度全部来自 transform + 物品 ID 已知宽度表——不依赖 renderer，
                // 避免 bounds 投影出的半宽过小导致两源贴在一起、声像塌在中间。
                float scale = Mathf.Abs(t.lossyScale.x);
                if (scale < 1e-4f) scale = 1f;
                float halfWidth = WidthForName(t.name) * scale * 0.5f;
                float spread = Mathf.Clamp(halfWidth * 0.85f, 0.4f, 2.5f);

                Vector3 center = t.position;

                // 左右按「观众面对屏幕」的视角判定，不是物品自身的 right——
                // 屏幕朝向相反时两者正好互换（面板零厚度方向=局部Y=法线，高度轴=局部Z）。
                // 法线取朝听者的一侧；面板上轴翻到世界上方；Unity 左手系 right = cross(up, forward)。
                Vector3 normal = t.up;
                if (_listener != null)
                {
                    Vector3 toListener = _listener.position - center;
                    if (normal.x * toListener.x + normal.y * toListener.y + normal.z * toListener.z < 0f)
                        normal = new Vector3(-normal.x, -normal.y, -normal.z);
                }
                Vector3 panelUp = t.forward;
                if (panelUp.y < 0f) panelUp = new Vector3(-panelUp.x, -panelUp.y, -panelUp.z);
                // 观众右手 = cross(面板上, 观众朝屏幕的前方(-normal))
                Vector3 viewerRight = Cross(panelUp, new Vector3(-normal.x, -normal.y, -normal.z));

                if (_srcL != null)
                {
                    _srcL.transform.position = new Vector3(
                        center.x - viewerRight.x * spread,
                        center.y - viewerRight.y * spread,
                        center.z - viewerRight.z * spread);
                    _srcL.spatialBlend = 1f;
                }
                if (_srcR != null)
                {
                    _srcR.transform.position = new Vector3(
                        center.x + viewerRight.x * spread,
                        center.y + viewerRight.y * spread,
                        center.z + viewerRight.z * spread);
                    _srcR.spatialBlend = 1f;
                }
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[VirtualStereo] 空间更新失败: " + e.Message);
            }
        }

        private static Transform FindScreen()
        {
            // 运行时实例名可能带 (Clone) 等后缀，GameObject.Find 精确匹配会扑空——
            // 改为遍历已加载场景，按前缀 + 物品编号匹配（与 ConstraintBaker 的认法一致）
            for (int s = 0; s < SceneManager.sceneCount; s++)
            {
                Scene scene = SceneManager.GetSceneAt(s);
                if (!scene.isLoaded) continue;
                var roots = scene.GetRootGameObjects();
                for (int i = 0; i < roots.Length; i++)
                {
                    var root = roots[i];
                    if (root == null) continue;
                    var transforms = root.GetComponentsInChildren<Transform>(true);
                    for (int j = 0; j < transforms.Length; j++)
                    {
                        var t = transforms[j];
                        if (t == null) continue;
                        string n = t.name;
                        if (n == null || !n.StartsWith("P_RoomItem_", StringComparison.Ordinal)) continue;
                        foreach (var id in ScreenIds)
                        {
                            if (n.IndexOf(id, StringComparison.Ordinal) >= 0)
                                return t;
                        }
                    }
                }
            }
            return null;
        }

        public static void StopSources()
        {
            try
            {
                if (_srcL != null) _srcL.Stop();
                if (_srcR != null) _srcR.Stop();
                if (_srcB != null) _srcB.Stop();
                if (_goL != null) Object.Destroy(_goL);
                if (_goR != null) Object.Destroy(_goR);
                if (_goB != null) Object.Destroy(_goB);
            }
            catch { }
            _goL = _goR = _goB = null;
            _srcL = _srcR = _srcB = null;
            _clipL = _clipR = _clipB = null;
            _markerL = _markerR = null;
            _screen = null;
        }

        private static long NowMs => Environment.TickCount64;
    }
}
