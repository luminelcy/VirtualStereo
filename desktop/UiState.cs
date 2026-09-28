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
        Processing = 1,  // 处理：前置/后置增益、电平、人头两耳
        Spatial = 2,     // 空间模拟：模式、几何、HRTF/SOFA、摆位图
        Speakers = 3,    // 音箱设置：分频指向性、图案
        Analysis = 4,    // 两耳分析：波形（ITD）/ 频谱
        Calibration = 5, // 校准：扫频测两耳频响，反相 EQ 拉平
        PostProcess = 6, // 后处理：输出 PEQ（校准之后、播放之前）
        Room = 7,        // 听音室：房间几何、一次反射、混响
        SpeakerCal = 8,  // 音箱校准：每箱扫频测到两耳（旁路 HRTF），源端补偿
        // ── 未来的家 ──
        // Recording = 9,  // 录制与回放
        // Compare = 10,   // A/B 对比
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
