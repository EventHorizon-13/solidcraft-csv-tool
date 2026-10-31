using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;

namespace SolidCraftCsvTool;

internal static class Program
{
    static readonly HashSet<string> Flags = ["--combine-step", "--export-current-csv", "--export-dxf", "--export-pdf", "--overwrite"];
    static readonly HashSet<string> Values = ["--csv", "--template", "--output", "--engine", "--drawing-template", "--step-name", "--dxf-name", "--stamp", "--dimension-style", "--hidden-line-scale", "--view-gap", "--font-dir"];

    static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args.Contains("--help", StringComparer.Ordinal) || args.Contains("-h", StringComparer.Ordinal))
        {
            PrintHelp();
            return 0;
        }
        if (args.Length == 1 && args[0] == "--version")
        {
            Console.WriteLine(typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
                ?? typeof(Program).Assembly.GetName().Version?.ToString(2) ?? "未知");
            return 0;
        }

        using CancellationTokenSource cancellation = new();
        Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
        try
        {
            Dictionary<string, string> options = Parse(args);
            bool exportPdf = options.ContainsKey("--export-pdf");
            bool exportDxf = options.ContainsKey("--export-dxf") || exportPdf;
            string csvPath = RequiredPath(options, "--csv");
            string templatePath = options.ContainsKey("--template") ? RequiredPath(options, "--template") :
                LocalTemplates.Find(AppContext.BaseDirectory, Environment.CurrentDirectory).Native ?? throw new FileNotFoundException("找不到内置标准辊密封模板，请完整解压软件包，或用 --template 指定模板。");
            string outputPath = Path.GetFullPath(options.GetValueOrDefault("--output") ?? Path.GetDirectoryName(csvPath)!);
            string engine = Path.GetFullPath(options.GetValueOrDefault("--engine") ?? Path.Combine(AppContext.BaseDirectory, "SolidCraft", "solidcraft-cli"));
            if (!File.Exists(engine)) throw new FileNotFoundException("找不到 SolidCraft CLI。请保留软件包中的 SolidCraft 文件夹，或用 --engine 指定路径。", engine);
            if (!File.Exists(csvPath)) throw new FileNotFoundException("找不到输入 CSV。", csvPath);
            if (!File.Exists(templatePath)) throw new FileNotFoundException("找不到 SolidCraft 模板。", templatePath);
            if (options.ContainsKey("--drawing-template") && !exportDxf)
                throw new ArgumentException("指定 --drawing-template 时还需要 --export-dxf 或 --export-pdf。");
            if (options.ContainsKey("--step-name") && !options.ContainsKey("--combine-step"))
                throw new ArgumentException("指定 --step-name 时还需要 --combine-step。");
            if (options.ContainsKey("--dxf-name") && !exportDxf)
                throw new ArgumentException("指定 --dxf-name 时还需要 --export-dxf 或 --export-pdf。");
            if (options.ContainsKey("--font-dir") && !options.ContainsKey("--export-pdf"))
                throw new ArgumentException("指定 --font-dir 时还需要 --export-pdf。");
            if ((options.ContainsKey("--dimension-style") || options.ContainsKey("--hidden-line-scale") || options.ContainsKey("--view-gap")) && !exportDxf)
                throw new ArgumentException("标注样式和出图间距参数需要同时启用 --export-dxf。");

            string stamp = options.GetValueOrDefault("--stamp") ?? OutputNames.Stamp;
            if (!Regex.IsMatch(stamp, "^[A-Za-z0-9-]{1,16}$")) throw new ArgumentException("--stamp 仅允许 1 至 16 位英文字母、数字或连字符。");
            string[][] csvRows = CsvFiles.Read(csvPath).Select(ModelRules.ExpandRow).ToArray();
            List<PreparedRow> rows = ModelRules.Validate(csvRows);
            ControlClient.HeadlessExecutable = engine;

            bool overwrite = options.ContainsKey("--overwrite");
            bool combine = options.ContainsKey("--combine-step");
            bool exportCsv = options.ContainsKey("--export-current-csv");
            DrawingOptions? drawing = null;
            if (exportDxf)
            {
                drawing = new DrawingOptions
                {
                    OverwriteOutput = overwrite,
                    OutputFileName = options.GetValueOrDefault("--dxf-name") ?? OutputNames.DefaultBase(stamp),
                    TemplatePath = options.GetValueOrDefault("--drawing-template") ?? "",
                    FontDirectory = options.GetValueOrDefault("--font-dir") ?? (exportPdf ? Path.Combine(AppContext.BaseDirectory, "模板", "字体") : ""),
                    DimensionStyleName = options.GetValueOrDefault("--dimension-style") ?? "_5",
                    HiddenLineScale = Number(options, "--hidden-line-scale", 2),
                    ViewGap = Number(options, "--view-gap", 60)
                };
                drawing.Validate();
                string dxfPath = Path.Combine(outputPath, drawing.ResolvedFileName);
                if (!overwrite && File.Exists(dxfPath)) throw new IOException("汇总 DXF 已存在，未覆盖原文件：" + dxfPath);
                if (exportPdf)
                {
                    if (string.IsNullOrWhiteSpace(drawing.TemplatePath))
                        throw new ArgumentException("PDF 需要指定固定 A4 图框 DXF 模板：--drawing-template PATH。");
                    _ = DrawingResources.Load(drawing);
                    PdfExportService.ValidateFontDirectory(drawing.FontDirectory);
                    string pdfPath = Path.Combine(outputPath, Path.ChangeExtension(drawing.ResolvedFileName, ".pdf"));
                    if (!overwrite && File.Exists(pdfPath)) throw new IOException("汇总 PDF 已存在，未覆盖原文件：" + pdfPath);
                }
            }
            var progress = new InlineProgress<ExportProgress>(entry => Console.WriteLine(entry.Message));
            var export = new ExportOptions(rows, templatePath, outputPath, 7878, 10, combine, overwrite,
                stamp, options.GetValueOrDefault("--step-name"), exportCsv, csvRows, ExportStep: !exportDxf || combine, Drawing: drawing, ExportPdf: exportPdf);
            await Exporter.RunAsync(export, progress, cancellation.Token);
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("已取消；已生成的文件保留在输出目录。");
            return 130;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("错误：" + ex.Message);
            return 1;
        }
    }

    static Dictionary<string, string> Parse(string[] args)
    {
        Dictionary<string, string> result = new(StringComparer.Ordinal);
        for (int index = 0; index < args.Length; index++)
        {
            string name = args[index];
            if (Flags.Contains(name))
            {
                if (!result.TryAdd(name, "true")) throw new ArgumentException($"参数重复：{name}");
                continue;
            }
            if (!Values.Contains(name)) throw new ArgumentException($"未知参数：{name}。运行 --help 查看用法。");
            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"参数 {name} 缺少值。");
            if (!result.TryAdd(name, args[++index])) throw new ArgumentException($"参数重复：{name}");
        }
        return result;
    }

    static string RequiredPath(IReadOnlyDictionary<string, string> options, string name)
    {
        if (!options.TryGetValue(name, out string? path) || string.IsNullOrWhiteSpace(path)) throw new ArgumentException($"必须指定 {name}。");
        return Path.GetFullPath(path);
    }

    static double Number(IReadOnlyDictionary<string, string> options, string name, double fallback)
    {
        if (!options.TryGetValue(name, out string? value)) return fallback;
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) || !double.IsFinite(parsed))
            throw new ArgumentException($"参数 {name} 必须是有效数字。");
        return parsed;
    }

    static void PrintHelp()
    {
        Console.WriteLine($"标准辊密封批量生成 CLI {typeof(Program).Assembly.GetName().Version?.ToString(2)}");
        Console.WriteLine("用法：standard-roller-seal --csv 参数.csv [--template 模板.solidcraft] [选项]");
        Console.WriteLine();
        Console.WriteLine("必需参数：");
        Console.WriteLine("  --csv PATH                 输入参数 CSV");
        Console.WriteLine();
        Console.WriteLine("选项：");
        Console.WriteLine("  --template PATH            自选 V13 SolidCraft 模板；默认内置标准辊密封模板");
        Console.WriteLine("  --output DIR               输出目录，默认与 CSV 相同");
        Console.WriteLine("  --combine-step             另行导出合并 STEP（需要单件 STEP，出图仍直接读取 .solidcraft）");
        Console.WriteLine("  --step-name NAME           合并 STEP 文件名，需同时启用 --combine-step");
        Console.WriteLine("  --export-current-csv       输出本次采用的完整参数 CSV");
        Console.WriteLine("  --export-dxf               从原生 .solidcraft 直接生成汇总 DXF，默认跳过中间 STEP 导出");
        Console.WriteLine("  --export-pdf               按每张固定 A4 图框另导出多页 PDF（同时生成 DXF）");
        Console.WriteLine("  --drawing-template PATH    图框/标注样式 DXF 模板；默认无图框");
        Console.WriteLine("  --font-dir PATH            外置 SHX 字体目录，需含 gbenor.shx 和 gbcbig.shx；默认 模板/字体");
        Console.WriteLine("  --dimension-style NAME     标注样式，默认 _5");
        Console.WriteLine("  --dxf-name NAME            汇总 DXF 文件名");
        Console.WriteLine("  --hidden-line-scale N      隐藏线比例，默认 2");
        Console.WriteLine("  --view-gap MM              视图间距，默认 60");
        Console.WriteLine("  --stamp TEXT               文件名前缀日期，默认当前 MMDD");
        Console.WriteLine("  --engine PATH              SolidCraft CLI 路径；默认软件包内的 SolidCraft/solidcraft-cli");
        Console.WriteLine("  --overwrite                覆盖同名文件");
        Console.WriteLine("  --version                  显示版本");
        Console.WriteLine("  --help                     显示帮助");
    }
}

internal sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
{
    public void Report(T value) => callback(value);
}
