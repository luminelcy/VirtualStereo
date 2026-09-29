// ImGui 界面：左侧导航 + 面板内容区（状态机见 UiState.cs）。
//
// 结构：Panels 注册表 = 全部面板（标题/小字/绘制函数）；导航由注册表生成，
// 加功能 = Page 枚举 + 注册表一行，见 UiState.cs 头注释。
// 中文用系统雅黑字体；♪ 字形缺失故发声标记用 [响]。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using ImGuiNET;
using VirtualStereo.Capture;
using VirtualStereo.Dsp;

namespace VirtualStereo.Desktop
{
    internal sealed class PanelDef
    {
        public Page Id;
        public string Title;
        public string Subtitle;
        public Action<DesktopApp> Draw;
    }

    internal static class Ui
    {
        // ── 面板注册表：新增面板在这里加一行 + 写一个 Draw*Panel ──
        private static readonly PanelDef[] Panels =
        {
            new PanelDef { Id = Page.Main, Title = "主面板", Subtitle = "音源与运行状态", Draw = DrawMainPanel },
            new PanelDef { Id = Page.Processing, Title = "处理", Subtitle = "前后增益 · 电平", Draw = DrawProcessingPanel },
            new PanelDef { Id = Page.Spatial, Title = "空间模拟", Subtitle = "模式 · 方位 · HRTF", Draw = DrawSpatialPanel },
            new PanelDef { Id = Page.Speakers, Title = "音箱设置", Subtitle = "分频指向性 · 图案", Draw = DrawSpeakersPanel },
            new PanelDef { Id = Page.Analysis, Title = "两耳分析", Subtitle = "波形 · 频谱", Draw = DrawAnalysisPanel },
            new PanelDef { Id = Page.Calibration, Title = "校准", Subtitle = "扫频 · 频响拉平", Draw = DrawCalibrationPanel },
            new PanelDef { Id = Page.PostProcess, Title = "后处理", Subtitle = "输出 PEQ · 曲线", Draw = DrawPostPanel },
            new PanelDef { Id = Page.Room, Title = "听音室", Subtitle = "房间 · 反射 · 混响", Draw = DrawRoomPanel },
            new PanelDef { Id = Page.SpeakerCal, Title = "音箱校准", Subtitle = "每箱扫频 · 源端补偿", Draw = DrawSpeakerCalPanel },
            new PanelDef { Id = Page.Stereo, Title = "声相分析", Subtitle = "李萨如 · 相关 · 平衡", Draw = DrawStereoPanel },
        };

        private struct Row
        {
            public int Pid;
            public string Name;
            public string Display;
        }

        private static Settings _cfg;
        private static string _pendingName = ""; // 上次会话的进程名：扫描到就自动选中
        private static string _search = "";
        private static string _sofaPath = "";
        private static string _status = "选择进程后点「开始捕获」";
        private static int _selectedPid = -1;
        private static string _selectedName = "";
        private static List<Row> _rows = new();
        private static long _nextScanAt;
        private static string _preGainText = "";
        private static string _postGainText = "";
        private static bool _silenceTmp = true;

        public static string CurrentSofaPath => _sofaPath;

        public static void Init(Settings s)
        {
            _cfg = s;
            _sofaPath = s.SofaPath ?? "";
            _pendingName = s.LastProcessName ?? "";
            // 恢复上次停留的面板（越界回退主面板）
            UiState.Current = s.LastPage >= 0 && s.LastPage < Panels.Length
                ? (Page)s.LastPage
                : Page.Main;
        }

        // ───────────────────────── 主框架 ─────────────────────────

        public static void Draw(DesktopApp app)
        {
            // 铺满视口的固定宿主：无标题栏/不可拖/不可折叠——
            // 可拖的内窗口拖出外层视口就找不回来（实测反馈）
            var io = ImGui.GetIO();
            ImGui.SetNextWindowPos(System.Numerics.Vector2.Zero);
            ImGui.SetNextWindowSize(io.DisplaySize);
            ImGui.Begin("##host", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings);

            DrawSidebar();
            ImGui.SameLine();
            DrawContent(app);

            ImGui.End();

            PumpCalibration(app); // 状态推进挂主循环：切面板不打断扫频/分析
        }

        private static void DrawSidebar()
        {
            ImGui.BeginChild("##nav", new System.Numerics.Vector2(200, -1), true);
            ImGui.Text("VirtualStereo");
            ImGui.TextDisabled("实验平台");
            ImGui.Separator();

            // 注意：Selectable 的尺寸必须显式给宽度——它不像 Button 支持 -1 填满，
            // 负宽度会生成塌缩矩形，文字被裁成残字（实测教训）
            float rowW = ImGui.GetContentRegionAvail().X;
            foreach (var p in Panels)
            {
                bool active = p.Id == UiState.Current;
                if (ImGui.Selectable(p.Title + "##nav" + (int)p.Id, active, 0, new System.Numerics.Vector2(rowW, 36)))
                {
                    UiState.Go(p.Id);
                    if (_cfg != null) _cfg.LastPage = (int)p.Id;
                }
                if (active)
                    ImGui.TextDisabled(p.Subtitle);
            }

            ImGui.EndChild();
        }

        private static void DrawContent(DesktopApp app)
        {
            ImGui.BeginChild("##content", new System.Numerics.Vector2(0, -1), false);

            PanelDef panel = Panels[0];
            foreach (var p in Panels)
                if (p.Id == UiState.Current) panel = p;

            ImGui.Text(panel.Title);
            ImGui.TextDisabled(panel.Subtitle);
            ImGui.Separator();

            panel.Draw(app);

            ImGui.Separator();
            ImGui.TextWrapped(_status);
            ImGui.EndChild();
        }

        // ───────────────────────── 主面板 ─────────────────────────

        private static void DrawMainPanel(DesktopApp app)
        {
            ImGui.Text("音源（程序集=进程树；[响]=正在发声；捕获=选中节点及其子进程）");
            ImGui.InputText("搜索", ref _search, 64);
            ImGui.SameLine();
            if (ImGui.Button("刷新")) Rescan(true);
            ImGui.SameLine();
            if (ImGui.Button("开始捕获"))
            {
                _status = _selectedPid > 0
                    ? app.StartCapture(_selectedPid)
                    : "先在列表里选一个进程";
            }
            ImGui.SameLine();
            if (ImGui.Button("停止"))
            {
                app.StopCapture();
                _status = "已停止（原声音量已恢复）";
            }

            if (Environment.TickCount64 >= _nextScanAt) Rescan(false);

            var io = ImGui.GetIO();
            float listH = Math.Max(160f, io.DisplaySize.Y * 0.42f);
            ImGui.BeginChild("proclist", new System.Numerics.Vector2(-1, listH), true);
            foreach (var row in _rows)
            {
                bool selected = row.Pid == _selectedPid;
                if (ImGui.Selectable(row.Display + "##p" + row.Pid, selected))
                {
                    _selectedPid = row.Pid;
                    _selectedName = row.Display.Trim();
                    if (_cfg != null) _cfg.LastProcessName = row.Name;
                }
            }
            if (_rows.Count == 0)
                ImGui.TextDisabled("（没有匹配的进程）");
            ImGui.EndChild();

            ImGui.Spacing();
            ImGui.Text($"选中: {_selectedName}");
            ImGui.Text(app.Capturing
                ? $"捕获中 RMS={app.CaptureRms:F4}  L={app.CaptureL:F4}  R={app.CaptureR:F4}"
                : "未捕获");
            ImGui.Text($"模式={ModeName(app)}   峰值={app.OutPeak:F3}");
            ImGui.TextDisabled("增益与电平细节见「处理」；声源方位见「空间模拟」");
        }

        private static string ModeName(DesktopApp app)
        {
            switch (app.CurrentMode)
            {
                case 1: return "双耳HRTF";
                case 2: return "ITD+ILD";
                default: return "直通";
            }
        }

        // ───────────────────────── 处理面板 ─────────────────────────

        private static void DrawProcessingPanel(DesktopApp app)
        {
            // 前置增益：进空间模拟之前
            GainRow("前置增益dB##pre", app.PreGainDb, -24f, 12f, v => app.PreGainDb = v, ref _preGainText);
            ImGui.TextDisabled("-> 进模拟之前（输入电平）");

            // 后置增益：空间模拟之后、出声之前
            GainRow("后置增益dB##post", app.PostGainDb, -24f, 12f, v => app.PostGainDb = v, ref _postGainText);
            ImGui.TextDisabled("-> 模拟之后（输出电平）");

            ImGui.Spacing();
            ImGui.Text(app.Capturing
                ? $"捕获 RMS={app.CaptureRms:F4}  L={app.CaptureL:F4}  R={app.CaptureR:F4}   " +
                  $"捕获缓冲 {app.CaptureBufferMs} ms（暂停/跳转的响应延迟主体）"
                : "未捕获");
            if (app.PlayerError != null)
                ImGui.TextColored(new System.Numerics.Vector4(1, 0.4f, 0.4f, 1), "播放错误: " + app.PlayerError);

            ImGui.Text("输出电平（后置增益之后；峰值保持 1.8s 后 30dB/s 回落）");
            DrawOutputMeters(app);

            ImGui.Spacing();
            if (ImGui.Checkbox("静音原声（消双响）", ref _silenceTmp))
                app.SilenceOriginal = _silenceTmp;

            // 人头两耳：虚拟人头各耳实际收到的信号
            ImGui.Spacing();
            ImGui.Text("人头两耳（空间化之后、校准 EQ 与后置增益之前）");
            var mon = app.Monitor;
            ImGui.PushStyleColor(ImGuiCol.PlotHistogram, new Vector4(0.31f, 0.59f, 1f, 1f));
            ImGui.ProgressBar(Math.Min(mon.PeakL, 1.5f) / 1.5f, new Vector2(320, 16), "");
            ImGui.PopStyleColor();
            ImGui.SameLine();
            ImGui.Text($"L耳   RMS {mon.RmsL:F4}   峰 {mon.PeakL:F3}");
            ImGui.PushStyleColor(ImGuiCol.PlotHistogram, new Vector4(1f, 0.37f, 0.35f, 1f));
            ImGui.ProgressBar(Math.Min(mon.PeakR, 1.5f) / 1.5f, new Vector2(320, 16), "");
            ImGui.PopStyleColor();
            ImGui.SameLine();
            ImGui.Text($"R耳   RMS {mon.RmsR:F4}   峰 {mon.PeakR:F3}");
            float diff = EarDiffDb(mon.RmsL, mon.RmsR);
            ImGui.Text("耳间声级差 (L-R): " + diff.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture) + " dB");

