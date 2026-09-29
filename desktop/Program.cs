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
// 单窗口 16 位索引上限 64k 顶点：指向性热图（双图约 7.5 万顶点）超限后索引回绕、
// 整页图形/字体错乱（看着像内存污染，其实是确定性索引环绕）。
// Veldrid.ImGui 的 DrawIndexed 已传 base vertex，开 RendererHasVtxOffset 让 ImGui 自动分片。
io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;

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
app.Monitor.Mode = settings.RecMode;
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
app.AimModeL = settings.AimModeL;
app.AimModeR = settings.AimModeR;
app.AimAzL = settings.AimAzL;
app.AimElL = settings.AimElL;
app.AimAzR = settings.AimAzR;
app.AimElR = settings.AimElR;
// 迁移：旧版共享朝向 -> 两源（防止升级后朝向静默重置成"朝向听者"=听不出效果）
if (settings.DirAim.HasValue)
{
    app.AimModeL = app.AimModeR = settings.DirAim.Value;
    if (settings.DirAimAz.HasValue) { app.AimAzL = app.AimAzR = settings.DirAimAz.Value; }
    if (settings.DirAimEl.HasValue) { app.AimElL = app.AimElR = settings.DirAimEl.Value; }
}
// 校准：曲线恢复（参数可继续调）+ 模式开关
app.Cal.SmoothOct = settings.CalSmoothOct;
app.Cal.MaxBoostDb = settings.CalMaxBoostDb;
if (settings.CalRate > 0) app.Cal.MeasRate = settings.CalRate;
app.Cal.LoadMeasured(settings.CalGridHz, settings.CalMeasL, settings.CalMeasR,
    settings.CalF1, settings.CalF2, settings.CalTimeTicks);
app.Cal.SetEnabled(settings.CalEnabled);
// 音箱校准
app.SpkCal.SmoothOct = settings.SpkCalSmoothOct;
app.SpkCal.MaxBoostDb = settings.SpkCalMaxBoostDb;
if (settings.SpkCalRate > 0) app.SpkCal.MeasRate = settings.SpkCalRate;
app.SpkCal.LoadMeasured(settings.SpkCalGridHz, settings.SpkCalMeasL, settings.SpkCalMeasR,
    settings.SpkCalF1, settings.SpkCalF2, settings.SpkCalTimeTicks);
app.SpkCal.SetEnabled(settings.SpkCalEnabled);
// 后处理 PEQ（数组长度对不上就用默认值，防手工改配置文件改坏）
if (settings.PostOn != null && settings.PostOn.Length == PostEq.Bands)
    for (int i = 0; i < PostEq.Bands; i++) app.Post.On[i] = settings.PostOn[i];
if (settings.PostType != null && settings.PostType.Length == PostEq.Bands)
    for (int i = 0; i < PostEq.Bands; i++) app.Post.Type[i] = settings.PostType[i];
if (settings.PostFreq != null && settings.PostFreq.Length == PostEq.Bands)
    for (int i = 0; i < PostEq.Bands; i++) app.Post.Freq[i] = settings.PostFreq[i];
if (settings.PostGain != null && settings.PostGain.Length == PostEq.Bands)
    for (int i = 0; i < PostEq.Bands; i++) app.Post.GainDb[i] = settings.PostGain[i];
if (settings.PostQ != null && settings.PostQ.Length == PostEq.Bands)
    for (int i = 0; i < PostEq.Bands; i++) app.Post.Q[i] = settings.PostQ[i];
app.Post.SetEnabled(settings.PostEnabled);
// 听音室
app.Room.Model.W = settings.RoomW;
app.Room.Model.D = settings.RoomD;
app.Room.Model.H = settings.RoomH;
app.Room.Model.ListenerX = settings.RoomListenerX;
app.Room.Model.ListenerY = settings.RoomListenerY;
app.Room.Model.ListenerZ = settings.RoomListenerZ;
app.Room.Model.YawDeg = settings.RoomYaw;
// 材质：新配置用 9 值曲线；旧配置单值 α → 全带平铺；都没有 → 保留模型默认（地毯/抹灰/木地板）
if (settings.RoomAbs != null && settings.RoomAbs.Length == 9)
{
    for (int s = 0; s < 3; s++)
        app.Room.Model.SetSurface(s, settings.RoomAbs[s * 3], settings.RoomAbs[s * 3 + 1], settings.RoomAbs[s * 3 + 2]);
}
else if (settings.RoomAbsorb.HasValue)
{
    float a = settings.RoomAbsorb.Value;
    for (int s = 0; s < 3; s++) app.Room.Model.SetSurface(s, a, a, a);
}
app.Room.ReflDb = settings.RoomReflDb;
app.Room.ReverbDb = settings.RoomReverbDb;
app.Room.Damp = settings.RoomDamp;
app.Room.Enabled = settings.RoomEnabled;
Ui.Init(settings);

