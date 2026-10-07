// Unity 侧：把捕获到的 L/R PCM 送进两个 AudioSource，摆在观影屏幕两侧。
// 数据通路两级：
//   1) OnAudioFilterRead（低延迟，注入组件的 Unity 音频回调）
//   2) AudioClip 流式 SetData（回调不触发时的兜底，由 Core.OnUpdate 泵）
using System;
using System.Threading;
using MelonLoader;
using UnityEngine;
using UnityEngine.SceneManagement;
using VirtualStereo.Capture;
using VirtualStereo.Dsp;
using VirtualStereo.Phonon;
using Object = UnityEngine.Object;

namespace VirtualStereo
{
    internal static class AudioEngine
    {
        // 观影派对屏幕的物品编号（Prefab 名 P_RoomItem_<id>，见 gogh-物品模型库/geometry/room_item_bounds.csv）
        private static readonly string[] ScreenIds = { "02624", "02625", "02626", "02627", "02628" };

        private static ProcessLoopbackCapture _capture;
        // L/R 只是"虚拟声源位置"载体（标记球/朝向箭头挂它们下面），不挂 AudioSource。
        // 出声不走 Unity：由 core/AudioCapture/WasapiOutput.cs 在音频线程里直接写声卡。
        private static Transform _screen;

        // ── 音频线程（不走 Unity 音频；节奏由声卡决定，与游戏帧率解耦）──
        // DSP 块长：48k 下 512 帧 ≈ 10.7ms。DSP 已经不在主线程上，块长只影响
        // 「听到的声音至少滞后一个块」这一项，所以从 1024 降到 512 换来 10.6ms 延迟；
        // 代价是音频线程上 HRTF/Room 的调用次数翻倍（每块固定开销），
        // 复核指标：面板「设备欠载」是否仍然不涨。
        private const int Block = 512;
        // ── 四对虚拟音箱（8 个声源）= 2×2 网格：外侧上下两对 + 内侧上下两对 ──
        // 层序：0/1 上外，2/3 上内，4/5 下内，6/7 下外。
        // 真实电视是"面声源"：垂直方向铺开几乎不糊水平声像，却能带来"一整块板子"的
        // 立体感。M 走外侧两对（4 只），S 走内侧两对（4 只）——两路各自都有纵向延展，
        // 于是形成真正的"面"，而不是一条线。两路的增益总和保持不变（M 仍 1.7、S 仍 1.15）。
        public const int RowCount = 4;
        public const int SrcCount = RowCount * 2;

        // ── 多台 watch party：同一房间里的投影放的是同一个网页，音频只有一路。
        // 现在**只让离摄像头（AudioListener）最近的那台发声**，其余投影完全不出声——
        // 多台同时响会把声像糊掉，单台定位才干净。以后想改成"全部一起响"，
        // 把 MaxScreens 改成 4 即可（代码里已经支持"主投影三对 + 其余各一对"）。
        public const int MaxScreens = 1;
        public const int MaxSrcTotal = SrcCount + (MaxScreens - 1) * 2;   // 现在 = 6
        private static readonly Transform[] _activeScreens = new Transform[MaxScreens];
        private static int _activeScreenCount;

        /// <summary>次投影（除主投影外的其他 watch party）的电平——MaxScreens=1 时用不到，
        /// 留作以后开多屏时的旋钮。</summary>
        public static float SecondaryScreenGain { get; set; } = 0.6f;
        private static readonly float[] _blockL = new float[Block];
        private static readonly float[] _blockR = new float[Block];
        private static readonly float[] _blockStereo = new float[Block * 2];
        private static WasapiOutput _out;
        private static Thread _audioThread;
        private static volatile bool _audioRunning;
        private static bool _audioStarved = true;
        private const int RoomTailMs = 600;

        // ── 主线程写、音频线程读的空间快照（Unity 的 Transform 只能在主线程读）──
        private static readonly SaiVector3[] _snapDir = new SaiVector3[MaxSrcTotal];
        private static readonly float[][] _srcBuf = NewSrcBufs();
        private static readonly float[] _roomL = new float[Block];
        private static readonly float[] _roomR = new float[Block];

        private static float[][] NewSrcBufs()
        {
            var b = new float[MaxSrcTotal][];
            for (int i = 0; i < MaxSrcTotal; i++) b[i] = new float[Block];
            return b;
        }
        // 每源一份快照（6 个声源：row*2+ch）
        private static readonly float[] _posX = new float[MaxSrcTotal];
        private static readonly float[] _posY = new float[MaxSrcTotal];
        private static readonly float[] _posZ = new float[MaxSrcTotal];
        private static readonly int[] _srcRow = new int[MaxSrcTotal];   // 层号；−1 = 次投影的单对
        private static readonly int[] _srcCh = new int[MaxSrcTotal];    // 0 = 左, 1 = 右
        private static readonly int[] _srcScreenIdx = new int[MaxSrcTotal]; // 属于第几台投影
        private static readonly float[] _srcAz = new float[MaxSrcTotal];
        private static readonly float[] _srcEl = new float[MaxSrcTotal];
        private static readonly float[] _srcDist = new float[MaxSrcTotal];
        private static readonly int[] _srcAimMode = new int[MaxSrcTotal];
        private static readonly float[] _srcAimAz = new float[MaxSrcTotal];
        private static readonly float[] _srcAimEl = new float[MaxSrcTotal];
        private static int _activeSrcCount;

        // ── 四对音箱的布局参数（面板可调）──
        private static readonly bool[] RowOn = { true, true, true, true };
        /// <summary>外侧两对相对屏幕中心的垂直偏移（米，上下各 ±）。</summary>
        public static float RowOuterDyM { get; set; } = 0.95f;
        /// <summary>内侧两对相对屏幕中心的垂直偏移（米，上下各 ±）——与外侧分开调。</summary>
        public static float RowInnerDyM { get; set; } = 0.7f;
        /// <summary>内侧两对的横向比例（1 = 与外侧同宽；越小越靠内侧）。</summary>
        public static float RowInnerScale { get; set; } = 1.2f;
        /// <summary>外侧两对的横向比例（1 = 屏幕半宽 × 0.85 的原始间距）。</summary>
        public static float RowOuterScale { get; set; } = 0.9f;
        /// <summary>外侧两对朝外偏的方位角（度，0 = 与内侧一样朝观众）。</summary>
        public static float RowOuterAimDeg { get; set; } = 0f;
        /// <summary>每对的 M 增益（实心程度）与 S 增益（铺开程度）。
        /// 外侧两对出 M（各 0.80），内侧两对出 S（各 0.425）——
        /// 与原来"三对"实测值等价（M 总和 1.7、S 总和 1.15），只是把中置那一路
        /// 由一对拆成上下两对，让 S 也有纵向延展。</summary>
        public static readonly float[] RowGainM = { 0.60f, 0.05f, 0.05f, 0.60f };
        public static readonly float[] RowGainS = { 0.10f, 0.50f, 0.50f, 0.10f };

        /// <summary>第 row 对的横向比例（内侧两对用 RowInnerScale）。</summary>
        private static float RowScaleFor(int row) => (row == 1 || row == 2) ? RowInnerScale : RowOuterScale;

