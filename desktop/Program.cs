// VirtualStereo Desktop —— 桌面壳入口：Veldrid 窗口 + ImGui 界面 + WASAPI 播放。
// 端到端：选进程 → 捕获 → 前置增益/双耳 → 后置增益 → 播放；面板实时可调。
// 设置记忆：启动载入 / 运行中定期保存 / 退出保存（VirtualStereo.settings.json）。
using System;
using ImGuiNET;
using Veldrid;
using Veldrid.StartupUtilities;
using VirtualStereo;
using VirtualStereo.Desktop;
using VirtualStereo.Dsp;

VsLog.OnInfo = s => Console.WriteLine(s);
VsLog.OnError = s => Console.Error.WriteLine("[E] " + s);

var settings = Settings.Load();

var windowCI = new WindowCreateInfo(
    settings.WindowX, settings.WindowY,
    Math.Max(640, settings.WindowW), Math.Max(480, settings.WindowH),
    WindowState.Normal, "VirtualStereo Desktop");
var gdCI = new GraphicsDeviceOptions(true, null, true, default, true, true);
VeldridStartup.CreateWindowAndGraphicsDevice(windowCI, gdCI, out var window, out var gd);
var cl = gd.ResourceFactory.CreateCommandList();

var controller = new ImGuiRenderer(
    gd, gd.MainSwapchain.Framebuffer.OutputDescription, (int)window.Width, (int)window.Height);

// 中文字体：ImGui 自带字体无 CJK，载入系统雅黑
var io = ImGui.GetIO();
io.Fonts.Clear();
io.Fonts.AddFontFromFileTTF(@"C:\Windows\Fonts\msyh.ttc", 16f, null, io.Fonts.GetGlyphRangesChineseFull());
controller.RecreateFontDeviceTexture();

window.Resized += () =>
{
    gd.MainSwapchain.Resize((uint)window.Width, (uint)window.Height);
    controller.WindowResized((int)window.Width, (int)window.Height);
};

using var app = new DesktopApp();

// 设置 → 应用状态
app.PreGainDb = settings.PreGainDb;
app.PostGainDb = settings.PostGainDb;
app.SilenceOriginal = settings.SilenceOriginal;
app.CurrentMode = settings.Mode;
app.AzL = settings.AzL;
app.AzR = settings.AzR;
app.ElL = settings.ElL;
app.ElR = settings.ElR;
app.DistL = settings.DistL;
app.DistR = settings.DistR;
app.Interpolation = settings.Interpolation;
app.Directivity.Enabled = settings.DirEnabled;
if (settings.DirFreqs != null && settings.DirFreqs.Length == DirectivityProcessor.Splits)
    for (int i = 0; i < DirectivityProcessor.Splits; i++) app.Directivity.Freqs[i] = settings.DirFreqs[i];
if (settings.DirW != null && settings.DirW.Length == DirectivityProcessor.Bands)
    for (int i = 0; i < DirectivityProcessor.Bands; i++) app.Directivity.W[i] = settings.DirW[i];
if (settings.DirP != null && settings.DirP.Length == DirectivityProcessor.Bands)
    for (int i = 0; i < DirectivityProcessor.Bands; i++) app.Directivity.P[i] = settings.DirP[i];
app.Directivity.Aim = settings.DirAim;
app.Directivity.AimAz = settings.DirAimAz;
app.Directivity.AimEl = settings.DirAimEl;
Ui.Init(settings);

void SaveSettings()
{
    settings.PreGainDb = app.PreGainDb;
    settings.PostGainDb = app.PostGainDb;
    settings.SilenceOriginal = app.SilenceOriginal;
    settings.Mode = app.CurrentMode;
    settings.AzL = app.AzL;
    settings.AzR = app.AzR;
    settings.ElL = app.ElL;
    settings.ElR = app.ElR;
    settings.DistL = app.DistL;
    settings.DistR = app.DistR;
    settings.Interpolation = app.Interpolation;
    settings.SofaPath = Ui.CurrentSofaPath;
    settings.DirEnabled = app.Directivity.Enabled;
    settings.DirFreqs = (float[])app.Directivity.Freqs.Clone();
    settings.DirW = (float[])app.Directivity.W.Clone();
    settings.DirP = (float[])app.Directivity.P.Clone();
    settings.DirAim = app.Directivity.Aim;
    settings.DirAimAz = app.Directivity.AimAz;
    settings.DirAimEl = app.Directivity.AimEl;
    settings.WindowX = window.X;
    settings.WindowY = window.Y;
    settings.WindowW = window.Width;
    settings.WindowH = window.Height;
    settings.Save();
}

var last = DateTime.UtcNow;
var nextAutoSave = DateTime.UtcNow.AddSeconds(5);

while (window.Exists)
{
    var snapshot = window.PumpEvents();
    if (!window.Exists) break;

    var now = DateTime.UtcNow;
    float dt = (float)(now - last).TotalSeconds;
    last = now;

    controller.Update(dt, snapshot);
    Ui.Draw(app);

    cl.Begin();
    cl.SetFramebuffer(gd.MainSwapchain.Framebuffer);
    cl.ClearColorTarget(0, new RgbaFloat(0.10f, 0.10f, 0.12f, 1f));
    controller.Render(gd, cl);
    cl.End();
    gd.SubmitCommands(cl);
    gd.SwapBuffers(gd.MainSwapchain);

    if (now >= nextAutoSave)
    {
        nextAutoSave = now.AddSeconds(5);
        SaveSettings(); // 定期保存，崩溃也不丢太多
    }
}

app.StopCapture();
SaveSettings();
