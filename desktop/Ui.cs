// ImGui 界面（中文用系统雅黑字体）。一帧绘制全部面板：
//   音源（进程树 + 发声标记 + 窗口标题） / 输出与电平 / 空间化（模式·增益·声源方位·SOFA）
//
// 进程选择器按"程序集 = 进程树"组织（一个程序可能是多个进程，如宿主+子进程），
// 选中任意节点即捕获该节点的整个子进程树；♪ = 该进程当前有活跃音频会话。
using System;
using System.Collections.Generic;
using System.Globalization;
using ImGuiNET;
using VirtualStereo.Capture;

namespace VirtualStereo.Desktop
{
    internal static class Ui
    {
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
        }

        public static void Draw(DesktopApp app)
        {
            // 铺满视口的固定宿主：无标题栏/不可拖/不可折叠——
            // 内窗口若可拖动，拖出外窗口可视区就找不回来（用户实测反馈）
            var io = ImGui.GetIO();
            ImGui.SetNextWindowPos(System.Numerics.Vector2.Zero);
            ImGui.SetNextWindowSize(io.DisplaySize);
            ImGui.Begin("##host", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings);

            float srcH = Math.Max(160f, io.DisplaySize.Y * 0.32f);
            DrawSourcePanel(app, srcH);
            ImGui.Separator();

            // 处理 | 空间模拟 两个独立面板
            float panelH = Math.Max(300f, io.DisplaySize.Y - srcH - 230f);
            float half = (ImGui.GetContentRegionAvail().X - 8f) / 2f;
            ImGui.BeginChild("##processing", new System.Numerics.Vector2(half, panelH), true);
            DrawProcessingPanel(app);
            ImGui.EndChild();
            ImGui.SameLine();
            ImGui.BeginChild("##spatial", new System.Numerics.Vector2(0, panelH), true);
            DrawSpatialPanel(app);
            ImGui.EndChild();

            ImGui.TextWrapped(_status);
            ImGui.End();
        }

        // ─────────────── 音源（进程树选择器） ───────────────

        private static void DrawSourcePanel(DesktopApp app, float listHeight)
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

            ImGui.BeginChild("proclist", new System.Numerics.Vector2(-1, listHeight), true);
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
            ImGui.Text($"选中: {_selectedName}");
        }

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

            // 根 = 父进程不在快照里；深先序遍历成行
            var rows = new List<Row>();
            var keep = new HashSet<int>(); // 过滤后保留的行（含命中行的祖先）

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
            // 用 [响] 而不是 ♪：雅黑的字形范围里没有音符字符（实测渲染成 ?）
            string badge = active.Contains(p.Pid) ? "[响] " : "     ";
            string title = titles.TryGetValue((uint)p.Pid, out var t) ? " — " + t : "";
            return new Row
            {
                Pid = p.Pid,
                Name = p.Name,
                Display = indent + badge + p.Name + " [" + p.Pid + "]" + title,
            };
        }

        // ─────────────── 处理面板（前后增益 · 电平） ───────────────

        private static void DrawProcessingPanel(DesktopApp app)
        {
            ImGui.Text("处理");

            // 前置增益：进空间模拟之前
            GainRow("前置增益dB##pre", app.PreGainDb, -24f, 12f, v => app.PreGainDb = v, ref _preGainText);
            ImGui.TextDisabled("↑ 进模拟之前（输入电平）");

            // 后置增益：空间模拟之后、出声之前
            GainRow("后置增益dB##post", app.PostGainDb, -24f, 12f, v => app.PostGainDb = v, ref _postGainText);
            ImGui.TextDisabled("↑ 模拟之后（输出电平）");

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
                ImGui.TextColored(new System.Numerics.Vector4(1, 0.4f, 0.4f, 1), "← 爆电平");

            ImGui.Spacing();
            if (ImGui.Checkbox("静音原声（ε，消双响）", ref _silenceTmp))
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

        // ─────────────── 空间模拟面板 ───────────────

        private static void DrawSpatialPanel(DesktopApp app)
        {
            ImGui.Text("空间模拟");

            int mode = app.CurrentMode;
            if (ImGui.RadioButton("直通", ref mode, 0)) app.CurrentMode = mode;
            ImGui.SameLine();
            if (ImGui.RadioButton("双耳HRTF", ref mode, 1)) app.CurrentMode = mode;
            ImGui.SameLine();
            if (ImGui.RadioButton("ITD+ILD", ref mode, 2)) app.CurrentMode = mode;

            // 声源方位（头坐标系：正前方 0°，右正）
            float azL = app.AzL, azR = app.AzR, eL = app.ElL, eR = app.ElR;
            ImGui.SetNextItemWidth(260);
            if (ImGui.SliderFloat("L 方位角", ref azL, -180f, 180f, "%.0f°")) app.AzL = azL;
            ImGui.SetNextItemWidth(260);
            if (ImGui.SliderFloat("R 方位角", ref azR, -180f, 180f, "%.0f°")) app.AzR = azR;
            ImGui.SetNextItemWidth(260);
            if (ImGui.SliderFloat("L 仰角", ref eL, -90f, 90f, "%.0f°")) app.ElL = eL;
            ImGui.SetNextItemWidth(260);
            if (ImGui.SliderFloat("R 仰角", ref eR, -90f, 90f, "%.0f°")) app.ElR = eR;

            // HRTF
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

        private static float Parse(string s)
        {
            return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : float.NaN;
        }
    }
}
