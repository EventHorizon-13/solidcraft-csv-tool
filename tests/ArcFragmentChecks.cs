using SolidCraftCsvTool;
using static SolidCraftCsvTool.MachiningDxf;

internal static class ArcFragmentChecks
{
    static Point2 At(double degrees, double x = 0, double y = 0) =>
        new(x + 45 * Math.Cos(degrees * Math.PI / 180), y + 45 * Math.Sin(degrees * Math.PI / 180));
    static void Check(bool ok, string reason) { if (!ok) throw new Exception(reason); }
    public static void Run()
    {
        foreach (double offset in new[] { 0.0, -4000.0, 1000000.0 })
        foreach (bool reverse in new[] { false, true })
        {
            var points = Enumerable.Range(0, 181).Select(i => At(reverse ? 360-i : 180+i, offset, offset)).ToArray();
            Check(TryFitArc(points, 0, out int end, out var center, out double radius, out bool ccw), "Fit semicircle");
            Check(end == 180 && Math.Abs(radius-45) < .001 && ccw != reverse &&
                Math.Abs(center.X-offset) < .001 && Math.Abs(center.Y-offset) < .001, "Translation-independent complete semicircle");
        }
        Check(!IsCoveredByArc(new(At(219.8),At(220.3),"VISIBLE"),
            [new(new(0,0),45,180,40,"VISIBLE")]), "Never suppress a chord extending beyond an arc endpoint");
        var arcs = new List<ArcPath> { new(new(0,0),45,180,40,"VISIBLE"),new(new(0,0),45,225,135,"VISIBLE") };
        var lines = Enumerable.Range(220,5).Select(i=>new Line2(At(i),At(i+1),"VISIBLE")).ToList();
        lines.Add(new(new(0,0),new(100,0),"VISIBLE"));
        var merged = MergeCircularFragments(arcs,lines);
        Check(merged.Count==1 && Math.Abs(merged[0].SweepDegrees-180)<1e-6 && lines.Count==1,"Bridge only connected tessellation chords");
        merged=MergeCircularFragments(arcs,[]);
        Check(merged.Count==2,"Preserve real five-degree gap");
        merged=MergeCircularFragments([new(new(0,0),45,350,10,"VISIBLE"),new(new(0,0),45,0,20,"VISIBLE")],[]);
        Check(merged.Count==1 && Math.Abs(merged[0].SweepDegrees-30)<1e-6,"Merge across zero degrees");
        merged=MergeCircularFragments([new(new(0,0),45,180,40,"VISIBLE"),new(new(0,0),45,220,140,"HIDDEN")],[]);
        Check(merged.Count==2,"Keep layers distinct");
        merged=MergeCircularFragments([new(new(0,0),45,0,180,"VISIBLE"),new(new(0,0),45,180,180,"VISIBLE")],[]);
        Check(merged.Count==2 && merged.All(a=>a.SweepDegrees==180),"Preserve complete circle as two nonzero arcs");
        Console.WriteLine("PASS: arc fragments, translated clockwise/counterclockwise semicircles, true gaps, wraparound, layers and full circles.");
    }
    public static void Repair(string input,string output)
    {
        var doc=netDxf.DxfDocument.Load(input);
        var originalArcs=doc.Entities.Arcs.Where(a=>a.Layer.Name is "VISIBLE" or "HIDDEN" && Math.Abs(a.Normal.Z-1)<1e-10).ToArray();
        var originalLines=doc.Entities.Lines.Where(l=>l.Layer.Name is "VISIBLE" or "HIDDEN").ToArray();
        var arcs=originalArcs.Select(a=>new ArcPath(new(a.Center.X,a.Center.Y),a.Radius,a.StartAngle,(a.EndAngle-a.StartAngle+360)%360,a.Layer.Name)).ToList();
        var lines=originalLines.Select(l=>new Line2(new(l.StartPoint.X,l.StartPoint.Y),new(l.EndPoint.X,l.EndPoint.Y),l.Layer.Name)).ToList();
        var merged=MergeCircularFragments(arcs,lines);
        // This supplied roller-seal drawing has semicircular whole-hole profiles.
        // Recover tiny missing spans left by the old endpoint suppression, only
        // where both horizontal endpoints and at least 178 degrees already exist.
        var todo = new List<ArcPath>(merged);
        merged.Clear();
        while (todo.Count > 0)
        {
            var a = todo[0];
            var group = todo.Where(b=>b.Layer==a.Layer &&
                Math.Abs(b.Center.X-a.Center.X)+Math.Abs(b.Center.Y-a.Center.Y)+Math.Abs(b.Radius-a.Radius)<.002).ToArray();
            foreach(var b in group)todo.Remove(b);
            var ordered=group.OrderBy(b=>b.StartDegrees).ToArray();
            double first=ordered[0].StartDegrees,last=ordered[^1].StartDegrees+ordered[^1].SweepDegrees;
            if(Math.Abs(first-180)<.002 && Math.Abs(last-360)<.002 &&
                ordered.Sum(b=>b.SweepDegrees)>=178)
                merged.Add(a with { StartDegrees=180, SweepDegrees=180 });
            else merged.AddRange(group);
        }
        var keep=lines.ToHashSet();
        for(int i=0;i<originalLines.Length;i++)
        {
            var l=originalLines[i];var key=new Line2(new(l.StartPoint.X,l.StartPoint.Y),new(l.EndPoint.X,l.EndPoint.Y),l.Layer.Name);
            if(!keep.Contains(key))doc.Entities.Remove(l);
        }
        foreach(var arc in originalArcs)doc.Entities.Remove(arc);
        foreach(var a in merged)
        {
            var source=originalArcs.First(s=>s.Layer.Name==a.Layer && Math.Abs(s.Radius-a.Radius)<.002 && Math.Abs(s.Center.X-a.Center.X)<.002 && Math.Abs(s.Center.Y-a.Center.Y)<.002);
            var arc=(netDxf.Entities.Arc)source.Clone();
            arc.Center=new(a.Center.X,a.Center.Y,source.Center.Z);arc.Radius=a.Radius;arc.StartAngle=a.StartDegrees;arc.EndAngle=a.StartDegrees+a.SweepDegrees;
            doc.Entities.Add(arc);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        doc.Save(output);
        Dxf2010Compatibility.NormalizeRasterOwner(output);
        Console.WriteLine($"Repaired: {originalArcs.Length} -> {merged.Count} arcs; removed {originalLines.Length-lines.Count} circular chords; {output}");
    }
}
