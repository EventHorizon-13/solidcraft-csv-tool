namespace SolidCraftCsvTool;

internal static partial class MachiningDxf
{
    static void DrawAnnotations(Writer w, Map front, Map top, Map end, IReadOnlyList<ArcPath> arcs)
    {
        // Dimension only supported geometry. Circle tessellation vertices are never
        // treated as step edges, and hidden fragments cannot introduce extra marks.
        double[] centers = arcs.Where(a => a.Layer == "VISIBLE" && a.SweepDegrees >= 15 &&
                (Math.Abs(a.Center.Y - front.Max.Y) < 0.08 || Math.Abs(a.Center.Y - front.Min.Y) < 0.08))
            .Select(a => a.Center.X - front.Dx).ToArray();
        if (centers.Length > 0)
            DrawHorizontalChain(w, front, IncludeEnds(centers, front.Projection.Bounds.MinX, front.Projection.Bounds.MaxX), front.Max.Y + 18);

        DrawLowerDimensions(w, front);
        Vertical(w, front.Min.X, front.Min.X - 18, front.Min.Y, front.Max.Y, Fmt(front.Height));
        DrawPerimeterHeight(w, front);

        double[] planMarks = AxisMarks(top.Projection, true);
        if (planMarks.Length > 2) DrawHorizontalChain(w, top, planMarks, top.Max.Y + 18);
        DrawLowerDimensions(w, top);
        Vertical(w, top.Min.X, top.Min.X - 18, top.Min.Y, top.Max.Y, Fmt(top.Height));
        DrawPerimeterHeight(w, top);

        Horizontal(w, end.Min.X, end.Max.X, end.Max.Y, end.Max.Y + 18, Fmt(end.Width));
        double[] endMarks = AxisMarks(end.Projection, true);
        if (endMarks.Length > 2) DrawHorizontalChainBelow(w, end, endMarks, end.Min.Y - 18);
        Vertical(w, end.Min.X, end.Min.X - 18, end.Min.Y, end.Max.Y, Fmt(end.Height));
        DrawPerimeterHeight(w, end);
    }

    static void DrawLowerDimensions(Writer w, Map map)
    {
        Box2 bounds = map.Projection.Bounds;
        // A tongue can increase overall length beyond the main body. Preserve
        // both measurements when a long, straight body edge supports the shorter one.
        var body = StraightEdges(map.Projection).Where(edge =>
            Math.Abs(edge.A.Y - edge.B.Y) < 0.01 && Math.Abs(edge.A.X - edge.B.X) > bounds.Width * 0.6 &&
            Math.Abs(edge.A.X - edge.B.X) < bounds.Width - 0.15 &&
            (Math.Abs(Math.Min(edge.A.X, edge.B.X) - bounds.MinX) < 0.05 ||
             Math.Abs(Math.Max(edge.A.X, edge.B.X) - bounds.MaxX) < 0.05))
            .OrderByDescending(edge => Math.Abs(edge.A.X - edge.B.X)).FirstOrDefault();
        if (body != null)
            Horizontal(w, map.P(body.A).X, map.P(body.B).X, map.Min.Y, map.Min.Y - 18, Fmt(Math.Abs(body.A.X - body.B.X)));
        Horizontal(w, map.Min.X, map.Max.X, map.Min.Y, map.Min.Y - (body == null ? 18 : 36), Fmt(map.Width));
    }

    static void DrawPerimeterHeight(Writer w, Map map)
    {
        Box2 bounds = map.Projection.Bounds;
        double[] marks = IncludeEnds(StraightEdges(map.Projection).Where(edge =>
            Math.Abs(edge.A.Y - edge.B.Y) < 0.01 && Math.Abs(edge.A.X - edge.B.X) >= 2 &&
            (Math.Abs(edge.A.X - bounds.MinX) < 0.05 || Math.Abs(edge.B.X - bounds.MinX) < 0.05 ||
             Math.Abs(edge.A.X - bounds.MaxX) < 0.05 || Math.Abs(edge.B.X - bounds.MaxX) < 0.05))
            .Select(edge => edge.A.Y).ToArray(), bounds.MinY, bounds.MaxY);
        if (marks.Length > 2) DrawVerticalChain(w, map, marks, map.Max.X + 18);
    }

    static double[] AxisMarks(Projection2 view, bool axisX)
    {
        var marks = new List<double>();
        Box2 b = view.Bounds;
        foreach (Line2 edge in StraightEdges(view))
        {
            double dx = Math.Abs(edge.A.X - edge.B.X), dy = Math.Abs(edge.A.Y - edge.B.Y);
            if (axisX && dx < 0.01 && dy >= 2) marks.Add((edge.A.X + edge.B.X) / 2);
            if (!axisX && dy < 0.01 && dx >= 2) marks.Add((edge.A.Y + edge.B.Y) / 2);
            // Include the end of a bevel where it meets the bottom/top boundary.
            if (axisX && dy < 0.01 && dx >= 2 &&
                (Math.Abs(edge.A.Y - b.MinY) < 0.05 || Math.Abs(edge.A.Y - b.MaxY) < 0.05))
            { marks.Add(edge.A.X); marks.Add(edge.B.X); }
        }
        return IncludeEnds(marks, axisX ? b.MinX : b.MinY, axisX ? b.MaxX : b.MaxY);
    }

    static IEnumerable<Line2> StraightEdges(Projection2 view)
    {
        foreach (Line2 line in view.Lines)
            if (!IsHidden(line.Layer)) yield return line;
        foreach (Polyline2 poly in view.Polylines)
        {
            if (IsHidden(poly.Layer)) continue;
            for (int i = 1; i < poly.Points.Count; i++) yield return new(poly.Points[i - 1], poly.Points[i], poly.Layer);
            if (poly.Closed && poly.Points.Count > 2) yield return new(poly.Points[^1], poly.Points[0], poly.Layer);
        }
    }

    static void DrawHorizontalChainBelow(Writer w, Map map, double[] marks, double y)
    {
        for (int i = 1; i < marks.Length; i++)
            if (marks[i] - marks[i - 1] > 0.2)
                Horizontal(w, map.Dx + marks[i - 1], map.Dx + marks[i], map.Min.Y, y, Fmt(marks[i] - marks[i - 1]));
    }
}
