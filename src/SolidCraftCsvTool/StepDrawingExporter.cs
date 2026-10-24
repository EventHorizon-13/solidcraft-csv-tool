using System.Globalization;
using System.Text;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

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
    IReadOnlyList<double> FrontBreaks, IReadOnlyList<double> PlanBreakX, IReadOnlyList<double> PlanBreakY,
    IReadOnlyList<ObliqueProjection> PlanSlants, string Note);
internal sealed record StepDrawingPage(string Name, Projection2 Front, Projection2 Top, Projection2 End, StepFeatureSet Features);

public static class StepDrawingExporter
{
    public static async Task RunAsync(IReadOnlyList<string> inputFiles, string outputPdf, int port,
        IProgress<ExportProgress> progress, CancellationToken cancellation)
    {
        if (inputFiles.Count == 0) throw new InvalidDataException("请选择至少一个 STEP 文件。");
        foreach (string path in inputFiles)
            if (!File.Exists(path) || !new[] { ".step", ".stp" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("输入列表中包含不存在或格式不支持的文件：" + path);

        string work = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SolidCraft", "csv-tool", "step-drawings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        string stagedPdf = Path.Combine(work, "加工图.pdf");
        var pages = new List<StepDrawingPage>(inputFiles.Count);
        bool published = false;
        try
        {
            using var client = await ControlClient.ConnectAsync(port);
            var running = await client.RequestAsync("control.running", new { });
            if (!running.TryGetProperty("running", out var jobs) || jobs.ValueKind != System.Text.Json.JsonValueKind.Array || jobs.GetArrayLength() > 0)
                throw new IOException("SolidCraft 当前正在执行其他命令，请完成后再批量出图。");

            var originalUi = await client.RequestAsync("ui.inspect", new { });
            var originalCamera = originalUi.TryGetProperty("camera", out var cam) ? cam.Clone() : default;
            bool? originalPerspective = originalUi.TryGetProperty("perspective", out var perspective) && perspective.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False
                ? perspective.GetBoolean() : null;
            bool currentStepOpen = false;
            try
            {
                await client.RequestAsync("ui.set", new { perspective = false });
                for (int i = 0; i < inputFiles.Count; i++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    string step = Path.GetFullPath(inputFiles[i]);
                    progress.Report(new($"正在读取 STEP 并投影三视图 {i + 1}/{inputFiles.Count}：{Path.GetFileName(step)}", i));
                    await client.ExecuteAsync("doc.open", new { path = step, up = "z" });
                    currentStepOpen = true;

                    Projection2 front = await ExportProjection(client, work, $"{i:D4}-front.dxf", "front", cancellation);
                    Projection2 top = await ExportProjection(client, work, $"{i:D4}-top.dxf", "top", cancellation);
                    Projection2 end = await ExportProjection(client, work, $"{i:D4}-right.dxf", "right", cancellation);
                    pages.Add(new(Path.GetFileNameWithoutExtension(step), front, top, end, DxfFeatureDetector.Analyze(front, top)));

                    await client.RequestAsync("ui.documents", new { action = "close", force = true });
                    currentStepOpen = false;
                    progress.Report(new($"已读取 {Path.GetFileName(step)}，三视图与可识别特征尺寸已提取。", i + 1));
                }
            }
            finally
            {
                if (currentStepOpen)
                {
                    try { await client.RequestAsync("ui.documents", new { action = "close", force = true }); } catch (IOException) { }
                }
                if (originalPerspective.HasValue)
                    try { await client.RequestAsync("ui.set", new { perspective = originalPerspective.Value }); } catch (IOException) { }
                if (originalCamera.ValueKind == System.Text.Json.JsonValueKind.Object)
                    try { await client.RequestAsync("ui.camera", originalCamera); } catch (IOException) { }
            }

            cancellation.ThrowIfCancellationRequested();
            progress.Report(new("正在排版带尺寸的多页 PDF…", inputFiles.Count));
            MachiningPdf.Write(stagedPdf, pages);
            cancellation.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPdf))!);
            if (File.Exists(outputPdf)) throw new IOException("PDF 目标文件已存在，未覆盖原文件。");
            File.Move(stagedPdf, outputPdf);
            published = true;
            progress.Report(new($"加工图 PDF 已生成：{pages.Count} 页。", inputFiles.Count));
        }
        finally
        {
            if (published || Directory.Exists(work))
            {
                try { Directory.Delete(work, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    static async Task<Projection2> ExportProjection(ControlClient client, string work, string file, string view, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        await client.RequestAsync("ui.view", new { view, animate = false });
        var ui = await client.RequestAsync("ui.inspect", new { });
        if (!ui.TryGetProperty("camera", out var camera) || camera.ValueKind != System.Text.Json.JsonValueKind.Object)
            throw new IOException($"SolidCraft 没有返回 {view} 视图相机，不能生成精确投影。");
        string path = Path.Combine(work, file);
        await client.ExecuteAsync("file.export_view_dxf", new { camera, hidden = "dashed", tolerance = 0.005, path });
        return DxfProjectionReader.Read(path, view);
    }
}

internal static class DxfProjectionReader
{
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
                var vertices = new List<Point2>(); double? x = null;
                foreach (var item in entity)
                {
                    if (item.Code == 10) { if (x.HasValue) throw new InvalidDataException("DXF 顶点缺少 Y 坐标。"); x = Parse(item.Value); }
                    else if (item.Code == 20 && x.HasValue) { vertices.Add(new(x.Value, Parse(item.Value))); x = null; }
                }
                if (x.HasValue) throw new InvalidDataException("DXF 顶点 Y 坐标缺失。");
                if (vertices.Count >= 2)
                {
                    bool closed = (int.TryParse(entity.FirstOrDefault(p => p.Code == 70).Value, out int flag) && (flag & 1) != 0);
                    polylines.Add(new(vertices, closed, layer));
                }
            }
        }

        var all = lines.SelectMany(l => new[] { l.A, l.B }).Concat(polylines.SelectMany(p => p.Points)).ToArray();
        if (all.Length == 0) throw new InvalidDataException($"{name} 投影视图没有可绘制的实体，请检查 STEP 文件。");
        return new(name, lines, polylines, new(all.Min(p => p.X), all.Min(p => p.Y), all.Max(p => p.X), all.Max(p => p.Y)));
    }

    static double? Number(List<(int Code, string Value)> pairs, int code)
    {
        var value = pairs.FirstOrDefault(p => p.Code == code).Value;
        return value == null ? null : Parse(value);
    }
    static double Parse(string value) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double result) && double.IsFinite(result) ? result : throw new InvalidDataException("DXF 中含有无效坐标。");
}

