using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using SolidCraftCsvTool;

// Regression: bevel dimensions are optional only when both bevel switches are off.
{
    foreach (var emptyColumns in new[] { new[] { 12 }, new[] { 13 }, new[] { 12, 13 } })
    {
        var bevelRow = ModelRules.DefaultRow();
        foreach (int column in emptyColumns) bevelRow[column] = "   ";
        var prepared = ModelRules.Validate(new[] { bevelRow });
        if (prepared[0].Parameters["斜边Y"] != "30" || prepared[0].Parameters["斜边Z"] != "30")
            throw new Exception("Disabled bevel blank dimensions did not receive defaults.");
        foreach (int flag in new[] { 10, 11 })
        {
            var enabledRow = bevelRow.ToArray(); enabledRow[flag] = "1";
            bool rejected = false;
            try { ModelRules.Validate(new[] { enabledRow }); } catch (InvalidDataException) { rejected = true; }
            if (!rejected) throw new Exception("Enabled bevel accepted blank dimensions.");
        }
    }
    Console.WriteLine("PASS: disabled/enabled bevel blank-dimension checks");
}

if(args.Length==2&&args[0]=="--anonymous-title-check")
{
    AnonymousTitleChecks.Run(args[1]);return;
}

if(args.Length==2&&args[0]=="--optional-dimensions-check")
{
    OptionalDimensionsChecks.Run(args[1]);return;
}
if(args.Length==3&&args[0]=="--optional-dimensions-headless")
{
    await OptionalDimensionsChecks.HeadlessAsync(args[1],args[2]);return;
}

if(args.Length==2&&args[0]=="--batch-options-check")
{
    BatchOptionsChecks.Run(args[1]);return;
}
if(args.Length==3&&args[0]=="--headless-export-check")
{
    await BatchOptionsChecks.HeadlessAsync(args[1],args[2]);return;
}

if(args.Length==4&&args[0]=="--projection-review")
{
    var front=DxfProjectionReader.Read(Path.Combine(args[1],"front.dxf"),"front");
    var top=DxfProjectionReader.Read(Path.Combine(args[1],"top.dxf"),"top");
    var end=DxfProjectionReader.Read(Path.Combine(args[1],"left.dxf"),"left");
    MachiningDxf.Write(args[3],new("review",front,top,end,DxfFeatureDetector.Analyze(front,top)),new(){TemplatePath=args[2]});
    Console.WriteLine("PASS: regenerated only the supplied private projection geometry.");
    return;
}

if (args.Length == 3 && args[0] == "--template-preview")
{
    int passed = DimensionChecks.Run(args[2], args[1]);
    Console.WriteLine($"Template preview passed: {passed} checks");
    return;
}

if (args.Length == 2 && args[0] == "--front-height-check")
{
    FrontHeightChecks.Run(args[1]);
    PlanAnnotationChecks.Run(args[1]);
    FrameFontChecks.Run(args[1]);
    return;
}

int checks = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
void Reject(Action action, string message) { try { action(); } catch (InvalidDataException) { checks++; return; } throw new Exception(message); }
JsonElement Json(string value) { using var doc = JsonDocument.Parse(value); return doc.RootElement.Clone(); }

// Regression: R45 arcs had 0.95 mm and longer gaps after short chords just
// outside fitted ARC endpoints were wrongly suppressed.
Point2 ArcPoint(double angle)
{
    double radians = angle * Math.PI / 180.0;
    return new(45 * Math.Cos(radians), 45 * Math.Sin(radians));
}
Check(!MachiningDxf.IsCoveredByArcFragment(ArcPoint(284.3658), ArcPoint(285.5742),
    new(0, 0), 45, 221.7219, 284.3658 - 221.7219),
    "R45 circle chord AFTER the fitted endpoint must not disappear");
Check(!MachiningDxf.IsCoveredByArcFragment(ArcPoint(180.0), ArcPoint(180.858),
    new(0, 0), 45, 185.291, 221.722 - 185.291),
    "R45 circle chord BEFORE the fitted start must not disappear");
