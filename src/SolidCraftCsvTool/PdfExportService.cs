using System.Diagnostics;
using System.Text.Json;
using System.Text;

namespace SolidCraftCsvTool;

internal static class PdfExportService
{
    public static void ValidateFontDirectory(string fontDirectory)
    {
        if (string.IsNullOrWhiteSpace(fontDirectory) || !Directory.Exists(fontDirectory))
            throw new DirectoryNotFoundException("PDF 导出需要外置 SHX 字体目录，请在“出图设置”中选择包含 gbenor.shx 和 gbcbig.shx 的文件夹。");
        string fontRoot = Directory.Exists(Path.Combine(fontDirectory, "fonts")) &&
            !File.Exists(Path.Combine(fontDirectory, "gbenor.shx"))
            ? Path.Combine(fontDirectory, "fonts")
            : fontDirectory;
        foreach (string font in new[] { "gbenor.shx", "gbcbig.shx" })
            if (!File.Exists(Path.Combine(fontRoot, font)))
                throw new FileNotFoundException($"PDF 导出缺少外置字体 {font}，请检查 SHX 字体目录。", Path.Combine(fontRoot, font));
    }

    public static async Task ExportAsync(IReadOnlyList<string> pageDxfFiles, string outputFile,
        string fontDirectory, string workDirectory, IProgress<ExportProgress> progress,
        CancellationToken cancellation)
    {
        if (pageDxfFiles.Count == 0) throw new InvalidDataException("没有可导出的 PDF 页面。");
        ValidateFontDirectory(fontDirectory);

        string runtime = Path.Combine(AppContext.BaseDirectory, "PdfExporter");
        string node = Path.Combine(runtime, OperatingSystem.IsWindows() ? "node.exe" : "node");
        string script = Path.Combine(runtime, "pdf-exporter.bundle.mjs");
        if (!File.Exists(node)) throw new FileNotFoundException("找不到随开发预览包附带的开源 Node.js 运行时。", node);
        if (!File.Exists(script)) throw new FileNotFoundException("找不到 MLightCAD PDF 渲染器。请完整解压开发预览包。", script);

        string manifest = Path.Combine(workDirectory, "pdf-manifest.json");
        await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(new
        {
            inputs = pageDxfFiles.Select(Path.GetFullPath).ToArray(),
            output = Path.GetFullPath(outputFile),
            fontDirectory = Path.GetFullPath(fontDirectory)
        }), cancellation);

        var start = new ProcessStartInfo(node)
        {
            WorkingDirectory = runtime,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(script);
        start.ArgumentList.Add("--manifest");
        start.ArgumentList.Add(manifest);
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new IOException("无法启动 PDF 渲染器。");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(cancellation);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch (InvalidOperationException) { }
            throw;
        }
        string output = await stdout;
        string error = await stderr;
        foreach (string line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            progress.Report(new(line, 0, true));
        if (process.ExitCode != 0)
            throw new IOException("开源 PDF 渲染器执行失败：" + (string.IsNullOrWhiteSpace(error) ? output.Trim() : error.Trim()));
        if (!File.Exists(outputFile) || new FileInfo(outputFile).Length < 8)
            throw new IOException("PDF 渲染器未生成有效文件。" + error);
        byte[] signature = new byte[5];
        await using var pdf = File.OpenRead(outputFile);
        if (await pdf.ReadAsync(signature, cancellation) != signature.Length ||
            !signature.AsSpan().SequenceEqual("%PDF-"u8))
            throw new InvalidDataException("PDF 渲染器输出内容无效。");
    }
}

