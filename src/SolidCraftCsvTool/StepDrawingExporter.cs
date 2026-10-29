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


    static async Task<Projection2> ExportProjection(ControlClient client, string work, string file, string view, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        System.Text.Json.JsonElement camera;
        if(client.IsHeadless)
        {
            var applied=await client.ExecuteAsync("view.apply",new{view});
            var node=System.Text.Json.Nodes.JsonNode.Parse(applied.GetProperty("camera").GetRawText())!;
            node["fov"]=0;camera=System.Text.Json.JsonSerializer.SerializeToElement(node);
        }
        else
        {
            await client.RequestAsync("ui.view", new { view, animate = false });
            var ui=await client.RequestAsync("ui.inspect",new{});
            if(!ui.TryGetProperty("camera",out camera)||camera.ValueKind!=System.Text.Json.JsonValueKind.Object)
                throw new IOException($"SolidCraft 没有返回 {view} 视图相机，不能生成精确投影。");
        }
        string path = Path.Combine(work, file);
        await client.ExecuteAsync("file.export_view_dxf", new { camera, hidden = "dashed", tolerance = 0.005, path });
        return DxfProjectionReader.Read(path, view);
    }
}

internal static class DxfProjectionReader
{
    sealed class EndpointNode(Point2 point)
    {
        public Point2 Point { get; } = point;
        public List<int> Edges { get; } = [];
    }
    readonly record struct EndpointKey(long X, long Y);
    readonly record struct ProjectionEdge(int A, int B, string Layer);

    public static Projection2 Read(string path, string name)
    {
        if (!File.Exists(path)) throw new IOException("SolidCraft 未输出投影视图：" + path);
        string[] raw = File.ReadAllLines(path, Encoding.ASCII);
        if (raw.Length % 2 != 0) throw new InvalidDataException("SolidCraft 输出了损坏的 DXF 投影视图。");
        var pairs = new List<(int Code, string Value)>(raw.Length / 2);
        for (int i = 0; i < raw.Length; i += 2)
            if (int.TryParse(raw[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int code)) pairs.Add((code, raw[i + 1].Trim()));

        var lines = new List<Line2>(); var polylines = new List<Polyline2>();
        bool inEntities = false;
        for (int i = 0; i < pairs.Count;)
        {
            if (pairs[i].Code == 0 && pairs[i].Value.Equals("SECTION", StringComparison.OrdinalIgnoreCase))
            {
                inEntities = i + 1 < pairs.Count && pairs[i + 1].Code == 2 && pairs[i + 1].Value.Equals("ENTITIES", StringComparison.OrdinalIgnoreCase);
                i += 2; continue;
            }
            if (pairs[i].Code == 0 && pairs[i].Value.Equals("ENDSEC", StringComparison.OrdinalIgnoreCase)) { inEntities = false; i++; continue; }
            if (!inEntities || pairs[i].Code != 0) { i++; continue; }

            string type = pairs[i++].Value;
            int start = i;
            while (i < pairs.Count && pairs[i].Code != 0) i++;
            var entity = pairs.GetRange(start, i - start);
            string layer = entity.FirstOrDefault(x => x.Code == 8).Value ?? "VISIBLE";
            if (type.Equals("LINE", StringComparison.OrdinalIgnoreCase))
            {
                double? x1 = Number(entity, 10), y1 = Number(entity, 20), x2 = Number(entity, 11), y2 = Number(entity, 21);
                if (x1.HasValue && y1.HasValue && x2.HasValue && y2.HasValue) lines.Add(new(new(x1.Value, y1.Value), new(x2.Value, y2.Value), layer));
            }
            else if (type.Equals("LWPOLYLINE", StringComparison.OrdinalIgnoreCase))
            {
                var vertices = new List<Point2>(); var bulges = new List<double>(); double? x = null, y = null; double bulge = 0;
                foreach (var item in entity)
                {
                    if (item.Code == 10)
                    {
                        if (x.HasValue)
                        {
                            if (!y.HasValue) throw new InvalidDataException("DXF 顶点缺少 Y 坐标。");
                            vertices.Add(new(x.Value, y.Value)); bulges.Add(bulge);
                        }
                        x = Parse(item.Value); y = null; bulge = 0;
                    }
                    else if (item.Code == 20 && x.HasValue) y = Parse(item.Value);
                    else if (item.Code == 42 && x.HasValue) bulge = Parse(item.Value);
                }
                if (x.HasValue)
                {
                    if (!y.HasValue) throw new InvalidDataException("DXF 顶点 Y 坐标缺失。");
                    vertices.Add(new(x.Value, y.Value)); bulges.Add(bulge);
                }
                if (vertices.Count >= 2)
                {
                    bool closed = (int.TryParse(entity.FirstOrDefault(p => p.Code == 70).Value, out int flag) && (flag & 1) != 0);
                    var expanded = ExpandBulges(vertices, bulges, closed);
                    polylines.Add(new(expanded, closed, layer));
                }
            }
            else if (type.Equals("ARC", StringComparison.OrdinalIgnoreCase) || type.Equals("CIRCLE", StringComparison.OrdinalIgnoreCase))
            {
                double? cx = Number(entity, 10), cy = Number(entity, 20), radius = Number(entity, 40);
                if (cx.HasValue && cy.HasValue && radius.HasValue && radius.Value > 0)
                {
                    bool circle = type.Equals("CIRCLE", StringComparison.OrdinalIgnoreCase);
                    double arcStart = circle ? 0 : Number(entity, 50) ?? 0, arcEnd = circle ? 360 : Number(entity, 51) ?? 360;
                    var points = SampleArc(new(cx.Value, cy.Value), radius.Value, arcStart, arcEnd, circle);
                    if (points.Count >= 3) polylines.Add(new(points, circle, layer));
                }
            }
        }

        var all = lines.SelectMany(l => new[] { l.A, l.B }).Concat(polylines.SelectMany(p => p.Points)).ToArray();
        if (all.Length == 0) throw new InvalidDataException($"{name} 投影视图没有可绘制的实体，请检查 STEP 文件。");
        var joined = JoinConnectedLines(DeduplicateLines(lines));
        polylines.AddRange(joined.Polylines);
        return new(name, joined.Lines, polylines, new(all.Min(p => p.X), all.Min(p => p.Y), all.Max(p => p.X), all.Max(p => p.Y)));
    }

    static List<Line2> DeduplicateLines(IReadOnlyList<Line2> source)
    {
        const double snap = 0.001;
        var result = new List<Line2>(source.Count);
        var seen = new HashSet<(EndpointKey A, EndpointKey B, string Layer)>();
        EndpointKey Key(Point2 p) => new((long)Math.Round(p.X / snap), (long)Math.Round(p.Y / snap));
        foreach (Line2 line in source)
        {
            EndpointKey a = Key(line.A), b = Key(line.B);
            if (a.X > b.X || (a.X == b.X && a.Y > b.Y)) (a, b) = (b, a);
            if (seen.Add((a, b, line.Layer.ToUpperInvariant()))) result.Add(line);
        }
        return result;
    }

    internal static (List<Line2> Lines, List<Polyline2> Polylines) JoinConnectedLines(IReadOnlyList<Line2> source)
    {
        const double snap = 0.001;
        var isolated = new List<Line2>(); var chains = new List<Polyline2>();
        foreach (var layerGroup in source.GroupBy(line => line.Layer, StringComparer.OrdinalIgnoreCase))
        {
            var nodes = new List<EndpointNode>(); var nodeIds = new Dictionary<EndpointKey, int>(); var edges = new List<ProjectionEdge>();
            int Node(Point2 point)
            {
                var key = new EndpointKey((long)Math.Round(point.X / snap), (long)Math.Round(point.Y / snap));
                if (nodeIds.TryGetValue(key, out int id)) return id;
                id = nodes.Count; nodes.Add(new(point)); nodeIds.Add(key, id); return id;
            }
            foreach (Line2 line in layerGroup)
            {
                int a = Node(line.A), b = Node(line.B);
                if (a == b) { isolated.Add(line); continue; }
                int edge = edges.Count; edges.Add(new(a, b, line.Layer)); nodes[a].Edges.Add(edge); nodes[b].Edges.Add(edge);
            }
            var visited = new bool[edges.Count];
            void Walk(int startNode, int firstEdge)
            {
                var points = new List<Point2> { nodes[startNode].Point }; int current = startNode, edgeId = firstEdge; bool closed = false;
                while (!visited[edgeId])
                {
                    visited[edgeId] = true; ProjectionEdge edge = edges[edgeId]; int next = edge.A == current ? edge.B : edge.A;
                    points.Add(nodes[next].Point);
                    if (next == startNode) { closed = true; break; }
                    if (nodes[next].Edges.Count != 2) break;
                    int nextEdge = nodes[next].Edges[0] == edgeId ? nodes[next].Edges[1] : nodes[next].Edges[0];
                    if (visited[nextEdge]) break;
                    current = next; edgeId = nextEdge;
                }
                if (closed && points.Count > 2 && Distance(points[0], points[^1]) <= snap * 1.5) points.RemoveAt(points.Count - 1);
                if (points.Count == 2) isolated.Add(new(points[0], points[1], layerGroup.Key));
                else if (points.Count > 2) chains.Add(new(points, closed, layerGroup.Key));
            }
            for (int node = 0; node < nodes.Count; node++)
                if (nodes[node].Edges.Count != 2)
                    foreach (int edge in nodes[node].Edges) if (!visited[edge]) Walk(node, edge);
            for (int edge = 0; edge < edges.Count; edge++) if (!visited[edge]) Walk(edges[edge].A, edge);
        }
        return (isolated, chains);
    }

    static double? Number(List<(int Code, string Value)> pairs, int code)
    {
        var value = pairs.FirstOrDefault(p => p.Code == code).Value;
        return value == null ? null : Parse(value);
    }
    static double Parse(string value) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double result) && double.IsFinite(result) ? result : throw new InvalidDataException("DXF 中含有无效坐标。");
    static double Distance(Point2 a, Point2 b) { double dx = a.X - b.X, dy = a.Y - b.Y; return Math.Sqrt(dx * dx + dy * dy); }

