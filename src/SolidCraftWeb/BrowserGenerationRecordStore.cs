using System.Text;
using System.Text.Json;

public sealed record BrowserGenerationRecordRequest(string? GeneratedAtUtc, string[][]? Rows);

public sealed record BrowserGenerationRecord(
    DateTimeOffset RecordedAtUtc,
    DateTimeOffset GeneratedAtUtc,
    string ClientIp,
    string[][] Rows);

public sealed class BrowserGenerationRecordStore
{
    static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    readonly string path;
    readonly SemaphoreSlim gate = new(1, 1);

    public BrowserGenerationRecordStore(string applicationRoot)
    {
        string directory = Path.Combine(applicationRoot, "WebData");
        Directory.CreateDirectory(directory);
        path = Path.Combine(directory, "browser-generation-records.jsonl");
    }

    public async Task AppendAsync(BrowserGenerationRecord record, CancellationToken cancellation)
    {
        await gate.WaitAsync(cancellation);
        try
        {
            string line = JsonSerializer.Serialize(record, JsonOptions);
            await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            await writer.WriteLineAsync(line.AsMemory(), cancellation);
        }
        finally
        {
            gate.Release();
        }
    }
}
