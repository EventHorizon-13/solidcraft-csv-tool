using System.Globalization;
using System.Text.RegularExpressions;

namespace SolidCraftCsvTool;

public static class OutputNames
{
    public static string Stamp => DateTime.Now.ToString("MMdd",CultureInfo.InvariantCulture);
    public static string DefaultBase(string? stamp=null)=>"辊密封_"+(stamp??Stamp);
    public static string FileName(string? value,string extension,string? stamp=null)
    {
        string name=(value??"").Trim();
        if(name.EndsWith(extension,StringComparison.OrdinalIgnoreCase))name=name[..^extension.Length];
        if(name.Length==0)name=DefaultBase(stamp);
        if(name.Length>180 || name.TrimEnd(' ','.')!=name || Regex.IsMatch(name,@"[<>:""/\\|?*\x00-\x1f]") ||
            Regex.IsMatch(name.Split('.')[0],@"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$",RegexOptions.IgnoreCase))
            throw new InvalidDataException("文件名无效，请只填写文件名，不要包含路径或特殊字符。");
        return name+extension;
    }
    public static string Part(string id,string? stamp=null)=>DefaultBase(stamp)+"_"+id;
}