Check(!MachiningDxf.IsCoveredByArcFragment(ArcPoint(284.0), ArcPoint(285.0),
    new(0, 0), 45, 221.722, 284.366 - 221.722),
    "R45 chord crossing arc endpoint must be preserved");
Check(MachiningDxf.IsCoveredByArcFragment(ArcPoint(260.0), ArcPoint(261.0),
    new(0, 0), 45, 221.722, 284.366 - 221.722),
    "R45 chord fully covered by an arc should be deduplicated");
Check(MachiningDxf.IsCoveredByArcFragment(ArcPoint(355.0), ArcPoint(356.0),
    new(0, 0), 45, 350, 20),
    "Arc overlap checks must handle the 360-degree seam");
Check(!MachiningDxf.IsCoveredByArcFragment(ArcPoint(349.0), ArcPoint(350.5),
    new(0, 0), 45, 350, 20),
    "Arc overlap checks must preserve chords crossing a wrapped start");

string[] Row(string holes, string left, string right) { var row = ModelRules.DefaultRow(); row[1] = holes; row[8] = left; row[9] = right; return row; }

foreach (string holes in new[] { "4.5", "5.5" })
{
    foreach (var ends in new[] { ("1", "0"), ("0", "1") }) Check(ModelRules.Validate([Row(holes, ends.Item1, ends.Item2)]).Count == 1, "小数孔单侧半孔应有效");
    foreach (var ends in new[] { ("0", "0"), ("1", "1") }) Reject(() => ModelRules.Validate([Row(holes, ends.Item1, ends.Item2)]), "小数孔无半孔或双半孔必须拒绝");
}
Check(ModelRules.Validate([Row("5", "0", "0")]).Count == 1, "5整孔有效");
Check(ModelRules.Validate([Row("5", "1", "1")]).Count == 1, "5孔左右双半孔有效");
Reject(() => ModelRules.Validate([Row("5", "1", "0")]), "整数单侧半孔必须拒绝");
Reject(() => ModelRules.Validate([Row("5.2", "1", "0")]), "非半整数孔数拒绝");
Reject(() => ModelRules.Validate([ModelRules.DefaultRow("a"), ModelRules.DefaultRow("A")]), "Windows编号不区分大小写");
Reject(() => ModelRules.Validate([ModelRules.DefaultRow("a/b"), ModelRules.DefaultRow("a_b")]), "清理文件名后的重名必须拒绝");
Check(ModelRules.Validate([ModelRules.DefaultRow("CON")])[0].Name == "_CON", "保留设备名称处理");
var badFlag = ModelRules.DefaultRow(); badFlag[6] = "2"; Reject(() => ModelRules.Validate([badFlag]), "开关非0/1拒绝");
var badSize = ModelRules.DefaultRow(); badSize[2] = "-10"; Reject(() => ModelRules.Validate([badSize]), "负尺寸拒绝");
var bevel = ModelRules.DefaultRow(); bevel[10] = "1"; bevel[12] = "0"; Reject(() => ModelRules.Validate([bevel]), "零尺寸斜边拒绝");

