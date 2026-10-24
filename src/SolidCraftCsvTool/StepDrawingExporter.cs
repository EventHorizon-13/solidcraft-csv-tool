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
        IProgress<ExportProgress> progress, CancellationToken cancellation)
    {
        if (inputFiles.Count == 0) throw new InvalidDataException("请选择至少一个 STEP 文件。");
        foreach (string path in inputFiles)
            if (!File.Exists(path) || !new[] { ".step", ".stp" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("输入列表中包含不存在或格式不支持的文件：" + path);

        inputFiles = inputFiles.OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase).ToArray();
        string destination = Path.GetFullPath(outputFolder);
        Directory.CreateDirectory(destination);
        string[] outputNames = inputFiles.Select((path, index) => $"{index + 1:D3}_{SafeFileName(Path.GetFileNameWithoutExtension(path))}_加工图.dxf").ToArray();
        if (outputNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() != outputNames.Length)
            throw new IOException("输入文件名生成了重复的输出文件名，请先重命名 STEP 文件。");
        string collision = outputNames.FirstOrDefault(name => File.Exists(Path.Combine(destination, name))) ?? "";
        if (collision.Length > 0) throw new IOException("目标文件已存在，未覆盖任何文件：" + Path.Combine(destination, collision));

        string work = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SolidCraft", "csv-tool", "step-drawings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var pages = new List<StepDrawingPage>(inputFiles.Count);
        var staged = new List<string>(inputFiles.Count);
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
                    progress.Report(new($"已读取 {Path.GetFileName(step)}，三视图与几何特征已提取。", i + 1));
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
            for (int i = 0; i < pages.Count; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                string path = Path.Combine(work, outputNames[i]);
                progress.Report(new($"正在生成尺寸标注 {i + 1}/{pages.Count}：{outputNames[i]}", i));
                MachiningDxf.Write(path, pages[i]);
                staged.Add(path);
            }
            cancellation.ThrowIfCancellationRequested();
            for (int i = 0; i < staged.Count; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                string target = Path.Combine(destination, outputNames[i]);
                if (File.Exists(target)) throw new IOException("目标文件在出图期间出现，未覆盖原文件：" + target);
                File.Move(staged[i], target);
                progress.Report(new($"已保存 {i + 1}/{staged.Count}：{outputNames[i]}", i + 1));
            }
            progress.Report(new($"加工图 DXF 已生成：{pages.Count} 个文件。", inputFiles.Count));
        }
        finally
        {
            if (Directory.Exists(work))
            {
                try { Directory.Delete(work, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        string safe = new string(value.Select(ch => invalid.Contains(ch) || char.IsControl(ch) ? '_' : ch).ToArray()).Trim(' ', '.');
        return safe.Length == 0 ? "STEP" : safe;
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
                    double start = circle ? 0 : Number(entity, 50) ?? 0, end = circle ? 360 : Number(entity, 51) ?? 360;
                    var points = SampleArc(new(cx.Value, cy.Value), radius.Value, start, end, circle);
                    if (points.Count >= 3) polylines.Add(new(points, circle, layer));
                }
            }
        }

        var all = lines.SelectMany(l => new[] { l.A, l.B }).Concat(polylines.SelectMany(p => p.Points)).ToArray();
        if (all.Length == 0) throw new InvalidDataException($"{name} 投影视图没有可绘制的实体，请检查 STEP 文件。");
        var joined = JoinConnectedLines(lines);
        polylines.AddRange(joined.Polylines);
        return new(name, joined.Lines, polylines, new(all.Min(p => p.X), all.Min(p => p.Y), all.Max(p => p.X), all.Max(p => p.Y)));
    }

    static (List<Line2> Lines, List<Polyline2> Polylines) JoinConnectedLines(IReadOnlyList<Line2> source)
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
internal static class MachiningDxf
{
    sealed class Writer(string path) : IDisposable
    {
        readonly StreamWriter output = new(path, false, DxfEncoding());
        public void Pair(int code, string value) { output.WriteLine(code.ToString(CultureInfo.InvariantCulture)); output.WriteLine(value); }
        public void Dispose() => output.Dispose();
        public void Line(Point2 a, Point2 b, string layer = "DIM")
        {
            if (Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y) < 0.00001) return;
            Pair(0, "LINE"); Pair(8, layer); Pair(10, F(a.X)); Pair(20, F(a.Y)); Pair(30, "0"); Pair(11, F(b.X)); Pair(21, F(b.Y)); Pair(31, "0");
        }
        public void Arc(Point2 center, double radius, double startDegrees, double endDegrees, string layer)
        {
            Pair(0, "ARC"); Pair(8, layer); Pair(10, F(center.X)); Pair(20, F(center.Y)); Pair(30, "0"); Pair(40, F(radius)); Pair(50, F(startDegrees)); Pair(51, F(endDegrees));
        }
        public void Polyline(IReadOnlyList<Point2> points, bool closed, string layer)
        {
            if (points.Count < 2) return;
            for (int i = 1; i < points.Count; i++) Line(points[i - 1], points[i], layer);
            if (closed) Line(points[^1], points[0], layer);
        }
        public void Text(string value, Point2 at, double height, string layer = "TEXT", double rotation = 0)
        {
            Pair(0, "TEXT"); Pair(8, layer); Pair(10, F(at.X)); Pair(20, F(at.Y)); Pair(30, "0"); Pair(40, F(height)); Pair(1, Escape(value)); Pair(7, "STANDARD");
            Pair(50, F(rotation));
            Pair(72, "1"); Pair(73, "2"); Pair(11, F(at.X)); Pair(21, F(at.Y)); Pair(31, "0");
        }
        public void Raw(string value) => output.Write(value);
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

    public static void Write(string path, StepDrawingPage page)
    {
        using (var writer = new Writer(path))
        {
            Header(writer);
            var front = new Map(page.Front, -page.Front.Bounds.MinX, -page.Front.Bounds.MinY);
            double topY = -(page.Front.Bounds.Height + page.Top.Bounds.Height + 76);
            var top = new Map(page.Top, -page.Top.Bounds.MinX, topY - page.Top.Bounds.MinY);
            var end = new Map(page.End, page.Front.Bounds.Width + 100 - page.End.Bounds.MinX, -page.End.Bounds.MinY);
            DrawView(writer, front); DrawView(writer, top); DrawView(writer, end);
            writer.Text(page.Name, new(front.Min.X, front.Max.Y + 72), 5.0, "TITLE");
            writer.Text("3 VIEW MACHINING DRAWING    UNIT: mm", new(front.Min.X, front.Max.Y + 62), 3.2, "TITLE");
            writer.Text("FRONT", new(front.Min.X, front.Max.Y + 8), 3.0, "TITLE");
            writer.Text("TOP", new(top.Min.X, top.Max.Y + 8), 3.0, "TITLE");
            writer.Text("END", new(end.Min.X, end.Max.Y + 8), 3.0, "TITLE");

            // 外形尺寸：总长、总高、总宽及端视图复核尺寸。
            Horizontal(writer, front.Min.X, front.Max.X, front.Min.Y, front.Min.Y - 22, Fmt(page.Front.Bounds.Width));
            Vertical(writer, front.Min.X, front.Min.X - 20, front.Min.Y, front.Max.Y, Fmt(page.Front.Bounds.Height));
            Horizontal(writer, end.Min.X, end.Max.X, end.Min.Y, end.Min.Y - 20, Fmt(page.End.Bounds.Width));
            Vertical(writer, end.Min.X, end.Min.X - 18, end.Min.Y, end.Max.Y, Fmt(page.End.Bounds.Height));
            Vertical(writer, top.Max.X, top.Max.X + 23, top.Min.Y, top.Max.Y, Fmt(page.Top.Bounds.Height));

            // 槽径、槽中心位置链；这对应成品图中的 Ø 标注与 60/120 等位置链。
            if (page.Features.Arcs.Count > 0 && page.Features.Diameter is double diameter)
            {
                var arcs = page.Features.Arcs.OrderBy(a => a.CenterX).ToArray();
                var first = arcs[0];
                Point2 target = front.At(first.CenterX, first.BaselineY - first.Depth);
                Point2 label = front.At(first.CenterX + Math.Max(diameter * 0.68, 28), front.Projection.Bounds.MaxY + 48);
                writer.Line(target, new(label.X, label.Y - 2), "DIM"); Arrow(writer, target, new(label.X, label.Y - 2), 2.4);
                writer.Text("%%c" + Fmt(diameter), label, 3.6, "TEXT");
                var marks = new[] { page.Front.Bounds.MinX }.Concat(arcs.Select(a => a.CenterX)).Append(page.Front.Bounds.MaxX).Distinct().Order().ToArray();
                DrawHorizontalChain(writer, front, marks, front.Max.Y + 18);
            }
            if (page.Features.FrontBreaks.Count > 0)
                DrawHorizontalChain(writer, front, IncludeEnds(page.Features.FrontBreaks.Concat(page.Features.Arcs.Select(a => a.CenterX)).ToArray(), page.Front.Bounds.MinX, page.Front.Bounds.MaxX), front.Max.Y + 34);

            // 轮廓折点尺寸链：覆盖条纹/槽距、左右半孔位置、端部台阶分段。
            DrawHorizontalChain(writer, top, IncludeEnds(page.Features.PlanBreakX, page.Top.Bounds.MinX, page.Top.Bounds.MaxX), top.Min.Y - 20);
            DrawVerticalChain(writer, top, IncludeEnds(page.Features.PlanBreakY, page.Top.Bounds.MinY, page.Top.Bounds.MaxY), top.Max.X + 39);
            DrawVerticalChain(writer, front, IncludeEnds(page.Features.FrontBreakY, page.Front.Bounds.MinY, page.Front.Bounds.MaxY), front.Max.X + 24);

            if (page.Features.PlanSlants.Count > 0)
            {
                string slopes = string.Join("；", page.Features.PlanSlants.Select(s => $"斜边 {Fmt(s.Dx)}×{Fmt(s.Dy)}"));
                writer.Text(slopes, new(front.Min.X, top.Min.Y - 48), 3.0, "TITLE");
            }
            writer.Text("STEP PROJECTION DIMENSIONS - REVIEW HALF HOLES AND END STEPS", new(front.Min.X, top.Min.Y - 60), 2.8, "TITLE");
            Footer(writer, page.Name, front.Max.X + 100, top.Min.Y - 60);
            writer.Pair(0, "ENDSEC"); writer.Pair(0, "EOF");
        }
    }

    static void Header(Writer w)
    {
        w.Pair(0, "SECTION"); w.Pair(2, "HEADER"); w.Pair(9, "$ACADVER"); w.Pair(1, "AC1009");
        w.Pair(9, "$DWGCODEPAGE"); w.Pair(3, "ANSI_936");
        w.Pair(0, "ENDSEC");
        w.Pair(0, "SECTION"); w.Pair(2, "TABLES");
        w.Pair(0, "TABLE"); w.Pair(2, "LTYPE"); w.Pair(70, "2");
        w.Pair(0, "LTYPE"); w.Pair(2, "CONTINUOUS"); w.Pair(70, "0"); w.Pair(3, "Solid line"); w.Pair(72, "65"); w.Pair(73, "0"); w.Pair(40, "0");
        w.Pair(0, "LTYPE"); w.Pair(2, "DASHED"); w.Pair(70, "0"); w.Pair(3, "Dashed __ __ __"); w.Pair(72, "65"); w.Pair(73, "2"); w.Pair(40, "6"); w.Pair(49, "4"); w.Pair(49, "-2");
        w.Pair(0, "ENDTAB");
        string[] layers = ["VISIBLE", "HIDDEN", "DIM", "TEXT", "TITLE"];
        short[] colors = [7, 8, 4, 3, 7];
        w.Pair(0, "TABLE"); w.Pair(2, "LAYER"); w.Pair(70, layers.Length.ToString(CultureInfo.InvariantCulture));
        for (int i = 0; i < layers.Length; i++) { w.Pair(0, "LAYER"); w.Pair(2, layers[i]); w.Pair(70, "0"); w.Pair(62, colors[i].ToString(CultureInfo.InvariantCulture)); w.Pair(6, layers[i] == "HIDDEN" ? "DASHED" : "CONTINUOUS"); }
        w.Pair(0, "ENDTAB");
        w.Pair(0, "TABLE"); w.Pair(2, "STYLE"); w.Pair(70, "1"); w.Pair(0, "STYLE"); w.Pair(2, "STANDARD"); w.Pair(70, "0"); w.Pair(40, "0"); w.Pair(41, "1"); w.Pair(50, "0"); w.Pair(71, "0"); w.Pair(42, "2.5"); w.Pair(3, "txt"); w.Pair(4, ""); w.Pair(0, "ENDTAB");
        w.Pair(0, "ENDSEC");
        w.Pair(0, "SECTION"); w.Pair(2, "BLOCKS");
        foreach (string block in new[] { "$MODEL_SPACE", "$PAPER_SPACE" })
        {
            w.Pair(0, "BLOCK"); w.Pair(8, "0"); w.Pair(2, block); w.Pair(70, "0"); w.Pair(10, "0"); w.Pair(20, "0"); w.Pair(30, "0"); w.Pair(3, block); w.Pair(1, "");
            w.Pair(0, "ENDBLK"); w.Pair(8, "0");
        }
        w.Pair(0, "ENDSEC"); w.Pair(0, "SECTION"); w.Pair(2, "ENTITIES");
    }

    static void DrawView(Writer w, Map map)
    {
        foreach (Line2 line in map.Projection.Lines) w.Line(map.P(line.A), map.P(line.B), IsHidden(line.Layer) ? "HIDDEN" : "VISIBLE");
        foreach (Polyline2 poly in map.Projection.Polylines)
        {
            var points = poly.Points.Select(map.P).ToList();
            if (poly.Closed && points.Count > 2) points.Add(points[0]);
            DrawCurvePolyline(w, points, IsHidden(poly.Layer) ? "HIDDEN" : "VISIBLE");
        }
    }

    static void DrawCurvePolyline(Writer w, IReadOnlyList<Point2> points, string layer)
    {
        int i = 0;
        while (i < points.Count - 1)
        {
            if (i + 2 < points.Count && TryFitArc(points, i, out int end, out Point2 center, out double radius, out bool counterClockwise))
            {
                double a = Angle(center, points[i]), b = Angle(center, points[end]);
                if (counterClockwise) w.Arc(center, radius, a, b, layer);
                else w.Arc(center, radius, b, a, layer);
                i = end;
            }
            else
            {
                w.Line(points[i], points[i + 1], layer);
                i++;
            }
        }
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
        w.Line(new(x1, featureY), new(x1, dimY - Math.Sign(dimY - featureY) * 2), "DIM");
        w.Line(new(x2, featureY), new(x2, dimY - Math.Sign(dimY - featureY) * 2), "DIM");
        w.Line(new(x1, dimY), new(x2, dimY), "DIM");
        Arrow(w, new(x1, dimY), new(x2, dimY), 2.0); Arrow(w, new(x2, dimY), new(x1, dimY), 2.0);
        w.Text(label, new((x1 + x2) / 2, dimY + 2.2), 3.2);
    }

    static void Vertical(Writer w, double featureX, double dimX, double y1, double y2, string label)
    {
        if (y2 < y1) (y1, y2) = (y2, y1);
        w.Line(new(featureX, y1), new(dimX + Math.Sign(dimX - featureX) * 2, y1), "DIM");
        w.Line(new(featureX, y2), new(dimX + Math.Sign(dimX - featureX) * 2, y2), "DIM");
        w.Line(new(dimX, y1), new(dimX, y2), "DIM");
        Arrow(w, new(dimX, y1), new(dimX, y2), 2.0); Arrow(w, new(dimX, y2), new(dimX, y1), 2.0);
        w.Text(label, new(dimX - 2.2, (y1 + y2) / 2), 3.2, rotation: 90);
    }

    static void DrawHorizontalChain(Writer w, Map map, double[] marks, double y)
    {
        for (int i = 1; i < marks.Length; i++)
        {
            double a = map.Dx + marks[i - 1], b = map.Dx + marks[i];
            if (b - a > 0.2) Horizontal(w, a, b, map.Max.Y, y, Fmt(marks[i] - marks[i - 1]));
        }
    }

    static void DrawVerticalChain(Writer w, Map map, double[] marks, double x)
    {
        for (int i = 1; i < marks.Length; i++)
        {
            double a = map.Dy + marks[i - 1], b = map.Dy + marks[i];
            if (b - a > 0.2) Vertical(w, map.Max.X, x, a, b, Fmt(marks[i] - marks[i - 1]));
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
