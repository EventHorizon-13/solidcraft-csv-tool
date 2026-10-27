using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.VisualBasic.FileIO;

namespace SolidCraftCsvTool;

public sealed record PreparedRow(string Name, Dictionary<string, string> Parameters);
public sealed record Bounds(double[] Min, double[] Max, int Bodies);

public static class ModelRules
{
    public static readonly string[] Columns = ["编号", "孔数", "孔径", "孔距", "高度", "宽度", "子头", "母槽", "左半圆", "右半圆", "左斜边", "右斜边", "斜边Y", "斜边Z", "子头宽", "子头突出", "母槽宽", "母槽深", "配合间隙"];
    public static readonly HashSet<string> Flags = ["子头", "母槽", "左半圆", "右半圆", "左斜边", "右斜边"];
    public static readonly string[] ParameterNames = ["孔数", "辊径", "辊距", "实体高度", "密封宽度", "子头开关", "母槽开关", "左半孔开关", "右半孔开关", "左斜边开关", "右斜边开关", "斜边Y", "斜边Z", "子头宽", "子头突出", "母槽宽", "母槽深", "配合间隙"];
    public const int RequiredColumnCount=14;
    public static readonly string[] RequiredParameterNames=ParameterNames.Take(RequiredColumnCount-1).ToArray();
    public static readonly string[] OptionalParameterNames=ParameterNames.Skip(RequiredColumnCount-1).ToArray();
    public static string[] DefaultRow(string name = "1") => [name, "5", "90", "120", "105", "200", "1", "1", "0", "0", "0", "0", "30", "30", "", "", "", "", ""];
    public static string[] ExpandRow(string[] row)
    {
        if(row.Length==Columns.Length)return row;
        if(row.Length==RequiredColumnCount)return row.Concat(Enumerable.Repeat("",Columns.Length-RequiredColumnCount)).ToArray();
        throw new InvalidDataException("参数列数不正确，请使用CSV模板。");
    }

