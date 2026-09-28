// VirtualStereo Desktop —— 桌面壳入口：Veldrid 窗口 + ImGui 界面 + WASAPI 播放。
// 端到端：选进程 → 捕获 → 前置增益/双耳 → 后置增益 → 播放；面板实时可调。
// 设置记忆：启动载入 / 运行中定期保存 / 退出保存（VirtualStereo.settings.json）。
using System;
using ImGuiNET;
using Veldrid;
using Veldrid.StartupUtilities;
using VirtualStereo;
using VirtualStereo.Desktop;

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
app.Interpolation = settings.Interpolation;
app.Directivity.Enabled = settings.DirEnabled;
if (settings.DirFreqs != null && settings.DirFreqs.Length == 3)
{
    app.Directivity.Freq1 = settings.DirFreqs[0];
    app.Directivity.Freq2 = settings.DirFreqs[1];
    app.Directivity.Freq3 = settings.DirFreqs[2];
}
if (settings.DirW != null && settings.DirW.Length == 4)
{
    app.Directivity.WLow = settings.DirW[0];
    app.Directivity.WMidLow = settings.DirW[1];
    app.Directivity.WMidHigh = settings.DirW[2];
    app.Directivity.WHigh = settings.DirW[3];
}
if (settings.DirP != null && settings.DirP.Length == 4)
{
    app.Directivity.PLow = settings.DirP[0];
    app.Directivity.PMidLow = settings.DirP[1];
    app.Directivity.PMidHigh = settings.DirP[2];
    app.Directivity.PHigh = settings.DirP[3];
}
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
    settings.Interpolation = app.Interpolation;
    settings.SofaPath = Ui.CurrentSofaPath;
    settings.DirEnabled = app.Directivity.Enabled;
    settings.DirFreqs = new[] { app.Directivity.Freq1, app.Directivity.Freq2, app.Directivity.Freq3 };
    settings.DirW = new[] { app.Directivity.WLow, app.Directivity.WMidLow, app.Directivity.WMidHigh, app.Directivity.WHigh };
    settings.DirP = new[] { app.Directivity.PLow, app.Directivity.PMidLow, app.Directivity.PMidHigh, app.Directivity.PHigh };
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
