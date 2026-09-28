// IMGUI 调试菜单（F10 开关）：空间化模式、前置增益（dB 滑条+文本框）、SOFA、
// 手动 L/R 坐标、标记球、实时读数。面板拖动。
//
// 游戏 IL2CPP 裁剪现实（查 dump.cs 确认幸存 API）：
//   被裁：BeginArea / DragWindow / HorizontalSlider / GetLastRect / TextField(Rect,...)
//   幸存：GUILayout 的 Box/Button/TextField(text,maxLen,...)/Label/Toggle/BeginHorizontal...
// 因此布局拆两区：标题+增益滑条=固定坐标手绘（鼠标交互自研）；其余=GUILayout 流式。
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
        private static string _gainText = "-6.0";
        private static string _status = "";

        // ── 面板几何 ──
        private const float PanelW = 408f;
        private const float PanelH = 412f;
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

            GUILayout.BeginHorizontal();
            GUILayout.Label("模式", GUILayout.Width(26));
            if (GUILayout.Button("双音箱")) SwitchMode(AudioEngine.SpatialMode.Speakers);
            if (GUILayout.Button("双耳HRTF")) SwitchMode(AudioEngine.SpatialMode.FullHrtf);
            if (GUILayout.Button("ITD+ILD")) SwitchMode(AudioEngine.SpatialMode.ItdIld);
            GUILayout.EndHorizontal();
            GUILayout.Label("当前: " + ModeName());

            if (AudioEngine.Mode != AudioEngine.SpatialMode.Speakers)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("HRTF插值", GUILayout.Width(60));
                if (GUILayout.Button("最近邻")) AudioEngine.SetInterpolation(0);
                if (GUILayout.Button("双线性")) AudioEngine.SetInterpolation(1);
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

        private static void SwitchMode(AudioEngine.SpatialMode m)
        {
            if (AudioEngine.SetMode(m)) _status = "模式: " + ModeName();
            else _status = "双耳引擎初始化失败（phonon.dll 在游戏目录吗？）";
        }

        private static string ModeName()
        {
            switch (AudioEngine.Mode)
            {
                case AudioEngine.SpatialMode.FullHrtf: return "双耳 HRTF（ITD+ILD+耳廓）";
                case AudioEngine.SpatialMode.ItdIld: return "ITD+ILD（无耳廓频谱）";
                default: return "双音箱对（Unity 3D panning 对照）";
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

        private static string Fmt(Vector3 v) =>
            $"({v.x.ToString("F1", CultureInfo.InvariantCulture)},{v.y.ToString("F1", CultureInfo.InvariantCulture)},{v.z.ToString("F1", CultureInfo.InvariantCulture)})";
    }
}
