// IMGUI 调试菜单（F10 开关）：空间化模式、前置增益（dB 滑条+文本框）、SOFA、
// 手动 L/R 坐标、标记球、实时读数。面板拖动。
//
// 游戏 IL2CPP 裁剪现实（查 dump.cs 确认幸存 API）：
//   被裁：BeginArea / DragWindow / HorizontalSlider / GetLastRect / TextField(Rect,...)
//   幸存：GUILayout 的 Box/Button/TextField(text,maxLen,...)/Label/Toggle/BeginHorizontal...
// 因此布局拆两区：标题+增益滑条=固定坐标手绘（鼠标交互自研）；其余=GUILayout 流式。
//
// 整个文件只在调试版（-p:DevBuild=true）里编译：发布版不含调试面板（F10 无响应）。
#if VS_DEV
using System;
using System.Globalization;
using MelonLoader;
using UnityEngine;
using VirtualStereo.Phonon;

namespace VirtualStereo
{
    internal static class DebugMenu
    {
        public static bool Visible;
        private static bool _markersOn = true;
        private static bool _fieldsInit;
        private static string _lx, _ly, _lz, _rx, _ry, _rz;
        private static string _sofaPath = "";
        private static string _gainText = "-12.0";
        private static string _status = "";

        // ── 面板几何 ──
        private const float PanelW = 408f;
        private const float PanelH = 897f;
        private const float TitleH = 30f;
        private const float ManualZoneH = 72f; // 标题 + 增益滑条区（手绘），其下是 GUILayout 区

        // ── 增益滑条几何（面板坐标）──
        private const float TrackX = 72f;
        private const float TrackY = 42f;
        private const float TrackW = 250f;
        private const float TrackH = 20f;
        private const float MinDb = -24f;
        private const float MaxDb = 12f;

        private static Vector2 _pos = new Vector2(16, 16);
        private static bool _dragging;
        private static Vector2 _dragOffset;
        private static bool _gainDragging;

        public static void Toggle()
        {
            Visible = !Visible;
            if (Visible) _fieldsInit = false;
        }

        // ───────────────────────── 输入（面板拖动 + 滑条拖动） ─────────────────────────

        private static void UpdateInput()
        {
            float gx = Input.mousePosition.x;
            float gy = Screen.height - Input.mousePosition.y;
            float lx = gx - _pos.x;
            float ly = gy - _pos.y;

            if (Input.GetMouseButtonDown(0))
            {
                // 标题栏 → 拖面板
                if (lx >= 0 && lx <= PanelW && ly >= 0 && ly <= TitleH)
                {
                    _dragging = true;
                    _dragOffset = new Vector2(gx - _pos.x, gy - _pos.y);
                }
                // 增益轨道 → 拖增益
                else if (lx >= TrackX - 6 && lx <= TrackX + TrackW + 6 && ly >= TrackY - 6 && ly <= TrackY + TrackH + 6)
                {
                    _gainDragging = true;
                }
            }
            if (_dragging)
            {
                if (Input.GetMouseButton(0))
                    _pos = new Vector2(gx - _dragOffset.x, gy - _dragOffset.y);
                else
                    _dragging = false;
            }
            if (_gainDragging)
            {
                if (Input.GetMouseButton(0))
                {
                    float t = (lx - TrackX) / TrackW;
                    if (t < 0f) t = 0f;
                    if (t > 1f) t = 1f;
                    SetGainDb(MinDb + t * (MaxDb - MinDb));
                }
                else
                {
                    _gainDragging = false;
                }
            }
        }

        // ───────────────────────── 绘制 ─────────────────────────