    public static List<PreparedRow> Validate(IEnumerable<string[]> source)
    {
        List<PreparedRow> result = [];
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (var cells in source)
        {
            int line = result.Count + 2;
            var row = ExpandRow(cells).Select(x => x.Trim()).ToArray();
            if (row.Take(RequiredColumnCount).Any(string.IsNullOrWhiteSpace)) throw new InvalidDataException($"第 {line} 行有空白参数。");
            string name = Regex.Replace(row[0], "[<>:\"/\\\\|?*\\x00-\\x1f]", "_").Trim(' ', '.');
            if (name.Length == 0) throw new InvalidDataException($"第 {line} 行编号无效。");
            if (Regex.IsMatch(name.Split('.')[0], "^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase)) name = "_" + name;
            if (!names.Add(name)) throw new InvalidDataException($"第 {line} 行编号重复：{name}");
            var numbers = new decimal[Columns.Length];
            for (int i = 1; i < Columns.Length; i++)
            {
                if(i>=RequiredColumnCount&&row[i].Length==0)continue;
                if (!decimal.TryParse(row[i], NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out numbers[i]))
                    throw new InvalidDataException($"第 {line} 行 {Columns[i]} 不是有效数字。");
                if (Flags.Contains(Columns[i]) && numbers[i] != 0 && numbers[i] != 1)
                    throw new InvalidDataException($"第 {line} 行 {Columns[i]} 只能为 0 或 1。");
                row[i] = numbers[i].ToString(CultureInfo.InvariantCulture);
            }
            for(int i=RequiredColumnCount;i<Columns.Length;i++)
                if(row[i].Length>0&&(numbers[i]<0||(Columns[i]!="配合间隙"&&numbers[i]==0)))
                    throw new InvalidDataException($"第 {line} 行 {Columns[i]} 必须为正数（配合间隙可为0）。");
            decimal holes = numbers[1];
            if (holes <= 0 || holes % 0.5m != 0) throw new InvalidDataException($"第 {line} 行孔数只允许正整数或 x.5。");
            decimal ends = numbers[8] + numbers[9];
            if (holes % 1 == 0.5m && ends != 1) throw new InvalidDataException($"第 {line} 行 {holes} 孔必须只选左半孔或右半孔。");
            if (holes % 1 == 0 && ends == 1) throw new InvalidDataException($"第 {line} 行整数孔数须左右都不选，或左右都选半孔。");
            for (int i = 2; i <= 5; i++) if (numbers[i] <= 0) throw new InvalidDataException($"第 {line} 行 {Columns[i]} 必须大于 0。");
            for (int i = 12; i <= 13; i++) if (numbers[i] < 0) throw new InvalidDataException($"第 {line} 行 {Columns[i]} 不能小于 0。");
            if ((numbers[10] == 1 || numbers[11] == 1) && (numbers[12] == 0 || numbers[13] == 0))
                throw new InvalidDataException($"第 {line} 行启用斜边时，斜边Y、斜边Z都必须大于 0。");
            result.Add(new(name, ParameterNames.Select((p, i) => (p, value: row[i + 1])).Where(x=>x.value.Length>0).ToDictionary(x => x.p, x => x.value)));
        }
        if (result.Count == 0) throw new InvalidDataException("请先添加参数行。");
        return result;
    }

    public static Dictionary<string,string> ChangesWithDefaults(PreparedRow row,IReadOnlyDictionary<string,string> defaults)
    {
        var unsupported=OptionalParameterNames.Where(name=>row.Parameters.ContainsKey(name)&&!defaults.ContainsKey(name)).ToArray();
        if(unsupported.Length>0)throw new InvalidDataException("模板不含这些子母槽参数："+string.Join("、",unsupported));
        var changes=defaults.ToDictionary(p=>p.Key,p=>p.Value);
        foreach(var pair in row.Parameters)changes[pair.Key]=pair.Value;
        return changes;
    }

    public static Bounds ReadBounds(JsonElement doc)
    {
        if (!doc.TryGetProperty("bodies", out var bodies) || bodies.ValueKind != JsonValueKind.Array || bodies.GetArrayLength() == 0)
            throw new InvalidDataException("SolidCraft 没有返回实体，已停止导出。");
        double[] min = [double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity];
        double[] max = [double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity];
        int count = 0;
        foreach (var body in bodies.EnumerateArray())
        {
            JsonElement bbox;
            if (!body.TryGetProperty("bbox", out bbox) && !body.TryGetProperty("bounding_box", out bbox))
                throw new InvalidDataException("有实体缺少包围盒，无法排布组合 STEP。");
            JsonElement lo, hi;
            if (bbox.ValueKind == JsonValueKind.Array && bbox.GetArrayLength() == 2) { lo = bbox[0]; hi = bbox[1]; }
            else if (bbox.ValueKind == JsonValueKind.Object)
            {
                if (!bbox.TryGetProperty("min", out lo) && !bbox.TryGetProperty("minimum", out lo)) throw new InvalidDataException("包围盒缺少下界。");
                if (!bbox.TryGetProperty("max", out hi) && !bbox.TryGetProperty("maximum", out hi)) throw new InvalidDataException("包围盒缺少上界。");
            }
            else throw new InvalidDataException("无法识别实体包围盒。");
            if (lo.ValueKind != JsonValueKind.Array || hi.ValueKind != JsonValueKind.Array || lo.GetArrayLength() != 3 || hi.GetArrayLength() != 3)
                throw new InvalidDataException("实体包围盒坐标不完整。");
            for (int axis = 0; axis < 3; axis++)
            {
                double a = lo[axis].GetDouble(), b = hi[axis].GetDouble();
                if (!double.IsFinite(a) || !double.IsFinite(b) || a > b) throw new InvalidDataException("实体包围盒坐标无效。");
                min[axis] = Math.Min(min[axis], a); max[axis] = Math.Max(max[axis], b);
            }
            count++;
        }
        return new(min, max, count);
    }

    public static double TranslationX(Bounds bounds, double cursor) => cursor - bounds.Min[0];

    public static JsonElement ReadOccurrenceReference(JsonElement insertion)
    {
        if (!insertion.TryGetProperty("occurrence", out var occurrence))
            throw new InvalidDataException("SolidCraft 没有返回 STEP 实例编号，无法安全排布组合 STEP。");
        if (occurrence.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.Array)
            return occurrence.Clone();
        if (occurrence.ValueKind == JsonValueKind.Object && occurrence.TryGetProperty("id", out var id) && (id.ValueKind is JsonValueKind.String or JsonValueKind.Number))
            return id.Clone();
        throw new InvalidDataException("SolidCraft 返回的 STEP 实例编号格式未知，无法安全移动组合零件。");
    }

    public static void RequireClearInterference(JsonElement result, int expectedBodies)
    {
        if (!result.TryGetProperty("interferences", out var overlaps) || overlaps.ValueKind != JsonValueKind.Array ||
            !result.TryGetProperty("unchecked", out var uncheckedPairs) || uncheckedPairs.ValueKind != JsonValueKind.Array ||
            !result.TryGetProperty("count", out var count) || !count.TryGetInt32(out int countValue) ||
            !result.TryGetProperty("checked", out var checkedBodies) || !checkedBodies.TryGetInt32(out int checkedValue))
            throw new InvalidDataException("相交检查结果缺少必要字段，组合 STEP 未导出。");
        if (countValue != 0 || overlaps.GetArrayLength() != 0 || uncheckedPairs.GetArrayLength() != 0 || checkedValue != expectedBodies ||
            IsTrue(result, "truncated") || IsTrue(result, "budget_exceeded") ||
            (result.TryGetProperty("pairs_not_checked", out var p) && (!p.TryGetInt32(out int pending) || pending != 0)))
        {
            var details = overlaps.EnumerateArray().Take(3).Select(pair =>
            {
                string a = pair.TryGetProperty("a", out var left) ? left.ToString() : "?";
                string b = pair.TryGetProperty("b", out var right) ? right.ToString() : "?";
                string volume = pair.TryGetProperty("volume_mm3", out var v) ? $", 相交体积 {v} mm³" : "";
                return $"{a} ↔ {b}{volume}";
            });
            throw new InvalidDataException($"组合 STEP 未导出：相交 {Math.Max(countValue, overlaps.GetArrayLength())} 对，未检查 {uncheckedPairs.GetArrayLength()} 项，已检查实体 {checkedValue}/{expectedBodies}。{string.Join("；", details)}");
        }
    }

    static bool IsTrue(JsonElement obj, string name) => obj.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.False && value.ValueKind != JsonValueKind.Null;
}

public static class CsvFiles
{
    public static List<string[]> Read(string path)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        byte[] bytes = File.ReadAllBytes(path);
        string text;
        if (bytes.Length >= 2 && ((bytes[0] == 0xff && bytes[1] == 0xfe) || (bytes[0] == 0xfe && bytes[1] == 0xff)))
            text = (bytes[0] == 0xff ? Encoding.Unicode : Encoding.BigEndianUnicode).GetString(bytes, 2, bytes.Length - 2);
        else
        {
            try { text = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\ufeff'); }
            catch (DecoderFallbackException) { text = Encoding.GetEncoding("GB18030", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback).GetString(bytes); }
        }
        using TextFieldParser parser = new(new StringReader(text)) { TextFieldType = FieldType.Delimited, HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = true };
        parser.SetDelimiters(",");
        var header = parser.ReadFields() ?? throw new InvalidDataException("CSV 没有表头。");
        int[] indices = ModelRules.Columns.Select(name => Array.IndexOf(header, name)).ToArray();
        if (indices.Take(ModelRules.RequiredColumnCount).Any(i => i < 0) || header.Distinct().Count() != header.Length) throw new InvalidDataException("CSV 表头缺列或重复。请使用工具导出的格式。");
        List<string[]> rows = [];
        while (!parser.EndOfData)
        {
            string[] cells = parser.ReadFields() ?? [];
            if (cells.All(string.IsNullOrWhiteSpace)) continue;
            if (cells.Length != header.Length) throw new InvalidDataException($"CSV 第 {parser.LineNumber - 1} 行列数与表头不一致。");
            var row = indices.Select(i => i<0?"":cells[i].Trim()).ToArray();
            foreach (int i in Enumerable.Range(0, ModelRules.Columns.Length).Where(i => ModelRules.Flags.Contains(ModelRules.Columns[i])))
                if (row[i] != "0" && row[i] != "1") throw new InvalidDataException($"CSV 第 {rows.Count + 2} 行 {ModelRules.Columns[i]} 只能填 0 或 1。");
            rows.Add(row);
        }
        if (rows.Count == 0) throw new InvalidDataException("CSV 没有数据行。");
        return rows;
    }

    public static void Write(string path, IEnumerable<string[]> rows)
    {
        using StreamWriter writer = new(path, false, new UTF8Encoding(true));
        static string Quote(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
        writer.WriteLine(string.Join(",", ModelRules.Columns.Select(Quote)));
        foreach (var row in rows) writer.WriteLine(string.Join(",", ModelRules.ExpandRow(row).Select(Quote)));
    }
}
