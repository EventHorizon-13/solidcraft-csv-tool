using SolidCraftCsvTool;
using System.Diagnostics;

// Exercise the real subprocess boundary without licensed fonts or a PDF engine.
// The renderer stub rejects relative arguments and verifies the manifest paths.
string node = Environment.GetEnvironmentVariable("PDF_PATH_TEST_NODE")
    ?? throw new InvalidOperationException("Set PDF_PATH_TEST_NODE to the absolute Node.js executable.");
string runtime = Path.Combine(AppContext.BaseDirectory, "PdfExporter");
Directory.CreateDirectory(runtime);
File.CreateSymbolicLink(Path.Combine(runtime, "node"), node);
await File.WriteAllTextAsync(Path.Combine(runtime, "pdf-exporter.bundle.mjs"), """
import fs from 'node:fs';
import path from 'node:path';
const manifest = process.argv[3];
if (!path.isAbsolute(manifest)) throw new Error('Manifest path must be absolute');
const data = JSON.parse(fs.readFileSync(manifest, 'utf8'));
for (const item of [...data.inputs, data.output, data.fontDirectory]) {
  if (!path.isAbsolute(item)) throw new Error('Relative manifest entry');
}
for (const input of data.inputs) fs.accessSync(input);
fs.accessSync(path.join(data.fontDirectory, 'gbenor.shx'));
fs.writeFileSync(data.output, '%PDF-1.4\nPath-boundary test stub\n');
""");
string previous = Environment.CurrentDirectory;
string root = Path.Combine(Path.GetTempPath(), "PDF 路径 check " + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    Environment.CurrentDirectory = root;
    foreach (bool absolute in new[] { false, true })
    {
        string work = absolute ? Path.Combine(root, "absolute run") : Path.Combine("相对目录", "run");
        Directory.CreateDirectory(work);
        string fonts = Path.Combine(work, "字体");
        Directory.CreateDirectory(fonts);
        foreach (string font in new[] { "gbenor.shx", "gbcbig.shx" })
            await File.WriteAllTextAsync(Path.Combine(fonts, font), "test placeholder, not a real font");
        string input = Path.Combine(work, "加工图.dxf");
        string output = Path.Combine(work, "结果.pdf");
        await File.WriteAllTextAsync(input, "DXF path-boundary fixture");
        await PdfExportService.ExportAsync([input], output, fonts, work,
            new Progress<ExportProgress>(_ => { }), CancellationToken.None);
        if (!File.Exists(output)) throw new Exception("PDF stub output missing");
    }
    Console.WriteLine("PDF subprocess absolute/relative paths, spaces and Unicode checks passed.");
}
finally
{
    Environment.CurrentDirectory = previous;
    Directory.Delete(root, true);
}
namespace SolidCraftCsvTool { internal sealed record ExportProgress(string Message, int Completed = 0, bool Log = false); }
