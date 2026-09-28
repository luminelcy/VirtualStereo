// VirtualStereo Desktop —— 桌面壳入口：Veldrid 窗口 + ImGui 界面 + WASAPI 播放。
// 端到端：选进程 → 捕获 → 前置增益/双耳 → 播放；面板实时可调。
using System;
using ImGuiNET;
using Veldrid;
using Veldrid.StartupUtilities;
using VirtualStereo;
using VirtualStereo.Desktop;

VsLog.OnInfo = s => Console.WriteLine(s);
VsLog.OnError = s => Console.Error.WriteLine("[E] " + s);

var windowCI = new WindowCreateInfo(80, 80, 1100, 740, WindowState.Normal, "VirtualStereo Desktop");
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
var last = DateTime.UtcNow;

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
}

app.StopCapture();
