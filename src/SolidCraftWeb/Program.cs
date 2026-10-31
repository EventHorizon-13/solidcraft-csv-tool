using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.Features;

using SolidCraftCsvTool;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 1024 * 1024;
    options.ValueLengthLimit = 128 * 1024;
});
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 2 * 1024 * 1024);

var app = builder.Build();
var jobs = new JobStore(AppContext.BaseDirectory);
var apiToken = Environment.GetEnvironmentVariable("ROLLERSEAL_WEB_TOKEN");

app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    context.Response.Headers["Content-Security-Policy"] =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'";
    if (context.Request.Path.StartsWithSegments("/api", StringComparison.Ordinal))
        context.Response.Headers["Cache-Control"] = "no-store";
    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles();

bool Authorized(HttpRequest request)
{
    if (string.IsNullOrEmpty(apiToken))
        return request.HttpContext.Connection.RemoteIpAddress is IPAddress address && IPAddress.IsLoopback(address);
    if (!request.Headers.TryGetValue("X-RollerSeal-Token", out var supplied)) return false;
    byte[] actual = Encoding.UTF8.GetBytes(supplied.ToString());
    byte[] expected = Encoding.UTF8.GetBytes(apiToken);
    return actual.Length == expected.Length && CryptographicOperations.FixedTimeEquals(actual, expected);
}

IResult Denied() => Results.Json(new { error = "未授权。请配置正确的 Web API 访问令牌。" }, statusCode: StatusCodes.Status401Unauthorized);

app.MapGet("/api/bootstrap", (HttpRequest request) =>
{
    if (!Authorized(request)) return Denied();
    var local = LocalTemplates.Find(AppContext.BaseDirectory, AppContext.BaseDirectory);
    return Results.Json(new
    {
        version = "0.1.0-preview",
        columns = ModelRules.Columns,
        flags = ModelRules.Flags.OrderBy(x => x).ToArray(),
        defaultRow = ModelRules.DefaultRow(),
        defaults = new
        {
            combineStep = true,
            exportCsv = true,
            exportPdf = false,
            dxfName = OutputNames.DefaultBase(),
            stepName = OutputNames.DefaultBase(),
            hiddenLineScale = 1,
            viewGap = 60,
            dimensionStyle = "_5"
        },
        capabilities = new
        {
            solidCraft = File.Exists(JobStore.EnginePath(AppContext.BaseDirectory)),
            drawingTemplate = local.Dxf != null,
            pdf = local.Dxf != null && Directory.Exists(Path.Combine(AppContext.BaseDirectory, "模板", "字体"))
        }
    });
});

app.MapPost("/api/csv/parse", async (HttpRequest request, CancellationToken cancellation) =>
{
    if (!Authorized(request)) return Denied();
    if (!request.HasFormContentType) return Results.BadRequest(new { error = "请上传 CSV 文件。" });
    var form = await request.ReadFormAsync(cancellation);
    var file = form.Files.GetFile("file");
    if (file == null || file.Length == 0) return Results.BadRequest(new { error = "CSV 文件为空。" });
    if (file.Length > 512 * 1024) return Results.BadRequest(new { error = "CSV 文件不能超过 512 KiB。" });
    if (!Path.GetExtension(file.FileName).Equals(".csv", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { error = "只允许上传 .csv 文件。" });

    string temporary = Path.Combine(Path.GetTempPath(), "roller-seal-" + Guid.NewGuid().ToString("N") + ".csv");
    try
    {
        await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous))
            await file.CopyToAsync(stream, cancellation);
        var rows = CsvFiles.Read(temporary).Select(ModelRules.ExpandRow).ToArray();
        ModelRules.Validate(rows);
        return Results.Json(new { rows });
    }
    catch (Exception ex) when (ex is InvalidDataException or DecoderFallbackException)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    finally
    {
        try { File.Delete(temporary); } catch (IOException) { }
    }
});

