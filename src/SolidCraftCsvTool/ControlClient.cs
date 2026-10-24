using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace SolidCraftCsvTool;

public sealed class ControlClient : IDisposable
{
    readonly TcpClient tcp;
    readonly StreamReader reader;
    readonly StreamWriter writer;
    readonly string token;
    int sequence;

    ControlClient(TcpClient tcp, string token)
    {
        this.tcp = tcp; this.token = token;
        reader = new(tcp.GetStream(), new UTF8Encoding(false, true), false, 4096, true);
        writer = new(tcp.GetStream(), new UTF8Encoding(false), 4096, true) { NewLine = "\n", AutoFlush = true };
    }

    public static async Task<ControlClient> ConnectAsync(int port, string? tokenPath = null)
    {
        string controlDirectory = Environment.GetEnvironmentVariable("SOLIDCRAFT_CONTROL_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SolidCraft", "control");
        tokenPath ??= Path.Combine(controlDirectory, $"{port}.token");
        if (!File.Exists(tokenPath)) throw new IOException($"找不到控制令牌。请先保存设计，再用 --control {port} 启动 SolidCraft。\n{tokenPath}");
        string token = (await File.ReadAllTextAsync(tokenPath)).Trim();
        if (token.Length < 32) throw new IOException("SolidCraft 控制令牌无效。");
        TcpClient tcp = new();
        try { using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(8)); await tcp.ConnectAsync(IPAddress.Loopback, port, timeout.Token); return new(tcp, token); }
        catch { tcp.Dispose(); throw new IOException($"无法连接 SolidCraft 本机端口 {port}。请确认桌面版已用 --control {port} 启动。"); }
    }

    async Task<JsonElement> ExchangeAsync(string method, object parameters)
    {
        int id = ++sequence;
        Dictionary<string, object> request = new() { ["id"] = id, ["method"] = method, ["params"] = parameters };
        if (id == 1) request["token"] = token;
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(90));
        try
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), timeout.Token);
            string? line = await reader.ReadLineAsync(timeout.Token);
            if (line == null) throw new IOException("SolidCraft 关闭了控制连接。");
            if (line.Length > 16 * 1024 * 1024) throw new IOException("控制响应超过大小限制。");
            using var doc = JsonDocument.Parse(line);
            var response = doc.RootElement;
            if (!response.TryGetProperty("id", out var replyId) || replyId.GetInt32() != id) throw new IOException("控制响应编号不匹配，已停止操作。");
            return response.Clone();
        }
        catch (OperationCanceledException) { throw new IOException($"等待 {method} 的响应超时。SolidCraft 可能仍在执行，请检查桌面状态后再操作。"); }
    }

    public async Task<JsonElement> RequestAsync(string method, object parameters)
    {
        var response = await ExchangeAsync(method, parameters);
        if (response.TryGetProperty("running", out var running) && running.ValueKind == JsonValueKind.True)
        {
            int pendingId = response.GetProperty("id").GetInt32();
            var elapsed = Stopwatch.StartNew();
            do
            {
                if (elapsed.Elapsed > TimeSpan.FromHours(4)) throw new IOException("SolidCraft 命令仍未完成。已停止等待，请在桌面检查；不要重复提交。");
                await Task.Delay(1000);
                var poll = Unwrap(await ExchangeAsync("control.pending", new { }));
                if (!poll.TryGetProperty("pending", out var pending) || pending.ValueKind == JsonValueKind.Null || pending.GetInt32() != pendingId)
                    throw new IOException("无法追踪 SolidCraft 正在执行的命令，已停止操作。");
                if (poll.TryGetProperty("done", out var done) && done.ValueKind == JsonValueKind.True)
                    return Unwrap(poll.GetProperty("reply"));
            } while (true);
        }
        return Unwrap(response);
    }

    static JsonElement Unwrap(JsonElement response)
    {
        if (!response.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
            throw new IOException("SolidCraft 请求失败：" + (response.TryGetProperty("error", out var error) ? error.ToString() : response.ToString()));
        if (!response.TryGetProperty("result", out var result)) throw new IOException("SolidCraft 没有返回命令结果。");
        return result.Clone();
    }

    public Task<JsonElement> ExecuteAsync(string command, object parameters) => RequestAsync("engine.execute", new { command, @params = parameters });
    public Task<JsonElement> InspectAsync() => RequestAsync("document.inspect", new { measure = true });
    public void Dispose() { writer.Dispose(); reader.Dispose(); tcp.Dispose(); }
}
