# VirtualStereo

虚拟立体声/双耳实验平台：把视频声音捕获进来做空间化（虚拟立体声、双耳 HRTF、
线索隔离、自定义 HRTF）。两个使用形态共享同一套 DSP 核心：

- **桌面**（开发中）：日常使用/先行验证的独立软件
- **gogh mod**：观影派对声音的空间化（Vuplex 桥进程捕获 → 屏幕两侧虚拟声源）

## 仓库布局

```
core/          共享 DSP 核心（纯 C#，无 Unity/MelonLoader 依赖）
  AudioCapture/  WASAPI 进程树回环捕获、会话音量、环形缓冲
  Phonon/        Steam Audio 双耳引擎（phonon.dll P/Invoke）
desktop/       桌面壳（开发中）
mod/           MelonLoader/Unity 壳（gogh mod）
CaptureProbe/  自测台（不随发布）
phonon/        phonon.dll 放这里（不入库；见下）
```

**phonon.dll 获取**（不进 git 的三方二进制）：从 Steam Audio 4.8.1 SDK
（`steamaudio/lib/windows-x64/phonon.dll`，Apache 2.0）复制到本仓库 `phonon/` 目录。
下载：https://valvesoftware.github.io/steam-audio/downloads.html → steamaudio_4.8.1.zip

core 以**源码链接**编进各输出（mod 保持"包里只有 Mods/<名>.dll"的发布形态）。

## 原理

```
桥进程树音频 → WASAPI 进程回环捕获（按 PID，隔离游戏自身音效）
            → ε 音量压原声 + 线性补偿（消双响）
            → L/R 环形缓冲 → 双 AudioSource（屏幕两侧）→ Unity 空间化输出
```

要点：

- CEF 音频由 Chromium 直通 WASAPI、不经过 Unity，所以用**进程树回环**截流
- 会话静音会连捕获一起清零（抽取点在音量之后），消双响只能用 **ε 音量 + 补偿增益**
- CEF 流的幅度 ∝ 会话音量（v¹），补偿 = 1/ε；waveOut 流才是 v²
- 所有 WASAPI COM 操作必须在 **MTA 线程**执行（Unity 主线程是 STA，跨套间调用会 QI 失败）

## 快捷键

| 键 | 功能 |
|---|---|
| F9 | 虚拟立体声总开关（关闭时恢复原声直出） |
| F10 | 调试菜单（空间化模式、手动 L/R 坐标、标记球开关、拖动面板） |

## 空间化模式（F10 菜单，实验用）

| 模式 | 说明 |
|---|---|
| 双音箱对 | Unity 内置 3D panning（对照组） |
| 双耳 HRTF | Steam Audio 完整人头模拟（ITD+ILD+耳廓频谱） |
| 仅 ITD | 线索隔离：只留双耳时间差（HRTF peakDelays 驱动纯分数延迟） |
| 仅 ILD | 线索隔离：只留双耳声级差（等功率幅度 panning） |

双耳模式的声源方位 = 虚拟声源位置 × 头部（相机）朝向，实时跟随转头。
HRTF 可选最近邻/双线性插值（菜单内切换）。

依赖 Steam Audio 核心库 `phonon.dll`（4.8.1，Apache 2.0，来自官方 SDK），
**部署时需放在游戏根目录**（构建脚本会自动复制）。房间几何/遮挡/衍射/混响
暂不接入——只做双耳本体。

标记球：发声位置的蓝色（L）/ 红色（R）小球，材质走 URP 着色器。

## 构建

- mod：`dotnet build mod/VirtualStereo.csproj`（输出自动部署到 Steam 游戏 Mods 目录 + phonon.dll 进游戏根目录）
- desktop：`dotnet build desktop/VirtualStereo.Desktop.csproj`

## CaptureProbe

独立自测台（不随 mod 发布），共享捕获层源码：

```
CaptureProbe --selftest              自发声 + 静音/补偿实验（不需要游戏）
CaptureProbe --vol-law [--mix-format] 音量-幅度定律标定
CaptureProbe --sta --selftest        STA 套间复现（Unity 主线程环境）
```
