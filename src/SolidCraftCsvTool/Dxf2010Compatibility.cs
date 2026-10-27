using System.Globalization;
using System.Text;

namespace SolidCraftCsvTool;

internal static class Dxf2010Compatibility
{
    public static void NormalizeRasterOwner(string path)
    {
        // netDxf 2023.11.10 emits the default RASTERVARIABLES owner as 0.
        // The ACAD_IMAGE_VARS dictionary entry must own this object instead.
        string[] lines = File.ReadAllLines(path, Encoding.UTF8);
        if (lines.Length % 2 != 0) throw new InvalidDataException("DXF 输出含有不完整的组码。");
        var records = new List<(string Type, int Start, int End)>();
        string type = ""; int start = 0;
        for (int i = 0; i < lines.Length; i += 2)
        {
            if (int.Parse(lines[i].Trim(), CultureInfo.InvariantCulture) != 0) continue;
            if (type.Length > 0) records.Add((type, start, i));
            type = lines[i + 1].Trim(); start = i;
        }
        if (type.Length > 0) records.Add((type, start, lines.Length));
        string? Value((string Type, int Start, int End) record, int code)
        {
            for (int i = record.Start + 2; i < record.End; i += 2)
                if (int.Parse(lines[i].Trim(), CultureInfo.InvariantCulture) == code) return lines[i + 1].Trim();
            return null;
        }
        string? root = records.Where(r => r.Type == "DICTIONARY" && Value(r, 330) == "0")
            .Select(r => Value(r, 5)).SingleOrDefault();
        if (string.IsNullOrEmpty(root)) throw new InvalidDataException("DXF 输出缺少根字典句柄。");
        bool changed = false;
        foreach (var record in records.Where(r => r.Type == "RASTERVARIABLES"))
        {
            bool found = false;
            for (int i = record.Start + 2; i < record.End; i += 2)
            {
                if (int.Parse(lines[i].Trim(), CultureInfo.InvariantCulture) != 330) continue;
                found = true;
                if (lines[i + 1].Trim() != root) { lines[i + 1] = root; changed = true; }
                break;
            }
            if (!found) throw new InvalidDataException("DXF 栅格设置对象缺少所属引用。");
        }
        // netDxf 2023.11.10 writes polyline HATCH paths with group 73 = 0 even
        // when the source path is closed. CAD then closes the loop with a straight
        // chord and can fill the logo's open area. Restore the required closed flag.
        foreach (var record in records.Where(r => r.Type == "HATCH"))
        {
            for (int i = record.Start + 2; i < record.End; i += 2)
            {
                if (int.Parse(lines[i].Trim(), CultureInfo.InvariantCulture) != 92) continue;
                int flags = int.Parse(lines[i + 1].Trim(), CultureInfo.InvariantCulture);
                if ((flags & 2) == 0) continue;
                int limit = record.End;
                for (int j = i + 2; j < record.End; j += 2)
                    if (int.Parse(lines[j].Trim(), CultureInfo.InvariantCulture) == 92) { limit = j; break; }
                bool foundClosedFlag = false;
                for (int j = i + 2; j < limit; j += 2)
                {
                    if (int.Parse(lines[j].Trim(), CultureInfo.InvariantCulture) != 73) continue;
                    foundClosedFlag = true;
                    if (lines[j + 1].Trim() != "1") { lines[j + 1] = "1"; changed = true; }
                    break;
                }
                if (!foundClosedFlag) throw new InvalidDataException("DXF 填充边界缺少闭合标记。");
            }
        }
        if (changed) File.WriteAllLines(path, lines, new UTF8Encoding(false));
    }
}
