namespace SolidCraftCsvTool;

internal static class LocalTemplates
{
    public static (string? Native,string? Dxf) Find(string applicationDirectory,string workingDirectory)
    {
        string? native=null,dxf=null;
        foreach(string root in new[]{applicationDirectory,workingDirectory}.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string folder=Path.Combine(root,"模板");if(!Directory.Exists(folder))continue;
            string[] files=Directory.GetFiles(folder);
            native??=files.Where(p=>Path.GetExtension(p).Equals(".solidcraft",StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(p=>Path.GetFileName(p).Contains("V13",StringComparison.OrdinalIgnoreCase))
                .ThenBy(p=>Path.GetFileName(p),StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            dxf??=files.Where(p=>Path.GetExtension(p).Equals(".dxf",StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(p=>Path.GetFileName(p).Contains("模板")||Path.GetFileName(p).Contains("图框"))
                .ThenBy(p=>Path.GetFileName(p),StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        }
        return(native,dxf);
    }
}
