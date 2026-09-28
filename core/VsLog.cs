// core 的日志出口：core 编进多个程序集（mod/桌面/探针），不能依赖 MelonLoader。
// 各壳在启动时注入自己的实现（mod→MelonLogger，桌面→Console）。
using System;

namespace VirtualStereo
{
    internal static class VsLog
    {
        public static Action<string> OnInfo;
        public static Action<string> OnError;

        public static void Info(string message) => OnInfo?.Invoke(message);
        public static void Error(string message) => OnError?.Invoke(message);
    }
}
