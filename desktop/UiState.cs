// 面板状态机：左侧导航切换主内容区显示的面板。
//
// 扩展方式（以后加功能照这两步）：
//   ① Page 枚举加一项
//   ② Ui.cs 的 Panels 注册表加一行（标题 + 小字 + 绘制函数）
// 导航按钮自动多一个入口；面板进入/离开的副作用挂 UiState.Changed 事件。
//
// 状态转移只有一条：任意面板 → 任意面板（Go(page)，幂等）。
// 当前页持久化在 Settings.LastPage。
using System;

namespace VirtualStereo.Desktop
{
    internal enum Page
    {
        Main = 0,        // 主面板：音源（进程树）与运行状态
        Processing = 1,  // 处理：前置/后置增益、电平
        Spatial = 2,     // 空间模拟：模式、方位、HRTF/SOFA
        Speakers = 3,    // 音箱设置：分频指向性、朝向
        // ── 未来的家 ──
        // Spectrum = 4,   // 频谱/波形分析
        // Recording = 5,  // 录制与回放
        // Compare = 6,    // A/B 对比
    }

    internal static class UiState
    {
        public static Page Current = Page.Main;

        /// <summary>转场事件：面板进入时的副作用（自动刷新、启停分析等）挂这里。</summary>
        public static event Action<Page> Changed;

        public static void Go(Page page)
        {
            if (page == Current) return;
            Current = page;
            Changed?.Invoke(page);
        }
    }
}