        /// <summary>第 row 对的垂直偏移（上面两对取正、下面两对取负；内外各自一套值）。</summary>
        private static float RowDyFor(int row)
        {
            float dy = RowIsInner(row) ? RowInnerDyM : RowOuterDyM;
            return (row == 0 || row == 1) ? dy : -dy;
        }

        /// <summary>是否内侧那两对（它们的朝向不额外外偏）。</summary>
        private static bool RowIsInner(int row) => row == 1 || row == 2;

        // 面板用的访问器（外层 = 上/下两排，共用一组增益）
        public static bool RowEnabled(int row) => row >= 0 && row < RowCount && RowOn[row];
        public static void SetRowEnabled(int row, bool on)
        {
            if (row >= 0 && row < RowCount) RowOn[row] = on;
        }
        public static float MidGainM
        {
            get => RowGainM[1];
            set { RowGainM[1] = RowGainM[2] = ClampF(value, 0f, 1.5f); }
        }
        public static float MidGainS
        {
            get => RowGainS[1];
            set { RowGainS[1] = RowGainS[2] = ClampF(value, 0f, 1.5f); }
        }
        public static float OuterGainM
        {
            get => RowGainM[0];
            set { RowGainM[0] = RowGainM[3] = ClampF(value, 0f, 1.5f); }
        }
        public static float OuterGainS
        {
            get => RowGainS[0];
            set { RowGainS[0] = RowGainS[3] = ClampF(value, 0f, 1.5f); }
        }

        /// <summary>读门槛（毫秒）：环电平掉到「低」以下出静音保护，回到「高」以上才恢复读。</summary>
        public static int PreRollLowMs { get; set; } = 10;
        public static int PreRollHighMs { get; set; } = 25;

        /// <summary>采集环当前电平（毫秒）。</summary>
        public static int CaptureBufferMs { get; private set; }

        /// <summary>设备缓冲已排队的深度（毫秒）。</summary>
        public static int OutFillMs { get; private set; }

        /// <summary>设备侧欠载次数（写不进去的次数）。</summary>
        public static int OutUnderruns => _out != null ? _out.Underruns : 0;

        private static int _lastLoggedUnderruns;

        /// <summary>屏幕正面朝向 = 面板自身局部 +Y 的方向（房间物品的安装面法线朝外）。
        /// **完全不看听者位置**：人站在哪、绕到屏幕哪一侧，都改变不了屏幕的朝向。
        /// 万一某个型号的正面是 -Y，开发面板里点「翻转正面」即可（默认 +Y）。</summary>
        public static float ScreenFrontSign { get; set; } = 1f;
        private static long _nextSpatialAt;
        private static long _nextStatAt;

        /// <summary>HRTF 插值方式（0 最近邻 / 1 双线性）——单模式，没有对照模式。</summary>
        public static int Interpolation { get; private set; } = PhononNative.InterpolationBilinear;

        /// <summary>前置增益（dB，作用在进空间化模拟之前的原始 L/R 上；双耳 HRTF 相干叠加易爆电平，默认 -12dB 留余量）。</summary>
        public static float PreGainDb { get; set; } = -12f;

        /// <summary>前置增益（线性）。用 Math 而不是 Mathf（数学成员按惯例避开 IL2CPP）。</summary>
        public static float PreGainLinear => (float)Math.Pow(10.0, PreGainDb / 20.0);

        /// <summary>最近一次泵写入信号的峰值（模拟之后），供面板观察电平。</summary>
        public static float LastPeak { get; private set; }

        // ── 声学：指向性 / 距离衰减 / 听音室（2 声源版，不涉及多声道）──
        // 链路顺序与桌面版一致：前置增益 → 指向性 → 距离衰减 → 空间化(HRTF) → 听音室。
        private static readonly DirectivityProcessor _directivity = NewDirectivity();
        private static readonly DirectivityState[] _dirState = NewDirStates();

        private static DirectivityState[] NewDirStates()
        {
            var a = new DirectivityState[MaxSrcTotal];
            for (int i = 0; i < SrcCount; i++) a[i] = new DirectivityState();
            return a;
        }

        // ── 材料库：三个表面各自一组选项，每项三带吸声系数（低<250Hz / 中250-2k / 高>2k）──
        // 数值取自常用吸声系数表：厚地毯(带垫层)、厚重窗帘、木板(薄板共振吸低频)等。
        // 数组下标的含义与 RoomModel 的表面序一致：0=地板 1=天花板 2=四壁
        private static readonly string[][] MatNames =
        {
            new[] { "厚地毯", "木地板", "瓷砖" },              // 0 地板
            new[] { "木板", "抹灰", "吸声板" },                // 1 天花板
            new[] { "窗帘", "石膏墙", "木板墙", "玻璃" },       // 2 四壁
        };

        private static readonly float[][][] MatAbs =
        {
            new[]                                              // 0 地板
            {
                new[] { 0.10f, 0.45f, 0.70f },  // 厚地毯
                new[] { 0.15f, 0.11f, 0.10f },  // 木地板
                new[] { 0.02f, 0.03f, 0.04f },  // 瓷砖
            },
            new[]                                              // 1 天花板
            {
                new[] { 0.20f, 0.12f, 0.10f },  // 木板（薄板共振：低频吸得多）
                new[] { 0.03f, 0.03f, 0.05f },  // 抹灰
                new[] { 0.30f, 0.70f, 0.80f },  // 吸声板
            },
            new[]                                              // 2 四壁
            {
                new[] { 0.15f, 0.50f, 0.70f },  // 窗帘
                new[] { 0.10f, 0.06f, 0.05f },  // 石膏墙
                new[] { 0.25f, 0.15f, 0.10f },  // 木板墙
                new[] { 0.05f, 0.03f, 0.02f },  // 玻璃
            },
        };

        /// <summary>每表面当前选中的材料序号（默认全 0 = 厚地毯 / 木板 / 窗帘）。</summary>
        private static readonly int[] MatSel = { 0, 0, 0 };

        private static readonly RoomRenderer _room = NewRoom();
        private static readonly float[] _rtScratch = new float[RoomModel.Bands];

        /// <summary>默认：指向性开（朝听者=平直，换「朝前」立刻听出离轴变色）、房间开。</summary>
        private static DirectivityProcessor NewDirectivity()
        {
            var d = new DirectivityProcessor { Enabled = true, Family = 0 };
            d.FillGradientAngle(180f, 0.3f, 120f, 1f); // 低频宽高频窄，与「标准」预设一致
            return d;
        }

        private static RoomRenderer NewRoom()
        {
            // 默认电平与明暗：反射 −10dB / 混响 −15dB / 明亮度 0.6（类默认是 −6/−12/0.4，
            // 这里按实测结论覆盖：尾巴收得更快、更暗，贴近软装客厅）
            var r = new RoomRenderer { Enabled = true, ReflDb = -10f, ReverbDb = -15f, Damp = 0.6f };
            // 默认按 mac 版「电视房」：3.8 × 3.8 地板 × 5.0 层高，听者在正中
            // → 电视墙离听者 1.9m（就是"电视挂在 2m 位置"那个设定）。
            var m = r.Model;
            m.W = 3.8f;
            m.D = 3.8f;
            m.H = 5.0f;
            m.ListenerX = 1.9f;
            m.ListenerY = 1.2f;
            m.ListenerZ = 1.9f;
            // 默认材料组合：地板=厚地毯 / 四壁=窗帘 / 天花板=木板（材料库里的第 0 项）
            for (int s = 0; s < RoomModel.Surfaces && s < MatAbs.Length; s++)
            {
                var a = MatAbs[s][MatSel[s]];
                m.SetSurface(s, a[0], a[1], a[2]);
            }
            return r;
        }