        public static void Draw()
        {
            if (!Visible) return;
            if (!_fieldsInit) RefreshFields();
            UpdateInput();

            // 区一：标题 + 增益滑条（固定坐标手绘）
            SetTranslate(_pos.x, _pos.y);
            GUI.Box(new Rect(0, 0, PanelW, PanelH), "");
            GUI.Label(new Rect(12, 6, PanelW - 24, 22), "VirtualStereo 调试（F10 关闭 · 拖标题移动）");
            DrawGainSlider();

            // 区二：其余内容（GUILayout 流式，平移到滑条区之下）
            SetTranslate(_pos.x, _pos.y + ManualZoneH);
            GUILayout.BeginVertical(GUILayout.Width(400));

            // 单模式：只有双耳 HRTF（对照实验的双音箱/ITD+ILD 已删除）
            {
                GUILayout.Label($"模式: 双耳 HRTF{(AudioEngine.HrtfReady ? "" : "（引擎未就绪）")}");
                GUILayout.BeginHorizontal();
                GUILayout.Label("HRTF插值", GUILayout.Width(60));
                if (GUILayout.Button("最近邻")) AudioEngine.SetInterpolation(0);
                if (GUILayout.Button("双线性")) AudioEngine.SetInterpolation(1);
                GUILayout.EndHorizontal();

                // 「面声源」旋钮：空间度（1 = 完全 HRTF 点声源，调低声像更宽）
                GUILayout.BeginHorizontal();
                GUILayout.Label($"空间度{AudioEngine.SpatialBlend:F2}", GUILayout.Width(80));
                if (GUILayout.Button("-.05", GUILayout.Width(38)))
                    AudioEngine.SpatialBlend = AudioEngine.SpatialBlend - 0.05f;
                if (GUILayout.Button("+.05", GUILayout.Width(38)))
                    AudioEngine.SpatialBlend = AudioEngine.SpatialBlend + 0.05f;
                GUILayout.EndHorizontal();

                // ── 三对虚拟音箱（面声源）：0 上 / 1 中 / 2 下 ──
                GUILayout.BeginHorizontal();
                GUILayout.Label("四对", GUILayout.Width(30));
                string[] rowNames = { "上外", "上内", "下内", "下外" };
                for (int row = 0; row < 4; row++)
                {
                    bool on = GUILayout.Toggle(AudioEngine.RowEnabled(row),
                        rowNames[row], GUILayout.Width(40));
                    if (on != AudioEngine.RowEnabled(row)) AudioEngine.SetRowEnabled(row, on);
                }
                GUILayout.EndHorizontal();

                // 内外层间距分开调
                GUILayout.BeginHorizontal();
                GUILayout.Label($"外间距{AudioEngine.RowOuterDyM:F2}m", GUILayout.Width(88));
                if (GUILayout.Button("-", GUILayout.Width(24)))
                    AudioEngine.RowOuterDyM = Math.Max(0f, AudioEngine.RowOuterDyM - 0.05f);
                if (GUILayout.Button("+", GUILayout.Width(24)))
                    AudioEngine.RowOuterDyM = Math.Min(2f, AudioEngine.RowOuterDyM + 0.05f);
                GUILayout.Label($"内间距{AudioEngine.RowInnerDyM:F2}m", GUILayout.Width(88));
                if (GUILayout.Button("-", GUILayout.Width(24)))
                    AudioEngine.RowInnerDyM = Math.Max(0f, AudioEngine.RowInnerDyM - 0.05f);
                if (GUILayout.Button("+", GUILayout.Width(24)))
                    AudioEngine.RowInnerDyM = Math.Min(2f, AudioEngine.RowInnerDyM + 0.05f);
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                GUILayout.Label($"内层横比{AudioEngine.RowInnerScale:F2}", GUILayout.Width(90));
                if (GUILayout.Button("-", GUILayout.Width(24)))
                    AudioEngine.RowInnerScale = Math.Max(0f, AudioEngine.RowInnerScale - 0.05f);
                if (GUILayout.Button("+", GUILayout.Width(24)))
                    AudioEngine.RowInnerScale = Math.Min(1.5f, AudioEngine.RowInnerScale + 0.05f);
                GUILayout.Label($"外层横比{AudioEngine.RowOuterScale:F2}", GUILayout.Width(90));
                if (GUILayout.Button("-", GUILayout.Width(24)))
                    AudioEngine.RowOuterScale = Math.Max(0f, AudioEngine.RowOuterScale - 0.05f);
                if (GUILayout.Button("+", GUILayout.Width(24)))
                    AudioEngine.RowOuterScale = Math.Min(1.5f, AudioEngine.RowOuterScale + 0.05f);
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                GUILayout.Label($"外偏{AudioEngine.RowOuterAimDeg:F0}°", GUILayout.Width(58));
                if (GUILayout.Button("-2", GUILayout.Width(30)))
                    AudioEngine.RowOuterAimDeg = Math.Max(0f, AudioEngine.RowOuterAimDeg - 2f);
                if (GUILayout.Button("+2", GUILayout.Width(30)))
                    AudioEngine.RowOuterAimDeg = Math.Min(60f, AudioEngine.RowOuterAimDeg + 2f);
                GUILayout.Label($"中层M{AudioEngine.MidGainM:F2}S{AudioEngine.MidGainS:F2}", GUILayout.Width(104));
                if (GUILayout.Button("M-", GUILayout.Width(26))) AudioEngine.MidGainM -= 0.05f;
                if (GUILayout.Button("M+", GUILayout.Width(26))) AudioEngine.MidGainM += 0.05f;
                if (GUILayout.Button("S-", GUILayout.Width(26))) AudioEngine.MidGainS -= 0.05f;
                if (GUILayout.Button("S+", GUILayout.Width(26))) AudioEngine.MidGainS += 0.05f;
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                GUILayout.Label($"外层M{AudioEngine.OuterGainM:F2}S{AudioEngine.OuterGainS:F2}", GUILayout.Width(104));
                if (GUILayout.Button("M-", GUILayout.Width(26))) AudioEngine.OuterGainM -= 0.05f;
                if (GUILayout.Button("M+", GUILayout.Width(26))) AudioEngine.OuterGainM += 0.05f;
                if (GUILayout.Button("S-", GUILayout.Width(26))) AudioEngine.OuterGainS -= 0.05f;
                if (GUILayout.Button("S+", GUILayout.Width(26))) AudioEngine.OuterGainS += 0.05f;
                GUILayout.Label("M 实心 / S 铺开", GUILayout.Width(110));
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                GUILayout.Label("SOFA", GUILayout.Width(40));
                _sofaPath = GUILayout.TextField(_sofaPath, 200, GUILayout.Width(180));
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("载入SOFA"))
                {
                    if (BinauralEngine.LoadSofa(_sofaPath)) _status = "已载入 SOFA: " + _sofaPath;
                    else _status = "SOFA 载入失败（路径对吗？）";
                }
                if (GUILayout.Button("恢复内置HRTF"))
                {
                    if (BinauralEngine.LoadSofa(null)) _status = "已恢复内置 HRTF";
                }
                GUILayout.EndHorizontal();

                // ── 声学：指向性 / 距离衰减 / 听音室 ──
                GUILayout.BeginHorizontal();
                bool dirOn = GUILayout.Toggle(AudioEngine.DirectivityOn, "指向性", GUILayout.Width(72));
                if (dirOn != AudioEngine.DirectivityOn) AudioEngine.DirectivityOn = dirOn;
                bool distOn = GUILayout.Toggle(AudioEngine.DistanceOn, "距离衰减", GUILayout.Width(80));
                if (distOn != AudioEngine.DistanceOn) AudioEngine.DistanceOn = distOn;
                bool roomOn = GUILayout.Toggle(AudioEngine.RoomOn, "听音室", GUILayout.Width(72));
                if (roomOn != AudioEngine.RoomOn) AudioEngine.RoomOn = roomOn;
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                GUILayout.Label("指向", GUILayout.Width(30));
                if (GUILayout.Button("全向", GUILayout.Width(44))) AudioEngine.SetDirectivityPreset(0);
                if (GUILayout.Button("宽", GUILayout.Width(34))) AudioEngine.SetDirectivityPreset(1);
                if (GUILayout.Button("标准", GUILayout.Width(44))) AudioEngine.SetDirectivityPreset(2);
                if (GUILayout.Button("强", GUILayout.Width(34))) AudioEngine.SetDirectivityPreset(3);
                if (GUILayout.Button("8字", GUILayout.Width(40))) AudioEngine.SetDirectivityFamily(1);
                GUILayout.EndHorizontal();

                // 朝向已固定为「观众席」（屏幕法线朝观众，世界系固定、不跟随视角），
                // 所以不再提供调整入口。下面两行保留备查：
                // GUILayout.BeginHorizontal();
                // GUILayout.Label("朝向L", GUILayout.Width(42));
                // if (GUILayout.Button("观众", GUILayout.Width(40))) AudioEngine.AimModeL = 3;
                // if (GUILayout.Button("听者", GUILayout.Width(40))) AudioEngine.AimModeL = 0;
                // if (GUILayout.Button("朝前", GUILayout.Width(40))) AudioEngine.AimModeL = 1;
                // if (GUILayout.Button("手动", GUILayout.Width(40))) AudioEngine.AimModeL = 2;
                // GUILayout.EndHorizontal();
                // GUILayout.BeginHorizontal();
                // GUILayout.Label("朝向R", GUILayout.Width(42));
                // if (GUILayout.Button("观众", GUILayout.Width(40))) AudioEngine.AimModeR = 3;
                // if (GUILayout.Button("听者", GUILayout.Width(40))) AudioEngine.AimModeR = 0;
                // if (GUILayout.Button("朝前", GUILayout.Width(40))) AudioEngine.AimModeR = 1;
                // if (GUILayout.Button("手动", GUILayout.Width(40))) AudioEngine.AimModeR = 2;
                // GUILayout.EndHorizontal();

                // 锥形渐变端点：低频 → 高频（锥角半角 / 锥外衰减指数）
                GUILayout.BeginHorizontal();
                GUILayout.Label($"锥角低{AudioEngine.DirConeLoDeg:F0}°", GUILayout.Width(80));
                if (GUILayout.Button("-5", GUILayout.Width(32)))
                {
                    AudioEngine.DirConeLoDeg = Math.Max(5f, AudioEngine.DirConeLoDeg - 5f);
                    AudioEngine.ApplyDirectivityGradient();
                }
                if (GUILayout.Button("+5", GUILayout.Width(32)))
                {
                    AudioEngine.DirConeLoDeg = Math.Min(180f, AudioEngine.DirConeLoDeg + 5f);
                    AudioEngine.ApplyDirectivityGradient();
                }
                GUILayout.Label($"高{AudioEngine.DirConeHiDeg:F0}°", GUILayout.Width(48));
                if (GUILayout.Button("-5", GUILayout.Width(32)))
                {
                    AudioEngine.DirConeHiDeg = Math.Max(5f, AudioEngine.DirConeHiDeg - 5f);
                    AudioEngine.ApplyDirectivityGradient();
                }
                if (GUILayout.Button("+5", GUILayout.Width(32)))
                {
                    AudioEngine.DirConeHiDeg = Math.Min(180f, AudioEngine.DirConeHiDeg + 5f);
                    AudioEngine.ApplyDirectivityGradient();
                }
                GUILayout.Label("锥角", GUILayout.Width(34));
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                GUILayout.Label($"锐度低{AudioEngine.DirSharpLo:F1}", GUILayout.Width(80));
                if (GUILayout.Button("-.2", GUILayout.Width(32)))
                {
                    AudioEngine.DirSharpLo = Math.Max(0f, AudioEngine.DirSharpLo - 0.2f);
                    AudioEngine.ApplyDirectivityGradient();
                }
                if (GUILayout.Button("+.2", GUILayout.Width(32)))
                {
                    AudioEngine.DirSharpLo = Math.Min(4f, AudioEngine.DirSharpLo + 0.2f);
                    AudioEngine.ApplyDirectivityGradient();
                }
                GUILayout.Label($"高{AudioEngine.DirSharpHi:F1}", GUILayout.Width(48));
                if (GUILayout.Button("-.2", GUILayout.Width(32)))
                {
                    AudioEngine.DirSharpHi = Math.Max(0f, AudioEngine.DirSharpHi - 0.2f);
                    AudioEngine.ApplyDirectivityGradient();
                }
                if (GUILayout.Button("+.2", GUILayout.Width(32)))
                {
                    AudioEngine.DirSharpHi = Math.Min(4f, AudioEngine.DirSharpHi + 0.2f);
                    AudioEngine.ApplyDirectivityGradient();
                }
                GUILayout.Label("锐度", GUILayout.Width(34));
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                GUILayout.Label("房间", GUILayout.Width(30));
                if (GUILayout.Button("电视房", GUILayout.Width(54))) AudioEngine.SetRoomSizePreset(0);
                if (GUILayout.Button("小", GUILayout.Width(30))) AudioEngine.SetRoomSizePreset(1);
                if (GUILayout.Button("中", GUILayout.Width(30))) AudioEngine.SetRoomSizePreset(2);
                if (GUILayout.Button("大", GUILayout.Width(30))) AudioEngine.SetRoomSizePreset(3);
                bool autoFit = GUILayout.Toggle(AudioEngine.RoomAutoFit, "自动", GUILayout.Width(52));
                if (autoFit != AudioEngine.RoomAutoFit) AudioEngine.RoomAutoFit = autoFit;
                GUILayout.Label("反射", GUILayout.Width(30));
                bool reflOn = GUILayout.Toggle(AudioEngine.RoomReflOn, "", GUILayout.Width(20));
                if (reflOn != AudioEngine.RoomReflOn) AudioEngine.RoomReflOn = reflOn;
                GUILayout.Label("混响", GUILayout.Width(30));
                bool revOn = GUILayout.Toggle(AudioEngine.RoomReverbOn, "", GUILayout.Width(20));
                if (revOn != AudioEngine.RoomReverbOn) AudioEngine.RoomReverbOn = revOn;
                GUILayout.EndHorizontal();

                // 材料逐表面选（0 地板 / 1 天花板 / 2 四壁）
                for (int surf = 0; surf < 3; surf++)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(SurfName(surf), GUILayout.Width(38));
                    int cnt = AudioEngine.SurfaceMatCount(surf);
                    int cur = AudioEngine.SurfaceMatIndex(surf);
                    for (int i = 0; i < cnt; i++)
                    {
                        // 当前材料用方括号标记，省掉额外状态显示
                        string nm = AudioEngine.SurfaceMatNameAt(surf, i);
                        string label = i == cur ? "[" + nm + "]" : nm;
                        if (GUILayout.Button(label, GUILayout.Width(56)))
                            AudioEngine.SetSurfaceMat(surf, i);
                    }
                    GUILayout.EndHorizontal();
                }

