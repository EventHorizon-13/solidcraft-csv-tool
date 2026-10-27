namespace SolidCraftCsvTool;

internal static class BatchDrawingInputs
{
    public static string[] FromFolder(string folder,string? combinedFileName=null) => Directory.EnumerateFiles(folder)
        .Where(path => Path.GetExtension(path).Equals(".step",StringComparison.OrdinalIgnoreCase) ||
            Path.GetExtension(path).Equals(".stp",StringComparison.OrdinalIgnoreCase))
        .Where(path => !Path.GetFileNameWithoutExtension(path).Equals("辊密封_组合",StringComparison.OrdinalIgnoreCase))
        .Where(path=>!Path.GetFileName(path).Equals(combinedFileName,StringComparison.OrdinalIgnoreCase))
        .Where(path=>!System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileNameWithoutExtension(path),@"^辊密封_\d{4}$"))
        .OrderBy(path => Path.GetFileName(path),StringComparer.OrdinalIgnoreCase).ToArray();
}
