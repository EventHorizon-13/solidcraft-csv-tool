namespace SolidCraftCsvTool;

internal static class BatchDrawingInputs
{
    public static string[] FromFolder(string folder) => Directory.EnumerateFiles(folder)
        .Where(path => Path.GetExtension(path).Equals(".step",StringComparison.OrdinalIgnoreCase) ||
            Path.GetExtension(path).Equals(".stp",StringComparison.OrdinalIgnoreCase))
        .Where(path => !Path.GetFileNameWithoutExtension(path).Equals("辊密封_组合",StringComparison.OrdinalIgnoreCase))
        .OrderBy(path => Path.GetFileName(path),StringComparer.OrdinalIgnoreCase).ToArray();
}