void SaveSettings()
{
    settings.PreGainDb = app.PreGainDb;
    settings.PostGainDb = app.PostGainDb;
    settings.SilenceOriginal = app.SilenceOriginal;
    settings.RecMode = app.Monitor.Mode;
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
    settings.AimModeL = app.AimModeL;
    settings.AimModeR = app.AimModeR;
    settings.AimAzL = app.AimAzL;
    settings.AimElL = app.AimElL;
    settings.AimAzR = app.AimAzR;
    settings.AimElR = app.AimElR;
    settings.CalEnabled = app.Cal.Enabled;
    settings.CalSmoothOct = app.Cal.SmoothOct;
    settings.CalMaxBoostDb = app.Cal.MaxBoostDb;
    settings.CalF1 = app.Cal.MeasF1;
    settings.CalF2 = app.Cal.MeasF2;
    settings.CalRate = (int)app.Cal.MeasRate;
    settings.CalTimeTicks = app.Cal.DoneAtTicks;
    settings.CalGridHz = app.Cal.HasCurve ? (float[])app.Cal.GridHz.Clone() : null;
    settings.CalMeasL = app.Cal.HasCurve ? (float[])app.Cal.MeasL.Clone() : null;
    settings.CalMeasR = app.Cal.HasCurve ? (float[])app.Cal.MeasR.Clone() : null;
    settings.SpkCalEnabled = app.SpkCal.Enabled;
    settings.SpkCalSmoothOct = app.SpkCal.SmoothOct;
    settings.SpkCalMaxBoostDb = app.SpkCal.MaxBoostDb;
    settings.SpkCalF1 = app.SpkCal.MeasF1;
    settings.SpkCalF2 = app.SpkCal.MeasF2;
    settings.SpkCalRate = (int)app.SpkCal.MeasRate;
    settings.SpkCalTimeTicks = app.SpkCal.DoneAtTicks;
    settings.SpkCalGridHz = app.SpkCal.HasCurve ? (float[])app.SpkCal.GridHz.Clone() : null;
    settings.SpkCalMeasL = app.SpkCal.HasCurve ? (float[])app.SpkCal.MeasSrcL.Clone() : null;
    settings.SpkCalMeasR = app.SpkCal.HasCurve ? (float[])app.SpkCal.MeasSrcR.Clone() : null;
    settings.PostEnabled = app.Post.Enabled;
    settings.PostOn = (bool[])app.Post.On.Clone();
    settings.PostType = (int[])app.Post.Type.Clone();
    settings.PostFreq = (float[])app.Post.Freq.Clone();
    settings.PostGain = (float[])app.Post.GainDb.Clone();
    settings.PostQ = (float[])app.Post.Q.Clone();
    settings.RoomEnabled = app.Room.Enabled;
    settings.RoomW = app.Room.Model.W;
    settings.RoomD = app.Room.Model.D;
    settings.RoomH = app.Room.Model.H;
    settings.RoomListenerX = app.Room.Model.ListenerX;
    settings.RoomListenerY = app.Room.Model.ListenerY;
    settings.RoomListenerZ = app.Room.Model.ListenerZ;
    settings.RoomYaw = app.Room.Model.YawDeg;
    settings.RoomAbs = new[]
    {
        app.Room.Model.Abs[0, 0], app.Room.Model.Abs[0, 1], app.Room.Model.Abs[0, 2],
        app.Room.Model.Abs[1, 0], app.Room.Model.Abs[1, 1], app.Room.Model.Abs[1, 2],
        app.Room.Model.Abs[2, 0], app.Room.Model.Abs[2, 1], app.Room.Model.Abs[2, 2],
    };
    settings.RoomReflDb = app.Room.ReflDb;
    settings.RoomReverbDb = app.Room.ReverbDb;
    settings.RoomDamp = app.Room.Damp;
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
