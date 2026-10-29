using System.Globalization;
using System.Text;

namespace SolidCraftCsvTool;

internal sealed record Point2(double X, double Y);
internal sealed record Line2(Point2 A, Point2 B, string Layer);
internal sealed record Polyline2(IReadOnlyList<Point2> Points, bool Closed, string Layer);
internal sealed record Box2(double MinX, double MinY, double MaxX, double MaxY)
{
    public double Width => MaxX - MinX;
    public double Height => MaxY - MinY;
}
internal sealed record Projection2(string Name, IReadOnlyList<Line2> Lines, IReadOnlyList<Polyline2> Polylines, Box2 Bounds);
internal sealed record ObliqueProjection(double Dx, double Dy);
internal sealed record StepFeatureSet(IReadOnlyList<DetectedArc> Arcs, double? Pitch, double? Diameter,
    IReadOnlyList<double> FrontBreaks, IReadOnlyList<double> FrontBreakY, IReadOnlyList<double> PlanBreakX, IReadOnlyList<double> PlanBreakY,
    IReadOnlyList<ObliqueProjection> PlanSlants, string Note);
internal sealed record StepDrawingPage(string Name, Projection2 Front, Projection2 Top, Projection2 End, StepFeatureSet Features);

public static class StepDrawingExporter
{
    public static async Task RunAsync(IReadOnlyList<string> inputFiles, string outputFolder, int port,
        IProgress<ExportProgress> progress, CancellationToken cancellation, DrawingOptions? drawingOptions = null,
        bool exportPdf = false)
    {
        drawingOptions ??= new DrawingOptions();
        drawingOptions.Validate();
        if (inputFiles.Count == 0) throw new InvalidDataException("请选择至少一个 SolidCraft 或 STEP 三维文件。");
        foreach (string path in inputFiles)
            if (!File.Exists(path) || !new[] { ".solidcraft", ".step", ".stp" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("输入列表中包含不存在或格式不支持的文件：" + path);

        inputFiles = inputFiles.OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase).ToArray();
        string destination = Path.GetFullPath(outputFolder);
        Directory.CreateDirectory(destination);
        string outputName = drawingOptions.ResolvedFileName;
        string outputPath = Path.Combine(destination, outputName);
        if (!drawingOptions.OverwriteOutput&&File.Exists(outputPath)) throw new IOException("汇总 DXF 已存在，未覆盖原文件：" + outputPath);
        string pdfOutputPath = Path.Combine(destination, Path.ChangeExtension(outputName, ".pdf"));
        if (exportPdf)
        {
            if (string.IsNullOrWhiteSpace(drawingOptions.TemplatePath))
                throw new InvalidDataException("PDF 导出需要选择包含固定 A4 图框的 DXF 模板。");
            _ = DrawingResources.Load(drawingOptions);
            if (!drawingOptions.OverwriteOutput && File.Exists(pdfOutputPath))
                throw new IOException("汇总 PDF 已存在，未覆盖原文件：" + pdfOutputPath);
            PdfExportService.ValidateFontDirectory(drawingOptions.FontDirectory);
        }

        string work = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SolidCraft", "csv-tool", "step-drawings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var pages = new List<StepDrawingPage>(inputFiles.Count);
        string? stagedPdf = null;
        try
        {
            using var client = await ControlClient.ConnectAsync(port);
            var running = await client.RequestAsync("control.running", new { });
            if (!running.TryGetProperty("running", out var jobs) || jobs.ValueKind != System.Text.Json.JsonValueKind.Array || jobs.GetArrayLength() > 0)
                throw new IOException("SolidCraft 当前正在执行其他命令，请完成后再批量出图。");

            var originalUi = client.IsHeadless ? default : await client.RequestAsync("ui.inspect", new { });
            var originalCamera = originalUi.ValueKind==System.Text.Json.JsonValueKind.Object&&originalUi.TryGetProperty("camera", out var cam) ? cam.Clone() : default;
            bool? originalPerspective = originalUi.ValueKind==System.Text.Json.JsonValueKind.Object&&originalUi.TryGetProperty("perspective", out var perspective) && perspective.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False
                ? perspective.GetBoolean() : null;
            bool currentStepOpen = false;
            try
            {
                if(!client.IsHeadless)await client.RequestAsync("ui.set", new { perspective = false });
                for (int i = 0; i < inputFiles.Count; i++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    string step = Path.GetFullPath(inputFiles[i]);
                    progress.Report(new($"正在读取三维模型并投影三视图 {i + 1}/{inputFiles.Count}：{Path.GetFileName(step)}", i));
                    await client.ExecuteAsync("doc.open", new { path = step, up = "z" });
                    currentStepOpen = true;

                    Projection2 front = await ExportProjection(client, work, $"{i:D4}-front.dxf", "front", cancellation);
                    Projection2 top = await ExportProjection(client, work, $"{i:D4}-top.dxf", "top", cancellation);
                    Projection2 end = await ExportProjection(client, work, $"{i:D4}-left.dxf", "left", cancellation);
                    pages.Add(new(Path.GetFileNameWithoutExtension(step), front, top, end, DxfFeatureDetector.Analyze(front, top)));

                    if(!client.IsHeadless)await client.RequestAsync("ui.documents", new { action = "close", force = true });
                    currentStepOpen = false;
                    progress.Report(new($"已读取 {Path.GetFileName(step)}，三视图与几何特征已提取。", i + 1));
                }
            }
            finally
            {
                if (currentStepOpen&&!client.IsHeadless)
                {
                    try { await client.RequestAsync("ui.documents", new { action = "close", force = true }); } catch (IOException) { }
                }
                if (originalPerspective.HasValue)
                    try { await client.RequestAsync("ui.set", new { perspective = originalPerspective.Value }); } catch (IOException) { }
                if (originalCamera.ValueKind == System.Text.Json.JsonValueKind.Object)
                    try { await client.RequestAsync("ui.camera", originalCamera); } catch (IOException) { }
            }

            cancellation.ThrowIfCancellationRequested();
            cancellation.ThrowIfCancellationRequested();
            string staged = Path.Combine(work, outputName);
            progress.Report(new($"正在将 {pages.Count} 张加工图排版到一个 DXF…", pages.Count - 1));
            MachiningDxf.WriteCombined(staged, pages, drawingOptions);
            if (exportPdf)
            {
                cancellation.ThrowIfCancellationRequested();
                string[] pdfPages = new string[pages.Count];
                for (int i = 0; i < pages.Count; i++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    pdfPages[i] = Path.Combine(work, $"pdf-page-{i + 1:D4}.dxf");
                    MachiningDxf.Write(pdfPages[i], pages[i], drawingOptions);
                }
                stagedPdf = Path.Combine(destination, $".{Path.GetFileNameWithoutExtension(outputName)}-{Guid.NewGuid():N}.tmp.pdf");
                progress.Report(new($"正在用外置 SHX 字体渲染 {pages.Count} 页 A4 PDF…", pages.Count - 1));
                await PdfExportService.ExportAsync(pdfPages, stagedPdf, drawingOptions.FontDirectory, work, progress, cancellation);
            }
            cancellation.ThrowIfCancellationRequested();
            if (!drawingOptions.OverwriteOutput&&File.Exists(outputPath)) throw new IOException("目标文件在出图期间出现，未覆盖原文件：" + outputPath);
            if (exportPdf && !drawingOptions.OverwriteOutput && File.Exists(pdfOutputPath)) throw new IOException("PDF 目标文件在出图期间出现，未覆盖原文件：" + pdfOutputPath);
            if (stagedPdf != null) { File.Move(stagedPdf, pdfOutputPath, drawingOptions.OverwriteOutput); stagedPdf = null; }
            File.Move(staged, outputPath,drawingOptions.OverwriteOutput);
            progress.Report(new(exportPdf
                ? $"已保存汇总 DXF 和 A4 PDF：{outputName}（{pages.Count} 页）"
                : $"已保存汇总 DXF：{outputName}（含 {pages.Count} 张加工图）", inputFiles.Count));
        }
        finally
        {
            if (stagedPdf != null && File.Exists(stagedPdf))
            {
                try { File.Delete(stagedPdf); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            if (Directory.Exists(work))
            {
                try { Directory.Delete(work, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

public static async Task RunAsync(IReadOnlyList<string> inputFiles, string outputFolder, int port,
        IProgress<ExportProgress> progress, CancellationToken cancellation, DrawingOptions? drawingOptions = null,
        bool exportPdf = false)
    {
        drawingOptions ??= new DrawingOptions();
        if (inputFiles.Count == 0) throw new InvalidDataException("请选择至少一个 SolidCraft 或 STEP 三维文件。");
        foreach (string path in inputFiles)
            if (!File.Exists(path) || !new[] { ".solidcraft", ".step", ".stp" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("输入列表中包含不存在或格式不支持的文件：" + path);

        ValidateOutputTargets(outputFolder, drawingOptions, exportPdf);
        inputFiles = inputFiles.OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase).ToArray();
        string destination = Path.GetFullPath(outputFolder);
        Directory.CreateDirectory(destination);
        string work = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SolidCraft", "csv-tool", "step-drawings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var pages = new List<StepDrawingPage>(inputFiles.Count);
        try
        {
            using var client = await ControlClient.ConnectAsync(port);
            var running = await client.RequestAsync("control.running", new { });
            if (!running.TryGetProperty("running", out var jobs) || jobs.ValueKind != System.Text.Json.JsonValueKind.Array || jobs.GetArrayLength() > 0)
                throw new IOException("SolidCraft 当前正在执行其他命令，请完成后再批量出图。");

            var originalUi = client.IsHeadless ? default : await client.RequestAsync("ui.inspect", new { });
            var originalCamera = originalUi.ValueKind == System.Text.Json.JsonValueKind.Object && originalUi.TryGetProperty("camera", out var cam) ? cam.Clone() : default;
            bool? originalPerspective = originalUi.ValueKind == System.Text.Json.JsonValueKind.Object && originalUi.TryGetProperty("perspective", out var perspective) && perspective.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False
                ? perspective.GetBoolean() : null;
            bool currentDocumentOpen = false;
            try
            {
                if (!client.IsHeadless) await client.RequestAsync("ui.set", new { perspective = false });
                for (int i = 0; i < inputFiles.Count; i++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    string input = Path.GetFullPath(inputFiles[i]);
                    progress.Report(new($"正在打开三维模型 {i + 1}/{inputFiles.Count}：{Path.GetFileName(input)}", i));
                    await client.ExecuteAsync("doc.open", new { path = input, up = "z" });
                    currentDocumentOpen = true;
                    pages.Add(await CaptureCurrentAsync(client, work, i, Path.GetFileNameWithoutExtension(input), progress, cancellation));

                    if (!client.IsHeadless) await client.RequestAsync("ui.documents", new { action = "close", force = true });
                    currentDocumentOpen = false;
                }
            }
            finally
            {
                if (currentDocumentOpen && !client.IsHeadless)
                {
                    try { await client.RequestAsync("ui.documents", new { action = "close", force = true }); } catch (IOException) { }
                }
                if (originalPerspective.HasValue)
                    try { await client.RequestAsync("ui.set", new { perspective = originalPerspective.Value }); } catch (IOException) { }
                if (originalCamera.ValueKind == System.Text.Json.JsonValueKind.Object)
                    try { await client.RequestAsync("ui.camera", originalCamera); } catch (IOException) { }
            }

            await WritePagesAsync(pages, destination, work, progress, cancellation, drawingOptions, exportPdf);
        }
        finally
        {
            if (Directory.Exists(work))
            {
                try { Directory.Delete(work, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    internal static void ValidateOutputTargets(string outputFolder, DrawingOptions drawingOptions, bool exportPdf)
    {
        drawingOptions.Validate();
        string destination = Path.GetFullPath(outputFolder);
        string outputPath = Path.Combine(destination, drawingOptions.ResolvedFileName);
        if (!drawingOptions.OverwriteOutput && File.Exists(outputPath))
            throw new IOException("汇总 DXF 已存在，未覆盖原文件：" + outputPath);
        if (exportPdf)
        {
            if (string.IsNullOrWhiteSpace(drawingOptions.TemplatePath))
                throw new InvalidDataException("PDF 导出需要选择包含固定 A4 图框的 DXF 模板。");
            _ = DrawingResources.Load(drawingOptions);
            string pdfOutputPath = Path.Combine(destination, Path.ChangeExtension(drawingOptions.ResolvedFileName, ".pdf"));
            if (!drawingOptions.OverwriteOutput && File.Exists(pdfOutputPath))
                throw new IOException("汇总 PDF 已存在，未覆盖原文件：" + pdfOutputPath);
            PdfExportService.ValidateFontDirectory(drawingOptions.FontDirectory);
        }
    }

    internal static async Task<StepDrawingPage> CaptureCurrentAsync(ControlClient client, string work, int index,
        string name, IProgress<ExportProgress> progress, CancellationToken cancellation)
    {
        Directory.CreateDirectory(work);
        progress.Report(new($"正在从当前模型提取三视图 {index + 1}：{name}", index));
        Projection2 front = await ExportProjection(client, work, $"{index:D4}-front.dxf", "front", cancellation);
        Projection2 top = await ExportProjection(client, work, $"{index:D4}-top.dxf", "top", cancellation);
        Projection2 end = await ExportProjection(client, work, $"{index:D4}-left.dxf", "left", cancellation);
        return new(name, front, top, end, DxfFeatureDetector.Analyze(front, top));
    }

    internal static async Task WritePagesAsync(IReadOnlyList<StepDrawingPage> pages, string outputFolder, string work,
        IProgress<ExportProgress> progress, CancellationToken cancellation, DrawingOptions drawingOptions, bool exportPdf)
    {
        if (pages.Count == 0) throw new InvalidDataException("没有可排版的三视图。");
        string destination = Path.GetFullPath(outputFolder);
        Directory.CreateDirectory(destination);
        string outputName = drawingOptions.ResolvedFileName;
        string outputPath = Path.Combine(destination, outputName);
        string pdfOutputPath = Path.Combine(destination, Path.ChangeExtension(outputName, ".pdf"));
        string? stagedPdf = null;
        try
        {
            cancellation.ThrowIfCancellationRequested();
            string staged = Path.Combine(work, outputName);
            progress.Report(new($"正在将 {pages.Count} 张加工图排版到一个 DXF…", pages.Count - 1));
            MachiningDxf.WriteCombined(staged, pages, drawingOptions);
            if (exportPdf)
            {
                cancellation.ThrowIfCancellationRequested();
                string[] pdfPages = new string[pages.Count];
                for (int i = 0; i < pages.Count; i++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    pdfPages[i] = Path.Combine(work, $"pdf-page-{i + 1:D4}.dxf");
                    MachiningDxf.Write(pdfPages[i], pages[i], drawingOptions);
                }
                stagedPdf = Path.Combine(destination, $".{Path.GetFileNameWithoutExtension(outputName)}-{Guid.NewGuid():N}.tmp.pdf");
                progress.Report(new($"正在用外置 SHX 字体渲染 {pages.Count} 页 A4 PDF…", pages.Count - 1));
                await PdfExportService.ExportAsync(pdfPages, stagedPdf, drawingOptions.FontDirectory, work, progress, cancellation);
            }
            cancellation.ThrowIfCancellationRequested();
            if (!drawingOptions.OverwriteOutput && File.Exists(outputPath)) throw new IOException("目标文件在出图期间出现，未覆盖原文件：" + outputPath);
            if (exportPdf && !drawingOptions.OverwriteOutput && File.Exists(pdfOutputPath)) throw new IOException("PDF 目标文件在出图期间出现，未覆盖原文件：" + pdfOutputPath);
            if (stagedPdf != null) { File.Move(stagedPdf, pdfOutputPath, drawingOptions.OverwriteOutput); stagedPdf = null; }
            File.Move(staged, outputPath, drawingOptions.OverwriteOutput);
            progress.Report(new(exportPdf
                ? $"已保存汇总 DXF 和 A4 PDF：{outputName}（{pages.Count} 页）"
                : $"已保存汇总 DXF：{outputName}（含 {pages.Count} 张加工图）", pages.Count));
        }
        finally
        {
            if (stagedPdf != null && File.Exists(stagedPdf))
            {
                try { File.Delete(stagedPdf); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