    static IReadOnlyList<Point2> ExpandBulges(IReadOnlyList<Point2> vertices, IReadOnlyList<double> bulges, bool closed)
    {
        var result = new List<Point2>();
        int segmentCount = closed ? vertices.Count : vertices.Count - 1;
        for (int i = 0; i < segmentCount; i++)
        {
            Point2 a = vertices[i], b = vertices[(i + 1) % vertices.Count];
            if (result.Count == 0) result.Add(a);
            double bulge = i < bulges.Count ? bulges[i] : 0;
            if (Math.Abs(bulge) < 1e-8) { result.Add(b); continue; }
            double dx = b.X - a.X, dy = b.Y - a.Y, chord = Math.Sqrt(dx * dx + dy * dy);
            double theta = 4 * Math.Atan(bulge), radius = chord * (1 + bulge * bulge) / (4 * Math.Abs(bulge));
            double offset = chord * (1 - bulge * bulge) / (4 * bulge);
            Point2 center = new((a.X + b.X) / 2 - dy / chord * offset, (a.Y + b.Y) / 2 + dx / chord * offset);
            double start = Math.Atan2(a.Y - center.Y, a.X - center.X), maxAngle = 2 * Math.Acos(Math.Clamp(1 - 0.005 / radius, -1, 1));
            int steps = Math.Clamp((int)Math.Ceiling(Math.Abs(theta) / Math.Max(maxAngle, 0.001)), 3, 720);
            for (int step = 1; step <= steps; step++)
            {
                double angle = start + theta * step / steps;
                result.Add(new(center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle)));
            }
        }
        if (!closed && segmentCount == 0) result.AddRange(vertices);
        return result;
    }

    static IReadOnlyList<Point2> SampleArc(Point2 center, double radius, double startDegrees, double endDegrees, bool closed)
    {
        double sweep = endDegrees - startDegrees;
        if (closed) sweep = 360;
        else { while (sweep <= 0) sweep += 360; }
        double maxAngle = 2 * Math.Acos(Math.Clamp(1 - 0.005 / radius, -1, 1));
        int steps = Math.Clamp((int)Math.Ceiling(sweep * Math.PI / 180 / Math.Max(maxAngle, 0.001)), 4, 720);
        int count = closed ? steps : steps + 1;
        var points = new List<Point2>(count);
        for (int i = 0; i < count; i++)
        {
            double angle = (startDegrees + sweep * i / steps) * Math.PI / 180;
            points.Add(new(center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle)));
        }
        return points;
    }
}

internal sealed record DetectedArc(double StartX, double EndX, double CenterX, double Diameter, double Depth, double BaselineY);

internal static class DxfFeatureDetector
{
    static double Distance(double dx, double dy) => Math.Sqrt(dx * dx + dy * dy);