app.MapPost("/api/validate", (HttpRequest request, JobRequest input) =>
{
    if (!Authorized(request)) return Denied();
    try
    {
        var rows = JobStore.NormalizeAndValidate(input.Rows);
        return Results.Json(new { ok = true, count = rows.Count });
    }
    catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapPost("/api/jobs", (HttpRequest request, JobRequest input) =>
{
    if (!Authorized(request)) return Denied();
    try
    {
        var state = jobs.Create(input);
        return Results.Json(new { id = state.Id, status = state.Status });
    }
    catch (Exception ex) when (ex is InvalidDataException or ArgumentException or FileNotFoundException or DirectoryNotFoundException)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapGet("/api/jobs/{id}", (HttpRequest request, string id) =>
{
    if (!Authorized(request)) return Denied();
    return jobs.TryGet(id, out var state)
        ? Results.Json(state.Snapshot())
        : Results.NotFound(new { error = "任务不存在或已清理。" });
});

app.MapPost("/api/jobs/{id}/cancel", (HttpRequest request, string id) =>
{
    if (!Authorized(request)) return Denied();
    if (!jobs.TryGet(id, out var state)) return Results.NotFound(new { error = "任务不存在。" });
    state.Cancel();
    return Results.Json(new { ok = true });
});

app.MapGet("/api/jobs/{id}/download", (HttpRequest request, string id) =>
{
    if (!Authorized(request)) return Denied();
    if (!jobs.TryGet(id, out var state) || state.Status != "completed" || state.DownloadPath == null || !File.Exists(state.DownloadPath))
        return Results.NotFound(new { error = "任务结果尚不可下载。" });
    return Results.File(state.DownloadPath, "application/zip", "辊密封_" + id[..8] + ".zip", enableRangeProcessing: true);
});

app.MapFallbackToFile("index.html");
app.Run();

public sealed record JobRequest(
    string[][] Rows,
    bool CombineStep = true,
    bool ExportCsv = true,
    bool ExportDxf = false,
    bool ExportPdf = false,
    string? DxfName = null,
    string? StepName = null,
    string? Stamp = null,
    double HiddenLineScale = 1,
    double ViewGap = 60,
    string DimensionStyle = "_5");

public sealed class JobStore
{
    readonly ConcurrentDictionary<string, JobState> jobs = new(StringComparer.Ordinal);
    readonly string root;
    readonly SemaphoreSlim gate = new(1, 1);

    public JobStore(string applicationRoot)
    {
        root = Path.Combine(applicationRoot, "WebData", "jobs");
        Directory.CreateDirectory(root);
    }

    public static string EnginePath(string applicationRoot) =>
        Environment.GetEnvironmentVariable("SOLIDCRAFT_CLI") ??
        Path.Combine(applicationRoot, "SolidCraft", OperatingSystem.IsWindows() ? "solidcraft-cli.exe" : "solidcraft-cli");

    public static List<PreparedRow> NormalizeAndValidate(string[][] rows)
    {
        if (rows == null || rows.Length == 0) throw new InvalidDataException("请至少添加一行参数。");
        if (rows.Length > 500) throw new InvalidDataException("单次任务最多允许 500 行。");
        foreach (var row in rows)
        {
            if (row == null || row.Length > ModelRules.Columns.Length + 1) throw new InvalidDataException("参数列数不正确。");
            if (row.Any(cell => cell != null && cell.Length > 200)) throw new InvalidDataException("单元格内容过长。");
        }
        return ModelRules.Validate(rows.Select(row => ModelRules.ExpandRow(row.Select(cell => cell ?? "").ToArray())));
    }

    public JobState Create(JobRequest request)
    {
        List<PreparedRow> prepared = NormalizeAndValidate(request.Rows);
        string stamp = string.IsNullOrWhiteSpace(request.Stamp) ? OutputNames.Stamp : request.Stamp.Trim();
        if (!Regex.IsMatch(stamp, "^[A-Za-z0-9-]{1,16}$")) throw new ArgumentException("日期/前缀只允许 1 至 16 位英文字母、数字或连字符。");
        if (!double.IsFinite(request.HiddenLineScale) || !double.IsFinite(request.ViewGap))
            throw new ArgumentException("出图参数必须是有效数字。");

        string engine = EnginePath(AppContext.BaseDirectory);
        if (!File.Exists(engine)) throw new FileNotFoundException("找不到 SolidCraft CLI。", engine);
        var local = LocalTemplates.Find(AppContext.BaseDirectory, AppContext.BaseDirectory);
        string template = local.Native ?? throw new FileNotFoundException("找不到内置标准辊密封模板。");

        string id = Guid.NewGuid().ToString("N");
        string directory = Path.Combine(root, id);
        string output = Path.Combine(directory, "output");
        Directory.CreateDirectory(output);

        DrawingOptions? drawing = null;
        if (request.ExportDxf || request.ExportPdf)
        {
            if (request.ExportPdf && local.Dxf == null)
                throw new FileNotFoundException("PDF 导出需要安装包中的固定 A4 DXF 图框模板。");
            string fontDirectory = Path.Combine(AppContext.BaseDirectory, "模板", "字体");
            drawing = new DrawingOptions
            {
                OverwriteOutput = true,
                OutputFileName = request.DxfName,
                TemplatePath = local.Dxf ?? "",
                FontDirectory = request.ExportPdf ? fontDirectory : "",
                DimensionStyleName = string.IsNullOrWhiteSpace(request.DimensionStyle) ? "_5" : request.DimensionStyle.Trim(),
                HiddenLineScale = request.HiddenLineScale,
                ViewGap = request.ViewGap,
                IncludeFrame = local.Dxf != null
            };
            drawing.Validate();
            if (request.ExportPdf) PdfExportService.ValidateFontDirectory(fontDirectory);
        }

        string? stepName = string.IsNullOrWhiteSpace(request.StepName) ? null : OutputNames.FileName(request.StepName, ".step", stamp)[..^5];
        if (!string.IsNullOrWhiteSpace(request.DxfName)) _ = OutputNames.FileName(request.DxfName, ".dxf", stamp);

        var state = new JobState(id);
        if (!jobs.TryAdd(id, state)) throw new IOException("无法创建任务。");

        var options = new ExportOptions(
            prepared,
            template,
            output,
            7878,
            10,
            request.CombineStep,
            true,
            stamp,
            stepName,
            request.ExportCsv,
            request.Rows,
            ExportStep: !request.ExportDxf || request.CombineStep,
            Drawing: drawing,
            ExportPdf: request.ExportPdf);

        _ = Task.Run(() => RunAsync(state, options, engine, directory, output));
        return state;
    }

    async Task RunAsync(JobState state, ExportOptions options, string engine, string directory, string output)
    {
        bool entered = false;
        try
        {
            await gate.WaitAsync(state.Token);
            entered = true;
            state.SetRunning();
            ControlClient.HeadlessExecutable = engine;
            var progress = new Progress<ExportProgress>(entry => state.Report(entry.Message, entry.Completed, entry.Log));
            await Exporter.RunAsync(options, progress, state.Token);

            string zip = Path.Combine(directory, "result.zip");
            if (File.Exists(zip)) File.Delete(zip);
            ZipFile.CreateFromDirectory(output, zip, CompressionLevel.Fastest, false);
            state.Complete(zip);
        }
        catch (OperationCanceledException)
        {
            state.MarkCancelled();
        }
        catch (Exception ex)
        {
            state.Fail(ex.Message);
        }
        finally
        {
            if (entered) gate.Release();
        }
    }

    public bool TryGet(string id, out JobState state)
    {
        state = null!;
        if (id.Length != 32 || !id.All(Uri.IsHexDigit)) return false;
        return jobs.TryGetValue(id, out state!);
    }
}

public sealed class JobState
{
    readonly object sync = new();
    readonly List<string> logs = [];
    readonly CancellationTokenSource cancellation = new();

    public string Id { get; }
    public string Status { get; private set; } = "queued";
    public string Message { get; private set; } = "等待执行";
    public int Completed { get; private set; }
    public string? DownloadPath { get; private set; }
    public CancellationToken Token => cancellation.Token;

    public JobState(string id) => Id = id;

    public void SetRunning()
    {
        lock (sync) { Status = "running"; Message = "开始生成"; }
    }

    public void Report(string message, int completed, bool log)
    {
        lock (sync)
        {
            Message = message;
            Completed = Math.Max(Completed, completed);
            if (log)
            {
                logs.Add(message);
                if (logs.Count > 200) logs.RemoveRange(0, logs.Count - 200);
            }
        }
    }

    public void Complete(string path)
    {
        lock (sync) { DownloadPath = path; Status = "completed"; Message = "生成完成"; }
    }

    public void Fail(string message)
    {
        lock (sync) { Status = "failed"; Message = message; logs.Add("错误：" + message); }
    }

    public void MarkCancelled()
    {
        lock (sync) { Status = "cancelled"; Message = "任务已取消"; }
    }

    public void Cancel() => cancellation.Cancel();

    public object Snapshot()
    {
        lock (sync)
        {
            return new
            {
                id = Id,
                status = Status,
                message = Message,
                completed = Completed,
                logs = logs.ToArray(),
                downloadable = Status == "completed" && DownloadPath != null
            };
        }
    }
}