            ImGui.Spacing();
            ImGui.Text("录制（32bit float WAV -> recordings/）");
            int rm = mon.Mode;
            if (ImGui.RadioButton("左耳##rec", ref rm, 0)) mon.Mode = rm;
            ImGui.SameLine();
            if (ImGui.RadioButton("右耳##rec", ref rm, 1)) mon.Mode = rm;
            ImGui.SameLine();
            if (ImGui.RadioButton("双声道##rec", ref rm, 2)) mon.Mode = rm;
            ImGui.SameLine();
            if (!mon.Recording)
            {
                if (ImGui.Button("开始录制"))
                {
                    if (!app.Capturing)
                    {
                        _status = "先在主面板开始捕获";
                    }
                    else
                    {
                        string path = mon.StartRecording(
                            (EarMonitor.RecMode)rm, app.CaptureRate,
                            System.IO.Path.Combine(AppContext.BaseDirectory, "recordings"));
                        _status = path != null ? "录制中: " + path : "录制启动失败: " + mon.LastError;
                    }
                }
            }
            else
            {
                if (ImGui.Button("停止录制"))
                {
                    mon.StopRecording();
                    _status = "录制已保存到 recordings/";
                }
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), $"录制中 {mon.RecSeconds:F1}s");
            }
        }

        // ── 输出电平表（峰值保持 1.8s 后 30dB/s 回落）──
        private static float _holdL = -60f, _holdR = -60f;
        private static long _holdLt, _holdRt;

        private static float ToDb(float v) => (float)(20.0 * Math.Log10(Math.Max(1e-7, v)));

        private static uint MeterColor(float db) =>
            db > 0f ? Col(240, 70, 60)
            : db > -6f ? Col(240, 130, 50)
            : db > -12f ? Col(230, 190, 60)
            : Col(80, 200, 90);

        private static float PeakHold(ref float baseVal, ref long since, float db, long now)
        {
            if (db >= baseVal) { baseVal = db; since = now; }
            float el = now - since;
            float decay = el > 1800f ? 30f * (el - 1800f) / 1000f : 0f;
            return Math.Max(db, baseVal - decay);
        }

        private static void DrawOutputMeters(DesktopApp app)
        {
            const float scaleMin = -60f, scaleMax = 6f;
            long now = Environment.TickCount64;
            float dbL = Math.Clamp(ToDb(app.OutPeakL), scaleMin, scaleMax);
            float dbR = Math.Clamp(ToDb(app.OutPeakR), scaleMin, scaleMax);
            float holdL = PeakHold(ref _holdL, ref _holdLt, dbL, now);
            float holdR = PeakHold(ref _holdR, ref _holdRt, dbR, now);

            const float rowH = 20f;
            float w = 470f;
            Vector2 o = ImGui.GetCursorScreenPos();
            ImGui.Dummy(new Vector2(w, rowH * 2 + 17));
            var dl = ImGui.GetWindowDrawList();

            float x0 = o.X + 14f, bw = w - 14f - 78f;
            DrawMeterRow(dl, x0, o.Y, bw, rowH - 4, dbL, holdL, "L", scaleMin, scaleMax);
            DrawMeterRow(dl, x0, o.Y + rowH, bw, rowH - 4, dbR, holdR, "R", scaleMin, scaleMax);

            // dB 刻度（与条对齐）
            float PosT(float db) => (Math.Clamp(db, scaleMin, scaleMax) - scaleMin) / (scaleMax - scaleMin) * bw;
            foreach (int t in new[] { -60, -40, -30, -20, -12, -6, 0 })
            {
                float x = x0 + PosT(t);
                dl.AddLine(new Vector2(x, o.Y + rowH * 2), new Vector2(x, o.Y + rowH * 2 + 3), Col(95, 100, 115));
                string lbl = t.ToString(CultureInfo.InvariantCulture);
                dl.AddText(new Vector2(x - (lbl.Length > 2 ? 11 : lbl.Length > 1 ? 7 : 4),
                    o.Y + rowH * 2 + 4), Col(95, 100, 115), lbl);
            }

            if (app.OutPeak > 1f)
                ImGui.TextColored(new Vector4(1f, 0.35f, 0.3f, 1f), "爆电平！");
        }

        private static void DrawMeterRow(ImDrawListPtr dl, float x, float y, float w, float h,
            float db, float hold, string label, float min, float max)
        {
            float Pos(float v) => (Math.Clamp(v, min, max) - min) / (max - min) * w;

            dl.AddRectFilled(new Vector2(x, y), new Vector2(x + w, y + h), Col(30, 32, 40));
            // 警告(-6..0)/危险(0..+)区段暗底
            dl.AddRectFilled(new Vector2(x + Pos(-6f), y + 1), new Vector2(x + Pos(0f), y + h - 1), Col(52, 44, 22));
            dl.AddRectFilled(new Vector2(x + Pos(0f), y + 1), new Vector2(x + w, y + h - 1), Col(58, 26, 26));
            // 峰值填充（按电平变色）
            float px = x + Pos(db);
            if (px > x + 1f)
                dl.AddRectFilled(new Vector2(x + 1, y + 1), new Vector2(px, y + h - 1), MeterColor(db));
            // 峰值保持线
            float hx = x + Pos(hold);
            dl.AddRectFilled(new Vector2(hx - 1, y + 1), new Vector2(hx + 1, y + h - 1), Col(235, 235, 240));
            dl.AddRect(new Vector2(x, y), new Vector2(x + w, y + h), Col(60, 60, 70));

            dl.AddText(new Vector2(x - 13, y + (h - 12) * 0.5f), Col(150, 155, 170), label);
            string val = db <= min + 0.01f ? "静音"
                : db.ToString("F1", CultureInfo.InvariantCulture) + " dB";
            dl.AddText(new Vector2(x + w + 6, y + (h - 12) * 0.5f),
                db > 0f ? Col(240, 70, 60) : Col(150, 155, 170), val);
        }

        /// <summary>增益行：滑条 + 文本框（Ctrl+点滑条也能输）。</summary>
        private static void GainRow(string label, float db, float min, float max,
            Action<float> set, ref string text)
        {
            float v = db;
            ImGui.SetNextItemWidth(180);
            if (ImGui.SliderFloat(label, ref v, min, max, "%.1f dB")) set(v);
            ImGui.SameLine();
            if (text.Length == 0 || Math.Abs(Parse(text) - db) > 0.05f)
                text = db.ToString("F1", CultureInfo.InvariantCulture);
            ImGui.SetNextItemWidth(60);
            if (ImGui.InputText("##t" + label, ref text, 8))
            {
                float t = Parse(text);
                if (!float.IsNaN(t)) set(Math.Clamp(t, min, max));
            }
        }

        // ───────────────────────── 空间模拟面板 ─────────────────────────

        private static void DrawSpatialPanel(DesktopApp app)
        {
            int mode = app.CurrentMode;
            if (ImGui.RadioButton("直通", ref mode, 0)) app.CurrentMode = mode;
            ImGui.SameLine();
            if (ImGui.RadioButton("双耳HRTF", ref mode, 1)) app.CurrentMode = mode;
            ImGui.SameLine();
            if (ImGui.RadioButton("ITD+ILD", ref mode, 2)) app.CurrentMode = mode;

            // 声源几何：方位 / 仰角 / 距离
            ImGui.Spacing();
            ImGui.Text("声源几何（头坐标系：正前 0°，右正；距离 2m 为参考）");
            float azL = app.AzL, eL = app.ElL, dL = app.DistL;
            ImGui.SetNextItemWidth(190);
            if (ImGui.SliderFloat("L 方位角", ref azL, -180f, 180f, "%.0f")) app.AzL = azL;
            ImGui.SameLine();
            ImGui.SetNextItemWidth(190);
            if (ImGui.SliderFloat("L 仰角", ref eL, -90f, 90f, "%.0f")) app.ElL = eL;
            ImGui.SameLine();
            ImGui.SetNextItemWidth(190);
            if (ImGui.SliderFloat("L 距离", ref dL, 0.3f, 20f, "%.1f m")) app.DistL = dL;

            float azR = app.AzR, eR = app.ElR, dR = app.DistR;
            ImGui.SetNextItemWidth(190);
            if (ImGui.SliderFloat("R 方位角", ref azR, -180f, 180f, "%.0f")) app.AzR = azR;
            ImGui.SameLine();
            ImGui.SetNextItemWidth(190);
            if (ImGui.SliderFloat("R 仰角", ref eR, -90f, 90f, "%.0f")) app.ElR = eR;
            ImGui.SameLine();
            ImGui.SetNextItemWidth(190);
            if (ImGui.SliderFloat("R 距离", ref dR, 0.3f, 20f, "%.1f m")) app.DistR = dR;

            ImGui.Text($"距离衰减（反比，每翻倍 -6dB）  L: {AttenDb(app.DistL):F1} dB   R: {AttenDb(app.DistR):F1} dB");

            // 音箱朝向（每源独立；决定指向性的离轴角）
            ImGui.Spacing();
            ImGui.Text("音箱朝向（每源独立）");
            int aim = app.AimModeL;
            ImGui.Text("L:");
            ImGui.SameLine();
            if (ImGui.RadioButton("朝向听者##L0", ref aim, 0)) app.AimModeL = aim;
            ImGui.SameLine();
            if (ImGui.RadioButton("固定朝前##L1", ref aim, 1)) app.AimModeL = aim;
            ImGui.SameLine();
            if (ImGui.RadioButton("手动##L2", ref aim, 2)) app.AimModeL = aim;
            if (aim == 2)
            {
                ImGui.SameLine();
                float aa = app.AimAzL, ae = app.AimElL;
                ImGui.SetNextItemWidth(140);
                if (ImGui.SliderFloat("方位##La", ref aa, -180f, 180f, "%.0f")) app.AimAzL = aa;
                ImGui.SameLine();
                ImGui.SetNextItemWidth(140);
                if (ImGui.SliderFloat("仰角##Le", ref ae, -90f, 90f, "%.0f")) app.AimElL = ae;
            }

            aim = app.AimModeR;
            ImGui.Text("R:");
            ImGui.SameLine();
            if (ImGui.RadioButton("朝向听者##R0", ref aim, 0)) app.AimModeR = aim;
            ImGui.SameLine();
            if (ImGui.RadioButton("固定朝前##R1", ref aim, 1)) app.AimModeR = aim;
            ImGui.SameLine();
            if (ImGui.RadioButton("手动##R2", ref aim, 2)) app.AimModeR = aim;
            if (aim == 2)
            {
                ImGui.SameLine();
                float aa = app.AimAzR, ae = app.AimElR;
                ImGui.SetNextItemWidth(140);
                if (ImGui.SliderFloat("方位##Ra", ref aa, -180f, 180f, "%.0f")) app.AimAzR = aa;
                ImGui.SameLine();
                ImGui.SetNextItemWidth(140);
                if (ImGui.SliderFloat("仰角##Re", ref ae, -90f, 90f, "%.0f")) app.AimElR = ae;
            }
            ImGui.TextDisabled("朝向听者=平直响应；固定朝前=声源偏离正前方即可听出高频变暗");

            // 摆位图示（俯视）
            DrawSourceDiagram(app);

            // HRTF
            ImGui.Spacing();
            ImGui.Text("HRTF");
            int interp = app.Interpolation;
            if (ImGui.RadioButton("最近邻", ref interp, 0)) { app.Interpolation = interp; app.ApplyInterpolation(); }
            ImGui.SameLine();
            if (ImGui.RadioButton("双线性", ref interp, 1)) { app.Interpolation = interp; app.ApplyInterpolation(); }

            ImGui.InputText("SOFA 路径", ref _sofaPath, 400);
            ImGui.SameLine();
            if (ImGui.Button("载入SOFA"))
                _status = app.LoadSofa(_sofaPath) ? "已载入 SOFA: " + _sofaPath : "SOFA 载入失败（路径对吗？）";
            ImGui.SameLine();
            if (ImGui.Button("恢复内置"))
            {
                app.LoadSofa(null);
                _status = "已恢复内置 HRTF（CIPIC #124）";
            }
        }

        /// <summary>距离衰减 dB（2m 参考，反比）。</summary>
        private static float AttenDb(float dist)
        {
            double g = 2.0 / Math.Max(0.3, dist);
            return (float)(20.0 * Math.Log10(g));
        }

        // ───────────────────────── 摆位图示（俯视） ─────────────────────────

        private static uint Col(byte r, byte g, byte b) => 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | b;

        /// <summary>俯视摆位图：听者居中（上方=正前），双音箱按方位/距离落点，箭头示朝向。</summary>
        private static void DrawSourceDiagram(DesktopApp app)
        {
            ImGui.Spacing();
            ImGui.Text("摆位图示（俯视：上方=正前，右方=右）");

            const float size = 330f;
            Vector2 origin = ImGui.GetCursorScreenPos();
            ImGui.Dummy(new Vector2(size, size)); // 占位推进布局
            var dl = ImGui.GetWindowDrawList();

            Vector2 c = new Vector2(origin.X + size / 2f, origin.Y + size / 2f);
            float half = size / 2f - 14f;
            float maxD = Math.Max(3f, Math.Max(app.DistL, app.DistR) * 1.15f);
            float px = half / maxD;

            dl.AddRectFilled(origin, origin + new Vector2(size, size), Col(24, 24, 30));
            dl.AddRect(origin, origin + new Vector2(size, size), Col(60, 60, 70));

            // 距离环（2m 参考加亮）
            foreach (float r in new[] { 2f, 5f, 10f, 20f })
            {
                if (r > maxD) continue;
                uint ringCol = Math.Abs(r - 2f) < 0.01f ? Col(75, 85, 105) : Col(45, 48, 58);
                dl.AddCircle(c, r * px, ringCol, 64);
                dl.AddText(new Vector2(c.X + 5f, c.Y - r * px - 15f), Col(95, 100, 115), r.ToString("F0") + "m");
            }

            // 前 / 右 轴
            dl.AddLine(c, new Vector2(c.X, c.Y - half), Col(70, 75, 90));
            dl.AddText(new Vector2(c.X + 6f, c.Y - half - 2f), Col(115, 122, 140), "前");
            dl.AddLine(c, new Vector2(c.X + half, c.Y), Col(60, 64, 78));
            dl.AddText(new Vector2(c.X + half - 16f, c.Y + 4f), Col(115, 122, 140), "右");

            // 听者（圆点 + 朝前小三角）
            dl.AddCircleFilled(c, 7f, Col(230, 230, 235));
            dl.AddTriangleFilled(
                new Vector2(c.X, c.Y - 14f),
                new Vector2(c.X - 6f, c.Y - 4f),
                new Vector2(c.X + 6f, c.Y - 4f),
                Col(230, 230, 235));
            dl.AddText(new Vector2(c.X - 22f, c.Y + 9f), Col(180, 182, 190), "听者");

            DrawSpeaker(dl, c, px, app, true);
            DrawSpeaker(dl, c, px, app, false);
        }

        private static void DrawSpeaker(ImDrawListPtr dl, Vector2 c, float px, DesktopApp app, bool left)
        {
            float az = left ? app.AzL : app.AzR;
            float el = left ? app.ElL : app.ElR;
            float dist = left ? app.DistL : app.DistR;
            int mode = left ? app.AimModeL : app.AimModeR;
            float aimAz = left ? app.AimAzL : app.AimAzR;
            float aimEl = left ? app.AimElL : app.AimElR;
            uint col = left ? Col(80, 150, 255) : Col(255, 95, 90);

            double a = az * Math.PI / 180.0;
            var p = new Vector2(
                c.X + (float)Math.Sin(a) * dist * px,
                c.Y - (float)Math.Cos(a) * dist * px);

            // 听者->音箱 连线
            dl.AddLine(c, p, Col(55, 58, 68));

            // 朝向箭头（顶视投影）
            double ax, ay;
            if (mode == 0)
            {
                float tx = c.X - p.X, ty = c.Y - p.Y;
                double len = Math.Max(1e-3, Math.Sqrt(tx * tx + ty * ty));
                ax = tx / len; ay = ty / len;
            }
            else if (mode == 1) { ax = 0; ay = -1; }
            else
            {
                double aa = aimAz * Math.PI / 180.0;
                ax = Math.Sin(aa); ay = -Math.Cos(aa);
            }
            var tip = new Vector2(p.X + (float)ax * 26f, p.Y + (float)ay * 26f);
            dl.AddLine(p, tip, col, 2f);
            var perp = new Vector2((float)-ay, (float)ax);
            dl.AddTriangleFilled(
                tip,
                new Vector2(tip.X - (float)ax * 7f + perp.X * 4f, tip.Y - (float)ay * 7f + perp.Y * 4f),
                new Vector2(tip.X - (float)ax * 7f - perp.X * 4f, tip.Y - (float)ay * 7f - perp.Y * 4f),
                col);

            // 点 + 标签（距离 / 离轴角）
            dl.AddCircleFilled(p, 6f, col);
            float theta = OffAxisDeg(az, el, mode, aimAz, aimEl);
            string tag = (left ? "L " : "R ") + dist.ToString("F1", CultureInfo.InvariantCulture) + "m 离轴" + theta.ToString("F0");
            dl.AddText(new Vector2(p.X + 9f, p.Y - 22f), col, tag);
        }

        /// <summary>离轴角（度）：音箱朝向 与 音箱->听者 方向 的夹角——指向性增益就是它决定的。</summary>
        private static float OffAxisDeg(float srcAz, float srcEl, int aimMode, float aimAz, float aimEl)
        {
            double a = srcAz * Math.PI / 180.0, e = srcEl * Math.PI / 180.0;
            double ce = Math.Cos(e);
            double dx = Math.Sin(a) * ce, dy = Math.Sin(e), dz = Math.Cos(a) * ce; // 头->源
            double ax, ay, az;
            if (aimMode == 0) { ax = -dx; ay = -dy; az = -dz; }           // 朝向听者
            else if (aimMode == 1) { ax = 0; ay = 0; az = 1; }             // 固定朝前
            else
            {
                double aa = aimAz * Math.PI / 180.0, ee = aimEl * Math.PI / 180.0;
                double cee = Math.Cos(ee);
                ax = Math.Sin(aa) * cee; ay = Math.Sin(ee); az = Math.Cos(aa) * cee;
            }
            double cosT = ax * -dx + ay * -dy + az * -dz;
            if (cosT > 1) cosT = 1;
            if (cosT < -1) cosT = -1;
            return (float)(Math.Acos(cosT) * 180.0 / Math.PI);
        }

        // ───────────────────────── 音箱设置面板（分频指向性） ─────────────────────────

        private static float _genLW = 0f, _genLP = 1f, _genHW = 0.9f, _genHP = 2f;

        private static void DrawSpeakersPanel(DesktopApp app)
        {
            var d = app.Directivity;

            bool en = d.Enabled;
            if (ImGui.Checkbox("启用指向性（分频模拟）", ref en)) d.Enabled = en;
            ImGui.TextDisabled("信号链: 前置增益 -> 分频指向性 -> 空间模拟 -> 后置增益");

            // 防呆：朝向听者（θ=0）或全向图案（w=0）时，任何设置都听不出效果
            if (en)
            {
                bool flatAim = app.AimModeL == 0 && app.AimModeR == 0;
                bool omni = true;
                for (int i = 0; i < DirectivityProcessor.Bands; i++)
                    if (d.W[i] > 0.001f) omni = false;
                var warn = new System.Numerics.Vector4(1f, 0.75f, 0.2f, 1f);
                if (flatAim)
                    ImGui.TextColored(warn, "提示: 两源均朝向听者 -> 离轴角恒 0 -> 响应平直，听不出效果");
                else if (omni)
                    ImGui.TextColored(warn, "提示: 权重全为 0（全向）-> 无指向性效果");
            }

            // 6 带 / 5 分频点
            ImGui.Spacing();
            ImGui.Text($"分频点（{DirectivityProcessor.Bands} 带，{DirectivityProcessor.Splits} 个分频点，最高 {DirectivityProcessor.MaxFreq:F0} Hz）");
            for (int k = 0; k < DirectivityProcessor.Splits; k++)
            {
                float f = d.Freqs[k];
                ImGui.SetNextItemWidth(260);
                if (ImGui.SliderFloat($"分频{k + 1}##sp{k}", ref f, DirectivityProcessor.MinFreq, DirectivityProcessor.MaxFreq, "%.0f Hz"))
                    d.Freqs[k] = f;
            }

            // 分带图案（带名 = 当前分频点算出的频段）
            ImGui.Spacing();
            ImGui.Text("分带图案  权重: 0=全向 0.5=心形 1=8字    锐度: 越大越窄");
            for (int i = 0; i < DirectivityProcessor.Bands; i++)
                BandRow(BandLabel(d, i), d.W, d.P, i);

            // 渐变生成器：定两端、一键均匀插值（"更均匀"的省事做法）
            ImGui.Spacing();
            ImGui.Text("渐变生成器（定两端，一键插值 6 带）");
            ImGui.SetNextItemWidth(140);
            ImGui.SliderFloat("低频端 权重", ref _genLW, 0f, 1f, "%.2f");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(140);
            ImGui.SliderFloat("低频端 锐度", ref _genLP, 0.3f, 4f, "%.1f");
            ImGui.SetNextItemWidth(140);
            ImGui.SliderFloat("高频端 权重", ref _genHW, 0f, 1f, "%.2f");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(140);
            ImGui.SliderFloat("高频端 锐度", ref _genHP, 0.3f, 4f, "%.1f");
            if (ImGui.Button("按端点渐变填充"))
                d.FillGradient(_genLW, _genLP, _genHW, _genHP);

            ImGui.Spacing();
            if (ImGui.Button("预设 全向"))
            {
                for (int i = 0; i < DirectivityProcessor.Bands; i++) { d.W[i] = 0f; d.P[i] = 1f; }
            }
            ImGui.SameLine();
            if (ImGui.Button("预设 均匀心形"))
            {
                for (int i = 0; i < DirectivityProcessor.Bands; i++) { d.W[i] = 0.5f; d.P[i] = 1f; }
            }
            ImGui.SameLine();
            if (ImGui.Button("预设 高频聚拢"))
            {
                d.FillGradient(0f, 1f, 0.9f, 2f);
            }

            // 朝向在「空间模拟」面板（几何归几何，图案归图案）

            // 实时分带增益
            ImGui.Spacing();
            d.GainsAt(app.AzL, app.ElL, app.AimModeL, app.AimAzL, app.AimElL, _gL);
            d.GainsAt(app.AzR, app.ElR, app.AimModeR, app.AimAzR, app.AimElR, _gR);
            ImGui.Text("当前分带增益  L: " + GainsText(_gL));
            ImGui.Text("              R: " + GainsText(_gR));
            if (AllNearOne(_gL) && AllNearOne(_gR))
                ImGui.TextDisabled("（增益全 1.00 = 当前配置无指向性效果）");
        }

        private static readonly float[] _gL = new float[DirectivityProcessor.Bands];
        private static readonly float[] _gR = new float[DirectivityProcessor.Bands];

        private static float EarDiffDb(float l, float r)
        {
            const float eps = 1e-6f;
            if (l < eps || r < eps) return 0f;
            return (float)(20.0 * Math.Log10(l / r));
        }

        private static bool AllNearOne(float[] g)
        {
            for (int i = 0; i < g.Length; i++)
                if (Math.Abs(g[i] - 1f) > 0.005f) return false;
            return true;
        }

        private static string GainsText(float[] g)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < g.Length; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(g[i].ToString("F2", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        private static string BandLabel(DirectivityProcessor d, int i)
        {
            if (i == 0) return "<" + d.Freqs[0].ToString("F0") + "Hz";
            if (i == DirectivityProcessor.Bands - 1)
                return ">" + d.Freqs[DirectivityProcessor.Splits - 1].ToString("F0") + "Hz";
            return d.Freqs[i - 1].ToString("F0") + "-" + d.Freqs[i].ToString("F0") + "Hz";
        }

        private static void BandRow(string label, float[] w, float[] p, int band)
        {
            ImGui.SetNextItemWidth(150);
            ImGui.SliderFloat(label + " 权重##w" + band, ref w[band], 0f, 1f, "%.2f");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(150);
            ImGui.SliderFloat(label + " 锐度##p" + band, ref p[band], 0.3f, 4f, "%.1f");
        }

        // ───────────────────────── 两耳分析面板（波形 / 频谱） ─────────────────────────

        private const int SpecN = 2048;
        private static readonly float[] _wL = new float[SpecN];
        private static readonly float[] _wR = new float[SpecN];
        private static readonly float[] _fftRe = new float[SpecN];
        private static readonly float[] _fftIm = new float[SpecN];
        private static readonly float[] _specL = new float[SpecN / 2];
        private static readonly float[] _specR = new float[SpecN / 2];
        private static long _nextSpecAt;

        private static void DrawAnalysisPanel(DesktopApp app)
        {
            var mon = app.Monitor;
            mon.Snapshot(_wL, _wR);

            float ms = SpecN * 1000f / Math.Max(1, mon.Rate);
            ImGui.Text($"两耳波形（滚动 {ms:F0} ms；蓝 L耳 / 红 R耳）");
            ImGui.TextDisabled("水平错位 = 时间差 ITD；上下幅度差 = 声级差 ILD");
            DrawWaveBox();

            ImGui.Spacing();
            ImGui.Text("每耳频谱（2048 点 Hann；纵 -90..0 dB，横 20Hz..Nyquist 对数；上下同刻度）");
            if (Environment.TickCount64 >= _nextSpecAt)
            {
                _nextSpecAt = Environment.TickCount64 + 100;
                ComputeSpectrum(_wL, _specL);
                ComputeSpectrum(_wR, _specR);
            }
            DrawSpecBox(mon.Rate, _specL, Col(80, 150, 255), "L耳");
            DrawSpecBox(mon.Rate, _specR, Col(255, 95, 90), "R耳");
        }

        private static void DrawWaveBox()
        {
            float w = Math.Min(900f, Math.Max(360f, ImGui.GetContentRegionAvail().X));
            const float h = 190f;
            Vector2 origin = ImGui.GetCursorScreenPos();
            ImGui.Dummy(new Vector2(w, h));
            var dl = ImGui.GetWindowDrawList();
            dl.AddRectFilled(origin, origin + new Vector2(w, h), Col(24, 24, 30));
            dl.AddRect(origin, origin + new Vector2(w, h), Col(60, 60, 70));
            float midY = origin.Y + h / 2f;
            dl.AddLine(new Vector2(origin.X, midY), new Vector2(origin.X + w, midY), Col(45, 48, 58));

            // 自动缩放到峰值（下限 0.1 满刻度，安静内容也看得见形状）
            float peak = 0.1f;
            for (int i = 0; i < SpecN; i += 4)
            {
                float a = Math.Abs(_wL[i]);
                float b = Math.Abs(_wR[i]);
                if (a > peak) peak = a;
                if (b > peak) peak = b;
            }
            float scale = h * 0.46f / peak;

            DrawWaveTrace(dl, origin, w, h, _wL, Col(80, 150, 255), scale);
            DrawWaveTrace(dl, origin, w, h, _wR, Col(255, 95, 90), scale);
            dl.AddText(new Vector2(origin.X + 6, origin.Y + 4), Col(120, 125, 140), "+/-" + peak.ToString("F2", CultureInfo.InvariantCulture));
        }

        private static void DrawWaveTrace(ImDrawListPtr dl, Vector2 origin, float w, float h,
            float[] data, uint col, float scale)
        {
            float midY = origin.Y + h / 2f;
            var prev = new Vector2(origin.X, midY - data[0] * scale);
            const int step = 4; // 2048 点 -> 512 段
            for (int i = step; i < SpecN; i += step)
            {
                var cur = new Vector2(origin.X + w * i / (SpecN - 1), midY - data[i] * scale);
                dl.AddLine(prev, cur, col, 1.2f);
                prev = cur;
            }
        }

        private static void DrawSpecBox(int rate, float[] spec, uint col, string label)
        {
            float w = Math.Min(900f, Math.Max(360f, ImGui.GetContentRegionAvail().X));
            const float h = 160f;
            Vector2 origin = ImGui.GetCursorScreenPos();
            ImGui.Dummy(new Vector2(w, h));
            var dl = ImGui.GetWindowDrawList();
            dl.AddRectFilled(origin, origin + new Vector2(w, h), Col(24, 24, 30));
            dl.AddRect(origin, origin + new Vector2(w, h), Col(60, 60, 70));

            float nyq = Math.Max(40f, rate * 0.5f);
            float lo = (float)Math.Log10(20f);
            float hi = (float)Math.Log10(nyq);

            foreach (int f in new[] { 100, 1000, 10000 })
            {
                if (f >= nyq) continue;
                float x = origin.X + w * ((float)Math.Log10(f) - lo) / (hi - lo);
                dl.AddLine(new Vector2(x, origin.Y), new Vector2(x, origin.Y + h), Col(45, 48, 58));
                string lbl = f >= 1000 ? (f / 1000) + "k" : f.ToString(CultureInfo.InvariantCulture);
                dl.AddText(new Vector2(x + 3, origin.Y + h - 16), Col(95, 100, 115), lbl);
            }
            foreach (int db in new[] { 0, -30, -60 })
            {
                float y = origin.Y + h * (0f - db) / 90f;
                dl.AddLine(new Vector2(origin.X, y), new Vector2(origin.X + w, y), Col(45, 48, 58));
                dl.AddText(new Vector2(origin.X + 4, y + 2), Col(95, 100, 115), db.ToString(CultureInfo.InvariantCulture));
            }

            DrawSpecTrace(dl, origin, w, h, spec, col, lo, hi, nyq);
            dl.AddText(new Vector2(origin.X + w - 34, origin.Y + 4), col, label);
        }

        private static void DrawSpecTrace(ImDrawListPtr dl, Vector2 origin, float w, float h,
            float[] spec, uint col, float lo, float hi, float nyq)
        {
            int bins = spec.Length;
            var prev = new Vector2(origin.X, origin.Y + h);
            for (int x = 1; x < (int)w; x++)
            {
                float f = (float)Math.Pow(10.0, lo + (hi - lo) * x / w);
                int bin = (int)(f / nyq * (bins - 1));
                if (bin >= bins) bin = bins - 1;
                float db = spec[bin];
                if (db < -90f) db = -90f;
                if (db > 0f) db = 0f;
                var p = new Vector2(origin.X + x, origin.Y + h * (0f - db) / 90f);
                dl.AddLine(prev, p, col, 1.2f);
                prev = p;
            }
        }

        private static void ComputeSpectrum(float[] src, float[] dst)
        {
            for (int i = 0; i < SpecN; i++)
            {
                double hann = 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / (SpecN - 1)));
                _fftRe[i] = src[i] * (float)hann;
                _fftIm[i] = 0f;
            }
            Fft.Forward(_fftRe, _fftIm);
            int bins = SpecN / 2;
            float norm = 2f / SpecN;
            for (int k = 0; k < bins; k++)
            {
                float m = (float)Math.Sqrt(_fftRe[k] * _fftRe[k] + _fftIm[k] * _fftIm[k]) * norm;
                dst[k] = 20f * (float)Math.Log10(Math.Max(1e-7f, m));
            }
        }

        // ───────────────────────── 校准面板 ─────────────────────────

        /// <summary>校准状态推进（每帧，与面板无关）：Done→后台分析；Ready→按参数重设计。</summary>
        private static void PumpCalibration(DesktopApp app)
        {
            var cal = app.Cal;
            int st = cal.State;
            if (st == Calibration.Done)
            {
                cal.State = Calibration.Analyzing;
                System.Threading.Tasks.Task.Run(() => cal.Analyze());
            }
            else if (cal.HasCurve && cal.DesignDirty && !cal.IsBusy)
            {
                // 刚分析完 / 参数改动 / 设置恢复的曲线：按当前参数设计 EQ
                cal.Redesign();
                cal.DesignDirty = false;
            }

            var spk = app.SpkCal;
            if (spk.State == SpeakerCal.Done)
            {
                spk.State = SpeakerCal.Analyzing;
                System.Threading.Tasks.Task.Run(() => spk.Analyze());
            }
            else if (spk.HasCurve && spk.DesignDirty && !spk.IsBusy)
            {
                spk.Redesign();
                spk.DesignDirty = false;
            }
        }

        private static void DrawCalibrationPanel(DesktopApp app)
        {
            var cal = app.Cal;

            ImGui.Text("双音箱扫频测量两耳频响 -> 反相 EQ 拉平（耳机回放前）");
            ImGui.TextDisabled("测量点 = 模拟人头两耳（空间化之后）；EQ 只作用于输出侧，两耳分析/录制始终是裸信号");
            ImGui.TextDisabled("改动摆位/朝向/距离/指向性/HRTF 后请重新校准");

            // 控制行
            if (cal.IsBusy)
            {
                if (ImGui.Button("取消校准")) app.CancelCalibration();
                ImGui.SameLine();
                ImGui.ProgressBar(cal.Progress, new Vector2(240, 18), "");
                ImGui.SameLine();
                ImGui.Text(cal.State == Calibration.Analyzing
                    ? "分析中..."
                    : $"双音箱扫频 {cal.Progress * 100:F0}%  {cal.CurFreq:F0}Hz");
            }
            else
            {
                if (ImGui.Button("开始校准"))
                {
                    string e = app.StartCalibration();
                    _status = e ?? $"校准中 {cal.MeasF1:F0}..{cal.MeasF2:F0}Hz — 扫频会从耳机出声，注意音量";
                }
                ImGui.SameLine();
                ImGui.Text(cal.HasCurve && cal.DoneAtTicks > 0
                    ? "上次校准: " + new DateTime(cal.DoneAtTicks).ToString("yyyy-MM-dd HH:mm:ss")
                    : (cal.HasCurve ? "已有测量曲线" : "未校准过"));
            }
            if (cal.Message.Length > 0)
                ImGui.TextColored(new Vector4(1f, 0.55f, 0.35f, 1f), cal.Message);

            // 校准模式：可开可关
            ImGui.Spacing();
            bool en = cal.Enabled;
            if (ImGui.Checkbox("校准模式（应用校准 EQ）", ref en)) cal.SetEnabled(en);

            // 参数：改动即时重设计（不用重扫）
            float sm = cal.SmoothOct;
            ImGui.SetNextItemWidth(180);
            if (ImGui.SliderFloat("平滑##cal", ref sm, 0.083f, 1f, "%.3f 倍频程"))
            {
                cal.SmoothOct = sm;
                cal.DesignDirty = true;
            }
            ImGui.SameLine();
            float mb = cal.MaxBoostDb;
            ImGui.SetNextItemWidth(180);
            if (ImGui.SliderFloat("提升上限##cal", ref mb, 0f, 12f, "%.1f dB"))
            {
                cal.MaxBoostDb = mb;
                cal.DesignDirty = true;
            }
            ImGui.SameLine();
            ImGui.TextDisabled("改动即时重算，不用重扫");

            if (!cal.HasCurve)
            {
                ImGui.Spacing();
                ImGui.TextDisabled("点「开始校准」后自动完成：双音箱同时扫频 -> 分析出两耳频响");
                return;
            }

            ImGui.Spacing();
            ImGui.Text("两耳频响（纵 -36..+18 dB，横对数；实测=平滑后中带归一 / EQ=绿 / 校准后预期=白）");
            ImGui.TextDisabled("EQ 曲线是实测的镜像（限幅后）；预期曲线越贴 0dB 线 = 拉得越平");
            DrawCalBox(cal.GridHz, cal.DispL, cal.CorrL, cal.PredL, Col(80, 150, 255), "L耳");
            DrawCalBox(cal.GridHz, cal.DispR, cal.CorrR, cal.PredR, Col(255, 95, 90), "R耳");
        }

        private static void DrawCalBox(float[] freqs, float[] disp, float[] corr, float[] pred,
            uint col, string label)
        {
            float w = Math.Min(900f, Math.Max(360f, ImGui.GetContentRegionAvail().X));
            const float h = 170f;
            Vector2 origin = ImGui.GetCursorScreenPos();
            ImGui.Dummy(new Vector2(w, h));
            var dl = ImGui.GetWindowDrawList();
            dl.AddRectFilled(origin, origin + new Vector2(w, h), Col(24, 24, 30));
            dl.AddRect(origin, origin + new Vector2(w, h), Col(60, 60, 70));

            float fLo = freqs[0], fHi = freqs[freqs.Length - 1];
            float lo = (float)Math.Log10(fLo), hi = (float)Math.Log10(fHi);
            const float dbLo = -36f, dbHi = 18f;

            foreach (int f in new[] { 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000, 20000 })
            {
                if (f < fLo || f > fHi) continue;
                float x = origin.X + w * ((float)Math.Log10(f) - lo) / (hi - lo);
                dl.AddLine(new Vector2(x, origin.Y), new Vector2(x, origin.Y + h), Col(45, 48, 58));
                string lbl = f >= 1000 ? (f / 1000) + "k" : f.ToString(CultureInfo.InvariantCulture);
                dl.AddText(new Vector2(x + 3, origin.Y + h - 16), Col(95, 100, 115), lbl);
            }
            foreach (int db in new[] { 12, 0, -12, -24, -36 })
            {
                float y = origin.Y + h * (dbHi - db) / (dbHi - dbLo);
                dl.AddLine(new Vector2(origin.X, y), new Vector2(origin.X + w, y),
                    db == 0 ? Col(70, 75, 90) : Col(45, 48, 58));
                dl.AddText(new Vector2(origin.X + 4, y + 2), Col(95, 100, 115),
                    db.ToString(CultureInfo.InvariantCulture));
            }

            DrawCalTrace(dl, origin, w, h, freqs, disp, col, lo, hi, dbLo, dbHi, 1.6f);
            DrawCalTrace(dl, origin, w, h, freqs, corr, Col(120, 220, 120), lo, hi, dbLo, dbHi, 1.2f);
            DrawCalTrace(dl, origin, w, h, freqs, pred, Col(225, 225, 230), lo, hi, dbLo, dbHi, 1.2f);
            dl.AddText(new Vector2(origin.X + w - 34, origin.Y + 4), col, label);
        }

        private static void DrawCalTrace(ImDrawListPtr dl, Vector2 origin, float w, float h,
            float[] freqs, float[] db, uint col, float lo, float hi,
            float dbLo, float dbHi, float thickness)
        {
            Vector2 prev = default;
            for (int i = 0; i < freqs.Length; i++)
            {
                float x = origin.X + w * ((float)Math.Log10(freqs[i]) - lo) / (hi - lo);
                float v = Math.Clamp(db[i], dbLo, dbHi);
                float y = origin.Y + h * (dbHi - v) / (dbHi - dbLo);
                var p = new Vector2(x, y);
                if (i > 0) dl.AddLine(prev, p, col, thickness);
                prev = p;
            }
        }

        // ───────────────────────── 后处理面板 ─────────────────────────

        private static void DrawPostPanel(DesktopApp app)
        {
            var eq = app.Post;

            ImGui.Text("输出 PEQ（校准 EQ 之后、播放之前）");
            ImGui.TextDisabled("左右声道同一套系数；校准拉平后低音显薄，用低架把低频垫回来即可");

            bool en = eq.Enabled;
            if (ImGui.Checkbox("后处理（应用 PEQ）", ref en)) eq.SetEnabled(en);
            ImGui.SameLine();
            if (ImGui.Button("低音+3dB"))
                SetLowShelfPreset(eq, 3f);
            ImGui.SameLine();
            if (ImGui.Button("低音+6dB"))
                SetLowShelfPreset(eq, 6f);
            ImGui.SameLine();
            if (ImGui.Button("全部平直"))
                eq.ClearAll();

            ImGui.Spacing();
            ImGui.Text("PEQ 带（勾=启用；每带 峰值/低架/高架，左右同参）");
            for (int b = 0; b < PostEq.Bands; b++)
            {
                bool on = eq.On[b];
                if (ImGui.Checkbox($"##pon{b}", ref on)) eq.On[b] = on;
                ImGui.SameLine();
                int ty = eq.Type[b];
                ImGui.SetNextItemWidth(72);
                if (ImGui.Combo($"##ptype{b}", ref ty, PostEq.TypeNames, PostEq.TypeNames.Length))
                    eq.Type[b] = ty;
                ImGui.SameLine();
                ImGui.SetNextItemWidth(150);
                float f = eq.Freq[b];
                if (ImGui.SliderFloat($"##pf{b}", ref f, 20f, 20000f, "%.0f Hz", ImGuiSliderFlags.Logarithmic))
                    eq.Freq[b] = f;
                ImGui.SameLine();
                ImGui.SetNextItemWidth(130);
                float g = eq.GainDb[b];
                if (ImGui.SliderFloat($"##pg{b}", ref g, -12f, 12f, "%.1f dB"))
                    eq.GainDb[b] = g;
                ImGui.SameLine();
                ImGui.SetNextItemWidth(90);
                float q = eq.Q[b];
                if (ImGui.SliderFloat($"##pq{b}", ref q, 0.1f, 10f, "Q %.2f"))
                    eq.Q[b] = q;
                ImGui.SameLine();
                ImGui.TextDisabled("带" + (b + 1));
            }

            ImGui.Spacing();
            ImGui.Text("输出曲线（绿=仅后处理 PEQ；白=输出合成）");
            ImGui.TextDisabled("输出合成 = 校准 EQ（模式开时叠加）+ 后处理 PEQ；调带参数即时更新");
            DrawPostBox(app);
        }

        private static void SetLowShelfPreset(PostEq eq, float gainDb)
        {
            eq.On[0] = true;
            eq.Type[0] = PostEq.TypeLowShelf;
            eq.Freq[0] = 120f;
            eq.GainDb[0] = gainDb;
            eq.Q[0] = 0.7071f;
        }

        private static void DrawPostBox(DesktopApp app)
        {
            var eq = app.Post;
            var cal = app.Cal;
            int rate = app.OutputRate;

            float w = Math.Min(900f, Math.Max(360f, ImGui.GetContentRegionAvail().X));
            const float h = 180f;
            Vector2 origin = ImGui.GetCursorScreenPos();
            ImGui.Dummy(new Vector2(w, h));
            var dl = ImGui.GetWindowDrawList();
            dl.AddRectFilled(origin, origin + new Vector2(w, h), Col(24, 24, 30));
            dl.AddRect(origin, origin + new Vector2(w, h), Col(60, 60, 70));

            const float fLo = 20f, fHi = 20000f;
            float lo = (float)Math.Log10(fLo), hi = (float)Math.Log10(fHi);
            const float dbLo = -36f, dbHi = 18f;

            foreach (int f in new[] { 50, 100, 200, 500, 1000, 2000, 5000, 10000, 20000 })
            {
                float x = origin.X + w * ((float)Math.Log10(f) - lo) / (hi - lo);
                dl.AddLine(new Vector2(x, origin.Y), new Vector2(x, origin.Y + h), Col(45, 48, 58));
                string lbl = f >= 1000 ? (f / 1000) + "k" : f.ToString(CultureInfo.InvariantCulture);
                dl.AddText(new Vector2(x + 3, origin.Y + h - 16), Col(95, 100, 115), lbl);
            }
            foreach (int db in new[] { 12, 0, -12, -24, -36 })
            {
                float y = origin.Y + h * (dbHi - db) / (dbHi - dbLo);
                dl.AddLine(new Vector2(origin.X, y), new Vector2(origin.X + w, y),
                    db == 0 ? Col(70, 75, 90) : Col(45, 48, 58));
                dl.AddText(new Vector2(origin.X + 4, y + 2), Col(95, 100, 115),
                    db.ToString(CultureInfo.InvariantCulture));
            }

            // 逐像素取样：仅 PEQ / 输出合成 两条
            Vector2 prevP = default, prevT = default;
            for (int x = 0; x <= (int)w; x++)
            {
                float f = (float)Math.Pow(10.0, lo + (hi - lo) * x / w);
                float peq = eq.MagDb(f, rate);
                float tot = peq + cal.EqMagDb(f);
                float yp = origin.Y + h * (dbHi - Math.Clamp(peq, dbLo, dbHi)) / (dbHi - dbLo);
                float yt = origin.Y + h * (dbHi - Math.Clamp(tot, dbLo, dbHi)) / (dbHi - dbLo);
                var pp = new Vector2(origin.X + x, yp);
                var pt = new Vector2(origin.X + x, yt);
                if (x > 0)
                {
                    dl.AddLine(prevP, pp, Col(120, 220, 120), 1.4f);
                    dl.AddLine(prevT, pt, Col(225, 225, 230), 1.2f);
                }
                prevP = pp;
                prevT = pt;
            }
        }

        // ───────────────────────── 听音室面板 ─────────────────────────

        private static void DrawRoomPanel(DesktopApp app)
        {
            var room = app.Room;
            var m = room.Model;

            ImGui.Text("听音室（房间几何 -> 一阶镜像反射 + 混响尾）");
            ImGui.TextDisabled("音箱仍按「空间模拟」的方位/距离绕听者摆位，这里定义它们处在多大的房间里");

            bool en = room.Enabled;
            if (ImGui.Checkbox("房间效果", ref en)) room.Enabled = en;
            ImGui.SameLine();
            if (ImGui.Button("小房间")) { m.W = 3.5f; m.D = 4f; m.H = 2.5f; m.ListenerX = 1.75f; m.ListenerY = 1.2f; m.ListenerZ = 2f; }
            ImGui.SameLine();
            if (ImGui.Button("听音室")) { m.W = 5.5f; m.D = 7f; m.H = 2.8f; m.ListenerX = 2.75f; m.ListenerY = 1.2f; m.ListenerZ = 3.5f; }
            ImGui.SameLine();
            if (ImGui.Button("大厅")) { m.W = 12f; m.D = 18f; m.H = 6f; m.ListenerX = 6f; m.ListenerY = 1.2f; m.ListenerZ = 9f; }

            ImGui.Spacing();
            ImGui.Text("房间尺寸（米）");
            ParamSlider("宽##rw", m.W, 2f, 20f, "%.2f m", v => m.W = v);
            ImGui.SameLine();
            ParamSlider("深##rd", m.D, 2f, 25f, "%.2f m", v => m.D = v);
            ImGui.SameLine();
            ParamSlider("高##rh", m.H, 2f, 8f, "%.2f m", v => m.H = v);

            ImGui.Text("听者位置（房间坐标）");
            ParamSlider("X##rx", m.ListenerX, 0f, m.W, "%.2f m", v => m.ListenerX = v);
            ImGui.SameLine();
            ParamSlider("Z##rz", m.ListenerZ, 0f, m.D, "%.2f m", v => m.ListenerZ = v);
            ImGui.SameLine();
            ParamSlider("离地高##ry", m.ListenerY, 0.5f, 2.2f, "%.2f m", v => m.ListenerY = v);
            ImGui.SameLine();
            ParamSlider("朝向##ryaw", m.YawDeg, -180f, 180f, "%.0f 度", v => m.YawDeg = v);

            // 材质：分表面 × 分频带吸声（低<250Hz 中250-2k 高>2k）
            ImGui.Spacing();
            ImGui.Text("材质（吸声 α，分频带：低<250Hz 中250-2k 高>2k；地板/天花板/四壁可不同）");
            DrawMaterialRow("地板", 0, m, ref _matSel0);
            DrawMaterialRow("天花板", 1, m, ref _matSel1);
            DrawMaterialRow("四壁", 2, m, ref _matSel2);

            float[] rt = _rtScratch;
            m.Rt60Bands(rt);
            ImGui.Text($"三带 RT60：低 {rt[0]:F2}s   中 {rt[1]:F2}s   高 {rt[2]:F2}s");
            ImGui.TextDisabled("吸高频多的材料（地毯/吸音板）会让 RT60 高频段明显变短，尾巴更暗");

            ImGui.Spacing();
            ParamSlider("早期反射##rrefl", room.ReflDb, -24f, 0f, "%.1f dB", v => room.ReflDb = v);
            ImGui.SameLine();
            ParamSlider("混响电平##rrev", room.ReverbDb, -40f, 0f, "%.1f dB", v => room.ReverbDb = v);
            ImGui.SameLine();
            ParamSlider("明暗##rdamp", room.Damp, 0f, 1f, "%.2f", v => room.Damp = v);

            ImGui.Spacing();
            ImGui.Text("房间俯视（上=前墙 +Z；蓝L/红R=音箱；细线=一次反射路径）");
            DrawRoomDiagram(app);
            ImGui.TextDisabled("注意：校准测量包含房间——改房间参数后建议重新校准，或关掉校准做 A/B");
        }

        // 材质预设（α 三频带，近似常见建材数据：低频平、高频上翘是软材料的共性）
        private static readonly string[] MaterialNames =
        {
            "自定义", "抹灰/瓷砖", "混凝土", "木地板", "薄地毯", "厚地毯", "吸音板", "窗帘",
        };
        private static readonly float[][] MaterialAbs =
        {
            null,                              // 自定义：不动
            new[] { 0.02f, 0.03f, 0.05f },     // 抹灰/瓷砖
            new[] { 0.02f, 0.02f, 0.03f },     // 混凝土
            new[] { 0.08f, 0.07f, 0.10f },     // 木地板
            new[] { 0.05f, 0.20f, 0.50f },     // 薄地毯（吸高频远多于低频）
            new[] { 0.15f, 0.45f, 0.80f },     // 厚地毯
            new[] { 0.25f, 0.60f, 0.90f },     // 吸音板
            new[] { 0.10f, 0.35f, 0.60f },     // 窗帘
        };
        private static readonly float[] _rtScratch = new float[3];
        private static int _matSel0 = 4, _matSel1 = 1, _matSel2 = 3; // 启动默认对齐模型默认材质

        private static void DrawMaterialRow(string label, int surface, RoomModel m, ref int sel)
        {
            ImGui.Text(label);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(110);
            if (ImGui.Combo("##mat" + surface, ref sel, MaterialNames, MaterialNames.Length))
            {
                var abs = MaterialAbs[sel];
                if (abs != null) m.SetSurface(surface, abs[0], abs[1], abs[2]);
            }
            for (int b = 0; b < 3; b++)
            {
                ImGui.SameLine();
                ImGui.Text(b == 0 ? "低" : b == 1 ? "中" : "高");
                ImGui.SameLine();
                float a = m.Abs[surface, b];
                ImGui.SetNextItemWidth(78);
                if (ImGui.SliderFloat($"##abs{surface}_{b}", ref a, 0f, 0.95f, "%.2f"))
                {
                    m.Abs[surface, b] = a;
                    sel = 0; // 手动改过 → 显示为"自定义"
                }
            }
        }

        /// <summary>参数滑条（无文本框版）。</summary>
        private static void ParamSlider(string label, float v, float min, float max,
            string fmt, Action<float> set)
        {
            float t = v;
            ImGui.SetNextItemWidth(170);
            if (ImGui.SliderFloat(label, ref t, min, max, fmt)) set(t);
        }

        private static void DrawRoomDiagram(DesktopApp app)
        {
            var m = app.Room.Model;
            float boxW = Math.Min(560f, Math.Max(360f, ImGui.GetContentRegionAvail().X));
            const float boxH = 320f;
            Vector2 origin = ImGui.GetCursorScreenPos();
            ImGui.Dummy(new Vector2(boxW, boxH));
            var dl = ImGui.GetWindowDrawList();
            dl.AddRectFilled(origin, origin + new Vector2(boxW, boxH), Col(24, 24, 30));
            dl.AddRect(origin, origin + new Vector2(boxW, boxH), Col(60, 60, 70));

            float w = Math.Max(1f, m.W), d = Math.Max(1f, m.D);
            const float pad = 34f;
            float scale = Math.Min((boxW - 2 * pad) / w, (boxH - 2 * pad) / d);
            float ox = origin.X + (boxW - w * scale) / 2f;
            float oy = origin.Y + (boxH - d * scale) / 2f;

            // 房间矩形（z=D 前墙在上，z=0 后墙在下）
            dl.AddRect(new Vector2(ox, oy), new Vector2(ox + w * scale, oy + d * scale), Col(90, 95, 110), 1.5f);
            dl.AddText(new Vector2(ox + 4, oy + 2), Col(95, 100, 115), "前墙 z=" + d.ToString("F1", CultureInfo.InvariantCulture));
            dl.AddText(new Vector2(ox + 4, oy + d * scale - 18), Col(95, 100, 115), "后墙 z=0");

            Vector2 ToPx(float x, float z) => new Vector2(ox + x * scale, oy + (d - z) * scale);

            // 音箱位置（由空间模拟的方位/距离换算）
            m.SpeakerPos(app.AzL, app.ElL, app.DistL, out float slx, out float sly, out float slz);
            m.SpeakerPos(app.AzR, app.ElR, app.DistR, out float srx, out float sry, out float srz);
            var pL = ToPx(slx, slz);
            var pR = ToPx(srx, srz);

            // 一次反射射线（镜像法：声源->墙点->听者）
            DrawReflectionRays(dl, m, slx, sly, slz, ToPx);
            DrawReflectionRays(dl, m, srx, sry, srz, ToPx);

            dl.AddCircleFilled(pL, 5f, Col(80, 150, 255));
            dl.AddCircleFilled(pR, 5f, Col(255, 95, 90));
            dl.AddText(pL + new Vector2(7, -6), Col(80, 150, 255), "L");
            dl.AddText(pR + new Vector2(7, -6), Col(255, 95, 90), "R");

            // 听者 + 朝向
            var lp = ToPx(m.ListenerX, m.ListenerZ);
            float psi = m.YawDeg * (float)Math.PI / 180f;
            var fwd = new Vector2((float)Math.Sin(psi), -(float)Math.Cos(psi));
            dl.AddLine(lp, lp + fwd * 24f, Col(220, 220, 230), 2f);
            dl.AddCircleFilled(lp, 6f, Col(220, 220, 230));
            dl.AddText(lp + new Vector2(9, 2), Col(220, 220, 230), "听者");
        }

        private static void DrawReflectionRays(ImDrawListPtr dl, RoomModel m,
            float sx, float sy, float sz, Func<float, float, Vector2> toPx)
        {
            for (int wall = 0; wall < RoomModel.Walls; wall++)
            {
                if (!BouncePoint(m, wall, sx, sy, sz, out float bx, out float by, out float bz))
                    continue;
                var pb = toPx(bx, bz);
                dl.AddLine(toPx(sx, sz), pb, Col(70, 80, 100), 1f);
                dl.AddLine(pb, toPx(m.ListenerX, m.ListenerZ), Col(70, 80, 100), 1f);
            }
        }

        /// <summary>镜像法求一次反射的墙上落点（落在墙矩形内才算）。</summary>
        private static bool BouncePoint(RoomModel m, int wall,
            float sx, float sy, float sz, out float bx, out float by, out float bz)
        {
            bx = by = bz = 0f;
            float lx = m.ListenerX, ly = m.ListenerY, lz = m.ListenerZ;
            float w = Math.Max(1f, m.W), h = Math.Max(1f, m.H), d = Math.Max(1f, m.D);

            float ix = sx, iy = sy, iz = sz;
            float cx = 0f, cy = 0f, cz = 0f;
            bool xWall = false, yWall = false;
            switch (wall)
            {
                case 0: ix = -sx; xWall = true; break;
                case 1: ix = 2 * w - sx; cx = w; xWall = true; break;
                case 2: iy = -sy; yWall = true; break;
                case 3: iy = 2 * h - sy; cy = h; yWall = true; break;
                case 4: iz = -sz; break;
                case 5: iz = 2 * d - sz; cz = d; break;
            }

            float t;
            if (xWall)
            {
                float den = lx - ix;
                if (Math.Abs(den) < 1e-5f) return false;
                t = (cx - ix) / den;
            }
            else if (yWall)
            {
                float den = ly - iy;
                if (Math.Abs(den) < 1e-5f) return false;
                t = (cy - iy) / den;
            }
            else
            {
                float den = lz - iz;
                if (Math.Abs(den) < 1e-5f) return false;
                t = (cz - iz) / den;
            }
            if (t < 0f || t > 1f) return false;

            bx = ix + (lx - ix) * t;
            by = iy + (ly - iy) * t;
            bz = iz + (lz - iz) * t;
            return bx > -0.01f && bx < w + 0.01f
                && by > -0.01f && by < h + 0.01f
                && bz > -0.01f && bz < d + 0.01f;
        }

        // ───────────────────────── 音箱校准面板 ─────────────────────────

        private static void DrawSpeakerCalPanel(DesktopApp app)
        {
            var cal = app.SpkCal;

            ImGui.Text("音箱校准（每只音箱单独扫频测到两耳，补偿到源端）");
            ImGui.TextDisabled("测量时旁路 HRTF（房间反射保留）——拉平的是音箱本身：指向性/距离/房间的染色");
            ImGui.TextDisabled("HRTF 叠在补偿之后不受影响 → 双耳线索原样保留；改指向性/摆位/房间后需重校");

            if (cal.IsBusy)
            {
                if (ImGui.Button("取消校准")) app.CancelSpeakerCalibration();
                ImGui.SameLine();
                ImGui.ProgressBar(cal.Progress, new Vector2(240, 18), "");
                ImGui.SameLine();
                ImGui.Text(cal.State == SpeakerCal.Analyzing
                    ? "分析中..."
                    : (cal.State == SpeakerCal.SweepL ? "左音箱" : "右音箱")
                        + $" 扫频 {cal.Progress * 100:F0}%  {cal.CurFreq:F0}Hz");
            }
            else
            {
                if (ImGui.Button("开始校准"))
                {
                    string e = app.StartSpeakerCalibration();
                    _status = e ?? $"音箱校准中 {cal.MeasF1:F0}..{cal.MeasF2:F0}Hz — 左箱扫完扫右箱，注意音量";
                }
                ImGui.SameLine();
                ImGui.Text(cal.HasCurve && cal.DoneAtTicks > 0
                    ? "上次校准: " + new DateTime(cal.DoneAtTicks).ToString("yyyy-MM-dd HH:mm:ss")
                    : (cal.HasCurve ? "已有测量曲线" : "未校准过"));
            }
            if (cal.Message.Length > 0)
                ImGui.TextColored(new Vector4(1f, 0.55f, 0.35f, 1f), cal.Message);

            ImGui.Spacing();
            bool en = cal.Enabled;
            if (ImGui.Checkbox("应用音箱校准（源端补偿）", ref en)) cal.SetEnabled(en);

            float sm = cal.SmoothOct;
            ImGui.SetNextItemWidth(180);
            if (ImGui.SliderFloat("平滑##spk", ref sm, 0.083f, 1f, "%.3f 倍频程"))
            {
                cal.SmoothOct = sm;
                cal.DesignDirty = true;
            }
            ImGui.SameLine();
            float mb = cal.MaxBoostDb;
            ImGui.SetNextItemWidth(180);
            if (ImGui.SliderFloat("提升上限##spk", ref mb, 0f, 12f, "%.1f dB"))
            {
                cal.MaxBoostDb = mb;
                cal.DesignDirty = true;
            }
            ImGui.SameLine();
            ImGui.TextDisabled("改动即时重算，不用重扫");

            if (!cal.HasCurve)
            {
                ImGui.Spacing();
                ImGui.TextDisabled("点「开始校准」后自动完成：左音箱扫频 -> 右音箱扫频 -> 每箱出一条曲线");
                return;
            }

            ImGui.Spacing();
            ImGui.Text("每箱频响（两耳 dB 平均；纵 -36..+18 dB；实测=蓝/红，EQ=绿，补偿后预期=白）");
            DrawCalBox(cal.GridHz, cal.DispSrcL, cal.CorrSrcL, cal.PredSrcL, Col(80, 150, 255), "L箱");
            DrawCalBox(cal.GridHz, cal.DispSrcR, cal.CorrSrcR, cal.PredSrcR, Col(255, 95, 90), "R箱");
        }

        // ───────────────────────── 声相分析面板 ─────────────────────────

        private static float[] _gonL = new float[4096];
        private static float[] _gonR = new float[4096];
        private static float[] _bandBal = new float[11];
        private static long _nextPanAt;

        // 分带边界（10 带，40Hz 起——2048 点窗的频率分辨率 ~23Hz）
        private static readonly float[] PanEdges =
            { 40, 80, 160, 315, 630, 1250, 2500, 5000, 10000, 16000, 20000 };

        private static void DrawStereoPanel(DesktopApp app)
        {
            var mon = app.Monitor;
            mon.Snapshot(_gonL, _gonR);

            ImGui.Text("声相分析（两耳信号）");
            ImGui.TextDisabled("半圆声相图：中轴=同相(中置)  贴底边=反相  半径=幅度（自动缩放）");
            DrawGoniBox();

            // 统计量（4096 样本 ≈ 85ms 窗）
            int n = _gonL.Length;
            double sumLR = 0, sL = 0, sR = 0, sM = 0, sS = 0;
            for (int i = 0; i < n; i++)
            {
                float l = _gonL[i], r = _gonR[i];
                sumLR += (double)l * r;
                sL += (double)l * l;
                sR += (double)r * r;
                float m = 0.5f * (l + r), s = 0.5f * (l - r);
                sM += (double)m * m;
                sS += (double)s * s;
            }
            bool silent = sL + sR < 1e-10;
            double rmsL = Math.Sqrt(sL / n), rmsR = Math.Sqrt(sR / n);
            double denom = Math.Sqrt(sL * sR);
            float corr = denom > 1e-12 ? (float)(sumLR / denom) : 0f;
            float balance = rmsL + rmsR > 1e-9 ? (float)((rmsR - rmsL) / (rmsR + rmsL)) : 0f;
            double width = Math.Sqrt(sM) > 1e-9 ? Math.Sqrt(sS / sM) : 99.0;

            ImGui.Spacing();
            string corrNote = silent ? "（静音）"
                : corr >= 0.5f ? "同相为主，中置感强"
                : corr >= 0.1f ? "正常立体声"
                : corr >= -0.1f ? "很宽 / 近去相关"
                : "含反相成分：折叠单声道会抵消";
            ImGui.Text("相关度");
            ImGui.SameLine();
            DrawCenterBar(corr, 240, corr >= 0f ? Col(110, 200, 120) : Col(230, 120, 90));
            ImGui.SameLine();
            ImGui.Text($"{corr:F2}  {corrNote}");

            ImGui.Text("声像平衡");
            ImGui.SameLine();
            DrawCenterBar(balance, 240, Col(120, 170, 240));
            ImGui.SameLine();
            string balNote = silent ? "（静音）" : balance > 0.12f ? "偏右" : balance < -0.12f ? "偏左" : "居中";
            ImGui.Text($"{balance:F2}（R−L）{balNote}");

            ImGui.Text("立体声宽度");
            ImGui.SameLine();
            DrawLeftBar((float)Math.Min(width, 2.0) / 2f, 240, Col(170, 140, 240));
            ImGui.SameLine();
            ImGui.Text(width >= 99.0 ? ">200%（强反相）" : $"{width * 100:F0}%（0=单声道）");

            if (app.Capturing && !float.IsNaN(app.SourceCorrelation))
                ImGui.TextDisabled($"对照：音源 L/R 相关 {app.SourceCorrelation:F2}（捕获输入侧）");
            ImGui.TextDisabled("双耳渲染后两耳天然去相关是正常的；要看出处理前后差异看上面的音源对照");

            // 分带声相：每带 R−L 平衡（100ms 节流，复用分析面板的频谱管线）
            ImGui.Spacing();
            ImGui.Text("声相随频率（每带平衡：中线=居中，红=偏右 / 蓝=偏左）");
            if (Environment.TickCount64 >= _nextPanAt)
            {
                _nextPanAt = Environment.TickCount64 + 100;
                mon.Snapshot(_wL, _wR);
                ComputeSpectrum(_wL, _specL);
                ComputeSpectrum(_wR, _specR);
                float binHz = mon.Rate / (float)SpecN;
                for (int b = 0; b < PanEdges.Length - 1; b++)
                {
                    int k0 = Math.Clamp((int)(PanEdges[b] / binHz), 1, _specL.Length - 1);
                    int k1 = Math.Clamp((int)(PanEdges[b + 1] / binHz) + 1, k0 + 1, _specL.Length);
                    double aL = 0, aR = 0;
                    int c = 0;
                    for (int k = k0; k < k1; k++) { aL += _specL[k]; aR += _specR[k]; c++; }
                    float db = c > 0 ? (float)((aR - aL) / c) : 0f;
                    // 平衡 = (Pr-Pl)/(Pr+Pl)，r=10^(db/10) → tanh(ln(r)/2)
                    _bandBal[b] = (float)Math.Tanh(db * 0.11512925);
                }
            }
            DrawBandPanBox();
        }

        private static void DrawGoniBox()
        {
            float w = Math.Min(560f, Math.Max(320f, ImGui.GetContentRegionAvail().X));
            const float h = 300f;
            Vector2 origin = ImGui.GetCursorScreenPos();
            ImGui.Dummy(new Vector2(w, h));
            var dl = ImGui.GetWindowDrawList();

            float cx = origin.X + w * 0.5f;
            float cy = origin.Y + h - 8f;            // 平底边
            float R = Math.Min(w * 0.5f - 10f, h - 12f);

            // 半圆盘（平底在下）+ 边缘线
            dl.PathClear();
            dl.PathLineTo(new Vector2(cx - R, cy));
            dl.PathArcTo(new Vector2(cx, cy), R, (float)Math.PI, 2f * (float)Math.PI, 72);
            dl.PathFillConvex(Col(24, 24, 30));
            dl.PathClear();
            dl.PathLineTo(new Vector2(cx - R, cy));
            dl.PathArcTo(new Vector2(cx, cy), R, (float)Math.PI, 2f * (float)Math.PI, 72);
            dl.PathLineTo(new Vector2(cx + R, cy));
            dl.PathStroke(Col(60, 60, 70), ImDrawFlags.Closed, 1.2f);

            // 指引：中轴、±45° 辐条、内弧
            dl.AddLine(new Vector2(cx, cy), new Vector2(cx, cy - R), Col(50, 54, 64));
            float d45 = R * 0.7071f;
            dl.AddLine(new Vector2(cx, cy), new Vector2(cx - d45, cy - d45), Col(50, 54, 64));
            dl.AddLine(new Vector2(cx, cy), new Vector2(cx + d45, cy - d45), Col(50, 54, 64));
            dl.PathClear();
            dl.PathArcTo(new Vector2(cx, cy), R * 0.55f, (float)Math.PI, 2f * (float)Math.PI, 48);
            dl.PathStroke(Col(50, 54, 64), ImDrawFlags.None, 1f);

            // 极坐标散点：角度=瞬时声相 atan2(R−L, R+L)，半径=幅度（峰值自动缩放，裁进盘内）
            float peak = 0.1f;
            for (int i = 0; i < _gonL.Length; i += 4)
            {
                float m = (float)Math.Sqrt((double)_gonL[i] * _gonL[i] + (double)_gonR[i] * _gonR[i]);
                if (m > peak) peak = m;
            }
            float scale = (R - 4f) / peak;
            const float clampA = 1.5533f; // 89°：反相内容堆在底边两侧（贴边=反相提示）
            uint dot = 0x90FFC44A;        // 琥珀色，重叠处自然增亮
            for (int i = 0; i < _gonL.Length; i++)
            {
                float l = _gonL[i], r = _gonR[i];
                float theta = (float)Math.Atan2(r - l, r + l);
                if (theta > clampA) theta = clampA;
                else if (theta < -clampA) theta = -clampA;
                float mag = (float)Math.Sqrt((double)l * l + (double)r * r) * scale;
                if (mag > R - 2f) mag = R - 2f;
                float x = cx + mag * (float)Math.Sin(theta);
                float y = cy - mag * (float)Math.Cos(theta);
                dl.AddRectFilled(new Vector2(x, y), new Vector2(x + 1, y + 1), dot);
            }

            dl.AddText(new Vector2(origin.X + 8, origin.Y + 6), Col(150, 155, 170), "L");
            dl.AddText(new Vector2(origin.X + w - 16, origin.Y + 6), Col(150, 155, 170), "R");
            dl.AddText(new Vector2(cx - 92, origin.Y + h - 17), Col(120, 125, 140),
                "中轴=同相  贴底边=反相  越外越响");
        }

        /// <summary>中心零点横条（-1..+1）。画完调用方再 SameLine 放数值。</summary>
        private static void DrawCenterBar(float v, float w, uint col)
        {
            Vector2 o = ImGui.GetCursorScreenPos();
            const float h = 16f;
            ImGui.Dummy(new Vector2(w, h));
            var dl = ImGui.GetWindowDrawList();
            dl.AddRectFilled(o, o + new Vector2(w, h), Col(30, 32, 40));
            float cx = o.X + w * 0.5f;
            dl.AddLine(new Vector2(cx, o.Y + 1), new Vector2(cx, o.Y + h - 1), Col(70, 75, 90));
            v = Math.Clamp(v, -1f, 1f);
            float x0 = Math.Min(cx, cx + v * w * 0.5f);
            float x1 = Math.Max(cx, cx + v * w * 0.5f);
            dl.AddRectFilled(new Vector2(x0, o.Y + 2), new Vector2(x1, o.Y + h - 2), col);
        }

        /// <summary>左起横条（0..1）。</summary>
        private static void DrawLeftBar(float v01, float w, uint col)
        {
            Vector2 o = ImGui.GetCursorScreenPos();
            const float h = 16f;
            ImGui.Dummy(new Vector2(w, h));
            var dl = ImGui.GetWindowDrawList();
            dl.AddRectFilled(o, o + new Vector2(w, h), Col(30, 32, 40));
            float fill = Math.Clamp(v01, 0f, 1f) * w;
            dl.AddRectFilled(new Vector2(o.X + 1, o.Y + 2), new Vector2(o.X + fill - 1, o.Y + h - 2), col);
        }

        private static void DrawBandPanBox()
        {
            int bands = PanEdges.Length - 1;
            float w = Math.Min(560f, Math.Max(360f, ImGui.GetContentRegionAvail().X));
            const float rowH = 22f;
            float h = bands * rowH + 28f;
            Vector2 origin = ImGui.GetCursorScreenPos();
            ImGui.Dummy(new Vector2(w, h));
            var dl = ImGui.GetWindowDrawList();
            dl.AddRectFilled(origin, origin + new Vector2(w, h), Col(24, 24, 30));
            dl.AddRect(origin, origin + new Vector2(w, h), Col(60, 60, 70));

            float labelW = 64f;
            float barX0 = origin.X + labelW;
            float barW = w - labelW - 56f;
            float cx = barX0 + barW * 0.5f;
            dl.AddLine(new Vector2(cx, origin.Y + 22), new Vector2(cx, origin.Y + h - 4), Col(70, 75, 90));
            dl.AddText(new Vector2(origin.X + 6, origin.Y + 5), Col(120, 125, 140), "Hz");
            dl.AddText(new Vector2(barX0, origin.Y + 5), Col(120, 125, 140), "左 <- 平衡 -> 右");

            for (int b = 0; b < bands; b++)
            {
                float y = origin.Y + 24 + b * rowH;
                float f0 = PanEdges[b];
                string lbl = f0 >= 1000 ? (f0 / 1000).ToString("0.#") + "k" : f0.ToString("0");
                dl.AddText(new Vector2(origin.X + 6, y + 3), Col(95, 100, 115), lbl);

                float v = _bandBal[b];
                float x0 = Math.Min(cx, cx + v * barW * 0.5f);
                float x1 = Math.Max(cx, cx + v * barW * 0.5f);
                uint col = v >= 0f ? Col(255, 95, 90) : Col(80, 150, 255);
                dl.AddRectFilled(new Vector2(x0, y + 4), new Vector2(x1, y + rowH - 5), col);

                string vs = (v >= 0f ? "+" : "") + (v * 100f).ToString("F0") + "%";
                dl.AddText(new Vector2(origin.X + w - 46, y + 3), Col(120, 125, 140), vs);
            }
        }

        // ───────────────────────── 共用 ─────────────────────────

        private static void Rescan(bool force)
        {
            _nextScanAt = Environment.TickCount64 + (force ? 300 : 3000);
            var all = ProcessTree.Snapshot();

            // 建森林：pid → 节点、父 → 子列表
            var byPid = new Dictionary<int, ProcessTree.ProcInfo>();
            var children = new Dictionary<int, List<ProcessTree.ProcInfo>>();
            foreach (var p in all) byPid[p.Pid] = p;
            foreach (var p in all)
            {
                if (!children.TryGetValue(p.ParentPid, out var list))
                    children[p.ParentPid] = list = new List<ProcessTree.ProcInfo>();
                list.Add(p);
            }
            foreach (var kv in children)
                kv.Value.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

            var titles = WinEnum.GetWindowTitles();
            var active = AudioSessions.ActivePids();

            // 根 = 父进程不在快照里；深先序遍历成行；过滤保留命中行的祖先
            var rows = new List<Row>();
            var keep = new HashSet<int>();

            void Walk(int pid, int depth)
            {
                if (!byPid.TryGetValue(pid, out var p)) return;
                bool selfHit = _search.Length == 0
                    || p.Name.IndexOf(_search, StringComparison.OrdinalIgnoreCase) >= 0
                    || (titles.TryGetValue((uint)pid, out var t) &&
                        t.IndexOf(_search, StringComparison.OrdinalIgnoreCase) >= 0);
                bool childHit = false;
                if (children.TryGetValue(pid, out var kids))
                    foreach (var k in kids)
                    {
                        Walk(k.Pid, depth + 1);
                        if (keep.Contains(k.Pid)) childHit = true;
                    }
                if (selfHit || childHit)
                {
                    keep.Add(pid);
                    rows.Add(MakeRow(p, depth, titles, active));
                }
            }

            var roots = new List<ProcessTree.ProcInfo>();
            foreach (var p in all)
                if (!byPid.ContainsKey(p.ParentPid)) roots.Add(p);
            roots.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            foreach (var r in roots) Walk(r.Pid, 0);

            _rows = rows;

            // 设置记忆：启动后第一次扫到上次的进程名就自动选中（不自动开捕获）
            if (_selectedPid <= 0 && _pendingName.Length > 0)
            {
                foreach (var r in rows)
                {
                    if (string.Equals(r.Name, _pendingName, StringComparison.OrdinalIgnoreCase))
                    {
                        _selectedPid = r.Pid;
                        _selectedName = r.Display.Trim();
                        break;
                    }
                }
            }
        }

        private static Row MakeRow(ProcessTree.ProcInfo p, int depth,
            Dictionary<uint, string> titles, HashSet<int> active)
        {
            string indent = depth == 0 ? "" : new string(' ', depth * 2) + "└ ";
            // [响] 而不是 ♪：雅黑字形范围无音符字符（渲染成 ?）
            string badge = active.Contains(p.Pid) ? "[响] " : "     ";
            string title = titles.TryGetValue((uint)p.Pid, out var t) ? " — " + t : "";
            return new Row
            {
                Pid = p.Pid,
                Name = p.Name,
                Display = indent + badge + p.Name + " [" + p.Pid + "]" + title,
            };
        }

        private static float Parse(string s)
        {
            return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : float.NaN;
        }
    }
}
