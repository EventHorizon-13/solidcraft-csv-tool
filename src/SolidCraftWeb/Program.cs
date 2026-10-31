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
    options.MultipartBodyLengthLimit = WebResources.MaxBodyBytes;
    options.ValueLengthLimit = 128 * 1024;
});
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = WebResources.MaxBodyBytes);
builder.Services.AddSingleton(new BrowserGenerationRecordStore(AppContext.BaseDirectory));

var app = builder.Build();
var resources = new WebResources(AppContext.BaseDirectory);
var jobs = new JobStore(AppContext.BaseDirectory, resources);
var apiToken = Environment.GetEnvironmentVariable("ROLLERSEAL_WEB_TOKEN");

app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    context.Response.Headers["Content-Security-Policy"] =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'";
    if (context.Request.Path.StartsWithSegments("/api", StringComparison.Ordinal))
    {
        context.Response.Headers["Cache-Control"] = "no-store";
        if (string.Equals(context.Request.Path.Value, "/api/generation-records", StringComparison.Ordinal))
        {
            string requestOrigin = context.Request.Headers["Origin"].ToString();
            bool trustedSite = requestOrigin is "https://iobyte.cn" or "https://www.iobyte.cn";
            if (trustedSite)
            {
                context.Response.Headers["Access-Control-Allow-Origin"] = requestOrigin;
                context.Response.Headers["Access-Control-Allow-Methods"] = "POST, OPTIONS";
                context.Response.Headers["Access-Control-Allow-Headers"] = "Content-Type";
                context.Response.Headers["Access-Control-Max-Age"] = "600";
                context.Response.Headers["Vary"] = "Origin";
            }
            if (HttpMethods.IsOptions(context.Request.Method))
            {
                context.Response.StatusCode = trustedSite ? StatusCodes.Status204NoContent : StatusCodes.Status403Forbidden;
                return;
            }
        }
    }
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
        version = "0.1.5-preview",
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
            hiddenLineScale = 2,
            viewGap = 60,
            dimensionStyle = "_5"
        },
        capabilities = new
        {
            solidCraft = File.Exists(JobStore.EnginePath(AppContext.BaseDirectory)),
            drawingTemplate = local.Dxf != null,
            pdf = File.Exists(PdfExportService.BundledFontPath) || resources.Resolve(null).FontsReady
        }
    });
});

app.MapGet("/api/resources/{id}", (HttpRequest request, string id) =>
{
    if (!Authorized(request)) return Denied();
    try { return Results.Json(resources.Resolve(id).Snapshot()); }
    catch (Exception ex) when (ex is InvalidDataException or FileNotFoundException)
    { return Results.NotFound(new { error = ex.Message }); }
});

app.MapPost("/api/resources", async (HttpRequest request, CancellationToken cancellation) =>
{
    if (!Authorized(request)) return Denied();
    if (!request.HasFormContentType) return Results.BadRequest(new { error = "请使用 multipart/form-data 上传图框和字体。" });
    try
    {
        var form = await request.ReadFormAsync(cancellation);
        return Results.Json((await resources.UploadAsync(form, cancellation)).Snapshot());
    }
    catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
    { return Results.Json(new { error = "上传请求不能超过 24 MiB。" }, statusCode: 413); }
    catch (Exception ex) when (ex is InvalidDataException or FileNotFoundException or ArgumentException)
    { return Results.BadRequest(new { error = ex.Message }); }
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
    return Results.File(state.DownloadPath, state.ContentType, state.DownloadName, enableRangeProcessing: true);
});