        /// <summary>指向性：按声源朝向与"听者方向"的夹角分带（6 带）着色。</summary>
        public static bool DirectivityOn
        {
            get => _directivity.Enabled;
            set => _directivity.Enabled = value;
        }

        /// <summary>声源朝向（固定，不给改）：0 朝听者 / 1 朝前（跟随视角）/ 2 手动 / 3 观众席。
        /// 现在恒为 3 = 两箱轴线平行、取屏幕法线朝观众那一侧，**世界系固定**，不随视角转。</summary>
        /// <summary>某虚拟声源当前朝向的世界系单位向量（图形标注与调试读数用）。</summary>
        public static Vector3 AimDirWorld(int src)
        {
            if (src < 0 || src >= _activeSrcCount) return new Vector3(0f, 0f, 1f);
            int row = _srcRow[src];
            float outDeg = row < 0 || RowIsInner(row) ? 0f : RowOuterAimDeg;
            return AimWorldForScreen(_activeScreens[_srcScreenIdx[src]], row, _srcCh[src], outDeg);
        }

        /// <summary>世界系方向 → 头坐标方位/仰角（度）。</summary>
        private static void HeadAngles(Vector3 dir, out float azDeg, out float elDeg)
        {
            azDeg = 0f;
            elDeg = 0f;
            if (_listener == null) return;
            var r = _listener.right;
            var u = _listener.up;
            var f = _listener.forward;
            float x = dir.x * r.x + dir.y * r.y + dir.z * r.z;
            float y = dir.x * u.x + dir.y * u.y + dir.z * u.z;
            float z = dir.x * f.x + dir.y * f.y + dir.z * f.z;
            float len = (float)Math.Sqrt(x * x + y * y + z * z);
            if (len < 1e-6f) return;
            azDeg = (float)(Math.Atan2(x, z) * 180.0 / Math.PI);
            float s = y / len;
            if (s > 1f) s = 1f; else if (s < -1f) s = -1f;
            elDeg = (float)(Math.Asin(s) * 180.0 / Math.PI);
        }

        /// <summary>房间反射用的声源方位：方位角与**水平距离**取真实发声点，
        /// 高度换成"电视挂在 TvHeightM 高"（默认 2m）。只喂给听音室。</summary>
        private static void RoomAzElDist(float azDeg, float elDeg, float dist,
            out float azOut, out float elOut, out float distOut)
        {
            azOut = azDeg;

            // 真实距离投影到水平面（去掉高度分量）
            double elRad = elDeg * Math.PI / 180.0;
            float horiz = dist * (float)Math.Cos(elRad);
            if (horiz < 0.2f) horiz = 0.2f;

            float rise = SourceRiseM;
            distOut = (float)Math.Sqrt(horiz * horiz + rise * rise);
            elOut = (float)(Math.Atan2(rise, horiz) * 180.0 / Math.PI);
        }

        /// <summary>距离衰减：参考距离处 0dB，距离翻倍 −6dB（与桌面版同一套 1/r 律）。</summary>
        public static bool DistanceOn { get; set; } = true;
        public static float RefDistM { get; set; } = 2f;

        /// <summary>耳朵高度（米）——与桌面版 Speaker.EarHeightM 一致。</summary>
        public const float EarHeightM = 1.2f;

        /// <summary>房间反射模拟用的"电视高度"（米）。默认 2m：装修音室时把声源当成挂在
        /// 前墙 2m 高处的电视，而不是屏幕两侧的实际发声点。**只影响房间反射**——
        /// HRTF 方位、指向性、距离衰减、标记球/箭头都还是真实发声位置。</summary>
        public static float TvHeightM { get; set; } = 2.0f;

        /// <summary>反射模拟里声源相对听者耳朵的高度差（米）。</summary>
        public static float SourceRiseM => TvHeightM - EarHeightM;

        // ── 延迟控制 ──
        // 总延迟 ≈ 读门槛（环电平）+ 渲染前置（写指针领先播放头）+ 捕获/系统缓冲。
        // 读门槛用迟滞（欠载后等 25ms 才恢复读，掉到 10ms 以下出静音），
        // 替代旧的固定 125ms——那是"暂停/跳转多久才响应"的主体。

        private static long _nextListenerScanAt;  // AudioListener 是整场扫描，别每帧找
        private static readonly System.Collections.Generic.List<Transform> _screens =
            new System.Collections.Generic.List<Transform>();

        /// <summary>听音室：一次反射 + FDN 混响尾（只在双耳模式下有输出缓冲可用）。</summary>
        public static bool RoomOn
        {
            get => _room.Enabled;
            set => _room.Enabled = value;
        }

        public static float RoomReflDb
        {
            get => _room.ReflDb;
            set => _room.ReflDb = value;
        }

        /// <summary>一次反射（早期反射）开关，与混响尾分开控制。</summary>
        public static bool RoomReflOn
        {
            get => _room.ReflOn;
            set => _room.ReflOn = value;
        }

        /// <summary>FDN 混响尾开关。</summary>
        public static bool RoomReverbOn
        {
            get => _room.ReverbOn;
            set => _room.ReverbOn = value;
        }

        public static float RoomReverbDb
        {
            get => _room.ReverbDb;
            set => _room.ReverbDb = value;
        }

        public static float RoomDamp
        {
            get => _room.Damp;
            set => _room.Damp = value;
        }

        /// <summary>房间尺寸自动跟随当前屏幕距离。默认关——默认按"电视房"固定尺寸，
        /// 屏幕离得远、声源要跑出房间时才需要打开它。</summary>
        public static bool RoomAutoFit { get; set; } = false;
        private static float _autoFitDist = -1f;

        public static float RoomW => _room.Model.W;
        public static float RoomD => _room.Model.D;
        public static float RoomH => _room.Model.H;

        /// <summary>房间中频 RT60（秒），面板显示用。</summary>
        public static float RoomRt60Mid
        {
            get { _room.Model.Rt60Bands(_rtScratch); return _rtScratch[1]; }
        }

        // 指向性锥形参数：按"低频端点 → 高频端点"线性渐变生成 6 带数据。
        // 锥角 = 半角（度，锥内满电平）；锐度 = 锥外衰减指数，增益 = (α/θ)^锐度，越大越指向。
        public static float DirConeLoDeg { get; set; } = 180f;
        public static float DirSharpLo { get; set; } = 0.3f;
        public static float DirConeHiDeg { get; set; } = 120f;
        public static float DirSharpHi { get; set; } = 1f;

        /// <summary>用当前四个端点值生成 6 带锥形数据（渐变生成器）。</summary>
        public static void ApplyDirectivityGradient()
        {
            _directivity.Family = 0;
            _directivity.FillGradientAngle(DirConeLoDeg, DirSharpLo, DirConeHiDeg, DirSharpHi);
        }

