using netDxf;
using netDxf.Entities;
using netDxf.Tables;

namespace SolidCraftCsvTool;

public sealed record DrawingOptions
{
    public bool OverwriteOutput { get; init; }
    public string OutputFileName { get; init; } = "";
    public string ResolvedFileName => OutputNames.FileName(OutputFileName,".dxf");
    public string TemplatePath { get; init; } = "";
    public string DimensionStyleName { get; init; } = "_5";
    public double HiddenLineScale { get; init; } = 1;
    public double ViewGap { get; init; } = 60;
    public double FrameMargin { get; init; } = 35;
    public double SheetGap { get; init; } = 100;
    public bool IncludeFrame { get; init; } = true;
    public void Validate()
    {
        _=ResolvedFileName;
        if (!double.IsFinite(HiddenLineScale) || HiddenLineScale < 0.05 || HiddenLineScale > 100 ||
            !double.IsFinite(ViewGap) || ViewGap < 10 || ViewGap > 5000 ||
            !double.IsFinite(FrameMargin) || FrameMargin < 10 || FrameMargin > 1000 ||
            !double.IsFinite(SheetGap) || SheetGap < 10 || SheetGap > 5000)
            throw new InvalidDataException("出图间距或隐藏线比例超出有效范围。");
        if (!string.IsNullOrWhiteSpace(TemplatePath) && !File.Exists(TemplatePath))
            throw new FileNotFoundException("找不到图框与标注模板，请重新选择 DXF 文件。", TemplatePath);
    }
}

internal sealed record FrameSource(Box2 Boundary, double TitleTop, IReadOnlyList<Insert> Components);
internal sealed record DrawingResources(DimensionStyle Style, FrameSource? Frame)
{
    public double TextHeight => Style.TextHeight * Math.Max(1, Style.DimScaleOverall);
    public double Scale => Math.Max(1, Style.DimScaleOverall);
    public static DrawingResources Load(DrawingOptions options)
    {
        options.Validate();
        if (string.IsNullOrWhiteSpace(options.TemplatePath)) return new(DefaultStyle(), null);
        DxfDocument template = DxfDocument.Load(options.TemplatePath) ?? throw new InvalidDataException("无法读取 DXF 模板。");
        if (!template.DimensionStyles.Contains(options.DimensionStyleName))
            throw new InvalidDataException($"模板不含标注样式“{options.DimensionStyleName}”，请在出图设置中选择。");
        var style = (DimensionStyle)template.DimensionStyles[options.DimensionStyleName].Clone();
        FrameSource? frame = options.IncludeFrame ? FindFrame(template) : null;
        if (options.IncludeFrame && frame == null) throw new InvalidDataException("模板中没有找到独立的矩形图框块；请关闭图框或使用含图框块的 DXF。");
        return new(style, frame);
    }
    public static DimensionStyle DefaultStyle() => new("_5")
    {
        TextStyle = new TextStyle("SYMBOLS", "isocp.shx") { BigFont = "extfont.shx" },
        TextHeight = 0.6, DimScaleOverall = 20, ArrowSize = 0.2, ExtLineOffset = 0.2,
        ExtLineExtend = 0.4, DimBaselineSpacing = 1, TextOffset = 0.1, DimRoundoff = 0.01,
        LengthPrecision = 3, TextColor = new AciColor(3), DimLineColor = new AciColor(4), ExtLineColor = new AciColor(4),
        TextVerticalPlacement = DimensionStyleTextVerticalPlacement.Above
    };