    public static StepFeatureSet Analyze(Projection2 front, Projection2 top)
    {
        var arcs = DetectRepeatedArcs(front);
        double? diameter = arcs.Count > 0 ? Median(arcs.Select(a => a.Diameter)) : null;
        var centers = arcs.OrderBy(a => a.CenterX).Select(a => a.CenterX).ToArray();
        var pitches = centers.Zip(centers.Skip(1), (a, b) => b - a).Where(x => diameter.HasValue && x > diameter.Value * 0.6 && x < diameter.Value * 4).ToArray();
        double? pitch = pitches.Length > 0 ? Median(pitches) : null;
        var frontBreaks = SharpBreaks(front, axisX: true);
        var frontBreakY = SharpBreaks(front, axisX: false);
        var planBreakX = SharpBreaks(top, axisX: true);
        var planBreakY = SharpBreaks(top, axisX: false);
        var slants = ObliqueEdges(top);
        string arcText = arcs.Count > 0 ? $"识别到 {arcs.Count} 处弧槽；Ø{diameter:0.##}" + (pitch.HasValue ? $"；中心距 {pitch.Value:0.##} mm" : "") : "未识别到重复弧槽";
        string slantText = slants.Count > 0 ? $"；斜边投影 {string.Join("、", slants.Select(s => $"{s.Dx:0.##}×{s.Dy:0.##}"))} mm" : "";
        string note = $"{arcText}{slantText}。尺寸由 STEP 投影几何提取。";
        return new(arcs, pitch, diameter, frontBreaks, frontBreakY, planBreakX, planBreakY, slants, note);
    }

    static IReadOnlyList<double> SharpBreaks(Projection2 view, bool axisX)
    {
        var values = new List<double>();
        double span = axisX ? view.Bounds.Width : view.Bounds.Height;
        double tolerance = Math.Max(span * 0.004, 0.5);
        foreach (var poly in view.Polylines)
        {
            for (int i = 1; i < poly.Points.Count - 1; i++)
            {
                Point2 a = poly.Points[i - 1], b = poly.Points[i], c = poly.Points[i + 1];
                double l1 = Distance(b.X - a.X, b.Y - a.Y), l2 = Distance(c.X - b.X, c.Y - b.Y);
                if (l1 < 2 || l2 < 2) continue;
                double dot = ((b.X - a.X) * (c.X - b.X) + (b.Y - a.Y) * (c.Y - b.Y)) / (l1 * l2);
                double turn = Math.Acos(Math.Clamp(dot, -1, 1)) * 180 / Math.PI;
                if (turn < 25 || turn > 155) continue;
                bool nearOutline = Math.Min(Math.Abs(b.X - view.Bounds.MinX), Math.Abs(view.Bounds.MaxX - b.X)) < view.Bounds.Width * 0.18 ||
                                   Math.Min(Math.Abs(b.Y - view.Bounds.MinY), Math.Abs(view.Bounds.MaxY - b.Y)) < view.Bounds.Height * 0.18;
                if (nearOutline) values.Add(axisX ? b.X : b.Y);
            }
        }
        double lo = axisX ? view.Bounds.MinX : view.Bounds.MinY, hi = axisX ? view.Bounds.MaxX : view.Bounds.MaxY;
        return values.Where(v => v > lo + tolerance && v < hi - tolerance).Order().Aggregate(new List<double>(), (list, value) =>
        {
            if (list.Count == 0 || value - list[^1] > tolerance * 2) list.Add(value);
            return list;
        });
    }

    static IReadOnlyList<ObliqueProjection> ObliqueEdges(Projection2 view)
    {
        var result = new List<ObliqueProjection>();
        foreach (var poly in view.Polylines)
        {
            for (int i = 1; i < poly.Points.Count; i++)
            {
                Point2 a = poly.Points[i - 1], b = poly.Points[i];
                double dx = Math.Abs(b.X - a.X), dy = Math.Abs(b.Y - a.Y);
                if (dx < 2 || dy < 2 || Distance(dx, dy) < 5) continue;
                bool nearCorner = (Math.Min(Math.Abs(a.X - view.Bounds.MinX), Math.Abs(view.Bounds.MaxX - a.X)) < view.Bounds.Width * 0.18 ||
                                   Math.Min(Math.Abs(b.X - view.Bounds.MinX), Math.Abs(view.Bounds.MaxX - b.X)) < view.Bounds.Width * 0.18) &&
                                  (Math.Min(Math.Abs(a.Y - view.Bounds.MinY), Math.Abs(view.Bounds.MaxY - a.Y)) < view.Bounds.Height * 0.18 ||
                                   Math.Min(Math.Abs(b.Y - view.Bounds.MinY), Math.Abs(view.Bounds.MaxY - b.Y)) < view.Bounds.Height * 0.18);
                if (!nearCorner) continue;
                if (!result.Any(x => Math.Abs(x.Dx - dx) < 0.5 && Math.Abs(x.Dy - dy) < 0.5)) result.Add(new(dx, dy));
            }
        }
        return result.OrderBy(x => x.Dx).ThenBy(x => x.Dy).Take(4).ToArray();
    }

    public static IReadOnlyList<DetectedArc> DetectRepeatedArcs(Projection2 view)
    {
        var result = new List<DetectedArc>();
        double tolerance = Math.Max(view.Bounds.Height * 0.002, 0.03);
        foreach (var poly in view.Polylines)
        {
            if (poly.Points.Count < 4) continue;
            foreach (double baseline in new[] { poly.Points.Max(p => p.Y), poly.Points.Min(p => p.Y) })
            {
                bool active = false; double x0 = 0, low = 0; double prevX = 0;
                for (int i = 0; i < poly.Points.Count; i++)
                {
                    Point2 p = poly.Points[i];
                    bool onBase = Math.Abs(p.Y - baseline) <= tolerance;
                    if (!active && !onBase && i > 0 && Math.Abs(poly.Points[i - 1].Y - baseline) <= tolerance) { active = true; x0 = prevX; low = p.Y; }
                    if (active && !onBase) low = baseline == poly.Points.Max(q => q.Y) ? Math.Min(low, p.Y) : Math.Max(low, p.Y);
                    if (active && onBase)
                    {
                        double span = Math.Abs(p.X - x0), depth = Math.Abs(baseline - low);
                        if (span >= view.Bounds.Width * 0.035 && span <= view.Bounds.Width * 0.38 && depth >= span * 0.3 && depth <= span * 0.72)
                            result.Add(new(Math.Min(x0, p.X), Math.Max(x0, p.X), (x0 + p.X) / 2, (span + 2 * depth) / 2, depth, baseline));
                        active = false;
                    }
                    prevX = p.X;
                }
            }
        }
        var unique = new List<DetectedArc>();
        foreach (var item in result.OrderBy(a => a.CenterX).ThenBy(a => a.Diameter))
            if (!unique.Any(other => Math.Abs(other.CenterX - item.CenterX) <= view.Bounds.Width * 0.012 && Math.Abs(other.Diameter - item.Diameter) <= Math.Max(item.Diameter, other.Diameter) * 0.15)) unique.Add(item);
        return unique;
    }

    static double Median(IEnumerable<double> source)
    {
        var values = source.Order().ToArray();
        return values.Length == 0 ? 0 : values.Length % 2 == 1 ? values[values.Length / 2] : (values[values.Length / 2 - 1] + values[values.Length / 2]) / 2;
    }
}
internal static partial class MachiningDxf
{
    sealed class Writer(string path, DrawingOptions options) : IDisposable
    {
        readonly netDxf.DxfDocument document = new(netDxf.Header.DxfVersion.AutoCad2010) { BuildDimensionBlocks = true };
        readonly Dictionary<string, netDxf.Tables.Layer> layers = new();
        netDxf.Tables.TextStyle titleStyle = null!;
        public bool SuppressPlanTongueDetail { get; set; }
        public DrawingOptions Options { get; } = options;
        public DrawingResources Resources { get; } = DrawingResources.Load(options);
        Scene? recording;
        double offsetX,offsetY;
        public double DimensionTextHeight => Resources.TextHeight;
        public double DimensionRow => Math.Max(DimensionTextHeight*2.4,Resources.Style.DimBaselineSpacing*Resources.Scale);
        public double ArrowSize => Resources.Style.ArrowSize*Resources.Scale;
        public double ExtensionGap => Resources.Style.ExtLineOffset*Resources.Scale;
        public double ExtensionBeyond => Resources.Style.ExtLineExtend*Resources.Scale;
        public double TextGap => Resources.Style.TextOffset*Resources.Scale;
        public string FormatDimension(double value)
        {
            double round=Resources.Style.DimRoundoff;
            if(round>0)value=Math.Round(value/round,MidpointRounding.AwayFromZero)*round;
            return value.ToString("0."+new string('#',Math.Clamp((int)Resources.Style.LengthPrecision,0,8)),CultureInfo.InvariantCulture).TrimEnd('.');
        }
        public void SetOffset(double x,double y){offsetX=x;offsetY=y;}
        public Scene Record(Action draw)
        {
            Scene? previous=recording;var scene=new Scene();recording=scene;
            try{draw();}finally{recording=previous;}return scene;
        }
        public void Initialize()
        {
            titleStyle=new netDxf.Tables.TextStyle("SC_TITLE","gbenor.shx"){BigFont="gbcbig.shx"};
            document.DrawingVariables.InsUnits=netDxf.Units.DrawingUnits.Millimeters;
            var hiddenType=new netDxf.Tables.Linetype("SC_HIDDEN",new netDxf.Tables.LinetypeSegment[]{new netDxf.Tables.LinetypeSimpleSegment(6),new netDxf.Tables.LinetypeSimpleSegment(-3)});
            foreach(var(name,color)in new[]{("VISIBLE",(short)7),("HIDDEN",(short)7),("DIM",(short)4),("TEXT",(short)3),("TITLE",(short)7),("FRAME",(short)7)})
            {
                var layer=new netDxf.Tables.Layer(name){Color=new netDxf.AciColor(color)};
                if(name=="HIDDEN")layer.Linetype=hiddenType;
                layers.Add(name,layer);document.Layers.Add(layer);
            }
            document.TextStyles.Add(titleStyle);document.DimensionStyles.Add(Resources.Style);
        }
        public void Dispose()
        {
            if(!document.Save(path))throw new IOException("无法保存 DXF 2010 文件："+path);
            Dxf2010Compatibility.NormalizeRasterOwner(path);
        }
        netDxf.Vector2 World2(Point2 p)=>new(p.X+offsetX,p.Y+offsetY);
        netDxf.Vector3 World3(Point2 p)=>new(p.X+offsetX,p.Y+offsetY,0);
        void Add(netDxf.Entities.EntityObject entity,string layer)
        {
            entity.Layer=layers[layer];if(layer=="HIDDEN")entity.LinetypeScale=Options.HiddenLineScale;
            document.Entities.Add(entity);
        }
        public void LinearDimension(Point2 a,Point2 b,Point2 line,Point2 text,double rotation,Action drawGraphics)
        {
            // Omit only the unwanted 1.11/1.26 mm plan details, without merging
            // neighboring dimensions or changing the projected geometry.
            double measured=rotation==0 ? Math.Abs(b.X-a.X) : Math.Abs(b.Y-a.Y);
            if(SuppressPlanTongueDetail && (FormatDimension(measured) is "1.11" or "1.26"))return;
            if(recording!=null)
            {
                Scene parent=recording;Scene graphics=Record(drawGraphics);Box2 box=graphics.Bounds;
                double pad=Math.Max(ArrowSize,ExtensionBeyond);
                parent.Add((x,y)=>{SetOffset(x,y);LinearDimension(a,b,line,text,rotation,drawGraphics);},new(box.MinX-pad,box.MinY-pad,box.MaxX+pad,box.MaxY+pad));return;
            }
            double distance=rotation==0 ? line.Y-a.Y : a.X-line.X;
            var dimension=new netDxf.Entities.LinearDimension(World2(a),World2(b),distance,rotation,Resources.Style)
            { UserText="<>",TextReferencePoint=World2(text) };
            Add(dimension,"DIM");
        }
        public void DiameterDimension(Point2 center,double radius,Point2 text)
        {
            if(recording!=null)
            {
                Box2 b=Union([new(center.X-radius,center.Y-radius,center.X+radius,center.Y+radius),TextBounds("Ø"+FormatDimension(radius*2),text,DimensionTextHeight,0)]);
                recording.Add((x,y)=>{SetOffset(x,y);DiameterDimension(center,radius,text);},b);return;
            }
            var dimension=new netDxf.Entities.DiametricDimension(World2(center),World2(new(center.X+radius,center.Y)),Resources.Style)
            {UserText="<>",TextReferencePoint=World2(text)};
            Add(dimension,"DIM");
        }
        public void Line(Point2 a,Point2 b,string layer="DIM")
        {
            if(Distance(a,b)<0.00001)return;
            if(recording!=null){recording.Add((x,y)=>{SetOffset(x,y);Line(a,b,layer);},DrawingResources.BoundsOf([a,b]));return;}
            Add(new netDxf.Entities.Line(World3(a),World3(b)),layer);
        }
        public void Arc(Point2 center,double radius,double startDegrees,double endDegrees,string layer)
        {
            if(recording!=null){recording.Add((x,y)=>{SetOffset(x,y);Arc(center,radius,startDegrees,endDegrees,layer);},new(center.X-radius,center.Y-radius,center.X+radius,center.Y+radius));return;}
            static double N(double a)=>(a%360+360)%360;
            Add(new netDxf.Entities.Arc(World3(center),radius,N(startDegrees),N(endDegrees)),layer);
        }
        public void Polyline(IReadOnlyList<Point2> points,bool closed,string layer)
        {
            if(points.Count<2)return;
            if(layer!="HIDDEN"){for(int i=1;i<points.Count;i++)Line(points[i-1],points[i],layer);if(closed)Line(points[^1],points[0],layer);return;}
            if(recording!=null){recording.Add((x,y)=>{SetOffset(x,y);Polyline(points,closed,layer);},DrawingResources.BoundsOf(points));return;}
            var poly=new netDxf.Entities.Polyline2D(points.Select(World2),closed){LinetypeGeneration=true};Add(poly,layer);
        }
        public void Text(string value,Point2 at,double height,string layer="TEXT",double rotation=0)
        {
            if(recording!=null){recording.Add((x,y)=>{SetOffset(x,y);Text(value,at,height,layer,rotation);},TextBounds(value,at,height,rotation));return;}
            var style=layer=="TEXT" ? Resources.Style.TextStyle : titleStyle;
            Add(new netDxf.Entities.Text(value.Replace("\r"," ").Replace("\n"," "),World3(at),height,style)
            {Alignment=netDxf.Entities.TextAlignment.MiddleCenter,Rotation=rotation},layer);
        }
        void ApplyFrameFonts(netDxf.Entities.Insert insert,HashSet<netDxf.Blocks.Block> visited)
        {
            foreach(var attribute in insert.Attributes)attribute.Style=titleStyle;
            if(!visited.Add(insert.Block))return;
            foreach(var definition in insert.Block.AttributeDefinitions.Values)definition.Style=titleStyle;
            foreach(var entity in insert.Block.Entities)
            {
                switch(entity)
                {
                    case netDxf.Entities.Text text:text.Style=titleStyle;break;
                    case netDxf.Entities.MText text:
                        text.Style=titleStyle;
                        text.Value=System.Text.RegularExpressions.Regex.Replace(text.Value,@"\\[fF][^;]*;","");
                        break;
                    case netDxf.Entities.Insert child:ApplyFrameFonts(child,visited);break;
                }
            }
        }
        public void DrawFrame(double sheetY,double factor,double width,double height,string name,int sequence)
        {
            SetOffset(0,sheetY);
            if(Resources.Frame is FrameSource frame)
            {
                foreach(var reference in frame.Components)
                {
                    var insert=(netDxf.Entities.Insert)reference.Clone();
                    ApplyFrameFonts(insert,new HashSet<netDxf.Blocks.Block>());
                    ApplyTitleAttributes(insert,name,sequence,new HashSet<netDxf.Blocks.Block>());
                    insert.TransformBy(netDxf.Matrix3.Scale(new netDxf.Vector3(factor,factor,factor)),
                        new netDxf.Vector3(-frame.Boundary.MinX*factor,-frame.Boundary.MinY*factor+sheetY,0));
                    document.Entities.Add(insert);
                }
                return;
            }
            double m=20*factor,band=135*factor;
            Polyline([new(0,0),new(width,0),new(width,height),new(0,height)],true,"FRAME");
            Polyline([new(m,m),new(width-m,m),new(width-m,height-m),new(m,height-m)],true,"FRAME");
            Line(new(m,band),new(width-m,band),"FRAME");
            Text(name,new(width*.5,band*.6),DimensionTextHeight,"TITLE");
        }

