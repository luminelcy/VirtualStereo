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
                ? $"捕获 RMS={app.CaptureRms:F4}  L={app.CaptureL:F4}  R={app.CaptureR:F4}"
                : "未捕获");
            if (app.PlayerError != null)
                ImGui.TextColored(new System.Numerics.Vector4(1, 0.4f, 0.4f, 1), "播放错误: " + app.PlayerError);

            ImGui.Text("输出峰值（后置增益之后）");
            ImGui.ProgressBar(Math.Min(app.OutPeak, 1.5f) / 1.5f, new System.Numerics.Vector2(-1, 18),
                $"{app.OutPeak:F3}");
            if (app.OutPeak > 1f)
                ImGui.TextColored(new System.Numerics.Vector4(1, 0.4f, 0.4f, 1), "爆电平！");

            ImGui.Spacing();
            if (ImGui.Checkbox("静音原声（消双响）", ref _silenceTmp))
                app.SilenceOriginal = _silenceTmp;
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
