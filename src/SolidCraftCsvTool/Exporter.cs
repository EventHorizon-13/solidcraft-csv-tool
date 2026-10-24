using System.Globalization;
using System.Text.Json;

namespace SolidCraftCsvTool;

public sealed record ExportOptions(List<PreparedRow> Rows, string Template, string Output, int Port, double Gap, bool Combined, bool Overwrite);
public sealed record ExportProgress(string Message, int Completed = 0, bool Log = true);

public static class Exporter
{
    public static async Task RunAsync(ExportOptions options, IProgress<ExportProgress> progress, CancellationToken cancellation)
    {
        string work = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SolidCraft", "csv-tool", "run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        bool success = false;
        try
        {
            string[] targets = options.Rows.SelectMany(row => new[] { Path.Combine(options.Output, row.Name + "_辊密封.solidcraft"), Path.Combine(options.Output, row.Name + "_辊密封.step") })
                .Concat(options.Combined ? [Path.Combine(options.Output, "辊密封_组合.step")] : Array.Empty<string>()).ToArray();
            if (!options.Overwrite && targets.Any(File.Exists)) throw new IOException("已有同名输出，请更换输出目录，或勾选“覆盖已有文件”。");
            Directory.CreateDirectory(options.Output);
            string template = Path.Combine(work, "template.solidcraft");
            File.Copy(options.Template, template);
            cancellation.ThrowIfCancellationRequested();
            using var client = await ControlClient.ConnectAsync(options.Port);
            var running = await client.RequestAsync("control.running", new { });
            if (!running.TryGetProperty("running", out var jobs) || jobs.ValueKind != JsonValueKind.Array || jobs.GetArrayLength() > 0)
                throw new IOException("SolidCraft 当前正在执行其他命令，请完成后再导出。");
            await client.ExecuteAsync("doc.open", new { path = template });
            var parameterResult = await client.ExecuteAsync("parameters.list", new { filter = "user" });
            if (!parameterResult.TryGetProperty("parameters", out var parameterList) || parameterList.ValueKind != JsonValueKind.Array)
                throw new IOException("无法读取模板参数。");
            var names = parameterList.EnumerateArray().Select(p => p.GetProperty("name").GetString()).ToHashSet();
            var missing = ModelRules.ParameterNames.Append("半孔校验").Where(p => !names.Contains(p)).ToArray();
            if (missing.Length > 0) throw new IOException("请使用 V13 模板，缺少参数：" + string.Join("、", missing));
            progress.Report(new("已连接 SolidCraft，已在新标签打开模板副本。"));
            List<(PreparedRow Row, string Path, Bounds Box)> pieces = [];
            int completed = 0;
            foreach (var row in options.Rows)
            {
                cancellation.ThrowIfCancellationRequested();
                progress.Report(new($"正在生成 {completed + 1}/{options.Rows.Count}：{row.Name}", completed));
                await client.ExecuteAsync("parameters.change", new { changes = row.Parameters });
                var check = await client.ExecuteAsync("parameters.list", new { filter = "user" });
                var checks = check.GetProperty("parameters").EnumerateArray().Where(p => p.GetProperty("name").GetString() == "半孔校验").ToArray();
                if (checks.Length != 1 || !checks[0].TryGetProperty("value", out var value) || !TryNumber(value, out double half) || Math.Abs(half) > 1e-9)
                    throw new IOException($"型号 {row.Name} 的半孔校验未通过。");
                var info = await client.InspectAsync();
                if (!info.TryGetProperty("bodies", out var bodies) || bodies.ValueKind != JsonValueKind.Array || bodies.GetArrayLength() == 0)
                    throw new IOException($"型号 {row.Name} 没有有效实体。");
                if (info.TryGetProperty("timeline", out var timeline))
                    foreach (var feature in timeline.EnumerateArray())
                        if (feature.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null && !string.IsNullOrWhiteSpace(error.ToString()))
                            throw new IOException($"型号 {row.Name} 重算失败：{error}");
                Bounds? bounds = options.Combined ? ModelRules.ReadBounds(info) : null;
                cancellation.ThrowIfCancellationRequested();
                string native = Path.Combine(work, row.Name + "_辊密封.solidcraft"), step = Path.Combine(work, row.Name + "_辊密封.step");
                await client.ExecuteAsync("file.save_as", new { path = native, format = "container", cache = true, preview = true });
                await client.ExecuteAsync("file.export", new { path = step, format = "step" });
                PublishFile(native, Path.Combine(options.Output, Path.GetFileName(native)), options.Overwrite);
                PublishFile(step, Path.Combine(options.Output, Path.GetFileName(step)), options.Overwrite);
                completed++;
                progress.Report(new($"已导出 {row.Name}：SolidCraft + STEP", completed));
                if (bounds != null) pieces.Add((row, step, bounds));
            }
            if (options.Combined)
            {
                cancellation.ThrowIfCancellationRequested();
                await client.ExecuteAsync("file.new", new { name = "辊密封组合" });
                double cursor = 0;
                foreach (var piece in pieces)
                {
                    cancellation.ThrowIfCancellationRequested();
                    double offset = ModelRules.TranslationX(piece.Box, cursor);
                    var inserted = await client.ExecuteAsync("file.insert_step", new { path = piece.Path, name = piece.Row.Name, up = "z" });
                    var occurrence = ModelRules.ReadOccurrenceReference(inserted);
                    await client.ExecuteAsync("occurrence.move", new { occurrence, space = "world", translate = new double[] { offset, 0, 0 } });
                    cursor += piece.Box.Max[0] - piece.Box.Min[0] + options.Gap;
                    int expectedPlacedBodies = pieces.TakeWhile(p => p.Row != piece.Row).Sum(p => p.Box.Bodies) + piece.Box.Bodies;
                    var placed = ModelRules.ReadBounds(await client.InspectAsync());
                    ModelRules.RequirePackedBounds(placed, expectedPlacedBodies, cursor - options.Gap);
                    progress.Report(new($"已核对 {piece.Row.Name} 的世界坐标摆放，X 范围延伸至 {cursor - options.Gap:0.###} mm。", completed));
                }
                var assemblyBounds = ModelRules.ReadBounds(await client.InspectAsync());
                int expected = pieces.Sum(p => p.Box.Bodies);
                if (assemblyBounds.Bodies != expected) throw new IOException("组合中的实体数量与单件不一致，已停止组合导出。");
                ModelRules.RequirePackedBounds(assemblyBounds, expected, cursor - options.Gap);
                progress.Report(new("正在检查组合实体相交…", completed));
                cancellation.ThrowIfCancellationRequested();
                var interference = await client.ExecuteAsync("inspect.interference", new { budget_s = 240 });
                ModelRules.RequireClearInterference(interference, expected);
                cancellation.ThrowIfCancellationRequested();
                string combined = Path.Combine(work, "辊密封_组合.step");
                await client.ExecuteAsync("file.export", new { path = combined, format = "step" });
                PublishFile(combined, Path.Combine(options.Output, Path.GetFileName(combined)), options.Overwrite);
                progress.Report(new("组合 STEP 已导出，实体数量一致且相交检查通过。", completed));
            }
            success = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new IOException(ex.Message + "\n本次临时文件保留在：" + work, ex);
        }
        finally
        {
            if (success) { try { Directory.Delete(work, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            else progress.Report(new("临时文件已保留：" + work));
        }
    }

    static bool TryNumber(JsonElement value, out double number)
    {
        if (value.ValueKind == JsonValueKind.Number) return value.TryGetDouble(out number) && double.IsFinite(number);
        return double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number) && double.IsFinite(number);
    }

    static void PublishFile(string source, string destination, bool overwrite)
    {
        if (!File.Exists(source) || new FileInfo(source).Length == 0) throw new IOException("SolidCraft 没有写出有效文件：" + source);
        string temporary = Path.Combine(Path.GetDirectoryName(destination)!, "." + Path.GetFileName(destination) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try { File.Copy(source, temporary); File.Move(temporary, destination, overwrite); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