        bool ApplyTitleAttributes(netDxf.Entities.Insert insert,string name,int sequence,HashSet<netDxf.Blocks.Block> visited)
        {
            bool perPart=false;
            string? Replacement(string tag,object? original)
            {
                var configured=Options.TitleBlockAttributes?.FirstOrDefault(p=>p.Key.Equals(tag,StringComparison.OrdinalIgnoreCase)).Value;
                if(!string.IsNullOrWhiteSpace(configured))return configured;
                string value=Convert.ToString(original,CultureInfo.InvariantCulture)??"";
                bool automatic=tag.Contains("图名",StringComparison.Ordinal)||tag.Equals("TITLE",StringComparison.OrdinalIgnoreCase)||
                    System.Text.RegularExpressions.Regex.IsMatch(value,@"^辊密封\d+$");
                perPart|=automatic;return automatic?name:null;
            }
            foreach(var attribute in insert.Attributes)
                if(Replacement(attribute.Tag,attribute.Value) is string value)attribute.Value=value;
            if(!visited.Add(insert.Block))return perPart;
            foreach(var definition in insert.Block.AttributeDefinitions.Values)
                if(Replacement(definition.Tag,definition.Value) is string value)definition.Value=value;
            foreach(var child in insert.Block.Entities.OfType<netDxf.Entities.Insert>())perPart|=ApplyTitleAttributes(child,name,sequence,visited);
            // DXF shares blocks by name. Per-sheet names keep nested/constant titles from reusing the first part's text.
            if(perPart)insert.Block.Name+="_SHEET_"+sequence;
            return perPart;
        }
    }

