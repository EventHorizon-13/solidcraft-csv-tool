using System.Reflection;

namespace SolidCraftCsvTool;

internal static class AppBuildInfo
{
    public const string ProductName="标准辊密封批量生成";
    public static string Version=>typeof(AppBuildInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ??typeof(AppBuildInfo).Assembly.GetName().Version?.ToString(3)??"未知";
    public static string BuildDate=>typeof(AppBuildInfo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(attribute=>attribute.Key=="BuildDate")?.Value??"未知";
    public static string WindowTitle=>$"{ProductName} · v{Version} · 编译于 {BuildDate}";
}