                GUILayout.BeginHorizontal();
                GUILayout.Label($"明暗{AudioEngine.RoomDamp:F1}", GUILayout.Width(66));
                if (GUILayout.Button("-.1", GUILayout.Width(32)))
                    AudioEngine.RoomDamp = Math.Max(0f, AudioEngine.RoomDamp - 0.1f);
                if (GUILayout.Button("+.1", GUILayout.Width(32)))
                    AudioEngine.RoomDamp = Math.Min(1f, AudioEngine.RoomDamp + 0.1f);
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                GUILayout.Label($"反射{AudioEngine.RoomReflDb:F0}dB", GUILayout.Width(64));
                if (GUILayout.Button("-2", GUILayout.Width(32))) AudioEngine.RoomReflDb = Math.Max(-30f, AudioEngine.RoomReflDb - 2f);
                if (GUILayout.Button("+2", GUILayout.Width(32))) AudioEngine.RoomReflDb = Math.Min(6f, AudioEngine.RoomReflDb + 2f);
                GUILayout.Label($"混响{AudioEngine.RoomReverbDb:F0}dB", GUILayout.Width(68));
                if (GUILayout.Button("-2", GUILayout.Width(32))) AudioEngine.RoomReverbDb = Math.Max(-40f, AudioEngine.RoomReverbDb - 2f);
                if (GUILayout.Button("+2", GUILayout.Width(32))) AudioEngine.RoomReverbDb = Math.Min(6f, AudioEngine.RoomReverbDb + 2f);
                GUILayout.Label($"RT60≈{AudioEngine.RoomRt60Mid:F2}s", GUILayout.Width(84));
                GUILayout.Label($"{AudioEngine.RoomW:F1}×{AudioEngine.RoomD:F1}×{AudioEngine.RoomH:F1}m",
                    GUILayout.Width(96));
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                GUILayout.Label($"反射高度{AudioEngine.TvHeightM:F1}m", GUILayout.Width(80));
                if (GUILayout.Button("-0.1", GUILayout.Width(40)))
                    AudioEngine.TvHeightM = Math.Max(0.6f, AudioEngine.TvHeightM - 0.1f);
                if (GUILayout.Button("+0.1", GUILayout.Width(40)))
                    AudioEngine.TvHeightM = Math.Min(4.0f, AudioEngine.TvHeightM + 0.1f);
                GUILayout.Label($"（仅房间反射用：声源比耳朵高{AudioEngine.SourceRiseM:F1}m，耳高{AudioEngine.EarHeightM:F1}m）",
                    GUILayout.Width(220));
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                GUILayout.Label($"屏幕正面 {(AudioEngine.ScreenFrontSign > 0f ? "+Y" : "−Y")}",
                    GUILayout.Width(110));
                if (GUILayout.Button("翻转正面", GUILayout.Width(70)))
                    AudioEngine.ScreenFrontSign = -AudioEngine.ScreenFrontSign;
                GUILayout.Label("（屏幕局部轴；箭头指向若反了就翻一次）", GUILayout.Width(200));
                GUILayout.EndHorizontal();