        /// <summary>指向性预设：0 全向 / 1 宽 / 2 标准（180°&0.3 → 120°&1）/ 3 强指向。</summary>
        public static void SetDirectivityPreset(int idx)
        {
            switch (idx)
            {
                case 0: // 全向：锐度 0 → 锥外也恒 1
                    DirConeLoDeg = 180f; DirSharpLo = 0f;
                    DirConeHiDeg = 180f; DirSharpHi = 0f;
                    break;
                case 1: // 宽
                    DirConeLoDeg = 160f; DirSharpLo = 0.3f;
                    DirConeHiDeg = 90f; DirSharpHi = 1.2f;
                    break;
                case 2: // 标准
                    DirConeLoDeg = 180f; DirSharpLo = 0.3f;
                    DirConeHiDeg = 120f; DirSharpHi = 1f;
                    break;
                default: // 强指向
                    DirConeLoDeg = 100f; DirSharpLo = 1f;
                    DirConeHiDeg = 30f; DirSharpHi = 3f;
                    break;
            }
            ApplyDirectivityGradient();
        }

        /// <summary>图案族：0 锥形（音箱）/ 1 偶极子系（心形…8 字）。</summary>
        public static void SetDirectivityFamily(int family)
        {
            if (family == 1)
            {
                _directivity.Family = 1;
                _directivity.FillGradient(1f, 1f, 1f, 1f); // 全带 8 字
            }
            else
            {
                SetDirectivityPreset(2);
            }
        }

        /// <summary>听音室尺寸预设：0 电视房（默认，照 mac 版）/ 1 小 / 2 中 / 3 大。
        /// 听者都摆在房间中心，所以前墙（电视墙）距离 = 进深的一半。</summary>
        public static void SetRoomSizePreset(int idx)
        {
            RoomAutoFit = false; // 手动选了就不跟屏幕走
            switch (idx)
            {
                case 0: SetRoomSize(3.8f, 3.8f, 5.0f); break;   // 电视房：3.8 见方地板 × 5m 层高，电视墙 1.9m
                case 1: SetRoomSize(4.0f, 5.0f, 2.6f); break;
                case 2: SetRoomSize(6.0f, 7.0f, 2.8f); break;
                default: SetRoomSize(8.0f, 10.0f, 3.2f); break;
            }
        }

        /// <summary>按声源距离自动定房间：深度约 2.6 倍视距，听者前留 60% 的进深，
        /// 保证虚拟声源落在房间里（手动摆位/游戏房间尺寸未知时的默认做法）。</summary>
        private static void AutoFitRoom(float dist)
        {
            float d = dist < 1.5f ? 1.5f : (dist > 8f ? 8f : dist);
            float w = ClampF(1.8f * d, 3.5f, 12f);
            float dep = ClampF(2.6f * d, 5f, 16f);
            var m = _room.Model;
            m.W = w;
            m.D = dep;
            m.H = 2.8f;
            m.ListenerX = w * 0.5f;
            m.ListenerY = 1.2f;
            m.ListenerZ = ClampF(0.6f * dep - d, 0.4f, dep - 0.6f);
        }

        private static float ClampF(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);

        private static void SetRoomSize(float w, float d, float h)
        {
            var m = _room.Model;
            m.W = w; m.D = d; m.H = h;
            m.ListenerX = w * 0.5f;
            m.ListenerZ = d * 0.5f;
            m.ListenerY = Math.Min(1.2f, h - 0.4f);
        }

        // ── 材料：逐表面选择 ──
        /// <summary>某表面可选的материals数量（0 地板 / 1 天花板 / 2 四壁）。</summary>
        public static int SurfaceMatCount(int surface) => MatNames[surface].Length;

        /// <summary>某表面当前材料名。</summary>
        public static string SurfaceMatName(int surface) => MatNames[surface][MatSel[surface]];

        /// <summary>某表面第 idx 个候选材料的名字。</summary>
        public static string SurfaceMatNameAt(int surface, int idx) => MatNames[surface][idx];

        /// <summary>某表面当前选中的材料序号。</summary>
        public static int SurfaceMatIndex(int surface) => MatSel[surface];

        /// <summary>切到某表面的第 idx 种材料。</summary>
        public static void SetSurfaceMat(int surface, int idx)
        {
            if (surface < 0 || surface >= MatNames.Length) return;
            if (idx < 0 || idx >= MatNames[surface].Length) return;
            MatSel[surface] = idx;
            var a = MatAbs[surface][idx];
            _room.Model.SetSurface(surface, a[0], a[1], a[2]);
        }

        /// <summary>整体材料预设（面板已改成逐表面选择，保留供脚本/将来用）：
        /// 0 吸声 / 1 中性 / 2 混响（硬墙）。</summary>
        public static void SetRoomMaterialPreset(int idx)
        {
            var m = _room.Model;
            switch (idx)
            {
                case 0:
                    m.SetSurface(0, 0.40f, 0.55f, 0.70f);
                    m.SetSurface(1, 0.25f, 0.40f, 0.55f);
                    m.SetSurface(2, 0.20f, 0.35f, 0.50f);
                    break;
                case 1:
                    m.SetSurface(0, 0.05f, 0.20f, 0.50f);
                    m.SetSurface(1, 0.02f, 0.03f, 0.05f);
                    m.SetSurface(2, 0.08f, 0.07f, 0.10f);
                    break;
                default:
                    m.SetSurface(0, 0.02f, 0.03f, 0.05f);
                    m.SetSurface(1, 0.02f, 0.03f, 0.05f);
                    m.SetSurface(2, 0.03f, 0.03f, 0.05f);
                    break;
            }
        }

        public static void SetInterpolation(int interpolation)
        {
            Interpolation = interpolation;
            BinauralEngine.SetInterpolation(interpolation);
        }

        /// <summary>双耳引擎 + 音频线程都就绪（Core 只在这里为真时才去压原声音量）。</summary>
        public static bool HrtfReady => BinauralEngine.Initialized && _audioRunning && _out != null;

        /// <summary>空间化程度 0.3~1（1 = 完全 HRTF 点声源）。调低 → 声像更宽、更像"面声源"，
        /// 轻微转头时的移动幅度也更小。真实电视是一整块面板在辐射，本来就该偏宽。</summary>
        public static float SpatialBlend
        {
            get => BinauralEngine.SpatialBlend;
            set => BinauralEngine.SpatialBlend = ClampF(value, 0.2f, 1f);
        }

        // ── 调试：手动坐标模式 + 标记球 ──
        private static bool _manualMode;
        private static Vector3 _manualL, _manualR;
        // 标记球 + 朝向箭头：只在调试版默认显示（发布版里根本不创建）
        private static bool _markersOn = BuildFlags.Dev;
        private static readonly GameObject[] _marker = new GameObject[SrcCount];
        private static readonly GameObject[] _aimArrow = new GameObject[SrcCount]; // 局部 +Y = 朝向
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
            // 手动坐标只管中间那对（调试用）；上下两层按同样的偏移/外扩比例跟着摆
            var inner = _manualR - _manualL;
            float halfInner = 0.5f * (float)Math.Sqrt(inner.x * inner.x + inner.y * inner.y + inner.z * inner.z);
            float halfOuter = RowInnerScale > 1e-3f ? halfInner / RowInnerScale * RowOuterScale : halfInner;
            Vector3 mid = new Vector3((_manualL.x + _manualR.x) * 0.5f,
                                      (_manualL.y + _manualR.y) * 0.5f,
                                      (_manualL.z + _manualR.z) * 0.5f);
            Vector3 ux = NormV(inner.x, inner.y, inner.z);
            for (int row = 0; row < RowCount; row++)
            {
                float half = RowIsInner(row) ? halfInner : halfOuter;
                float dy = RowDyFor(row);
                _posX[row * 2] = mid.x - ux.x * half;
                _posY[row * 2] = mid.y - ux.y * half + dy;
                _posZ[row * 2] = mid.z - ux.z * half;
                _posX[row * 2 + 1] = mid.x + ux.x * half;
                _posY[row * 2 + 1] = mid.y + ux.y * half + dy;
                _posZ[row * 2 + 1] = mid.z + ux.z * half;
            }
        }