internal sealed record DetectedArc(double StartX, double EndX, double CenterX, double Diameter, double Depth, double BaselineY);

internal static class DxfFeatureDetector
{
    public static StepFeatureSet Analyze(Projection2 front, Projection2 top)
    {
        var arcs = DetectRepeatedArcs(front);
        double? diameter = arcs.Count > 0 ? Median(arcs.Select(a => a.Diameter)) : null;
        var centers = arcs.OrderBy(a => a.CenterX).Select(a => a.CenterX).ToArray();
        var pitches = centers.Zip(centers.Skip(1), (a, b) => b - a).Where(x => diameter.HasValue && x > diameter.Value * 0.6 && x < diameter.Value * 4).ToArray();
        double? pitch = pitches.Length > 0 ? Median(pitches) : null;
        var frontBreaks = SharpBreaks(front, axisX: true);
        var planBreakX = SharpBreaks(top, axisX: true);
        var planBreakY = SharpBreaks(top, axisX: false);
        var slants = ObliqueEdges(top);
        string arcText = arcs.Count > 0 ? $"识别到 {arcs.Count} 处弧槽；Ø{diameter:0.##}" + (pitch.HasValue ? $"；中心距 {pitch.Value:0.##} mm" : "") : "未识别到重复弧槽";
        string slantText = slants.Count > 0 ? $"；斜边投影 {string.Join("、", slants.Select(s => $"{s.Dx:0.##}×{s.Dy:0.##}"))} mm" : "";
        string note = $"{arcText}{slantText}。STEP 轮廓尺寸初标，请复核半槽和端部特征。";
        return new(arcs, pitch, diameter, frontBreaks, planBreakX, planBreakY, slants, note);
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
                double l1 = Math.Hypot(b.X - a.X, b.Y - a.Y), l2 = Math.Hypot(c.X - b.X, c.Y - b.Y);
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
                if (dx < 2 || dy < 2 || Math.Hypot(dx, dy) < 5) continue;
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

internal static class MachiningPdf
{
    static readonly XColor Outline = XColor.FromArgb(25, 31, 38);
    static readonly XColor Dimension = XColor.FromArgb(0, 188, 196);
    static readonly XColor DimensionText = XColor.FromArgb(0, 170, 38);
    static readonly XFont Text = new("Microsoft YaHei", 8, XFontStyleEx.Regular);
    static readonly XFont Small = new("Microsoft YaHei", 6.5, XFontStyleEx.Regular);
    static readonly XFont Title = new("Microsoft YaHei", 17, XFontStyleEx.Regular);

    public static void Write(string path, IReadOnlyList<StepDrawingPage> pages)
    {
        if (pages.Count == 0) throw new InvalidDataException("没有可写入 PDF 的 STEP 图页。");
        using PdfDocument pdf = new();
        pdf.Info.Title = "辊密封加工图";
        pdf.Info.Subject = "由 STEP 实体投影自动生成；尺寸以 STEP 几何测量值为准";
        for (int i = 0; i < pages.Count; i++) DrawPage(pdf, pages[i], i + 1, pages.Count);
        pdf.Save(path);
    }

    static void DrawPage(PdfDocument pdf, StepDrawingPage item, int pageNumber, int pageCount)
    {
        PdfPage page = pdf.AddPage(); page.Size = PageSize.A4; page.Orientation = PageOrientation.Landscape;
        using XGraphics g = XGraphics.FromPdfPage(page);
        const double w = 841.89, h = 595.28, margin = 17;
        var black = new XPen(Outline, 0.65); var thin = new XPen(Outline, 0.4); var dimPen = new XPen(Dimension, 0.55); var dimBrush = new XSolidBrush(DimensionText);
        g.DrawRectangle(black, margin, margin, w - 2 * margin, h - 2 * margin);
        g.DrawString("辊密封加工图", Title, new XSolidBrush(Outline), new XPoint(margin + 14, margin + 22));
        g.DrawString("STEP 实体投影 · 尺寸单位 mm", Small, XBrushes.DimGray, new XPoint(margin + 15, margin + 37));

        double scale = Math.Min(430 / Math.Max(item.Front.Bounds.Width, item.Top.Bounds.Width),
            Math.Min(145 / Math.Max(item.Front.Bounds.Height, 1), Math.Min(145 / Math.Max(item.Top.Bounds.Height, 1), 295 / Math.Max(item.Front.Bounds.Height + item.Top.Bounds.Height, 1))));
        scale = Math.Min(scale, 118 / Math.Max(item.End.Bounds.Width, 1));
        double left = 92, frontTop = 98;
        var front = new ViewMap(item.Front, left, frontTop, scale);
        DrawView(g, front, thin);
        g.DrawString("主视图", Small, XBrushes.DimGray, new XPoint(left, frontTop - 8));
        DrawHorizontalDimension(g, front.X(item.Front.Bounds.MinX), front.X(item.Front.Bounds.MaxX), front.Y(item.Front.Bounds.MinY), front.Y(item.Front.Bounds.MinY) + 23, Format(item.Front.Bounds.Width), dimPen, dimBrush);
        DrawVerticalDimension(g, front.X(item.Front.Bounds.MinX), front.X(item.Front.Bounds.MinX) - 27, front.Y(item.Front.Bounds.MinY), front.Y(item.Front.Bounds.MaxY), Format(item.Front.Bounds.Height), dimPen, dimBrush);

        double topTop = frontTop + item.Front.Bounds.Height * scale + 66;
        var top = new ViewMap(item.Top, left, topTop, scale);
        DrawView(g, top, thin);
        g.DrawString("俯视图", Small, XBrushes.DimGray, new XPoint(left, topTop - 8));
        DrawVerticalDimension(g, top.X(item.Top.Bounds.MaxX), top.X(item.Top.Bounds.MaxX) + 26, top.Y(item.Top.Bounds.MinY), top.Y(item.Top.Bounds.MaxY), Format(item.Top.Bounds.Height), dimPen, dimBrush);
        DrawChain(g, top, item.Features.PlanBreakX, axisX: true, top.Y(item.Top.Bounds.MinY) + 30, dimPen, dimBrush);
        DrawChain(g, top, item.Features.PlanBreakY, axisX: false, top.X(item.Top.Bounds.MaxX) + 47, dimPen, dimBrush);

        double endX = 655, endTop = frontTop;
        var end = new ViewMap(item.End, endX, endTop, scale);
        DrawView(g, end, thin);
        g.DrawString("端视图", Small, XBrushes.DimGray, new XPoint(endX, endTop - 8));
        DrawHorizontalDimension(g, end.X(item.End.Bounds.MinX), end.X(item.End.Bounds.MaxX), end.Y(item.End.Bounds.MinY), end.Y(item.End.Bounds.MinY) + 22, Format(item.End.Bounds.Width), dimPen, dimBrush);

        string feature = item.Features.Note;
        g.DrawString(feature, Text, XBrushes.Black, new XRect(640, 263, 178, 72), XStringFormats.TopLeft);
        g.DrawString("主视/俯视/端视来自输入 STEP；外形尺寸取投影边界。端部台阶链尺寸取轮廓折点。", Small,
            XBrushes.DimGray, new XRect(640, 339, 178, 42), XStringFormats.TopLeft);
        DrawFeatureDimensions(g, front, item.Features, dimPen, dimBrush);

        DrawTitleBlock(g, item.Name, pageNumber, pageCount, DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), black, thin);
    }

    static void DrawView(XGraphics g, ViewMap map, XPen pen)
    {
        foreach (var line in map.Projection.Lines)
            g.DrawLine(IsHidden(line.Layer) ? new XPen(Outline, 0.35) { DashStyle = XDashStyle.Dash } : pen, map.Point(line.A), map.Point(line.B));
        foreach (var poly in map.Projection.Polylines)
        {
            for (int i = 1; i < poly.Points.Count; i++) g.DrawLine(IsHidden(poly.Layer) ? new XPen(Outline, 0.35) { DashStyle = XDashStyle.Dash } : pen, map.Point(poly.Points[i - 1]), map.Point(poly.Points[i]));
            if (poly.Closed) g.DrawLine(pen, map.Point(poly.Points[^1]), map.Point(poly.Points[0]));
        }
    }

    static void DrawHorizontalDimension(XGraphics g, double x1, double x2, double featureY, double dimY, string label, XPen pen, XBrush brush)
    {
        if (x2 < x1) (x1, x2) = (x2, x1);
        g.DrawLine(pen, x1, featureY, x1, dimY + Math.Sign(dimY - featureY) * 3);
        g.DrawLine(pen, x2, featureY, x2, dimY + Math.Sign(dimY - featureY) * 3);
        double mid = (x1 + x2) / 2, textW = Math.Max(24, g.MeasureString(label, Small).Width + 7);
        if (x2 - x1 > textW + 16) { g.DrawLine(pen, x1, dimY, mid - textW / 2, dimY); g.DrawLine(pen, mid + textW / 2, dimY, x2, dimY); }
        else g.DrawLine(pen, x1, dimY, x2, dimY);
        Arrow(g, x1, dimY, 1, 0, pen); Arrow(g, x2, dimY, -1, 0, pen);
        g.DrawString(label, Small, brush, new XRect(mid - textW / 2, dimY - 8, textW, 14), XStringFormats.Center);
    }

    static void DrawVerticalDimension(XGraphics g, double featureX, double dimX, double y1, double y2, string label, XPen pen, XBrush brush)
    {
        if (y2 < y1) (y1, y2) = (y2, y1);
        g.DrawLine(pen, featureX, y1, dimX + Math.Sign(dimX - featureX) * 3, y1);
        g.DrawLine(pen, featureX, y2, dimX + Math.Sign(dimX - featureX) * 3, y2);
        g.DrawLine(pen, dimX, y1, dimX, y2); Arrow(g, dimX, y1, 0, 1, pen); Arrow(g, dimX, y2, 0, -1, pen);
        XGraphicsState state = g.Save(); g.TranslateTransform(dimX - 7, (y1 + y2) / 2); g.RotateTransform(-90);
        g.DrawString(label, Small, brush, new XRect(-27, -6, 54, 12), XStringFormats.Center); g.Restore(state);
    }

    static void DrawFeatureDimensions(XGraphics g, ViewMap front, StepFeatureSet features, XPen pen, XBrush brush)
    {
        if (features.Arcs.Count == 0 || !features.Diameter.HasValue) return;
        var arcs = features.Arcs.OrderBy(a => a.CenterX).ToArray();
        var first = arcs[0];
        double targetX = front.X(first.CenterX), targetY = front.Y(first.BaselineY - first.Depth);
        double labelX = Math.Min(600, front.X(front.Projection.Bounds.MaxX) + 58), labelY = front.Y(front.Projection.Bounds.MaxY) + 16;
        g.DrawLine(pen, targetX, targetY, labelX, labelY + 5);
        Arrow(g, targetX, targetY, Math.Sign(targetX - labelX), Math.Sign(targetY - (labelY + 5)), pen);
        g.DrawString($"Ø{Format(features.Diameter.Value)}", Small, brush, new XPoint(labelX, labelY));
        if (arcs.Length >= 2)
        {
            double baseline = arcs.Average(a => a.BaselineY);
            double edgeLeft = front.X(front.Projection.Bounds.MinX), edgeRight = front.X(front.Projection.Bounds.MaxX);
            double dimY = Math.Max(68, front.Y(baseline) - 28);
            var marks = new[] { edgeLeft }.Concat(arcs.Select(a => front.X(a.CenterX))).Append(edgeRight).Distinct().Order().ToArray();
            for (int i = 1; i < marks.Length; i++)
                DrawHorizontalDimension(g, marks[i - 1], marks[i], front.Y(baseline), dimY, Format(Math.Abs(marks[i] - marks[i - 1]) / front.Scale), pen, brush);
        }
        DrawChain(g, front, features.FrontBreaks, axisX: true, front.Y(front.Projection.Bounds.MinY) + 43, pen, brush);
    }

    static void DrawChain(XGraphics g, ViewMap map, IReadOnlyList<double> internalMarks, bool axisX, double dimensionLine, XPen pen, XBrush brush)
    {
        if (internalMarks.Count == 0) return;
        double min = axisX ? map.Projection.Bounds.MinX : map.Projection.Bounds.MinY;
        double max = axisX ? map.Projection.Bounds.MaxX : map.Projection.Bounds.MaxY;
        var marks = new[] { min }.Concat(internalMarks).Append(max).Distinct().Order().ToArray();
        if (axisX)
        {
            double featureY = map.Y(min);
            for (int i = 1; i < marks.Length; i++)
                DrawHorizontalDimension(g, map.X(marks[i - 1]), map.X(marks[i]), featureY, dimensionLine, Format(marks[i] - marks[i - 1]), pen, brush);
        }
        else
        {
            double featureX = map.X(max);
            for (int i = 1; i < marks.Length; i++)
                DrawVerticalDimension(g, featureX, dimensionLine, map.Y(marks[i - 1]), map.Y(marks[i]), Format(marks[i] - marks[i - 1]), pen, brush);
        }
    }

    static void Arrow(XGraphics g, double x, double y, double dx, double dy, XPen pen)
    {
        const double len = 4, wing = 1.6;
        double px = -dy, py = dx;
        g.DrawLine(pen, x, y, x - dx * len + px * wing, y - dy * len + py * wing);
        g.DrawLine(pen, x, y, x - dx * len - px * wing, y - dy * len - py * wing);
    }

    static void DrawTitleBlock(XGraphics g, string name, int pageNumber, int total, string pagesafeDate, XPen border, XPen thin)
    {
        const double x = 466, y = 493, w = 358, h = 78;
        g.DrawRectangle(border, x, y, w, h);
        double[] cuts = [x + 70, x + 144, x + 218, x + 278];
        g.DrawLine(thin, x, y + 24, x + w, y + 24); g.DrawLine(thin, x, y + 49, x + w, y + 49);
        foreach (double cut in cuts) { g.DrawLine(thin, cut, y, cut, y + 49); }
        g.DrawLine(thin, x + 218, y + 62, x + w, y + 62);
        g.DrawString("图名", Small, XBrushes.Black, new XRect(x + 6, y + 6, 56, 14), XStringFormats.CenterLeft);
        g.DrawString(name, Text, XBrushes.Black, new XRect(x + 74, y + 1, w - 80, 22), XStringFormats.CenterLeft);
        g.DrawString("单位", Small, XBrushes.Black, new XRect(x + 6, y + 29, 56, 14), XStringFormats.CenterLeft);
        g.DrawString("mm", Text, XBrushes.Black, new XRect(x + 74, y + 25, 63, 22), XStringFormats.Center);
        g.DrawString("页数", Small, XBrushes.Black, new XRect(x + 150, y + 29, 56, 14), XStringFormats.CenterLeft);
        g.DrawString($"{pageNumber} / {total}", Text, XBrushes.Black, new XRect(x + 222, y + 25, 52, 22), XStringFormats.Center);
        g.DrawString("来源", Small, XBrushes.Black, new XRect(x + 286, y + 29, 56, 14), XStringFormats.CenterLeft);
        g.DrawString("STEP", Text, XBrushes.Black, new XRect(x + 6, y + 53, 60, 17), XStringFormats.CenterLeft);
        g.DrawString("恒瑞 · 自动出图", Small, XBrushes.Black, new XRect(x + 76, y + 51, 130, 18), XStringFormats.CenterLeft);
        g.DrawString("日期", Small, XBrushes.Black, new XRect(x + 222, y + 51, 48, 18), XStringFormats.CenterLeft);
        g.DrawString(pagesafeDate, Small, XBrushes.Black, new XRect(x + 280, y + 51, 74, 18), XStringFormats.Center);
        g.DrawString("尺寸以 STEP 实体测量值为准；未识别的复杂特征请复核原始模型。", Small, XBrushes.DimGray,
            new XRect(26, 539, 424, 24), XStringFormats.CenterLeft);
    }

    static bool IsHidden(string layer) => layer.Contains("HIDDEN", StringComparison.OrdinalIgnoreCase);
    static string Format(double value) => Math.Abs(value - Math.Round(value)) < 0.005 ? Math.Round(value).ToString("0", CultureInfo.InvariantCulture) : value.ToString("0.##", CultureInfo.InvariantCulture);

    sealed class ViewMap(Projection2 projection, double left, double top, double scale)
    {
        public Projection2 Projection { get; } = projection;
        public double Scale { get; } = scale;
        public double X(double x) => left + (x - Projection.Bounds.MinX) * scale;
        public double Y(double y) => top + (Projection.Bounds.MaxY - y) * scale;
        public XPoint Point(Point2 p) => new(X(p.X), Y(p.Y));
    }
}