    sealed record Map(Projection2 Projection, double Dx, double Dy)
    {
        public Point2 P(Point2 p) => new(p.X + Dx, p.Y + Dy);
        public Point2 At(double x, double y) => new(x + Dx, y + Dy);
        public Point2 Min => At(Projection.Bounds.MinX, Projection.Bounds.MinY);
        public Point2 Max => At(Projection.Bounds.MaxX, Projection.Bounds.MaxY);
        public double Width => Projection.Bounds.Width;
        public double Height => Projection.Bounds.Height;
    }
    sealed record ArcPath(Point2 Center, double Radius, double StartDegrees, double SweepDegrees, string Layer);

    public static void Write(string path,StepDrawingPage page,DrawingOptions? options=null)=>WriteCombined(path,[page],options);
    public static void WriteCombined(string path,IReadOnlyList<StepDrawingPage> pages,DrawingOptions? options=null)
    {
        if(pages.Count==0)throw new InvalidDataException("没有可写入汇总图的 STEP 加工图。");
        using var writer=new Writer(path,options??new DrawingOptions());
        Header(writer);RenderSheets(writer,pages);
    }

    static void Header(Writer w) => w.Initialize();

    static List<ArcPath> DrawView(Writer w, Map map)
    {
        var fittedArcs = new List<ArcPath>();
        var linework = new List<Line2>(map.Projection.Lines.Count);
        foreach (Polyline2 poly in map.Projection.Polylines)
        {
            var points = poly.Points.Select(map.P).ToList();
            if (poly.Closed && points.Count > 2) points.Add(points[0]);
            var converted = DrawCurvePolyline(w, points, IsHidden(poly.Layer) ? "HIDDEN" : "VISIBLE");
            fittedArcs.AddRange(converted.Arcs);
            linework.AddRange(converted.Lines);
        }
        foreach (Line2 line in map.Projection.Lines)
        {
            string renderedLayer = IsHidden(line.Layer) ? "HIDDEN" : "VISIBLE";
            linework.Add(new(map.P(line.A), map.P(line.B), renderedLayer));
        }
        var reconstructed = ReconstructHiddenArcs(linework, fittedArcs);
        foreach (ArcPath arc in reconstructed.Arcs)
        {
            w.Arc(arc.Center, arc.Radius, arc.StartDegrees, arc.StartDegrees + arc.SweepDegrees, arc.Layer);
            fittedArcs.Add(arc);
        }
        var hidden=new List<Line2>();
        for(int i=0;i<linework.Count;i++)
        {
            if(reconstructed.ConsumedLines.Contains(i))continue;
            Line2 line=linework[i];if(IsCoveredByArc(line,fittedArcs))continue;
            if(IsHidden(line.Layer))hidden.Add(line);else w.Line(line.A,line.B,line.Layer);
        }
        var joined=DxfProjectionReader.JoinConnectedLines(hidden);
        foreach(var line in joined.Lines)w.Line(line.A,line.B,line.Layer);
        foreach(var poly in joined.Polylines)w.Polyline(poly.Points,poly.Closed,poly.Layer);
        return fittedArcs;
    }

