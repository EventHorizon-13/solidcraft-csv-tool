using System.Text;
using netDxf;

// Uploads are immutable resource sets outside wwwroot. Jobs refer to an opaque ID,
// never a client-supplied filesystem path. Re-uploading creates a new set.
public sealed class WebResources
{
    readonly string root;
    readonly string applicationRoot;
    public const long MaxBodyBytes = 20 * 1024 * 1024;
    public WebResources(string applicationRoot)
    {
        this.applicationRoot = applicationRoot;
        root = Path.Combine(applicationRoot, "WebData", "resources");
        Directory.CreateDirectory(root);
    }

    public ResourcePaths Resolve(string? id)
    {
        var local = SolidCraftCsvTool.LocalTemplates.Find(applicationRoot, applicationRoot);
        if (string.IsNullOrEmpty(id))
            return new(null, local.Dxf, Path.Combine(applicationRoot, "模板", "字体"));
        if (id.Length != 32 || !id.All(Uri.IsHexDigit))
            throw new InvalidDataException("资源编号无效，请重新上传图框和字体。");
        string directory = Path.Combine(root, id);
        if (!File.Exists(Path.Combine(directory, "ready")))
            throw new FileNotFoundException("上传资源不存在，请重新上传图框和字体。");
        return new(id, File.Exists(Path.Combine(directory, "frame.dxf")) ? Path.Combine(directory, "frame.dxf") : null,
            Path.Combine(directory, "fonts"));
    }

    public async Task<ResourcePaths> UploadAsync(IFormCollection form, CancellationToken cancellation)
    {
        if (form.Files.Count is < 1 or > 3) throw new InvalidDataException("请选择一个 DXF 图框及最多两个 SHX 字体文件。");
        if (form.Files.Sum(f => f.Length) > 16 * 1024 * 1024) throw new InvalidDataException("上传文件合计不能超过 16 MiB。");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in form.Files)
        {
            string name = file.FileName;
            if (name.Contains('/') || name.Contains('\\') || name.Contains(':')) throw new InvalidDataException("文件名不能包含路径。");
            string target;
            long limit;
            if (file.Name == "drawingTemplate" && Path.GetExtension(name).Equals(".dxf", StringComparison.OrdinalIgnoreCase))
            { target = "frame.dxf"; limit = 8 * 1024 * 1024; }
            else if (file.Name == "fonts" && (name.Equals("gbenor.shx", StringComparison.OrdinalIgnoreCase) || name.Equals("gbcbig.shx", StringComparison.OrdinalIgnoreCase)))
            { target = name.ToLowerInvariant(); limit = 4 * 1024 * 1024; }
            else throw new InvalidDataException("仅接受 drawingTemplate 字段的 .dxf 和 fonts 字段的 gbenor.shx、gbcbig.shx。");
            if (!names.Add(target)) throw new InvalidDataException("同名资源只能上传一次。");
            if (file.Length <= 0 || file.Length > limit) throw new InvalidDataException("DXF 最大 8 MiB，每个字体最大 4 MiB，文件不能为空。");
        }
        var previous = Resolve(form["resourceId"].ToString());
        string id = Guid.NewGuid().ToString("N");
        string directory = Path.Combine(root, id);
        string fonts = Path.Combine(directory, "fonts");
        Directory.CreateDirectory(fonts);
        try
        {
            if (previous.Dxf != null) File.Copy(previous.Dxf, Path.Combine(directory, "frame.dxf"));
            foreach (string font in new[] { "gbenor.shx", "gbcbig.shx" })
            {
                string source = Path.Combine(previous.FontDirectory, font);
                if (!File.Exists(source)) source = Path.Combine(previous.FontDirectory, "fonts", font);
                if (File.Exists(source)) File.Copy(source, Path.Combine(fonts, font));
            }
            foreach (var file in form.Files)
            {
                string target = file.Name == "drawingTemplate" ? Path.Combine(directory, "frame.dxf") : Path.Combine(fonts, file.FileName.ToLowerInvariant());
                await using (var stream = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
                    await file.CopyToAsync(stream, cancellation);
                if (file.Name == "drawingTemplate")
                {
                    try
                    {
                        if (DxfDocument.Load(target) == null) throw new InvalidDataException();
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
                    { throw new InvalidDataException("无法读取 DXF 图框，请上传有效的 DXF 文件。", ex); }
                }
                else
                {
                    byte[] header = new byte[64];
                    using var stream = File.OpenRead(target);
                    int length = stream.Read(header);
                    string signature = Encoding.ASCII.GetString(header, 0, length);
                    if (!signature.StartsWith("AutoCAD-86 ", StringComparison.Ordinal) ||
                        !(signature.Contains("shapes") || signature.Contains("bigfont") || signature.Contains("unifont")) || !header.Take(length).Contains((byte)26))
                        throw new InvalidDataException("字体不是有效的编译 SHX 文件，请上传您获授权的原始 SHX 字体。");
                }
            }
            await File.WriteAllTextAsync(Path.Combine(directory, "ready"), "1", cancellation);
            return Resolve(id);
        }
        catch
        {
            Directory.Delete(directory, true);
            throw;
        }
    }
}

public sealed record ResourcePaths(string? Id, string? Dxf, string FontDirectory)
{
    public bool FontsReady => new[] { "gbenor.shx", "gbcbig.shx" }.All(font =>
        File.Exists(Path.Combine(FontDirectory, font)) || File.Exists(Path.Combine(FontDirectory, "fonts", font)));
    public object Snapshot() => new { resourceId = Id, drawingTemplate = Dxf != null, fonts = new {
        gbenor = File.Exists(Path.Combine(FontDirectory, "gbenor.shx")) || File.Exists(Path.Combine(FontDirectory, "fonts", "gbenor.shx")),
        gbcbig = File.Exists(Path.Combine(FontDirectory, "gbcbig.shx")) || File.Exists(Path.Combine(FontDirectory, "fonts", "gbcbig.shx")) }, pdf = Dxf != null && FontsReady };
}