app.MapPost("/api/generation-records", async (HttpContext context, BrowserGenerationRecordRequest input, BrowserGenerationRecordStore store, CancellationToken cancellation) =>
{
    if (!context.Request.Headers.TryGetValue("Origin", out var originValue) ||
        !Uri.TryCreate(originValue.ToString(), UriKind.Absolute, out var origin))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    bool trustedSiteOrigin = origin.Scheme == Uri.UriSchemeHttps &&
        (string.Equals(origin.Authority, "iobyte.cn", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(origin.Authority, "www.iobyte.cn", StringComparison.OrdinalIgnoreCase));
    bool sameOrigin = string.Equals(origin.Authority, context.Request.Host.Value, StringComparison.OrdinalIgnoreCase);
    if (!trustedSiteOrigin && !sameOrigin)
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (context.Request.ContentLength is > 2_097_152)
        return Results.Json(new { error = "生成记录不能超过 2 MiB。" }, statusCode: StatusCodes.Status413PayloadTooLarge);
    if (input.Rows == null || input.Rows.Length == 0 || input.Rows.Length > 500)
        return Results.BadRequest(new { error = "生成记录必须包含 1 至 500 行参数。" });
    if (!DateTimeOffset.TryParse(input.GeneratedAtUtc, out var generatedAt))
        return Results.BadRequest(new { error = "生成时间格式无效。" });
    foreach (var row in input.Rows)
    {
        if (row == null || row.Length != 18 || row.Any(cell => cell == null || cell.Length > 200))
            return Results.BadRequest(new { error = "生成参数格式无效。" });
    }

    string clientIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    if (context.Connection.RemoteIpAddress is IPAddress peer && IPAddress.IsLoopback(peer) &&
        context.Request.Headers.TryGetValue("X-Forwarded-For", out var forwarded))
    {
        var addresses = forwarded.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (int index = addresses.Length - 1; index >= 0; index--)
        {
            if (IPAddress.TryParse(addresses[index], out var parsed))
            {
                clientIp = parsed.IsIPv4MappedToIPv6 ? parsed.MapToIPv4().ToString() : parsed.ToString();
                break;
            }
        }
    }
    var record = new BrowserGenerationRecord(DateTimeOffset.UtcNow, generatedAt, clientIp, input.Rows);
    await store.AppendAsync(record, cancellation);
    return Results.Json(new { ok = true, recordedAtUtc = record.RecordedAtUtc }, statusCode: StatusCodes.Status201Created);
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
    double HiddenLineScale = 2,
    double ViewGap = 60,
    string DimensionStyle = "_5",
    string? ResourceId = null,
    string? OutputFormat = null,
    bool IncludeFrame = true);

public sealed class JobStore
{
    readonly ConcurrentDictionary<string, JobState> jobs = new(StringComparer.Ordinal);
    readonly string root;
    readonly WebResources resources;
    readonly SemaphoreSlim gate = new(1, 1);

    public JobStore(string applicationRoot, WebResources resources)
    {
        this.resources = resources;
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
        string? format = request.OutputFormat?.ToLowerInvariant();
        if (format != null && format is not ("step" or "dxf" or "pdf")) throw new InvalidDataException("输出格式仅支持 step、dxf、pdf。");
        if (format != null) request = request with {
            ExportDxf = format is "dxf" or "pdf", ExportPdf = format == "pdf",
            CombineStep = format == "step" && request.CombineStep, ExportCsv = format == "step" && request.ExportCsv };
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

        DrawingOptions? drawing = null;
        if (request.ExportDxf || request.ExportPdf)
        {
            var selected = resources.Resolve(request.ResourceId);
            string fontDirectory = selected.FontDirectory;
            drawing = new DrawingOptions
            {
                OverwriteOutput = true,
                OutputFileName = request.DxfName ?? "",
                TemplatePath = selected.Dxf ?? "",
                FontDirectory = fontDirectory,
                UseBundledFont = true,
                UseDefaultSourceHan = !selected.HasUploadedFonts,
                DimensionStyleName = string.IsNullOrWhiteSpace(request.DimensionStyle) ? "_5" : request.DimensionStyle.Trim(),
                HiddenLineScale = request.HiddenLineScale,
                ViewGap = request.ViewGap,
                IncludeFrame = request.IncludeFrame && selected.Dxf != null
            };
            drawing.Validate();
            if (selected.Dxf != null) _ = DrawingResources.Load(drawing);
            if (request.ExportPdf) PdfExportService.ValidateFontDirectory(fontDirectory);
        }

        string? stepName = string.IsNullOrWhiteSpace(request.StepName) ? null : OutputNames.FileName(request.StepName, ".step", stamp)[..^5];
        if (!string.IsNullOrWhiteSpace(request.DxfName)) _ = OutputNames.FileName(request.DxfName, ".dxf", stamp);

        Directory.CreateDirectory(output);
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

        _ = Task.Run(() => RunAsync(state, options, engine, directory, output, format));
        return state;
    }

    async Task RunAsync(JobState state, ExportOptions options, string engine, string directory, string output, string? format)
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

            if (format is "pdf" or "dxf")
            {
                string name = Path.ChangeExtension(options.Drawing!.ResolvedFileName, "." + format);
                string file = Path.Combine(output, name);
                if (!File.Exists(file)) throw new FileNotFoundException("未生成所选格式文件。");
                state.Complete(file, format == "pdf" ? "application/pdf" : "application/dxf", name);
                return;
            }
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
    public string ContentType { get; private set; } = "application/zip";
    public string DownloadName { get; private set; } = "result.zip";
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
            if (log && !message.StartsWith("PDF 已保存：", StringComparison.Ordinal) &&
                !message.StartsWith("PDF 已保存:", StringComparison.Ordinal))
            {
                logs.Add(message);
                if (logs.Count > 200) logs.RemoveRange(0, logs.Count - 200);
            }
        }
    }

    public void Complete(string path, string contentType = "application/zip", string? name = null)
    {
        lock (sync) { DownloadPath = path; ContentType = contentType; DownloadName = name ?? "辊密封_" + Id[..8] + ".zip"; Status = "completed"; Message = "生成完成"; }
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
                downloadName = DownloadName,
                contentType = ContentType,
                downloadable = Status == "completed" && DownloadPath != null
            };
        }
    }
}



