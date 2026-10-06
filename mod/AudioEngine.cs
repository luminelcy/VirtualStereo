// Unity 侧：把捕获到的 L/R PCM 送进两个 AudioSource，摆在观影屏幕两侧。
// 数据通路两级：
//   1) OnAudioFilterRead（低延迟，注入组件的 Unity 音频回调）
//   2) AudioClip 流式 SetData（回调不触发时的兜底，由 Core.OnUpdate 泵）
using System;
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
        // L/R 只是"虚拟声源位置"载体（标记球/朝向箭头挂它们下面），不挂 AudioSource——
        // 出声的是听者附近那个 2D 立体声源（双耳渲染结果）。原来对照实验用的
        // 双音箱直通 / ITD+ILD 两条路径已删除，mod 只保留 HRTF。
        private static GameObject _goL, _goR;
        private static long _startedAt;
        private static int _writePos;
        private static Transform _screen;

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
        private static readonly DirectivityState _dirStateL = new DirectivityState();
        private static readonly DirectivityState _dirStateR = new DirectivityState();

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
            d.FillGradientAngle(180f, 0.3f, 110f, 1.5f); // 低频宽高频窄，与「标准」预设一致
            return d;
        }

        private static RoomRenderer NewRoom()
        {
            // 默认电平：反射 −10dB / 混响 −15dB（类默认是 −6/−12，这里按调试结论收紧）
            var r = new RoomRenderer { Enabled = true, ReflDb = -10f, ReverbDb = -15f };
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
        public const int AimModeL = 3;
        public const int AimModeR = 3;
        public static float AimAz { get; set; }
        public static float AimEl { get; set; }

        /// <summary>某声道当前朝向的世界系单位向量（图形标注与调试读数用）。</summary>
        public static Vector3 AimDirWorld(int channel)
        {
            var go = channel == 0 ? _goL : _goR;
            var pos = go != null ? go.transform.position : Vector3.zero;
            return AimDir(channel == 0 ? AimModeL : AimModeR, pos);
        }

        /// <summary>朝向 → 世界系方向。0 朝听者 / 1 朝前（跟随头部）/ 2 手动（头坐标角度）。</summary>
        private static Vector3 AimDir(int mode, Vector3 sourcePos)
        {
            if (_listener == null) return new Vector3(0f, 0f, 1f);

            if (mode == 1) return _listener.forward; // 朝前：头（相机）前方

            if (mode == 3) // 观众席：两箱轴线平行，方向 = 屏幕中心 → 听者
            {
                // 有屏幕时用**屏幕自身的正面法线**：音箱钉在屏幕上，轴向与面板垂直。
                // 只依赖屏幕的摆放旋转，跟听者位置、视角都无关。
                if (_screen != null)
                {
                    ScreenFrame(_screen, out Vector3 nf, out _);
                    float lf = (float)Math.Sqrt(nf.x * nf.x + nf.y * nf.y + nf.z * nf.z);
                    if (lf > 1e-4f) return new Vector3(nf.x / lf, nf.y / lf, nf.z / lf);
                }

                // 没有屏幕时退化为：两箱中点 → 听者
                Vector3 from;
                if (_goL != null && _goR != null)
                {
                    var a = _goL.transform.position;
                    var b = _goR.transform.position;
                    from = new Vector3((a.x + b.x) * 0.5f, (a.y + b.y) * 0.5f, (a.z + b.z) * 0.5f);
                }
                else
                {
                    return new Vector3(0f, 0f, 1f);
                }
                float x3 = _listener.position.x - from.x;
                float y3 = _listener.position.y - from.y;
                float z3 = _listener.position.z - from.z;
                float l3 = (float)Math.Sqrt(x3 * x3 + y3 * y3 + z3 * z3);
                if (l3 < 1e-4f) return new Vector3(0f, 0f, 1f);
                return new Vector3(x3 / l3, y3 / l3, z3 / l3);
            }

            if (mode == 2) // 手动：头坐标方位/仰角 → 世界系
            {
                double a = AimAz * Math.PI / 180.0;
                double e = AimEl * Math.PI / 180.0;
                float ce = (float)Math.Cos(e);
                float x = (float)(Math.Sin(a) * ce);
                float y = (float)Math.Sin(e);
                float z = (float)(Math.Cos(a) * ce);
                var r = _listener.right;
                var u = _listener.up;
                var f = _listener.forward;
                return new Vector3(x * r.x + y * u.x + z * f.x,
                                   x * r.y + y * u.y + z * f.y,
                                   x * r.z + y * u.z + z * f.z);
            }

            // 0 朝听者：声源 → 听者
            float dx = _listener.position.x - sourcePos.x;
            float dy = _listener.position.y - sourcePos.y;
            float dz = _listener.position.z - sourcePos.z;
            float len = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (len < 1e-4f) return new Vector3(0f, 0f, 1f);
            return new Vector3(dx / len, dy / len, dz / len);
        }

        /// <summary>把声道朝向设置换算成 DirectivityProcessor 需要的（模式, 头坐标方位, 仰角）。
        /// 「观众席」定义在世界系，这里换算成头坐标角度后走手动分支。</summary>
        private static void ResolveAim(int channel, out int mode, out float azDeg, out float elDeg)
        {
            mode = channel == 0 ? AimModeL : AimModeR;
            azDeg = AimAz;
            elDeg = AimEl;
            if (mode != 3) return;

            var go = channel == 0 ? _goL : _goR;
            var pos = go != null ? go.transform.position : Vector3.zero;
            HeadAngles(AimDir(3, pos), out azDeg, out elDeg);
            mode = 2; // DirectivityProcessor 的手动角度分支
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

        /// <summary>渲染前置（毫秒）：写指针领先播放头的量 = 听到的声音至少滞后这么多。
        /// 主线程按帧泵 1024 采样块（48k 下 21ms/块），60ms ≈ 3 块余量；
        /// 调太小会在掉帧时欠载（出咔哒）。</summary>
        public static int RenderLeadMs { get; set; } = 60;

        /// <summary>最近的环电平（毫秒，取 L/R 较小者），面板诊断用。</summary>
        public static int RingLevelMs { get; private set; }

        /// <summary>最近的渲染前置实测值（毫秒，仅 SetData 兜底路径有意义）。</summary>
        public static int LeadMs { get; private set; }

        /// <summary>欠载次数（播放头追到写指针 → 跳转 + 清零）。调前置大小时看它。</summary>
        public static int Underruns { get; private set; }

        private static bool _pumpStarved = true;  // SetData 泵路径的读门槛迟滞态
        private static long _nextListenerScanAt;  // AudioListener 是整场扫描，别每帧找

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
        public static float DirConeHiDeg { get; set; } = 110f;
        public static float DirSharpHi { get; set; } = 1.5f;

        /// <summary>用当前四个端点值生成 6 带锥形数据（渐变生成器）。</summary>
        public static void ApplyDirectivityGradient()
        {
            _directivity.Family = 0;
            _directivity.FillGradientAngle(DirConeLoDeg, DirSharpLo, DirConeHiDeg, DirSharpHi);
        }

        /// <summary>指向性预设：0 全向 / 1 宽 / 2 标准（180°&0.3 → 110°&1.5）/ 3 强指向。</summary>
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
                    DirConeHiDeg = 110f; DirSharpHi = 1.5f;
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

        private static GameObject _goB;
        private static AudioSource _srcB;
        private static AudioClip _clipB;
        private static readonly float[] _blockStereo = new float[Block * 2];

        public static void SetInterpolation(int interpolation)
        {
            Interpolation = interpolation;
            BinauralEngine.SetInterpolation(interpolation);
        }

        /// <summary>双耳引擎是否已经就绪（Core 只在这里为真时才去压原声音量）。</summary>
        public static bool HrtfReady => BinauralEngine.Initialized;

        public static bool SourcesAlive => _srcB != null;

        // ── 调试：手动坐标模式 + 标记球 ──
        private static bool _manualMode;
        private static Vector3 _manualL, _manualR;
        // 标记球 + 朝向箭头：只在调试版默认显示（发布版里根本不创建）
        private static bool _markersOn = BuildFlags.Dev;
        private static GameObject _markerL, _markerR;
        private static GameObject _aimL, _aimR;   // 朝向箭头（杆 + 尖），局部 +Y = 朝向
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
            if (_goL != null) _goL.transform.position = _manualL;
            if (_goR != null) _goR.transform.position = _manualR;
        }

        public static void GetPositions(out Vector3 l, out Vector3 r, out Vector3 listener)
        {
            l = _goL != null ? _goL.transform.position : Vector3.zero;
            r = _goR != null ? _goR.transform.position : Vector3.zero;
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
                if (_aimL != null) _aimL.SetActive(value);
                if (_aimR != null) _aimR.SetActive(value);
            }
        }

        private static void EnsureMarkers()
        {
            if (!_markersOn || _goL == null || _goR == null) return;
            if (_markerL == null) _markerL = MakeMarker("VS_Marker_L", _goL.transform, new Color(0.25f, 0.55f, 1f));
            if (_markerR == null) _markerR = MakeMarker("VS_Marker_R", _goR.transform, new Color(1f, 0.35f, 0.3f));
            if (_aimL == null) _aimL = MakeAimArrow("VS_Aim_L", _goL.transform, new Color(0.25f, 0.55f, 1f));
            if (_aimR == null) _aimR = MakeAimArrow("VS_Aim_R", _goR.transform, new Color(1f, 0.35f, 0.3f));

            // 朝听者方向偏 10cm，避免半个球埋进屏幕/墙里看不见
            if (_markerL != null) _markerL.transform.localPosition = NudgeTowardListener(_goL);
            if (_markerR != null) _markerR.transform.localPosition = NudgeTowardListener(_goR);

            // 朝向箭头每帧更新（「朝前」跟随头部转，「朝听者」跟随声源与听者的相对位置变）
            if (_aimL != null) _aimL.transform.up = AimDirWorld(0);
            if (_aimR != null) _aimR.transform.up = AimDirWorld(1);
        }

        /// <summary>朝向标注：细杆 + 尖端小球，挂在声源物体上，局部 +Y 就是朝向
        /// （用 transform.up 赋世界方向，避开被 IL2CPP 裁掉的 FromToRotation / LookRotation）。</summary>
        private static GameObject MakeAimArrow(string name, Transform parent, Color color)
        {
            var root = new GameObject(name);
            root.transform.SetParent(parent, false);
            root.transform.localPosition = Vector3.zero;

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

        private static Vector3 NudgeTowardListener(GameObject src)
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

        public static void AttachCapture(ProcessLoopbackCapture capture)
        {
            _capture = capture;
            _startedAt = NowMs;
            _writePos = 0;
            _samplesDue = 0;
            _lastPumpAt = 0;
        }

        public static void DetachCapture()
        {
            StopSources();
            _capture = null;
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
                string lPos = _goL != null ? $"{_goL.transform.position.x:F1},{_goL.transform.position.y:F1},{_goL.transform.position.z:F1}" : "-";
                string rPos = _goR != null ? $"{_goR.transform.position.x:F1},{_goR.transform.position.y:F1},{_goR.transform.position.z:F1}" : "-";
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

                int rate = _capture != null && _capture.SampleRate > 0 ? _capture.SampleRate : 48000;
                if (!BinauralEngine.Initialized && !BinauralEngine.Init(rate, Block))
                {
                    // 没有对照模式可退：引擎起不来就什么也不做，让游戏保持原声，
                    // 下一帧继续重试（比如 phonon.dll 还没放好）。
                    MelonLogger.Error("[VirtualStereo] 双耳引擎不可用，保持游戏原声: " + BinauralEngine.LastError);
                    Object.Destroy(_goL);
                    Object.Destroy(_goR);
                    _goL = _goR = null;
                    return false;
                }

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
                _writePos = rate * RenderLeadMs / 1000;
                _srcB.clip = _clipB;
                _srcB.loop = true;
                _srcB.Play();
                return true;
            }
            catch (Exception e)
            {
                MelonLogger.Error("[VirtualStereo] 创建音频源失败: " + e);
                StopSources();
                return false;
            }
        }

        private static double _samplesDue;
        private static long _lastPumpAt;
        private const int Block = 1024;
        private static readonly float[] _blockL = new float[Block];
        private static readonly float[] _blockR = new float[Block];

        private static void PumpSetData()
        {
            if (_capture == null || _clipB == null) return;

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
            // 积压上限 250ms：欠载/卡顿后**宁可丢内容**，也不要在一帧里补几百毫秒的 DSP
            if (_samplesDue > rate / 4) _samplesDue = rate / 4;

            // 溢出保险（捕获时钟快于播放时钟时会慢慢堆积）：超 500ms 砍回 250ms，
            // 宁可一次内容跳跃，也不要常驻越来越大的延迟。
            int lvl = _capture.RingL.Available < _capture.RingR.Available
                ? _capture.RingL.Available : _capture.RingR.Available;
            if (lvl > rate / 2)
            {
                int drop = lvl - rate / 4;
                _capture.RingL.Discard(drop);
                _capture.RingR.Discard(drop);
                lvl = rate / 4;
            }
            RingLevelMs = lvl * 1000 / Math.Max(1, rate);

            // 读门槛（迟滞）：硬下限 10ms 出静音保护，回到 25ms 才恢复读
            if (_pumpStarved)
            {
                if (lvl >= rate / 40) _pumpStarved = false;
            }
            else if (lvl < rate / 100)
            {
                _pumpStarved = true;
            }
            bool buffered = !_pumpStarved;

            // 写指针自愈：只在**真的欠载**（播放头追到写指针）或领先过大时跳，
            // 并且把从播放头到新写指针这段**清零**——不清的话播放头会读到上一圈
            // 留下的旧内容，听感就是"切片乱跳"。
            var playSrc = _srcB;
            int clipLen = _clipB.samples;
            int playFrame = playSrc.timeSamples;
            int lead = (_writePos - playFrame + clipLen) % clipLen;
            LeadMs = lead * 1000 / Math.Max(1, rate);
            if (lead <= 0 || lead > clipLen / 2)
            {
                int leadSamples = rate * RenderLeadMs / 1000;
                ZeroClipRange(playFrame, leadSamples, clipLen);
                _writePos = (playFrame + leadSamples) % clipLen;
                _samplesDue = 0; // 已经跳过了：积压的那段直接丢，别再补
                Underruns++;
            }

            // 声源方位：方向（HRTF）、方位角/仰角（指向性与房间反射）、真实距离（距离衰减）都出自同一处
            float azL = 0f, elL = 0f, distL = RefDistM;
            float azR = 0f, elR = 0f, distR = RefDistM;
            {
                // 找 AudioListener 是整场扫描，最多 2 秒一次（UpdateSpatial 也会兜底找）
                if (_listener == null && now >= _nextListenerScanAt)
                {
                    _nextListenerScanAt = now + 2000;
                    var al = Object.FindObjectOfType<AudioListener>();
                    _listener = al != null ? al.transform : null;
                }
                // 方向按当前虚拟声源位置 × 头部（相机）朝向换算到头坐标系
                var pL = _goL != null ? _goL.transform.position : Vector3.zero;
                var pR = _goR != null ? _goR.transform.position : Vector3.zero;
                BinauralEngine.SetDirections(HeadDir(pL), HeadDir(pR));
                if (_goL != null) HeadAzElDist(pL, out azL, out elL, out distL);
                if (_goR != null) HeadAzElDist(pR, out azR, out elR, out distR);

                if (_room.Enabled && RoomAutoFit)
                {
                    float dMax = distL > distR ? distL : distR;
                    if (Math.Abs(dMax - _autoFitDist) > 0.3f)
                    {
                        _autoFitDist = dMax;
                        AutoFitRoom(dMax);
                    }
                }
            }

            int blocks = 0;
            float tickPeak = 0f;
            float gain = PreGainLinear;
            // 单帧最多写 3 块（48k 下约 64ms）：卡顿时不至于在一帧里跑满 DSP 把帧率拖死
            // 单帧最多写满"积压上限"那么多（≤250ms）。上限本身已经由上面的 clamp 保证，
            // 这里不再额外收紧——收紧到低于实时消费速度会让缓冲持续堆积、反而丢内容。
            while (_samplesDue >= Block && blocks < rate / 4 / Block)
            {
                if (buffered)
                {
                    _capture.RingL.Read(_blockL, Block);
                    _capture.RingR.Read(_blockR, Block);
                }
                else
                {
                    // 环电平低于 10ms：写静音（欠账照记，回到 25ms 后追平）
                    Array.Clear(_blockL, 0, Block);
                    Array.Clear(_blockR, 0, Block);
                }

                // 前置增益：进空间化模拟之前的输入衰减
                if (gain != 1f)
                {
                    for (int i = 0; i < Block; i++) { _blockL[i] *= gain; _blockR[i] *= gain; }
                }

                if (buffered)
                {
                    // 指向性：按"该声源朝哪"与"听者在该方向看它"的夹角分带着色（离轴才变色）
                    if (_directivity.Enabled)
                    {
                        ResolveAim(0, out int amL, out float aaL, out float aeL);
                        ResolveAim(1, out int amR, out float aaR, out float aeR);
                        _directivity.Process(_dirStateL, _blockL, Block, rate,
                            azL, elL, amL, aaL, aeL);
                        _directivity.Process(_dirStateR, _blockR, Block, rate,
                            azR, elR, amR, aaR, aeR);
                    }

                    // 距离衰减：参考距离处 0dB、距离翻倍 −6dB（与桌面版同一套 1/r 律）
                    if (DistanceOn)
                    {
                        float gl = RefDistM / Math.Max(0.3f, distL);
                        float gr = RefDistM / Math.Max(0.3f, distR);
                        if (gl != 1f) for (int i = 0; i < Block; i++) _blockL[i] *= gl;
                        if (gr != 1f) for (int i = 0; i < Block; i++) _blockR[i] *= gr;
                    }

                    BinauralEngine.Process(_blockL, _blockR, _blockStereo, Block, SaiMode.FullHrtf);
                }
                else
                {
                    // 断流：直接出静音，别在静音上白跑 HRTF/指向性（房间照走，混响尾自然衰减）
                    Array.Clear(_blockStereo, 0, Block * 2);
                }

                // 听音室：一次反射（ITD/ILD + 材料频带吸收）+ 混响尾，累加到两耳（不清零）
                if (_room.Enabled)
                {
                    // 房间几何用"电视挂在 2m 高"的假设：方位与水平距离取真实发声点，
                    // 高度换成电视高度——反射路径因此是"墙上一台电视"的反射，
                    // 而不是屏幕两侧那个真实仰角的反射。
                    RoomAzElDist(azL, elL, distL, out float rAzL, out float rElL, out float rDistL);
                    RoomAzElDist(azR, elR, distR, out float rAzR, out float rElR, out float rDistR);
                    _room.Process(_blockL, _blockR, _blockStereo, Block, rate,
                        rAzL, rElL, rDistL, rAzR, rElR, rDistR);
                }

                _clipB.SetData(_blockStereo, _writePos);
                for (int i = 0; i < Block * 2; i++)
                {
                    float a = _blockStereo[i] < 0 ? -_blockStereo[i] : _blockStereo[i];
                    if (a > tickPeak) tickPeak = a;
                }
                _writePos += Block;
                if (_writePos >= clipLen) _writePos = 0;
                _samplesDue -= Block;
                blocks++;
            }

            // 断流时把双耳声源静音：环形 AudioClip 仍在循环，但不让它把残留内容复读出来
            if (_srcB != null)
            {
                float want = _pumpStarved ? 0f : 1f;
                if (_srcB.volume != want) _srcB.volume = want;
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

        /// <summary>声源相对听者的头坐标方位与距离：az 正前 0° / 右正，el 上正，dist 为真实 3D 距离（米）。
        /// 指向性和听音室都按这套角度工作（与 HeadDir 同一坐标系）。</summary>
        private static void HeadAzElDist(Vector3 sourcePos, out float azDeg, out float elDeg, out float dist)
        {
            azDeg = 0f;
            elDeg = 0f;
            dist = RefDistM;
            if (_listener == null) return;

            var lp = _listener.position;
            float dx = sourcePos.x - lp.x, dy = sourcePos.y - lp.y, dz = sourcePos.z - lp.z;
            float len = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (len < 1e-4f) return;

            var r = _listener.right;
            var u = _listener.up;
            var f = _listener.forward;
            float x = dx * r.x + dy * r.y + dz * r.z;
            float y = dx * u.x + dy * u.y + dz * u.z;
            float z = dx * f.x + dy * f.y + dz * f.z;

            dist = len;
            azDeg = (float)(Math.Atan2(x, z) * 180.0 / Math.PI);
            float s = y / len;
            if (s > 1f) s = 1f; else if (s < -1f) s = -1f;
            elDeg = (float)(Math.Asin(s) * 180.0 / Math.PI);
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
                ScreenFrame(t, out _, out Vector3 viewerRight);

                if (_goL != null)
                {
                    _goL.transform.position = new Vector3(
                        center.x - viewerRight.x * spread,
                        center.y - viewerRight.y * spread,
                        center.z - viewerRight.z * spread);
                }
                if (_goR != null)
                {
                    _goR.transform.position = new Vector3(
                        center.x + viewerRight.x * spread,
                        center.y + viewerRight.y * spread,
                        center.z + viewerRight.z * spread);
                }
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[VirtualStereo] 空间更新失败: " + e.Message);
            }
        }

        /// <summary>屏幕自身的坐标系：正面法线 + 观众右手。只看屏幕 transform 的局部轴，
        /// 与听者位置无关——屏幕是钉上去的一面板子，朝向由摆放时的旋转决定。
        /// （面板零厚度方向=局部Y=法线；高度轴=局部Z，按世界上方翻正。）</summary>
        private static void ScreenFrame(Transform t, out Vector3 normal, out Vector3 right)
        {
            Vector3 n = t.up;
            if (ScreenFrontSign < 0f) n = new Vector3(-n.x, -n.y, -n.z);

            Vector3 up = t.forward;
            if (up.y < 0f) up = new Vector3(-up.x, -up.y, -up.z);

            normal = n;
            // Unity 左手系：观众右手 = cross(面板上, 观众朝屏幕的前方(-normal))
            right = Cross(up, new Vector3(-n.x, -n.y, -n.z));
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
                if (_srcB != null) _srcB.Stop();
                if (_goL != null) Object.Destroy(_goL);
                if (_goR != null) Object.Destroy(_goR);
                if (_goB != null) Object.Destroy(_goB);
            }
            catch { }
            _goL = _goR = _goB = null;
            _srcB = null;
            _clipB = null;
            _markerL = _markerR = null;
            _aimL = _aimR = null;
            _screen = null;
        }

        /// <summary>把 clip 上 [startFrame, startFrame+frames) 写成静音（立体声，offset 以帧计）。
        /// 只在写指针跳转时使用：不清零的话播放头会念到上一圈残留的旧内容（切片乱跳）。</summary>
        private static void ZeroClipRange(int startFrame, int frames, int clipLen)
        {
            if (_clipB == null || frames <= 0) return;
            int done = 0;
            while (done < frames)
            {
                int off = (startFrame + done) % clipLen;
                int n = Block;
                if (n > clipLen - off) n = clipLen - off; // 不越过 clip 末尾
                if (n > frames - done) n = frames - done;
                if (n <= 0) break;
                float[] z = n == Block ? _zeroBlock : new float[n * 2];
                _clipB.SetData(z, off);
                done += n;
            }
        }

        private static readonly float[] _zeroBlock = new float[Block * 2];

        private static long NowMs => Environment.TickCount64;
    }
}