    static (List<ArcPath> Arcs, HashSet<int> ConsumedLines) ReconstructHiddenArcs(
        IReadOnlyList<Line2> lines, IReadOnlyList<ArcPath> referenceArcs)
    {
        // SolidCraft exports some hidden circular edges as many short LINE entities.
        // Use the fitted visible arc as a geometric guide, fit a circle to the hidden
        // fragments, and emit one HIDDEN ARC for each well-supported arc span.
        var reconstructed = new List<ArcPath>();
        var consumed = new HashSet<int>();
        foreach (ArcPath reference in referenceArcs
                     .Where(arc => arc.Layer == "VISIBLE" && arc.Radius >= 1 && arc.SweepDegrees >= 6)
                     .OrderByDescending(arc => arc.SweepDegrees))
        {
            var candidates = new List<int>();
            var fitPoints = new List<Point2>();
            var relativeAngles = new List<double>();
            for (int i = 0; i < lines.Count; i++)
            {
                if (consumed.Contains(i)) continue;
                Line2 line = lines[i];
                if (line.Layer != "HIDDEN") continue;
                double length = Distance(line.A, line.B);
                if (length < 0.0001 || length > 3.0) continue;

                Point2 midpoint = new((line.A.X + line.B.X) / 2, (line.A.Y + line.B.Y) / 2);
                double relative = RelativeAngle(Angle(reference.Center, midpoint), reference.StartDegrees);
                if (relative > reference.SweepDegrees + 0.25) continue;
                if (Math.Abs(Distance(midpoint, reference.Center) - reference.Radius) > 1.5 ||
                    Math.Abs(Distance(line.A, reference.Center) - reference.Radius) > 1.5 ||
                    Math.Abs(Distance(line.B, reference.Center) - reference.Radius) > 1.5) continue;

                candidates.Add(i);
                fitPoints.Add(line.A);
                fitPoints.Add(line.B);
                relativeAngles.Add(relative);
            }

            if (candidates.Count < 8) continue;
            double coveredSpan = relativeAngles.Max() - relativeAngles.Min();
            if (coveredSpan < Math.Max(5.0, reference.SweepDegrees * 0.55) ||
                coveredSpan > reference.SweepDegrees + 5.0) continue;

            Point2 center;
            double radius;
            if (reference.SweepDegrees >= 20)
            {
                if (!TryFitCircle(fitPoints, reference.Center, out center, out radius)) continue;
            }
            else
            {
                // A very short arc does not provide enough angular spread for a
                // stable center fit. Keep the reference center and fit its radius.
                center = reference.Center;
                radius = fitPoints.Average(point => Distance(point, center));
            }
            if (Distance(center, reference.Center) > 1.5 || Math.Abs(radius - reference.Radius) > 1.5) continue;

            var inliers = fitPoints.Where(point => Math.Abs(Distance(point, center) - radius) <= 0.05).ToList();
            if (inliers.Count < fitPoints.Count * 0.9 || inliers.Count < 16 ||
                inliers.Average(point => Math.Pow(Distance(point, center) - radius, 2)) > 0.0004) continue;
            if (reference.SweepDegrees >= 20 && !TryFitCircle(inliers, reference.Center, out center, out radius)) continue;
            if (Distance(center, reference.Center) > 1.5 || Math.Abs(radius - reference.Radius) > 1.5) continue;
            if (inliers.Any(point => Math.Abs(Distance(point, center) - radius) > 0.05)) continue;

            reconstructed.Add(new(center, radius, reference.StartDegrees, reference.SweepDegrees, "HIDDEN"));
            foreach (int index in candidates)
            {
                Line2 line = lines[index];
                Point2 midpoint = new((line.A.X + line.B.X) / 2, (line.A.Y + line.B.Y) / 2);
                double relative = RelativeAngle(Angle(reference.Center, midpoint), reference.StartDegrees);
                if (relative <= reference.SweepDegrees + 0.25 &&
                    Math.Abs(Distance(midpoint, center) - radius) <= 0.05 &&
                    Math.Abs(Distance(line.A, center) - radius) <= 0.05 &&
                    Math.Abs(Distance(line.B, center) - radius) <= 0.05)
                    consumed.Add(index);
            }
        }
        return (reconstructed, consumed);
    }

    static bool TryFitCircle(IReadOnlyList<Point2> points, Point2 origin, out Point2 center, out double radius)
    {
        center = origin;
        radius = 0;
        if (points.Count < 3) return false;
        double xx = 0, xy = 0, x = 0, yy = 0, y = 0, n = points.Count;
        double xq = 0, yq = 0, q = 0;
        foreach (Point2 point in points)
        {
            double px = point.X - origin.X, py = point.Y - origin.Y;
            double squared = px * px + py * py;
            xx += px * px; xy += px * py; x += px;
            yy += py * py; y += py;
            xq -= px * squared; yq -= py * squared; q -= squared;
        }
        double[,] matrix = { { xx, xy, x }, { xy, yy, y }, { x, y, n } };
        double[] rhs = [xq, yq, q];
        if (!Solve3x3(matrix, rhs, out double[] solution)) return false;
        double offsetX = -solution[0] / 2, offsetY = -solution[1] / 2;
        double radiusSquared = offsetX * offsetX + offsetY * offsetY - solution[2];
        if (!double.IsFinite(radiusSquared) || radiusSquared <= 0) return false;
        center = new(origin.X + offsetX, origin.Y + offsetY);
        radius = Math.Sqrt(radiusSquared);
        return double.IsFinite(radius) && radius >= 1 && radius <= 100000;
    }

