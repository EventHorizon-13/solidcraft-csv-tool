using System.Globalization;
using System.Text;
using SolidCraftCsvTool;

internal static class DimensionChecks
{
    public static int Run(string? outputDirectory)
    {
        int count = 0;
        void Check(bool value, string reason) { if (!value) throw new Exception(reason); count++; }
        Point2 Shift(double x, double y) => new(x - 40, y + 25);
        var contour = new List<Point2> { Shift(0, 100) };
        foreach (double center in new[] { 60.0, 180.0 })
            for (int i = 0; i <= 180; i++)
            {
                double angle = Math.PI + i * Math.PI / 180;
                contour.Add(Shift(center + 30 * Math.Cos(angle), 100 + 30 * Math.Sin(angle)));
            }
        contour.AddRange([Shift(300, 100), Shift(300, 0), Shift(0, 0)]);
        Projection2 front = new("front", [], [new(contour, true, "VISIBLE")], new(-40, 25, 260, 125));
        Point2[] plan = [new(0, 0), new(300, 0), new(300, 50), new(291, 50), new(291, 110), new(300, 110), new(300, 160), new(0, 160)];
        var planLines = new List<Line2>();
        foreach (double x in new[] { 30.0, 90.0, 150.0, 210.0 }) planLines.Add(new(new(x, 0), new(x, 160), "VISIBLE"));
        planLines.Add(new(new(35, 0), new(35, 160), "HIDDEN"));
        Projection2 top = new("top", planLines, [new(plan, true, "VISIBLE")], new(0, 0, 300, 160));
        Point2[] side = [new(0, 0), new(140, 0), new(160, 20), new(160, 100), new(0, 100)];
        Projection2 end = new("right", [], [new(side, true, "VISIBLE")], new(0, 0, 160, 100));
        StepDrawingPage page = new("dimension-check", front, top, end, DxfFeatureDetector.Analyze(front, top));
        string directory = outputDirectory == null ? Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")) : Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, "annotated-sample.dxf");
        try
        {
            MachiningDxf.WriteCombined(file, [page, page with { Name = "second-page" }]);
            string[] raw = File.ReadAllLines(file, Encoding.UTF8);
            Check(raw.Length % 2 == 0, "DXF group pairs must be complete");
            var entities = new List<(string Type, Dictionary<int, string> Values)>();
            Dictionary<int, string>? current = null;
            for (int i = 0; i < raw.Length; i += 2)
            {
                int code = int.Parse(raw[i], CultureInfo.InvariantCulture);
                if (code == 0) { current = new(); entities.Add((raw[i + 1], current)); }
                else if (current != null) current[code] = raw[i + 1];
            }
            var dimensions = entities.Where(e => e.Type == "DIMENSION").Select(e => e.Values).ToArray();
            var blocks = entities.Where(e => e.Type == "BLOCK").Select(e => e.Values[2]).ToHashSet();
            Check(dimensions.Length >= 30 && dimensions.Length % 2 == 0, "Both pages need native editable dimensions");
            Check(dimensions.All(d => blocks.Contains(d[2])) && dimensions.Select(d => d[2]).Distinct().Count() == dimensions.Length,
                "Each native dimension needs a unique graphics block");
            double N(Dictionary<int, string> d, int code) => double.Parse(d[code], CultureInfo.InvariantCulture);
            double Measurement(Dictionary<int, string> d) => Math.Abs(N(d, 50) == 0 ? N(d, 14) - N(d, 13) : N(d, 24) - N(d, 23));
            foreach (var d in dimensions)
            {
                Check(Measurement(d) > 0 && d[1] == "<>" && d.ContainsKey(5) && d.ContainsKey(330), "Native dimensions need measurements, handles and owning block records");
            }
            Check(raw.Contains("AC1024") && raw.Contains("BLOCK_RECORD") && raw.Contains("OBJECTS"),
                "DXF 2010 needs complete symbol tables, block records and object structure");
            var reopened = netDxf.DxfDocument.Load(file);
            Check(reopened != null && reopened.Entities.Dimensions.Count() == dimensions.Length, "Saved DXF 2010 must reopen with all native dimensions");
            Check(reopened!.Entities.Texts.Any(t => t.Value.Contains("序号", StringComparison.Ordinal)), "Unicode drawing titles must survive a complete library round trip");
            var sizes = dimensions.Select(Measurement).ToArray();
            foreach (double expected in new[] { 9.0, 20.0, 30.0, 50.0, 60.0, 100.0, 120.0, 140.0, 160.0, 300.0 })
                Check(sizes.Any(v => Math.Abs(v - expected) < 0.001), "Missing measured feature: " + expected);
            Check(!sizes.Any(v => Math.Abs(v - 35) < 0.001), "Hidden edges must not create plan dimension marks");
            Check(dimensions.Any(d => N(d, 50) == 90 && Math.Abs(Measurement(d) - 20) < 0.001 && N(d, 13) > 300),
                "End bevel needs a vertical depth dimension as well as its horizontal run");
            Check(entities.Any(e => e.Type == "TEXT" && e.Values.TryGetValue(8, out string? layer) && layer == "TEXT" && N(e.Values, 40) > 4),
                "Dimension text must scale with the drawing span");
            int half = dimensions.Length / 2;
            for (int i = 0; i < half; i++)
            {
                Check(Math.Abs(Measurement(dimensions[i]) - Measurement(dimensions[i + half])) < 0.0001, "Page translation cannot change measurements");
                Check(Math.Abs(N(dimensions[i], 23) - N(dimensions[i + half], 23) - 492) < 0.0001, "Definition points must use page offsets exactly once");
            }
            Check(entities.Any(e => e.Type == "ARC"), "Dimensioning must preserve fitted arcs");
            Check(AppBuildInfo.WindowTitle.StartsWith("标准辊密封批量生成 · v", StringComparison.Ordinal) &&
                !AppBuildInfo.WindowTitle.Contains("未知", StringComparison.Ordinal), "Title must contain product name, version and embedded build date");
            string? expectedVersion = Environment.GetEnvironmentVariable("BUILD_VERSION");
            if (expectedVersion != null) Check(AppBuildInfo.WindowTitle.Contains("v" + expectedVersion + " ·", StringComparison.Ordinal), "Title version must match release version");
            Console.WriteLine("DXF sample: " + file);
            Console.WriteLine("Window title: " + AppBuildInfo.WindowTitle);
        }
        finally { if (outputDirectory == null) Directory.Delete(directory, true); }
        return count;
    }
}
