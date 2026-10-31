using System.Globalization;
using System.Text.Json;

namespace SolidCraftCsvTool;

public sealed record ExportOptions(List<PreparedRow> Rows, string Template, string Output, int Port, double Gap, bool Combined, bool Overwrite, string? DateStamp=null, string? CombinedFileName=null, bool ExportCsv=false, IReadOnlyList<string[]>? CsvRows=null, bool ExportStep=true, DrawingOptions? Drawing=null, bool ExportPdf=false);
public sealed record ExportProgress(string Message, int Completed = 0, bool Log = true);

public static class Exporter
{
    public static async Task RunAsync(ExportOptions options, IProgress<ExportProgress> progress, CancellationToken cancellation)
    {
        string stamp=options.DateStamp??OutputNames.Stamp;
        string combinedName=OutputNames.FileName(options.CombinedFileName,".step",stamp);
        string csvName=OutputNames.DefaultBase(stamp)+".csv";
        if(options.Combined&&!options.ExportStep)throw new InvalidDataException("生成组合 STEP 时必须启用单件 STEP 导出。");
        if(options.Drawing!=null)StepDrawingExporter.ValidateOutputTargets(options.Output,options.Drawing,options.ExportPdf);
        else if(options.ExportPdf)throw new InvalidDataException("导出 PDF 时必须提供加工图设置。");
        if(options.Combined&&options.Rows.Any(row=>(OutputNames.Part(row.Name,stamp)+".step").Equals(combinedName,StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("组合 STEP 名不能与单件 STEP 名相同。");
        string dataRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(dataRoot)) dataRoot = Path.GetTempPath();
        string work = Path.GetFullPath(Path.Combine(dataRoot, "SolidCraft", "csv-tool", "run-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(work);
        bool success = false;
        try
        {
            string[] targets = options.Rows.Select(row => Path.Combine(options.Output, OutputNames.Part(row.Name,stamp) + ".solidcraft"))
                .Concat(options.ExportStep ? options.Rows.Select(row => Path.Combine(options.Output, OutputNames.Part(row.Name,stamp) + ".step")) : Array.Empty<string>())
                .Concat(options.Combined ? [Path.Combine(options.Output, combinedName)] : Array.Empty<string>())
                .Concat(options.ExportCsv ? [Path.Combine(options.Output,csvName)] : Array.Empty<string>())
                .Concat(options.Drawing!=null ? [Path.Combine(options.Output,options.Drawing.ResolvedFileName)] : Array.Empty<string>())
                .Concat(options.Drawing!=null&&options.ExportPdf ? [Path.Combine(options.Output,Path.ChangeExtension(options.Drawing.ResolvedFileName,".pdf"))] : Array.Empty<string>()).ToArray();
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
            var missing = ModelRules.RequiredParameterNames.Append("半孔校验").Where(p => !names.Contains(p)).ToArray();
            if (missing.Length > 0) throw new IOException("请使用 V13 模板，缺少参数：" + string.Join("、", missing));
            // Capture expressions once from the user's selected template. Restore
            // them for every row before applying optional explicit dimensions.
            var defaults=new Dictionary<string,string>();
            foreach(var parameter in parameterList.EnumerateArray())
            {
                string name=parameter.GetProperty("name").GetString()!;
                if(!ModelRules.OptionalParameterNames.Contains(name))continue;
                if(parameter.TryGetProperty("expression",out var expression))
                    defaults[name]=expression.ValueKind==JsonValueKind.String?expression.GetString()!:expression.GetRawText();
                else if(parameter.TryGetProperty("value",out var value)&&TryNumber(value,out double numeric))
                    defaults[name]=numeric.ToString("R",CultureInfo.InvariantCulture);
                else throw new IOException("无法读取模板默认尺寸："+name);
            }
            foreach(var row in options.Rows)_=ModelRules.ChangesWithDefaults(row,defaults);
            progress.Report(new("已连接 SolidCraft，模板副本与默认子母槽尺寸已读取。"));
            List<(PreparedRow Row, string Path, Bounds Box)> pieces = [];
            List<StepDrawingPage> drawingPages = [];
            int completed = 0;
            List<string[]> snapshotRows=[];
            foreach (var row in options.Rows)
            {
                cancellation.ThrowIfCancellationRequested();
                progress.Report(new($"正在生成 {completed + 1}/{options.Rows.Count}：{row.Name}", completed));
                await client.ExecuteAsync("parameters.change", new { changes = ModelRules.ChangesWithDefaults(row,defaults) });
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
                if(options.ExportCsv)
                {
                    string[] snapshot=options.CsvRows!=null?ModelRules.ExpandRow(options.CsvRows[completed]).ToArray():
                        new[]{row.Name}.Concat(ModelRules.ParameterNames.Select(p=>row.Parameters.GetValueOrDefault(p,""))).ToArray();
                    var actual=check.GetProperty("parameters").EnumerateArray().ToDictionary(p=>p.GetProperty("name").GetString()!);
                    for(int column=ModelRules.RequiredColumnCount;column<ModelRules.Columns.Length;column++)
                    {
                        string name=ModelRules.ParameterNames[column-1];
                        if(actual.TryGetValue(name,out var parameter)&&parameter.TryGetProperty("value",out var actualValue)&&TryNumber(actualValue,out double numeric))
                            snapshot[column]=numeric.ToString("0.########",CultureInfo.InvariantCulture);
                    }
                    snapshotRows.Add(snapshot);
                }
                Bounds? bounds = options.Combined ? ModelRules.ReadBounds(info) : null;
                cancellation.ThrowIfCancellationRequested();
                string native = Path.Combine(work, OutputNames.Part(row.Name,stamp) + ".solidcraft"), step = Path.Combine(work, OutputNames.Part(row.Name,stamp) + ".step");
                await client.ExecuteAsync("file.save_as", new { path = native, format = "container", cache = true, preview = true });
                if(options.Drawing!=null)drawingPages.Add(await StepDrawingExporter.CaptureCurrentAsync(client,work,completed,row.Name,progress,cancellation));
                if(options.ExportStep)await client.ExecuteAsync("file.export", new { path = step, format = "step" });
                PublishFile(native, Path.Combine(options.Output, Path.GetFileName(native)), options.Overwrite);
                if(options.ExportStep)PublishFile(step, Path.Combine(options.Output, Path.GetFileName(step)), options.Overwrite);
                completed++;
                progress.Report(new(options.ExportStep ? $"已导出 {row.Name}：SolidCraft + STEP" : $"已导出 {row.Name}：SolidCraft（跳过 STEP 中间文件）", completed));
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
                    await client.ExecuteAsync("occurrence.ground",new{occurrence,grounded=false});
                    await client.ExecuteAsync("occurrence.move", new { occurrence, space = "world", translate = new double[] { offset, 0, 0 } });
                    cursor += piece.Box.Max[0] - piece.Box.Min[0] + options.Gap;
                    progress.Report(new($"已将 {piece.Row.Name} 沿 X 方向移至组合位置，目标间距 {options.Gap:0.###} mm。", completed));
                }
                var assemblyInfo = await client.InspectAsync();
                var assemblyBounds = ModelRules.ReadBounds(assemblyInfo);
                int expected = pieces.Sum(p => p.Box.Bodies);
                if (assemblyBounds.Bodies != expected) throw new IOException("组合中的实体数量与单件不一致，已停止组合导出。");
                progress.Report(new("正在检查组合实体相交…", completed));
                cancellation.ThrowIfCancellationRequested();
                var interference = await client.ExecuteAsync("inspect.interference", new { budget_s = 240 });
                ModelRules.RequireClearInterference(interference, expected);
                cancellation.ThrowIfCancellationRequested();
                string combined = Path.Combine(work, combinedName);
                await client.ExecuteAsync("file.export", new { path = combined, format = "step" });
                PublishFile(combined, Path.Combine(options.Output, Path.GetFileName(combined)), options.Overwrite);
                progress.Report(new("组合 STEP 已导出，实体数量一致且相交检查通过。", completed));
            }
            if(options.Drawing!=null)
                await StepDrawingExporter.WritePagesAsync(drawingPages,options.Output,work,progress,cancellation,options.Drawing,options.ExportPdf);
            if(options.ExportCsv)
            {
                var csvRows=snapshotRows;
                string stagedCsv=Path.Combine(work,csvName);CsvFiles.Write(stagedCsv,csvRows);
                PublishFile(stagedCsv,Path.Combine(options.Output,csvName),options.Overwrite);
                progress.Report(new("当前任务 CSV 已保存："+csvName,completed));
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