                GUILayout.Label("提示：箭头=固定朝向（两箱平行、屏幕法线朝观众，不随视角转）");
            }

            // 增益细调（滑条之外的文本框与步进）
            GUILayout.BeginHorizontal();
            GUILayout.Label("增益dB", GUILayout.Width(48));
            _gainText = GUILayout.TextField(_gainText, 8, GUILayout.Width(56));
            if (GUILayout.Button("-1dB", GUILayout.Width(44))) SetGainDb(AudioEngine.PreGainDb - 1f);
            if (GUILayout.Button("+1dB", GUILayout.Width(44))) SetGainDb(AudioEngine.PreGainDb + 1f);
            GUILayout.Label($"峰值={AudioEngine.LastPeak:F2}", GUILayout.Width(96));
            GUILayout.EndHorizontal();
            SyncGainText();

            // 延迟诊断：采集环电平（读门槛）+ 设备缓冲深度 + 设备欠载次数。
            // 音频不再经 Unity：出声在音频线程上直接写声卡，所以这里没有"渲染前置"这个旋钮了。
                GUILayout.BeginHorizontal();
                GUILayout.Label($"延迟 采集环{AudioEngine.CaptureBufferMs}ms 设备缓冲{AudioEngine.OutFillMs}ms 设备欠载{AudioEngine.OutUnderruns}",
                    GUILayout.Width(300));
                GUILayout.Label($"门槛{AudioEngine.PreRollLowMs}/{AudioEngine.PreRollHighMs}ms", GUILayout.Width(100));
                GUILayout.EndHorizontal();