    static bool Solve3x3(double[,] matrix, double[] rhs, out double[] solution)
    {
        solution = new double[3];
        for (int column = 0; column < 3; column++)
        {
            int pivot = column;
            for (int row = column + 1; row < 3; row++)
                if (Math.Abs(matrix[row, column]) > Math.Abs(matrix[pivot, column])) pivot = row;
            if (Math.Abs(matrix[pivot, column]) < 1e-12) return false;
            if (pivot != column)
            {
                for (int j = column; j < 3; j++) (matrix[column, j], matrix[pivot, j]) = (matrix[pivot, j], matrix[column, j]);
                (rhs[column], rhs[pivot]) = (rhs[pivot], rhs[column]);
            }
            double divisor = matrix[column, column];
            for (int j = column; j < 3; j++) matrix[column, j] /= divisor;
            rhs[column] /= divisor;
            for (int row = 0; row < 3; row++)
            {
                if (row == column) continue;
                double factor = matrix[row, column];
                for (int j = column; j < 3; j++) matrix[row, j] -= factor * matrix[column, j];
                rhs[row] -= factor * rhs[column];
            }
        }
        Array.Copy(rhs, solution, 3);
        return solution.All(double.IsFinite);
    }

    static double RelativeAngle(double angle, double start) => (angle - start + 360) % 360;

    static (List<ArcPath> Arcs, List<Line2> Lines) DrawCurvePolyline(Writer w, IReadOnlyList<Point2> points, string layer)
    {
        var fitted = new List<ArcPath>();
        var lines = new List<Line2>();
        int i = 0;
        while (i < points.Count - 1)
        {
            if (i + 2 < points.Count && TryFitArc(points, i, out int end, out Point2 center, out double radius, out bool counterClockwise))
            {
                double a = Angle(center, points[i]), b = Angle(center, points[end]);
                if (counterClockwise) w.Arc(center, radius, a, b, layer);
                else w.Arc(center, radius, b, a, layer);
                double start = counterClockwise ? a : b, finish = counterClockwise ? b : a;
                double sweep = (finish - start + 360) % 360;
                fitted.Add(new(center, radius, start, sweep, layer));
                i = end;
            }
            else
            {
                lines.Add(new(points[i], points[i + 1], layer));
                i++;
            }
        }
        return (fitted, lines);
    }

    static bool IsCoveredByArc(Line2 line, IReadOnlyList<ArcPath> arcs)
    {
        const double radialTolerance = 0.025;
        double dx = line.B.X - line.A.X, dy = line.B.Y - line.A.Y;
        double length = Math.Sqrt(dx * dx + dy * dy);
        Point2[] samples = [line.A, new((line.A.X + line.B.X) / 2, (line.A.Y + line.B.Y) / 2), line.B];
        foreach (ArcPath arc in arcs)
        {
            bool sameLayer = string.Equals(line.Layer, arc.Layer, StringComparison.OrdinalIgnoreCase);
            bool hiddenEdgeCoveredByVisibleArc = line.Layer == "HIDDEN" && arc.Layer == "VISIBLE";
            if (!sameLayer && !hiddenEdgeCoveredByVisibleArc) continue;
            double minX = Math.Min(line.A.X, line.B.X), maxX = Math.Max(line.A.X, line.B.X);
            double minY = Math.Min(line.A.Y, line.B.Y), maxY = Math.Max(line.A.Y, line.B.Y);
            if (maxX < arc.Center.X - arc.Radius - radialTolerance || minX > arc.Center.X + arc.Radius + radialTolerance ||
                maxY < arc.Center.Y - arc.Radius - radialTolerance || minY > arc.Center.Y + arc.Radius + radialTolerance) continue;

            double angleMargin = Math.Max(1.5, Math.Asin(Math.Clamp(length / (2 * arc.Radius), 0, 1)) * 180 / Math.PI + 0.5);
            bool covered = true;
            bool extendsPastArcEndpoint = false;
            foreach (Point2 point in samples)
            {
                double radialError = Math.Abs(Distance(point, arc.Center) - arc.Radius);
                double angle = Angle(arc.Center, point);
                double relative = (angle - arc.StartDegrees + 360) % 360;
                double beforeStart = (arc.StartDegrees - angle + 360) % 360;
                double afterEnd = (angle - arc.StartDegrees - arc.SweepDegrees + 720) % 360;
                bool insideArc = relative <= arc.SweepDegrees;
                bool nearArc = insideArc || beforeStart <= angleMargin || afterEnd <= angleMargin;
                if (!insideArc && (beforeStart <= angleMargin || afterEnd <= angleMargin)) extendsPastArcEndpoint = true;
                if (radialError > radialTolerance || !nearArc) { covered = false; break; }
            }
            if (covered && (sameLayer || (hiddenEdgeCoveredByVisibleArc && extendsPastArcEndpoint))) return true;
        }
        return false;
    }

    static bool TryFitArc(IReadOnlyList<Point2> points, int start, out int end, out Point2 center, out double radius, out bool counterClockwise)
    {
        end = start; center = new(0, 0); radius = 0; counterClockwise = true;
        Point2 a = points[start], b = points[start + 1], c = points[start + 2];
        double determinant = 2 * (a.X * (b.Y - c.Y) + b.X * (c.Y - a.Y) + c.X * (a.Y - b.Y));
        double scale = Math.Max(1, Math.Max(Math.Abs(a.X) + Math.Abs(a.Y), Math.Max(Math.Abs(b.X) + Math.Abs(b.Y), Math.Abs(c.X) + Math.Abs(c.Y))));
        if (Math.Abs(determinant) < scale * scale * 1e-9) return false;
        double aa = a.X * a.X + a.Y * a.Y, bb = b.X * b.X + b.Y * b.Y, cc = c.X * c.X + c.Y * c.Y;
        center = new((aa * (b.Y - c.Y) + bb * (c.Y - a.Y) + cc * (a.Y - b.Y)) / determinant,
            (aa * (c.X - b.X) + bb * (a.X - c.X) + cc * (b.X - a.X)) / determinant);
        radius = Distance(center, a);
        if (!double.IsFinite(radius) || radius < 1 || radius > 100000) return false;
        double cross = Cross(a, b, c);
        if (Math.Abs(cross) < 1e-8) return false;
        counterClockwise = cross > 0;
        double tolerance = Math.Clamp(radius * 0.00015, 0.012, 0.035);
        double firstTurn = SignedAngle(a, b, center), secondTurn = SignedAngle(b, c, center);
        if ((counterClockwise && (firstTurn <= 0 || secondTurn <= 0)) || (!counterClockwise && (firstTurn >= 0 || secondTurn >= 0))) return false;
        double sweep = firstTurn + secondTurn;
        end = start + 2;
        for (int k = start + 3; k < points.Count; k++)
        {
            if (Math.Abs(Distance(center, points[k]) - radius) > tolerance) break;
            double step = SignedAngle(points[k - 1], points[k], center);
            if ((counterClockwise && step <= 0) || (!counterClockwise && step >= 0) || Math.Abs(step) > 30) break;
            if (Math.Abs(sweep + step) > 170) break;
            sweep += step;
            end = k;
        }
        if (Math.Abs(sweep) < 8 || end - start < 4) { end = start; return false; }
        return true;
    }

