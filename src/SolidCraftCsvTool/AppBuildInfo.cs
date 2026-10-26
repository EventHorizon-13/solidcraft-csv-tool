using System.Reflection;

namespace SolidCraftCsvTool;

internal static class AppBuildInfo
{
    public const string ProductName = "标准辊密封批量生成";
    public static string WindowTitle
    {
        get
        {
            Assembly assembly = typeof(AppBuildInfo).Assembly;
            string version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
                ?? assembly.GetName().Version?.ToString(3) ?? "未知";
            string date = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(attribute => attribute.Key == "BuildDate")?.Value ?? "未知";
            return $"{ProductName} · v{version} · 编译于 {date}";
        }
    }
}
