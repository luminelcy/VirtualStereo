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

        // 空间模拟
        public int Mode { get; set; } = 1;
        public float AzL { get; set; } = -30f;
        public float AzR { get; set; } = 30f;
        public float ElL { get; set; } = 0f;
        public float ElR { get; set; } = 0f;
        public float DistL { get; set; } = 2f;
        public float DistR { get; set; } = 2f;
        public int Interpolation { get; set; } = 1;
        public string SofaPath { get; set; } = "";

        // 音源
        public string LastProcessName { get; set; } = "";

        // 界面
        public int LastPage { get; set; } = 0;

        // 音箱指向性（分频）：5 分频点 / 6 带权重 / 6 带锐度
        public bool DirEnabled { get; set; } = false;
        public float[] DirFreqs { get; set; } = { 125f, 350f, 1000f, 3000f, 10000f };
        public float[] DirW { get; set; } = { 0f, 0f, 0f, 0f, 0f, 0f };
        public float[] DirP { get; set; } = { 1f, 1f, 1f, 1f, 1f, 1f };
        public int DirAim { get; set; } = 0;
        public float DirAimAz { get; set; } = 0f;
        public float DirAimEl { get; set; } = 0f;

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