    static double Distance(Point2 a, Point2 b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    static double Cross(Point2 a, Point2 b, Point2 c) => (b.X - a.X) * (c.Y - b.Y) - (b.Y - a.Y) * (c.X - b.X);
    static double SignedAngle(Point2 a, Point2 b, Point2 center) => Math.Atan2((a.X - center.X) * (b.Y - center.Y) - (a.Y - center.Y) * (b.X - center.X), (a.X - center.X) * (b.X - center.X) + (a.Y - center.Y) * (b.Y - center.Y)) * 180 / Math.PI;
    static double Angle(Point2 center, Point2 p) { double degrees = Math.Atan2(p.Y - center.Y, p.X - center.X) * 180 / Math.PI; return degrees < 0 ? degrees + 360 : degrees; }

    static void Horizontal(Writer w, double x1, double x2, double featureY, double dimY, string label)
    {
        if (x2 < x1) (x1, x2) = (x2, x1);
        if (x2 - x1 < 0.05) return;
        double direction = Math.Sign(dimY - featureY);
        Point2 text = new((x1 + x2) / 2, dimY + w.DimensionTextHeight / 2 + w.TextGap);
        w.LinearDimension(new(x1, featureY), new(x2, featureY), new(x2, dimY), text, 0, () =>
        {
            w.Line(new(x1, featureY + direction*w.ExtensionGap), new(x1, dimY + direction*w.ExtensionBeyond), "DIM");
            w.Line(new(x2, featureY + direction*w.ExtensionGap), new(x2, dimY + direction*w.ExtensionBeyond), "DIM");
            w.Line(new(x1, dimY), new(x2, dimY), "DIM");
            double size = Math.Min(w.ArrowSize, (x2 - x1) / 3);
            Arrow(w, new(x1, dimY), new(x2, dimY), size); Arrow(w, new(x2, dimY), new(x1, dimY), size);
            w.Text(label, text, w.DimensionTextHeight);
        });
    }

    static void Vertical(Writer w, double featureX, double dimX, double y1, double y2, string label)
    {
        if (y2 < y1) (y1, y2) = (y2, y1);
        if (y2 - y1 < 0.05) return;
        double direction = Math.Sign(dimX - featureX);
        Point2 text = new(dimX - w.DimensionTextHeight / 2 - w.TextGap, (y1 + y2) / 2);
        w.LinearDimension(new(featureX, y1), new(featureX, y2), new(dimX, y2), text, 90, () =>
        {
            w.Line(new(featureX + direction*w.ExtensionGap, y1), new(dimX + direction*w.ExtensionBeyond, y1), "DIM");
            w.Line(new(featureX + direction*w.ExtensionGap, y2), new(dimX + direction*w.ExtensionBeyond, y2), "DIM");
            w.Line(new(dimX, y1), new(dimX, y2), "DIM");
            double size = Math.Min(w.ArrowSize, (y2 - y1) / 3);
            Arrow(w, new(dimX, y1), new(dimX, y2), size); Arrow(w, new(dimX, y2), new(dimX, y1), size);
            w.Text(label, text, w.DimensionTextHeight, rotation: 90);
        });
    }

    static void DrawHorizontalChain(Writer w, Map map, double[] marks, double y)
    {
        int[] lanes=ChainLanes(w,marks);
        for(int i=1;i<marks.Length;i++)
        {
            double a=map.Dx+marks[i-1],b=map.Dx+marks[i];
            if(b-a>.2)Horizontal(w,a,b,map.Max.Y,y+lanes[i-1]*w.DimensionRow,w.FormatDimension(marks[i]-marks[i-1]));
        }
    }

    static void DrawVerticalChain(Writer w, Map map, double[] marks, double x)
    {
        int[] lanes=ChainLanes(w,marks);
        for(int i=1;i<marks.Length;i++)
        {
            double a=map.Dy+marks[i-1],b=map.Dy+marks[i];
            if(b-a>.2)Vertical(w,map.Max.X,x+lanes[i-1]*w.DimensionRow,a,b,w.FormatDimension(marks[i]-marks[i-1]));
        }
    }

    static double[] IncludeEnds(IReadOnlyList<double> inner, double min, double max) => new[] { min }.Concat(inner.Where(v => v > min + 0.05 && v < max - 0.05)).Append(max).Order().Aggregate(new List<double>(), (list, v) => { if (list.Count == 0 || v - list[^1] > 0.15) list.Add(v); return list; }).ToArray();

    static void Arrow(Writer w, Point2 tip, Point2 toward, double size)
    {
        double dx = toward.X - tip.X, dy = toward.Y - tip.Y, length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 0.0001) return;
        dx /= length; dy /= length; double px = -dy, py = dx, wing = size * 0.42;
        w.Line(tip, new(tip.X + dx * size + px * wing, tip.Y + dy * size + py * wing), "DIM");
        w.Line(tip, new(tip.X + dx * size - px * wing, tip.Y + dy * size - py * wing), "DIM");
    }

    static void Footer(Writer w, string name, double x, double y)
    {
        w.Line(new(x, y + 7), new(x + 90, y + 7), "TITLE");
        w.Text("PART: " + name, new(x + 2, y + 2), 2.8, "TITLE");
        w.Text("UNITS: mm   SOURCE: STEP", new(x + 2, y - 4), 2.8, "TITLE");
    }

    static bool IsHidden(string layer) => layer.Contains("HIDDEN", StringComparison.OrdinalIgnoreCase);
    static string F(double value) => value.ToString("0.#####", CultureInfo.InvariantCulture);
    static string Fmt(double value) => Math.Abs(value - Math.Round(value)) < 0.005 ? Math.Round(value).ToString("0", CultureInfo.InvariantCulture) : value.ToString("0.##", CultureInfo.InvariantCulture);
    static string Escape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
    static Encoding DxfEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(936, EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback);
    }
}