        public static void GetPositions(out Vector3 l, out Vector3 r, out Vector3 listener)
        {
            // 面板读数用中间那对（上下两层的完整位置在标记球上看）
            l = new Vector3(_posX[2], _posY[2], _posZ[2]);
            r = new Vector3(_posX[3], _posY[3], _posZ[3]);
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
                for (int s = 0; s < MaxSrcTotal; s++)
                {
                    if (_marker[s] != null) _marker[s].SetActive(value);
                    if (_aimArrow[s] != null) _aimArrow[s].SetActive(value);
                }
            }
        }

        private static void EnsureMarkers()
        {
            if (!_markersOn) return;

            for (int s = 0; s < _activeSrcCount; s++)
            {
                int row = _srcRow[s], ch = _srcCh[s];
                var pos = new Vector3(_posX[s], _posY[s], _posZ[s]);
                Color col = ch == 0 ? new Color(0.25f, 0.55f, 1f) : new Color(1f, 0.35f, 0.3f);
                // 内侧两对最亮（它们出 S、是"面"的主体）、外侧两对暗一点；次投影再暗一档
                float shade = row < 0 ? 0.35f : (RowIsInner(row) ? 1f : 0.6f);
                col = new Color(col.r * shade, col.g * shade, col.b * shade);

                if (_marker[s] == null) _marker[s] = MakeMarker($"VS_Marker_{s}", col);
                if (_aimArrow[s] == null) _aimArrow[s] = MakeAimArrow($"VS_Aim_{s}", col);

                // 朝听者方向偏 10cm，避免半个球埋进屏幕/墙里看不见
                var nudge = NudgeTowardListener(pos);
                if (_marker[s] != null)
                    _marker[s].transform.position = new Vector3(pos.x + nudge.x, pos.y + nudge.y, pos.z + nudge.z);
                if (_aimArrow[s] != null)
                {
                    _aimArrow[s].transform.position = pos;
                    _aimArrow[s].transform.up = AimDirWorld(s);
                }
            }
        }

        /// <summary>朝向标注：细杆 + 尖端小球，挂在声源物体上，局部 +Y 就是朝向
        /// （用 transform.up 赋世界方向，避开被 IL2CPP 裁掉的 FromToRotation / LookRotation）。</summary>
        private static GameObject MakeAimArrow(string name, Color color)
        {
            var root = new GameObject(name);
            Object.DontDestroyOnLoad(root);

            var shaft = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            shaft.name = name + "_Shaft";
            var cs = shaft.GetComponent<Collider>();
            if (cs != null) cs.enabled = false;
            shaft.transform.SetParent(root.transform, false);
            shaft.transform.localPosition = new Vector3(0f, 0.42f, 0f);
            shaft.transform.localScale = new Vector3(0.035f, 0.32f, 0.035f);

            var tip = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            tip.name = name + "_Tip";
            var ct = tip.GetComponent<Collider>();
            if (ct != null) ct.enabled = false;
            tip.transform.SetParent(root.transform, false);
            tip.transform.localPosition = new Vector3(0f, 0.80f, 0f);
            tip.transform.localScale = new Vector3(0.12f, 0.12f, 0.12f);

            var mat = MakeMarkerMaterial(color);
            if (mat != null)
            {
                var rs = shaft.GetComponent<Renderer>();
                if (rs != null) rs.material = mat;
                var rt = tip.GetComponent<Renderer>();
                if (rt != null) rt.material = mat;
            }
            return root;
        }

        private static Vector3 NudgeTowardListener(Vector3 pos)
        {
            if (_listener == null) return Vector3.zero;
            var to = _listener.position;
            float dx = to.x - pos.x, dy = to.y - pos.y, dz = to.z - pos.z;
            float len = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (len < 1e-3f) return Vector3.zero;
            return new Vector3(dx / len * 0.1f, dy / len * 0.1f, dz / len * 0.1f);
        }

        private static GameObject MakeMarker(string name, Color color)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = name;
            var col = go.GetComponent<Collider>();
            if (col != null) col.enabled = false; // 绝不干扰游戏物理/交互
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

        public static void AttachCapture(ProcessLoopbackCapture capture)
        {
            _capture = capture;
            _screen = null;      // 重新挑一次目标（离摄像头最近那台）
            ScanScreens();
            StartAudio();
        }

        public static void DetachCapture()
        {
            StopAudio();
            StopSources();
            _capture = null;
        }

        /// <summary>主线程每帧：位置载体、标记/箭头、空间快照、统计。
        /// **音频不再经过 Unity**——DSP 与播放都在自己的线程上（见 StartAudio/DspLoop）。</summary>
        public static void Tick()
        {
            if (_capture == null) return;
            long now = NowMs;

            // 屏幕只在"还没有目标"时扫一次；不做周期重扫——周期重扫会在走动时
            // 自动换台（跨过两台投影中点就跳），也会白白整场遍历。目标被销毁时
            // Unity 的 null 判定会让这里重新扫一次。
            if (_screen == null) ScanScreens();

            EnsureMarkers();
            SnapshotSpatial();

            if (now >= _nextSpatialAt)
            {
                _nextSpatialAt = now + (_screen != null ? 500 : 2000);
                UpdateSpatial();
            }

            if (now >= _nextStatAt)
            {
                _nextStatAt = now + 10000;
                // 设备侧欠载只在"有变化"时打一条：日志里就能看到发生时间与当时的缓冲状态
                if (_out != null && _out.Underruns != _lastLoggedUnderruns)
                {
                    MelonLogger.Msg($"[VirtualStereo] 设备欠载累计 {_out.Underruns}" +
                        $"（本次 +{_out.Underruns - _lastLoggedUnderruns}）" +
                        $"采集环 {CaptureBufferMs}ms 设备缓冲 {OutFillMs}ms");
                    _lastLoggedUnderruns = _out.Underruns;
                }
                var listener = Object.FindObjectOfType<AudioListener>();
                string lp = listener != null ? $"{listener.transform.position.x:F1},{listener.transform.position.y:F1},{listener.transform.position.z:F1}" : "无";
                string lPos = $"{_posX[2]:F1},{_posY[2]:F1},{_posZ[2]:F1}";
                string rPos = $"{_posX[3]:F1},{_posY[3]:F1},{_posZ[3]:F1}";
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

        /// <summary>只建两个位置载体（标记球/朝向箭头挂它们下面），不再建任何 AudioSource。</summary>
        // ───────────────────────── 音频线程：采集 → DSP → 自己的 WASAPI 输出 ─────────────────────────
        // 主线程只做 Unity 侧的事（位置载体、标记/箭头、相机快照、统计）；
        // 音频线程读快照做 DSP，节奏由**声卡**决定：设备缓冲有空位就产出一块。
        // 这样音频实时性彻底不受游戏帧率影响（原来主线程 SetData 喂环形 clip 的做法，
        // 帧率一掉就欠载、音频结束时更严重）。

        /// <summary>主线程每帧：把 Unity 侧的位置/朝向算成纯数据快照，供音频线程使用。</summary>
        private static void SnapshotSpatial()
        {
            // 找 AudioListener 是整场扫描，最多 2 秒一次（UpdateSpatial 也会兜底找）
            if (_listener == null && NowMs >= _nextListenerScanAt)
            {
                _nextListenerScanAt = NowMs + 2000;
                var al = Object.FindObjectOfType<AudioListener>();
                _listener = al != null ? al.transform : null;
            }

            // 每个虚拟声源各算一份：方向（头坐标单位向量）、方位/仰角/距离、朝向角
            int n = _activeSrcCount;
            if (n <= 0) return;
            for (int s = 0; s < n; s++)
            {
                var p = new Vector3(_posX[s], _posY[s], _posZ[s]);
                Vector3 dir = HeadDirV(p, out float dist);
                _snapDir[s] = new SaiVector3(dir.x, dir.y, dir.z);
                _srcDist[s] = dist;
                _srcAz[s] = (float)(Math.Atan2(dir.x, dir.z) * 180.0 / Math.PI);
                _srcEl[s] = (float)(Math.Asin(ClampF(dir.y, -1f, 1f)) * 180.0 / Math.PI);

                // 朝向：中间层朝观众（屏幕法线），上下两层各朝外偏 RowOuterAimDeg；
                // 次投影只有一对，朝它自己的观众侧（偏移 0）
                int row = _srcRow[s];
                int ch = _srcCh[s];
                float outDeg = row < 0 || RowIsInner(row) ? 0f : RowOuterAimDeg;
                Vector3 aim = AimWorldForScreen(_activeScreens[_srcScreenIdx[s]], row, ch, outDeg);
                HeadAngles(aim, out float aaz, out float ael);
                _srcAimMode[s] = 2; // 手动角度分支
                _srcAimAz[s] = aaz;
                _srcAimEl[s] = ael;
            }

            if (_room.Enabled && RoomAutoFit)
            {
                float dMax = Math.Max(_srcDist[2], _srcDist[3]); // 用内侧那对（近屏幕中心）代表"视距"
                if (Math.Abs(dMax - _autoFitDist) > 0.3f)
                {
                    _autoFitDist = dMax;
                    AutoFitRoom(dMax);
                }
            }
        }

        /// <summary>某一层某个声道的朝向（世界系）：屏幕法线绕"面板上轴"外偏 outDeg。
        /// 左声道往外偏 = 朝屏幕左侧转，右声道对称。</summary>
        private static Vector3 AimWorldForScreen(Transform screen, int row, int ch, float outDeg)
        {
            if (screen == null) return new Vector3(0f, 0f, 1f);
            ScreenFrame(screen, out Vector3 normal, out Vector3 right, out _);
            float a = outDeg * (float)Math.PI / 180f;
            float c = (float)Math.Cos(a), s = (float)Math.Sin(a);
            float sign = ch == 0 ? -1f : 1f;
            return NormV(normal.x * c + right.x * s * sign,
                         normal.y * c + right.y * s * sign,
                         normal.z * c + right.z * s * sign);
        }

        /// <summary>打开输出设备 + 起音频线程；失败就报错并保持游戏原声（Core 只在 HrtfReady 时压原声）。</summary>
        private static void StartAudio()
        {
            StopAudio();
            if (_capture == null) return;

            int rate = _capture.SampleRate > 0 ? _capture.SampleRate : 48000;
            if (!BinauralEngine.Initialized && !BinauralEngine.Init(rate, Block))
            {
                MelonLogger.Error("[VirtualStereo] 双耳引擎不可用，保持游戏原声: " + BinauralEngine.LastError);
                return;
            }
            BinauralEngine.SetInterpolation(Interpolation);

            try
            {
                _out = WasapiOutput.Open(rate);
            }
            catch (Exception e)
            {
                MelonLogger.Error("[VirtualStereo] 打开输出设备失败，保持游戏原声: " + e.Message);
                _out = null;
                return;
            }

            _audioRunning = true;
            _audioThread = new Thread(() => DspLoop(rate))
            {
                IsBackground = true,
                Name = "VirtualStereo.Dsp",
                Priority = System.Threading.ThreadPriority.AboveNormal,
            };
            _audioThread.Start();
            MelonLogger.Msg($"[VirtualStereo] 音频线程已启动: {rate}Hz 块 {Block} 设备缓冲 {_out.BufferFrames} 帧" +
                $"（约 {_out.BufferFrames * 1000 / Math.Max(1, rate)}ms）");
        }

        private static void StopAudio()
        {
            _audioRunning = false;
            try { _audioThread?.Join(500); } catch { }
            _audioThread = null;
            try { _out?.Dispose(); } catch { }
            _out = null;
            CaptureBufferMs = 0;
            OutFillMs = 0;
        }

        /// <summary>音频线程：设备-paced —— 设备缓冲有空位就产出一块。</summary>
        private static void DspLoop(int rate)
        {
            float gain = PreGainLinear;
            long silentSince = 0;

            while (_audioRunning)
            {
                try
                {
                    if (_out == null) { Thread.Sleep(5); continue; }
                    if (_out.FreeFrames <= 0) { Thread.Sleep(1); continue; } // 设备缓冲满：等声卡

                    // 采集环：溢出自愈（超 250ms 砍到 100ms）+ 读门槛迟滞
                    int lvl = Math.Min(_capture.RingL.Available, _capture.RingR.Available);
                    if (lvl > rate / 4)
                    {
                        int drop = lvl - rate / 10;
                        _capture.RingL.Discard(drop);
                        _capture.RingR.Discard(drop);
                        lvl = rate / 10;
                    }
                    CaptureBufferMs = lvl * 1000 / Math.Max(1, rate);

                    int hi = rate * PreRollHighMs / 1000;
                    int lo = rate * PreRollLowMs / 1000;
                    if (_audioStarved)
                    {
                        if (lvl >= hi) _audioStarved = false;
                    }
                    else if (lvl < lo)
                    {
                        _audioStarved = true;
                    }
                    bool buffered = !_audioStarved;

                    if (buffered)
                    {
                        _capture.RingL.Read(_blockL, Block);
                        _capture.RingR.Read(_blockR, Block);
                    }
                    else
                    {
                        Array.Clear(_blockL, 0, Block);
                        Array.Clear(_blockR, 0, Block);
                    }

                    // 断流超过 RoomTailMs 就跳过房间处理（混响尾已衰完，没必要白烧 CPU）
                    long now = NowMs;
                    if (buffered) silentSince = 0;
                    else if (silentSince == 0) silentSince = now;
                    bool roomAlive = silentSince == 0 || now - silentSince <= RoomTailMs;

                    ProcessBlock(rate, buffered, roomAlive, gain);

                    // 把这一块按"设备能吃的粒度"推出去：设备缓冲可能比 Block 还小
                    // （10ms 周期下可能只有 480 帧），所以不能要求一次写得下整块。
                    int off = 0;
                    while (off < Block && _audioRunning)
                    {
                        int free = _out.FreeFrames;
                        if (free <= 0) { Thread.Sleep(1); continue; }
                        int n = Block - off;
                        if (n > free) n = free;
                        if (!_out.Write(_blockStereo, off, n)) break;
                        off += n;
                    }
                    OutFillMs = _out.FillFrames * 1000 / Math.Max(1, rate);
                }
                catch (Exception e)
                {
                    MelonLogger.Error("[VirtualStereo] 音频线程异常: " + e.Message);
                    Thread.Sleep(20);
                }
            }
        }

        /// <summary>一块的完整 DSP。只读主线程写好的空间快照，不做任何 Unity 调用。</summary>
        private static void ProcessBlock(int rate, bool buffered, bool roomAlive, float gain)
        {
            float peak = 0f;

            // 前置增益：进空间化模拟之前的输入衰减
            if (gain != 1f)
            {
                for (int i = 0; i < Block; i++) { _blockL[i] *= gain; _blockR[i] *= gain; }
            }

            if (buffered)
            {
                // 三对音箱的输入用 M/S 分权合成：M=(L+R)/2 实心、S=(L−R)/2 铺开。
                // M 主要给中间那层、S 主要给上下两层——既避免同一信号多路相干叠加的
                // 梳状染色，又能单独调"宽度"。
                int n = _activeSrcCount;
                for (int s = 0; s < n; s++)
                {
                    int row = _srcRow[s];
                    float gm, gs;
                    if (row < 0)
                    {
                        // 次投影：一对，直接吃整份 L/R（同一网页音频从这块投影也发出来）
                        gm = gs = SecondaryScreenGain;
                    }
                    else
                    {
                        gm = RowOn[row] ? RowGainM[row] : 0f;
                        gs = RowOn[row] ? RowGainS[row] : 0f;
                    }
                    // 左箱拿 M+S、右箱拿 M−S —— 因为 L = M+S、R = M−S，
                    // 这样"这只箱子在屏幕左边"与"它放的是 L 内容"才对得上。
                    // （原来写成 −S / +S，等于把左右内容对调了。）
                    float side = _srcCh[s] == 0 ? 1f : -1f;
                    float distG = DistanceOn ? RefDistM / Math.Max(0.3f, _srcDist[s]) : 1f;
                    float[] b = _srcBuf[s];
                    for (int i = 0; i < Block; i++)
                    {
                        float m = (_blockL[i] + _blockR[i]) * 0.5f;
                        float sd = (_blockL[i] - _blockR[i]) * 0.5f;
                        b[i] = (m * gm + sd * gs * side) * distG;
                    }

                    // 指向性：按"该声源朝哪"与"听者在该方向看它"的夹角分带着色
                    if (_directivity.Enabled)
                    {
                        _directivity.Process(_dirState[s], b, Block, rate,
                            _srcAz[s], _srcEl[s], _srcAimMode[s], _srcAimAz[s], _srcAimEl[s]);
                    }
                }

                // 听音室吃"三层之和"的两路干信号（否则反射路径要 36 条）
                Array.Clear(_roomL, 0, Block);
                Array.Clear(_roomR, 0, Block);
                for (int row = 0; row < RowCount; row++)
                {
                    for (int i = 0; i < Block; i++)
                    {
                        _roomL[i] += _srcBuf[row * 2][i];
                        _roomR[i] += _srcBuf[row * 2 + 1][i];
                    }
                }

                BinauralEngine.SetDirections(_snapDir, n);
                BinauralEngine.Process(_srcBuf, n, _blockStereo, Block);
            }
            else
            {
                // 断流：直接出静音，别在静音上白跑 HRTF/指向性
                Array.Clear(_blockStereo, 0, Block * 2);
            }

            // 听音室：一次反射 + 混响尾，累加到两耳（不清零）
            if (_room.Enabled && roomAlive)
            {
                // 房间几何用"电视挂在 2m 高"的假设：方位与水平距离取真实发声点，高度换成电视高度
                RoomAzElDist(_srcAz[2], _srcEl[2], _srcDist[2], out float rAzL, out float rElL, out float rDistL);
                RoomAzElDist(_srcAz[3], _srcEl[3], _srcDist[3], out float rAzR, out float rElR, out float rDistR);
                _room.Process(_roomL, _roomR, _blockStereo, Block, rate,
                    rAzL, rElL, rDistL, rAzR, rElR, rDistR);
            }

            for (int i = 0; i < Block * 2; i++)
            {
                float a = _blockStereo[i] < 0 ? -_blockStereo[i] : _blockStereo[i];
                if (a > peak) peak = a;
            }
            LastPeak = peak;
        }

        /// <summary>世界位置 → 头坐标系单位向量（X=右, Y=上, Z=前）+ 真实 3D 距离（米）。
        /// 双耳引擎、指向性、听音室都用这一套坐标。</summary>
        private static Vector3 HeadDirV(Vector3 sourcePos, out float dist)
        {
            dist = RefDistM;
            if (_listener == null) return new Vector3(0f, 0f, 1f);

            var lp = _listener.position;
            float dx = sourcePos.x - lp.x, dy = sourcePos.y - lp.y, dz = sourcePos.z - lp.z;
            float len = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (len < 1e-4f) return new Vector3(0f, 0f, 1f);
            dist = len;

            var r = _listener.right;
            var u = _listener.up;
            var f = _listener.forward;
            float x = dx * r.x + dy * r.y + dz * r.z;
            float y = dx * u.x + dy * u.y + dz * u.z;
            float z = dx * f.x + dy * f.y + dz * f.z;
            return NormV(x, y, z);
        }

        /// <summary>归一化（长度为 0 时退化为正前方）。</summary>
        private static Vector3 NormV(float x, float y, float z)
        {
            float len = (float)Math.Sqrt(x * x + y * y + z * z);
            if (len < 1e-4f) return new Vector3(0f, 0f, 1f);
            return new Vector3(x / len, y / len, z / len);
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
                    ScanScreens();   // 兜底：目标销毁后重新挑一台（正常由 Tick 里的判定触发）

                if (_screen == null)
                {
                    // 没有屏幕：无处摆声源，保持上一帧位置（HRTF 方向会继续跟着听者/头转）
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

                // 屏幕自身的朝向：不看听者，人站哪都不影响
                ScreenFrame(t, out _, out Vector3 viewerRight, out Vector3 panelUp);

                // 主投影四对（外侧两对出 M、内侧两对出 S，内外间距各自可调）+ 其余投影各一对
                LayoutSources();
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[VirtualStereo] 空间更新失败: " + e.Message);
            }
        }

        /// <summary>屏幕自身的坐标系：正面法线 + 观众右手。只看屏幕 transform 的局部轴，
        /// 与听者位置无关——屏幕是钉上去的一面板子，朝向由摆放时的旋转决定。
        /// （面板零厚度方向=局部Y=法线；高度轴=局部Z，按世界上方翻正。）</summary>
        /// <summary>按当前生效的投影铺开全部虚拟声源：主投影三对，其余投影各一对。</summary>
        private static void LayoutSources()
        {
            _activeSrcCount = 0;
            for (int k = 0; k < _activeScreenCount; k++)
            {
                var sc = _activeScreens[k];
                if (sc == null) continue;
                if (k == 0)
                {
                    for (int row = 0; row < RowCount; row++)
                    {
                        for (int ch = 0; ch < 2; ch++)
                        {
                            if (_activeSrcCount >= MaxSrcTotal) return;
                            int s = _activeSrcCount++;
                            _srcScreenIdx[s] = k;
                            _srcRow[s] = row;
                            _srcCh[s] = ch;
                            SourcePos(sc, row, ch, out _posX[s], out _posY[s], out _posZ[s]);
                        }
                    }
                }
                else
                {
                    for (int ch = 0; ch < 2; ch++)
                    {
                        if (_activeSrcCount >= MaxSrcTotal) return;
                        int s = _activeSrcCount++;
                        _srcScreenIdx[s] = k;
                        _srcRow[s] = -1;   // 次投影：只在屏幕两侧各一只
                        _srcCh[s] = ch;
                        SourcePos(sc, -1, ch, out _posX[s], out _posY[s], out _posZ[s]);
                    }
                }
            }
        }

        /// <summary>某个虚拟声源的世界位置：屏幕中心 + 观众右手×横向比例 + 面板上轴×垂直偏移。</summary>
        private static void SourcePos(Transform screen, int row, int ch,
            out float px, out float py, out float pz)
        {
            px = py = pz = 0f;
            if (screen == null) return;

            float scale = Mathf.Abs(screen.lossyScale.x);
            if (scale < 1e-4f) scale = 1f;
            float halfWidth = WidthForName(screen.name) * scale * 0.5f;
            float spread = Mathf.Clamp(halfWidth * 0.85f, 0.4f, 2.5f);
            float scaleR = row < 0 ? RowOuterScale : RowScaleFor(row);
            float dy = row < 0 ? 0f : RowDyFor(row);
            float sign = ch == 0 ? -1f : 1f;

            ScreenFrame(screen, out _, out Vector3 right, out Vector3 upAxis);
            var c = screen.position;
            px = c.x + right.x * spread * scaleR * sign + upAxis.x * dy;
            py = c.y + right.y * spread * scaleR * sign + upAxis.y * dy;
            pz = c.z + right.z * spread * scaleR * sign + upAxis.z * dy;
        }

        private static void ScreenFrame(Transform t, out Vector3 normal, out Vector3 right, out Vector3 up)
        {
            Vector3 n = t.up;
            if (ScreenFrontSign < 0f) n = new Vector3(-n.x, -n.y, -n.z);

            up = t.forward;
            if (up.y < 0f) up = new Vector3(-up.x, -up.y, -up.z);

            normal = n;
            // Unity 左手系：观众右手 = cross(面板上, 观众朝屏幕的前方(-normal))
            right = Cross(up, new Vector3(-n.x, -n.y, -n.z));
        }

        /// <summary>枚举房间里**所有** watch party 屏幕（原来是抓到第一个就用）。
        /// 运行时实例名可能带 (Clone) 后缀，所以按前缀 + 物品编号匹配。</summary>
        private static void FindScreens()
        {
            _screens.Clear();
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
                        bool hit = false;
                        foreach (var id in ScreenIds)
                        {
                            if (n.IndexOf(id, StringComparison.Ordinal) >= 0) { hit = true; break; }
                        }
                        if (hit) _screens.Add(t);
                    }
                }
            }
        }

        /// <summary>重扫屏幕列表并解析"当前生效的那一台"。</summary>
        private static void ScanScreens()
        {
            FindScreens();
            _activeScreenCount = 0;
            for (int i = 0; i < MaxScreens; i++) _activeScreens[i] = null;

            if (_screens.Count == 0)
            {
                _screen = null;
                return;
            }

            // 主投影 = 离听者最近那台（它用完整三对）；其余投影各加一对，全部一起发声
            _screen = NearestScreen();
            _activeScreens[0] = _screen;
            _activeScreenCount = 1;
            for (int i = 0; i < _screens.Count && _activeScreenCount < MaxScreens; i++)
            {
                var t = _screens[i];
                if (t == null || t == _screen) continue;
                _activeScreens[_activeScreenCount++] = t;
            }
        }

        /// <summary>离听者最近的那一台（自动模式）。</summary>
        private static Transform NearestScreen()
        {
            if (_screens.Count == 0) return null;
            if (_listener == null) return _screens[0];
            var lp = _listener.position;
            Transform best = _screens[0];
            float bestD = float.MaxValue;
            for (int i = 0; i < _screens.Count; i++)
            {
                var t = _screens[i];
                if (t == null) continue;
                var p = t.position;
                float dx = p.x - lp.x, dy = p.y - lp.y, dz = p.z - lp.z;
                float d = dx * dx + dy * dy + dz * dz;
                if (d < bestD) { bestD = d; best = t; }
            }
            return best;
        }

        /// <summary>屏幕数量（0 台 = 没找到）。</summary>
        public static int ScreenCount => _screens.Count;

        /// <summary>实际参与发声的投影数（主投影 + 其余，上限 MaxScreens）。</summary>
        public static int ActiveScreenCount => _activeScreenCount;

        /// <summary>第 idx 台投影的说明（面板用）。idx = 0 是主投影。</summary>
        public static string ActiveScreenLabel(int idx)
        {
            if (idx < 0 || idx >= _activeScreenCount) return "-";
            var t = _activeScreens[idx];
            if (t == null) return "-";
            string d = "?";
            if (_listener != null)
            {
                var p = t.position;
                var lp = _listener.position;
                float dx = p.x - lp.x, dy = p.y - lp.y, dz = p.z - lp.z;
                d = ((float)Math.Sqrt(dx * dx + dy * dy + dz * dz)).ToString("F1");
            }
            return (idx == 0 ? "主 " : "#" + (idx + 1) + " ") + t.name + " " + d + "m";
        }

        /// <summary>面板/日志用的一行说明：名字 + 离听者距离。</summary>
        public static string ScreenLabel(int idx)
        {
            if (idx < 0) return _screens.Count == 0 ? "自动（未找到屏幕）" : "自动（最近）";
            if (idx >= _screens.Count) return "自动（超出范围）";
            var t = _screens[idx];
            if (t == null) return $"#{idx + 1}（已销毁）";
            string d = "?";
            if (_listener != null)
            {
                var p = t.position;
                var lp = _listener.position;
                float dx = p.x - lp.x, dy = p.y - lp.y, dz = p.z - lp.z;
                d = ((float)Math.Sqrt(dx * dx + dy * dy + dz * dz)).ToString("F1");
            }
            return $"#{idx + 1} {t.name}  {d}m";
        }

        public static void StopSources()
        {
            try
            {
                for (int s = 0; s < MaxSrcTotal; s++)
                {
                    if (_marker[s] != null) Object.Destroy(_marker[s]);
                    if (_aimArrow[s] != null) Object.Destroy(_aimArrow[s]);
                    _marker[s] = null;
                    _aimArrow[s] = null;
                }
            }
            catch { }
            _screen = null;
        }

        private static long NowMs => Environment.TickCount64;
    }
}
