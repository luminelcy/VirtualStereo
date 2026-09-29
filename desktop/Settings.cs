// 设置记忆：JSON 持久化（exe 目录下 VirtualStereo.settings.json）。
// 记住上次关闭时的参数与窗口位置，下次打开直接续上。
using System;
using System.IO;
using System.Text.Json;

namespace VirtualStereo.Desktop
{
    internal class Settings
    {
        // 处理
        public float PreGainDb { get; set; } = -6f;
        public float PostGainDb { get; set; } = 0f;
        public bool SilenceOriginal { get; set; } = true;
        public int RecMode { get; set; } = 2; // 录制声道: 0=左耳 1=右耳 2=双声道

        // 空间模拟
        public int Mode { get; set; } = 1;
        public float AzL { get; set; } = -30f;
        public float AzR { get; set; } = 30f;
        public float ElL { get; set; } = 0f;
        public float ElR { get; set; } = 0f;
        public float DistL { get; set; } = 2f;
        public float DistR { get; set; } = 2f;
        // 音箱朝向（每源独立；mode 0=朝向听者 1=固定朝前 2=手动）
        // 默认 固定朝前：朝向听者时 θ=0 增益恒 1，指向性听不出效果（防呆）
        public int AimModeL { get; set; } = 1;
        public int AimModeR { get; set; } = 1;
        public float AimAzL { get; set; } = 0f;
        public float AimElL { get; set; } = 0f;
        public float AimAzR { get; set; } = 0f;
        public float AimElR { get; set; } = 0f;

        // 旧版共享朝向（仅迁移用；null = 配置文件里没有）
        public int? DirAim { get; set; } = null;
        public float? DirAimAz { get; set; } = null;
        public float? DirAimEl { get; set; } = null;
        public int Interpolation { get; set; } = 1;
        public string SofaPath { get; set; } = "";

        // 音源
        public string LastProcessName { get; set; } = "";

        // 校准（扫频测两耳频响 + 反相 EQ；曲线留存则参数可继续调，不必重扫）
        public bool CalEnabled { get; set; } = false;
        public float CalSmoothOct { get; set; } = 0.333f;
        public float CalMaxBoostDb { get; set; } = 6f;
        public float CalF1 { get; set; } = 10f;
        public float CalF2 { get; set; } = 24000f;
        public int CalRate { get; set; } = 48000;
        public long CalTimeTicks { get; set; } = 0;
        public float[] CalGridHz { get; set; } = null;
        public float[] CalMeasL { get; set; } = null;
        public float[] CalMeasR { get; set; } = null;

        // 音箱校准（源端补偿；每箱两耳 dB 平均一条曲线）
        public bool SpkCalEnabled { get; set; } = false;
        public float SpkCalSmoothOct { get; set; } = 0.333f;
        public float SpkCalMaxBoostDb { get; set; } = 6f;
        public float SpkCalF1 { get; set; } = 10f;
        public float SpkCalF2 { get; set; } = 24000f;
        public int SpkCalRate { get; set; } = 48000;
        public long SpkCalTimeTicks { get; set; } = 0;
        public float[] SpkCalGridHz { get; set; } = null;
        public float[] SpkCalMeasL { get; set; } = null;
        public float[] SpkCalMeasR { get; set; } = null;

        // 后处理 PEQ（输出调音；8 带，左右同参）
        public bool PostEnabled { get; set; } = false;
        public bool[] PostOn { get; set; } = null;
        public int[] PostType { get; set; } = null;
        public float[] PostFreq { get; set; } = null;
        public float[] PostGain { get; set; } = null;
        public float[] PostQ { get; set; } = null;

        // 听音室（房间几何 + 一次反射 + 混响）
        public bool RoomEnabled { get; set; } = false;
        public float RoomW { get; set; } = 6f;
        public float RoomD { get; set; } = 7f;
        public float RoomH { get; set; } = 2.8f;
        public float RoomListenerX { get; set; } = 3f;
        public float RoomListenerY { get; set; } = 1.2f;
        public float RoomListenerZ { get; set; } = 3.5f;
        public float RoomYaw { get; set; } = 0f;
        // 材质吸声 9 值：[表面(地板/天花板/四壁)×3][频带(低/中/高)]
        public float[] RoomAbs { get; set; } = null;
        public float? RoomAbsorb { get; set; } = null; // 旧版单值 α（迁移用；null=配置里没有）
        public float RoomReflDb { get; set; } = -6f;
        public float RoomReverbDb { get; set; } = -12f;
        public float RoomDamp { get; set; } = 0.4f;

        // 界面
        public int LastPage { get; set; } = 0;

        // 音箱指向性（分频）：5 分频点 / 6 带权重 / 6 带锐度
        public bool DirEnabled { get; set; } = false;
        public int DirFamily { get; set; } = 0; // 图案族：0=锥形（音箱默认） 1=偶极子系（麦）
        public float[] DirFreqs { get; set; } = { 125f, 350f, 1000f, 3000f, 10000f };
        public float[] DirW { get; set; } = { 0f, 0f, 0f, 0f, 0f, 0f };
        public float[] DirP { get; set; } = { 1f, 1f, 1f, 1f, 1f, 1f };

        // 窗口
        public int WindowX { get; set; } = 80;
        public int WindowY { get; set; } = 80;
        public int WindowW { get; set; } = 1100;
        public int WindowH { get; set; } = 740;

        private static string FilePath =>
            Path.Combine(AppContext.BaseDirectory, "VirtualStereo.settings.json");

        public static Settings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath));
                    if (s != null) return s;
                }
            }
            catch { }
            return new Settings();
        }

        public void Save()
        {
            try
            {
                File.WriteAllText(FilePath,
                    JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }
    }
}
