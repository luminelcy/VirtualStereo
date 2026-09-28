// ImGui 界面：左侧导航 + 面板内容区（状态机见 UiState.cs）。
//
// 结构：Panels 注册表 = 全部面板（标题/小字/绘制函数）；导航由注册表生成，
// 加功能 = Page 枚举 + 注册表一行，见 UiState.cs 头注释。
// 中文用系统雅黑字体；♪ 字形缺失故发声标记用 [响]。
using System;
using System.Collections.Generic;
using System.Globalization;
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
            new PanelDef { Id = Page.Speakers, Title = "音箱设置", Subtitle = "分频指向性 · 朝向", Draw = DrawSpeakersPanel },
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

            ImGui.Spacing();
            ImGui.Text("声源方位（头坐标系：正前方 0°，右正）");
            float azL = app.AzL, azR = app.AzR, eL = app.ElL, eR = app.ElR;
            ImGui.SetNextItemWidth(260);
            if (ImGui.SliderFloat("L 方位角", ref azL, -180f, 180f, "%.0f°")) app.AzL = azL;
            ImGui.SetNextItemWidth(260);
            if (ImGui.SliderFloat("R 方位角", ref azR, -180f, 180f, "%.0f°")) app.AzR = azR;
            ImGui.SetNextItemWidth(260);
            if (ImGui.SliderFloat("L 仰角", ref eL, -90f, 90f, "%.0f°")) app.ElL = eL;
            ImGui.SetNextItemWidth(260);
            if (ImGui.SliderFloat("R 仰角", ref eR, -90f, 90f, "%.0f°")) app.ElR = eR;

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

        // ───────────────────────── 音箱设置面板（分频指向性） ─────────────────────────

        private static void DrawSpeakersPanel(DesktopApp app)
        {
            var d = app.Directivity;

            bool en = d.Enabled;
            if (ImGui.Checkbox("启用指向性（分频模拟）", ref en)) d.Enabled = en;
            ImGui.TextDisabled("信号链: 前置增益 -> 分频指向性 -> 空间模拟 -> 后置增益");
            ImGui.TextDisabled("指向性属声源属性（真实喇叭低频绕射、高频聚拢），三种空间模式共用");

            ImGui.Spacing();
            ImGui.Text("分频点（4 带: 低 / 中低 / 中高 / 高）");
            float f;
            f = d.Freq1;
            ImGui.SetNextItemWidth(260);
            if (ImGui.SliderFloat("低 | 中低", ref f, 60f, 1500f, "%.0f Hz")) d.Freq1 = f;
            f = d.Freq2;
            ImGui.SetNextItemWidth(260);
            if (ImGui.SliderFloat("中低 | 中高", ref f, 200f, 6000f, "%.0f Hz")) d.Freq2 = f;
            f = d.Freq3;
            ImGui.SetNextItemWidth(260);
            if (ImGui.SliderFloat("中高 | 高", ref f, 800f, 8000f, "%.0f Hz")) d.Freq3 = f;

            ImGui.Spacing();
            ImGui.Text("分带图案  权重: 0=全向 0.5=心形 1=8字    锐度: 越大越窄");
            BandRow("低频", d, 0);
            BandRow("中低", d, 1);
            BandRow("中高", d, 2);
            BandRow("高频", d, 3);

            ImGui.Spacing();
            if (ImGui.Button("预设 全向"))
            {
                d.WLow = d.WMidLow = d.WMidHigh = d.WHigh = 0f;
                d.PLow = d.PMidLow = d.PMidHigh = d.PHigh = 1f;
            }
            ImGui.SameLine();
            if (ImGui.Button("预设 均匀心形"))
            {
                d.WLow = d.WMidLow = d.WMidHigh = d.WHigh = 0.5f;
                d.PLow = d.PMidLow = d.PMidHigh = d.PHigh = 1f;
            }
            ImGui.SameLine();
            if (ImGui.Button("预设 高频聚拢"))
            {
                d.WLow = 0f; d.WMidLow = 0.1f; d.WMidHigh = 0.5f; d.WHigh = 0.9f;
                d.PLow = 1f; d.PMidLow = 1f; d.PMidHigh = 1.5f; d.PHigh = 2f;
            }

            ImGui.Spacing();
            ImGui.Text("朝向（决定离轴角）");
            int aim = d.Aim;
            if (ImGui.RadioButton("朝向听者", ref aim, 0)) d.Aim = aim;
            ImGui.SameLine();
            if (ImGui.RadioButton("固定朝前", ref aim, 1)) d.Aim = aim;
            ImGui.SameLine();
            if (ImGui.RadioButton("手动", ref aim, 2)) d.Aim = aim;
            if (aim == 2)
            {
                float aa = d.AimAz, ae = d.AimEl;
                ImGui.SetNextItemWidth(260);
                if (ImGui.SliderFloat("朝向 方位角", ref aa, -180f, 180f, "%.0f")) d.AimAz = aa;
                ImGui.SetNextItemWidth(260);
                if (ImGui.SliderFloat("朝向 仰角", ref ae, -90f, 90f, "%.0f")) d.AimEl = ae;
            }
            ImGui.TextDisabled("朝向听者=平直响应；固定朝前=声源偏离正前方即可听出高频变暗");

            ImGui.Spacing();
            d.GainsAt(app.AzL, app.ElL, out float gl0, out float gl1, out float gl2, out float gl3);
            d.GainsAt(app.AzR, app.ElR, out float gr0, out float gr1, out float gr2, out float gr3);
            ImGui.Text($"当前分带增益  L: {gl0:F2} {gl1:F2} {gl2:F2} {gl3:F2}");
            ImGui.Text($"              R: {gr0:F2} {gr1:F2} {gr2:F2} {gr3:F2}");
            ImGui.TextDisabled("（依次为 低/中低/中高/高）");
        }

        private static void BandRow(string name, DirectivityProcessor d, int band)
        {
            float w = 0f, p = 1f;
            switch (band)
            {
                case 0: w = d.WLow; p = d.PLow; break;
                case 1: w = d.WMidLow; p = d.PMidLow; break;
                case 2: w = d.WMidHigh; p = d.PMidHigh; break;
                default: w = d.WHigh; p = d.PHigh; break;
            }

            ImGui.SetNextItemWidth(150);
            if (ImGui.SliderFloat(name + " 权重##w" + band, ref w, 0f, 1f, "%.2f"))
            {
                switch (band)
                {
                    case 0: d.WLow = w; break;
                    case 1: d.WMidLow = w; break;
                    case 2: d.WMidHigh = w; break;
                    default: d.WHigh = w; break;
                }
            }
            ImGui.SameLine();
            ImGui.SetNextItemWidth(150);
            if (ImGui.SliderFloat(name + " 锐度##p" + band, ref p, 0.3f, 4f, "%.1f"))
            {
                switch (band)
                {
                    case 0: d.PLow = p; break;
                    case 1: d.PMidLow = p; break;
                    case 2: d.PMidHigh = p; break;
                    default: d.PHigh = p; break;
                }
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
