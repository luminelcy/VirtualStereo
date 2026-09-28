// 顶层窗口枚举：给进程列表补"窗口标题"（任务管理器式识别名）。
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace VirtualStereo.Desktop
{
    internal static class WinEnum
    {
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        private static EnumWindowsProc _keepAlive;

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int maxCount);

        [DllImport("user32.dll")]
        private static extern int GetWindowTextLengthW(IntPtr hWnd);

        /// <summary>pid → 首个可见顶层窗口标题。</summary>
        public static Dictionary<uint, string> GetWindowTitles()
        {
            var map = new Dictionary<uint, string>();
            _keepAlive = (hWnd, lParam) =>
            {
                try
                {
                    if (!IsWindowVisible(hWnd)) return true;
                    GetWindowThreadProcessId(hWnd, out uint pid);
                    int len = GetWindowTextLengthW(hWnd);
                    if (len <= 0) return true;
                    var sb = new StringBuilder(len + 1);
                    GetWindowTextW(hWnd, sb, sb.Capacity);
                    string title = sb.ToString();
                    if (title.Length > 0 && !map.ContainsKey(pid))
                        map[pid] = title;
                }
                catch { }
                return true;
            };
            EnumWindows(_keepAlive, IntPtr.Zero);
            return map;
        }
    }
}
