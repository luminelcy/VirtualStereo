// 构建期开关：只有"调试版"（我们自己机器上）才有 F10 调试面板和标记球/朝向箭头，
// 发给别人用的发布版不含这些（F10 无响应，场景里不会出现任何辅助图形）。
//
// 调试版构建：
//   dotnet build mod/VirtualStereo.csproj -c Release -p:Platform=x64 -p:DevBuild=true
// 发布版构建（默认）：
//   dotnet build mod/VirtualStereo.csproj -c Release -p:Platform=x64
namespace VirtualStereo
{
    internal static class BuildFlags
    {
#if VS_DEV
        public const bool Dev = true;
#else
        public const bool Dev = false;
#endif
    }
}