    static FrameSource? FindFrame(DxfDocument template)
    {
        var candidates = new List<(Insert Insert, Box2 Bounds)>();
        foreach (Insert insert in template.Entities.Inserts)
        {
            if (insert.Block.Name.StartsWith('*') || Math.Abs(insert.Rotation) > 0.001 || insert.Scale.X <= 0 || insert.Scale.Y <= 0) continue;
            foreach (Polyline2D poly in insert.Block.Entities.OfType<Polyline2D>())
            {
                if (!poly.IsClosed || poly.Vertexes.Count != 4 || poly.Vertexes.Any(v => Math.Abs(v.Bulge) > 1e-8)) continue;
                var points = poly.Vertexes.Select(v => new Point2(v.Position.X, v.Position.Y)).ToArray();
                var local = BoundsOf(points);
                if (local.Width < 1 || local.Height < 1 || points.Any(p =>
                    Math.Min(Math.Abs(p.X-local.MinX), Math.Abs(p.X-local.MaxX)) > .01 ||
                    Math.Min(Math.Abs(p.Y-local.MinY), Math.Abs(p.Y-local.MaxY)) > .01)) continue;
                var world = new Box2(insert.Position.X + (local.MinX-insert.Block.Origin.X)*insert.Scale.X,
                    insert.Position.Y + (local.MinY-insert.Block.Origin.Y)*insert.Scale.Y,
                    insert.Position.X + (local.MaxX-insert.Block.Origin.X)*insert.Scale.X,
                    insert.Position.Y + (local.MaxY-insert.Block.Origin.Y)*insert.Scale.Y);
                if (world.Width/world.Height is >= 1.1 and <= 2.1) candidates.Add((insert,world));
            }
        }
        var selected = candidates.OrderByDescending(c => c.Bounds.Width*c.Bounds.Height).FirstOrDefault();
        if (selected.Insert == null) return null;
        Box2 border = selected.Bounds;
        var components = new List<Insert> { selected.Insert };
        double titleTop = border.MinY + border.Height*.15;
        foreach (Insert insert in template.Entities.Inserts)
        {
            if (ReferenceEquals(insert,selected.Insert) || insert.Block.Name.StartsWith('*') || Math.Abs(insert.Rotation) > .001) continue;
            Box2? b = EntityBounds(insert);
            if (b == null || b.Width < border.Width*.2 || b.Height > border.Height*.3) continue;
            if (b.MinX >= border.MinX-1 && b.MaxX <= border.MaxX+1 && b.MinY >= border.MinY-1 && b.MaxY <= border.MinY+border.Height*.3)
            { components.Add(insert); titleTop = Math.Max(titleTop,b.MaxY); }
        }
        return new(border,titleTop,components);
    }

    internal static Box2 BoundsOf(IEnumerable<Point2> points)
    {
        Point2[] p = points.ToArray();
        return new(p.Min(v=>v.X),p.Min(v=>v.Y),p.Max(v=>v.X),p.Max(v=>v.Y));
    }
    internal static Box2? EntityBounds(EntityObject entity, int depth=0)
    {
        if (depth > 8) throw new InvalidDataException("图框嵌套层级过深。");
        IEnumerable<Point2> points = entity switch
        {
            Line l => [new(l.StartPoint.X,l.StartPoint.Y),new(l.EndPoint.X,l.EndPoint.Y)],
            Polyline2D p => p.Vertexes.Select(v=>new Point2(v.Position.X,v.Position.Y)),
            Arc a => [new(a.Center.X-a.Radius,a.Center.Y-a.Radius),new(a.Center.X+a.Radius,a.Center.Y+a.Radius)],
            Circle c => [new(c.Center.X-c.Radius,c.Center.Y-c.Radius),new(c.Center.X+c.Radius,c.Center.Y+c.Radius)],
            Text t => [new(t.Position.X,t.Position.Y)],
            MText t => [new(t.Position.X,t.Position.Y)],
            Insert i => i.Explode().Select(e=>EntityBounds(e,depth+1)).Where(b=>b!=null).SelectMany(b=>new[]{new Point2(b!.MinX,b.MinY),new Point2(b.MaxX,b.MaxY)}),
            _ => []
        };
        Point2[] all = points.ToArray();
        return all.Length == 0 ? null : BoundsOf(all);
    }
}