            // ── watch party：房间里可能有多台投影，但只让**离摄像头最近的那台**发声 ──
            GUILayout.Label($"watch party {AudioEngine.ScreenCount} 台，只从最近那台发声");
            for (int i = 0; i < AudioEngine.ActiveScreenCount; i++)
                GUILayout.Label("  " + AudioEngine.ActiveScreenLabel(i));

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("恢复自动", GUILayout.Width(90)))
            {
                AudioEngine.ClearManual();
                _status = "已恢复自动跟随屏幕";
            }
            if (GUILayout.Button("读取当前", GUILayout.Width(90)))
            {
                RefreshFields();
                _status = "已读取当前坐标";
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("L x", GUILayout.Width(26));
            _lx = GUILayout.TextField(_lx, 16, GUILayout.Width(66));
            GUILayout.Label("y", GUILayout.Width(12));
            _ly = GUILayout.TextField(_ly, 16, GUILayout.Width(66));
            GUILayout.Label("z", GUILayout.Width(12));
            _lz = GUILayout.TextField(_lz, 16, GUILayout.Width(66));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("R x", GUILayout.Width(26));
            _rx = GUILayout.TextField(_rx, 16, GUILayout.Width(66));
            GUILayout.Label("y", GUILayout.Width(12));
            _ry = GUILayout.TextField(_ry, 16, GUILayout.Width(66));
            GUILayout.Label("z", GUILayout.Width(12));
            _rz = GUILayout.TextField(_rz, 16, GUILayout.Width(66));
            GUILayout.EndHorizontal();

            if (GUILayout.Button("应用坐标", GUILayout.Width(120)))
            {
                if (TryParse3(_lx, _ly, _lz, out var l) && TryParse3(_rx, _ry, _rz, out var r))
                {
                    AudioEngine.SetManualPositions(l, r);
                    _status = $"手动定位生效 L={Fmt(l)} R={Fmt(r)}";
                }
                else
                {
                    _status = "坐标解析失败（要数字）";
                }
            }

            bool newMarkers = GUILayout.Toggle(_markersOn, "标记球（L 蓝 / R 红）");
            if (newMarkers != _markersOn)
            {
                _markersOn = newMarkers;
                AudioEngine.MarkersOn = newMarkers;
            }

            AudioEngine.GetPositions(out var cl, out var cr, out var listener);
            GUILayout.Label($"L={Fmt(cl)}   R={Fmt(cr)}");
            GUILayout.Label($"朝向 L={Fmt(AudioEngine.AimDirWorld(0))}  R={Fmt(AudioEngine.AimDirWorld(1))}");
            GUILayout.Label($"听者={Fmt(listener)}");
            if (_status.Length > 0) GUILayout.Label(_status);
            GUILayout.EndVertical();

            SetTranslate(0, 0); // 还原，别影响游戏其它 IMGUI
        }

        /// <summary>手绘增益滑条：轨道 + 填充 + 滑块 + dB 读数。</summary>
        private static void DrawGainSlider()
        {
            float t = (AudioEngine.PreGainDb - MinDb) / (MaxDb - MinDb);
            if (t < 0f) t = 0f;
            if (t > 1f) t = 1f;

            GUI.Box(new Rect(TrackX, TrackY, TrackW, TrackH), "");
            Color old = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.25f, 0.55f, 1f);
            GUI.Box(new Rect(TrackX, TrackY, TrackW * t, TrackH), "");
            GUI.backgroundColor = old;
            GUI.Box(new Rect(TrackX + TrackW * t - 3f, TrackY - 2f, 6f, TrackH + 4f), "");

            GUI.Label(new Rect(12, TrackY, 58, TrackH), "前置增益");
            GUI.Label(new Rect(TrackX + TrackW + 10, TrackY, 68, TrackH),
                AudioEngine.PreGainDb.ToString("F1", CultureInfo.InvariantCulture) + " dB");
        }

        /// <summary>平移矩阵：把面板内容挪到拖动位置（BeginArea 被裁剪；Matrix4x4.Translate 属损坏数学成员，字段手工搭）。</summary>
        private static void SetTranslate(float x, float y)
        {
            var m = new Matrix4x4();
            m.m00 = 1f; m.m11 = 1f; m.m22 = 1f; m.m33 = 1f;
            m.m03 = x; m.m13 = y;
            GUI.matrix = m;
        }

        // ───────────────────────── 逻辑 ─────────────────────────

        private static void SetGainDb(float db)
        {
            if (db < MinDb) db = MinDb;
            if (db > MaxDb) db = MaxDb;
            AudioEngine.PreGainDb = db;
            _gainText = db.ToString("F1", CultureInfo.InvariantCulture);
        }

        /// <summary>文本框自由输入（dB）：解析成功即生效，不回写文本免得打断输入。</summary>
        private static void SyncGainText()
        {
            string s = _gainText.Trim().TrimEnd('d', 'B', 'D', 'b', ' ');
            if (float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float db))
            {
                if (db >= MinDb && db <= MaxDb && Math.Abs(db - AudioEngine.PreGainDb) > 0.05f)
                    AudioEngine.PreGainDb = db;
            }
        }

        private static void RefreshFields()
        {
            AudioEngine.GetPositions(out var l, out var r, out _);
            _lx = F(l.x); _ly = F(l.y); _lz = F(l.z);
            _rx = F(r.x); _ry = F(r.y); _rz = F(r.z);
            _fieldsInit = true;
        }

        private static bool TryParse3(string a, string b, string c, out Vector3 v)
        {
            v = Vector3.zero;
            if (!float.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out float x)) return false;
            if (!float.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out float y)) return false;
            if (!float.TryParse(c, NumberStyles.Float, CultureInfo.InvariantCulture, out float z)) return false;
            v = new Vector3(x, y, z);
            return true;
        }

        private static string F(float v) => v.ToString("F2", CultureInfo.InvariantCulture);

        /// <summary>表面名（与 RoomModel 的表面序一致：0 地板 / 1 天花板 / 2 四壁）。</summary>
        private static string SurfName(int surface)
        {
            switch (surface)
            {
                case 0: return "地板";
                case 1: return "天花";
                default: return "四壁";
            }
        }

        private static string Fmt(Vector3 v) =>
            $"({v.x.ToString("F1", CultureInfo.InvariantCulture)},{v.y.ToString("F1", CultureInfo.InvariantCulture)},{v.z.ToString("F1", CultureInfo.InvariantCulture)})";
    }
}
#endif