string temp = Path.Combine(Path.GetTempPath(), "solidcraft-checks-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temp);
try
{
    string csv = Path.Combine(temp, "参数.csv"); var quoted = ModelRules.DefaultRow("型号,一\"A\"");
    CsvFiles.Write(csv, [quoted, Row("4.5", "1", "0")]);
    Check(File.ReadAllBytes(csv).Take(3).SequenceEqual(new byte[] { 0xef, 0xbb, 0xbf }), "CSV UTF8 BOM");
    var decoded = CsvFiles.Read(csv); Check(decoded.Count == 2 && decoded[0].SequenceEqual(quoted), "CSV转义和多行往返");
    string plain = File.ReadAllText(csv);
    foreach (var encoding in new[] { Encoding.GetEncoding("GB18030"), Encoding.Unicode, Encoding.BigEndianUnicode })
    {
        File.WriteAllText(csv, plain, encoding); Check(CsvFiles.Read(csv)[0].SequenceEqual(quoted), "中文CSV编码兼容：" + encoding.WebName);
    }
    File.WriteAllText(csv, "编号,孔数\n1,5\n"); Reject(() => CsvFiles.Read(csv), "缺列CSV必须拒绝");

    var bounds = ModelRules.ReadBounds(Json("""{"bodies":[{"bbox":{"min":[-20,-2,-3],"max":[100,3,4]}},{"bbox":[[99,-8,0],[150,10,9]]}]}"""));
    Check(bounds.Min.SequenceEqual(new double[] { -20, -8, -3 }) && bounds.Max.SequenceEqual(new double[] { 150, 10, 9 }) && bounds.Bodies == 2, "组合包围盒合并");
    Check(ModelRules.TranslationX(bounds, 180) == 200, "负原点偏移正确");
    Reject(() => ModelRules.ReadBounds(Json("""{"bodies":[{}]}""")), "缺失包围盒拒绝");
    ModelRules.RequireClearInterference(Json("""{"count":0,"checked":2,"interferences":[],"unchecked":[],"pairs_not_checked":0}"""), 2); checks++;
    foreach (var data in new[] {
        """{"count":1,"checked":2,"interferences":[{"a":"a","b":"b"}],"unchecked":[]}""",
        """{"count":0,"checked":2,"interferences":[],"unchecked":[{"reason":"budget"}]}""",
        """{"count":0,"checked":2,"interferences":[],"unchecked":[],"truncated":true}""",
        """{"count":0,"checked":1,"interferences":[],"unchecked":[]}""",
        """{"overlaps":[],"unchecked":[]}""",
        """{"count":0,"checked":2,"interferences":[],"unchecked":[],"budget_exceeded":true}""" })
        Reject(() => ModelRules.RequireClearInterference(Json(data), 2), "未知或未完成相交检查必须拒绝");

    var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
    string tokenFile = Path.Combine(temp, "token"); string token = new('a', 64); File.WriteAllText(tokenFile, token);
    var server = Task.Run(async () =>
    {
        using var peer = await listener.AcceptTcpClientAsync();
        using var reader = new StreamReader(peer.GetStream(), Encoding.UTF8, false, 4096, true);
        using var writer = new StreamWriter(peer.GetStream(), new UTF8Encoding(false), 4096, true) { AutoFlush = true };
        var first = Json((await reader.ReadLineAsync())!);
        if (first.GetProperty("token").GetString() != token || first.GetProperty("method").GetString() != "engine.execute") throw new Exception("首请求认证错误");
        int originalId = first.GetProperty("id").GetInt32();
        await writer.WriteLineAsync(JsonSerializer.Serialize(new { id = originalId, ok = false, running = true, error = "timeout" }));
        var pending = Json((await reader.ReadLineAsync())!);
        if (pending.GetProperty("method").GetString() != "control.pending") throw new Exception("超时命令被重复提交");
        await writer.WriteLineAsync(JsonSerializer.Serialize(new { id = pending.GetProperty("id").GetInt32(), ok = true, result = new { pending = originalId, done = true, reply = new { id = originalId, ok = true, result = new { saved = true } } } }));
    });
    try
    {
        using var client = await ControlClient.ConnectAsync(port, tokenFile);
        var answer = await client.ExecuteAsync("file.export", new { path = "stub.step" });
        Check(answer.GetProperty("saved").GetBoolean(), "长任务通过pending拿结果，修改命令只发送一次");
        await server.WaitAsync(TimeSpan.FromSeconds(10));
    }
    finally { listener.Stop(); }
}
finally { Directory.Delete(temp, true); }
checks += DimensionChecks.Run(args.Length > 0 ? args[0] : null);
Console.WriteLine($"PASS: {checks} checks; no CAD models generated.");
