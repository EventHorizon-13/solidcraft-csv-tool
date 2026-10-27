namespace SolidCraftCsvTool;

internal static partial class MachiningDxf
{
    static void DrawFrontAnnotations(Writer w, Map front, IReadOnlyList<ArcPath> arcs)
    {
        // Dimension only supported geometry. Circle tessellation vertices are never
        // treated as step edges, and hidden fragments cannot introduce extra marks.
        double[] centers = arcs.Where(a => a.Layer == "VISIBLE" && a.SweepDegrees >= 15 &&
                (Math.Abs(a.Center.Y - front.Max.Y) < 0.08 || Math.Abs(a.Center.Y - front.Min.Y) < 0.08))
            .Select(a => a.Center.X - front.Dx).ToArray();
        if (centers.Length > 0)
            DrawHorizontalChain(w, front, IncludeEnds(centers, front.Projection.Bounds.MinX, front.Projection.Bounds.MaxX), front.Max.Y + w.DimensionRow);

        DrawLowerDimensions(w, front);
        Vertical(w, front.Min.X, front.Min.X - w.DimensionRow, front.Min.Y, front.Max.Y, w.FormatDimension(front.Height));
        DrawHoleDiameters(w, front, arcs);

    }

    static void DrawPlanAnnotations(Writer w,Map top,IReadOnlyList<ArcPath> frontArcs)
    {
        bool previous=w.SuppressPlanTongueDetail;
        w.SuppressPlanTongueDetail=true;
        try{DrawPlanGrooves(w,top,frontArcs);}finally{w.SuppressPlanTongueDetail=previous;}
    }
    static void DrawEndAnnotations(Writer w,Map end)=>DrawEndSizeAndBevel(w,end);

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
            Horizontal(w, map.P(body.A).X, map.P(body.B).X, map.Min.Y, map.Min.Y - w.DimensionRow, w.FormatDimension(Math.Abs(body.A.X - body.B.X)));
        Horizontal(w, map.Min.X, map.Max.X, map.Min.Y, map.Min.Y - (body == null ? w.DimensionRow : w.DimensionRow*2), w.FormatDimension(map.Width));
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
        int[] lanes=ChainLanes(w,marks);
        for(int i=1;i<marks.Length;i++)
            if(marks[i]-marks[i-1]>.2)
                Horizontal(w,map.Dx+marks[i-1],map.Dx+marks[i],map.Min.Y,y-lanes[i-1]*w.DimensionRow,w.FormatDimension(marks[i]-marks[i-1]));
    }
}
