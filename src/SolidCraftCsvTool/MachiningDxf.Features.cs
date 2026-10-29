namespace SolidCraftCsvTool;

internal static partial class MachiningDxf
{
    sealed record EndGroove(double OuterX,double InnerX,double[] Heights,bool Left);

    static void DrawHoleDiameters(Writer w,Map front,IReadOnlyList<ArcPath> arcs)
    {
        var holes=new List<(Point2 Center,double Radius)>();
        Point2[] points=front.Projection.Polylines.Where(p=>!IsHidden(p.Layer)).SelectMany(p=>p.Points).Select(front.P).ToArray();
        foreach(var arc in arcs.Where(a=>a.Layer=="VISIBLE"&&a.SweepDegrees>=15&&
            (Math.Abs(a.Center.Y-front.Max.Y)<.08||Math.Abs(a.Center.Y-front.Min.Y)<.08)))
        {
            Point2 center=arc.Center;double radius=arc.Radius;
            var fit=points.Where(p=>Math.Abs(Distance(p,center)-radius)<.08).ToArray();
            if(fit.Length>=8&&TryFitCircle(fit,center,out var c,out var r)&&Distance(c,center)<.1&&Math.Abs(r-radius)<.1){center=c;radius=r;}
            if(!holes.Any(h=>Distance(h.Center,center)<.1&&Math.Abs(h.Radius-radius)<.02))holes.Add((center,radius));
        }
        foreach(var group in holes.GroupBy(h=>Math.Round(h.Radius*2,2)))
        {
            var hole=group.OrderByDescending(h=>h.Center.X-h.Radius>=front.Min.X-.05&&h.Center.X+h.Radius<=front.Max.X+.05).First();
            double direction=Math.Abs(hole.Center.Y-front.Max.Y)<.1?-1:1;
            Point2 label=hole.Radius>=w.DimensionTextHeight*1.5 ?
                new(hole.Center.X+hole.Radius*.4,hole.Center.Y+direction*hole.Radius*.3) :
                new(hole.Center.X,front.Max.Y+w.DimensionRow*2);
            w.DiameterDimension(hole.Center,hole.Radius,label);
        }
    }

    static EndGroove? FindGroove(Map map,bool left)
    {
        var b=map.Projection.Bounds;double outer=left?b.MinX:b.MaxX;
        // A shoulder may be split at projected intersections, or hidden by
        // a bevel. Join collinear fragments before looking for the two shoulders.
        var horizontal=StraightEdges(map.Projection,true).Where(e=>Math.Abs(e.A.Y-e.B.Y)<.01)
            .OrderBy(e=>e.A.Y).ThenBy(e=>Math.Min(e.A.X,e.B.X)).ToArray();
        var joined=new List<Line2>();
        foreach(var edge in horizontal)
        {
            double lo=Math.Min(edge.A.X,edge.B.X),hi=Math.Max(edge.A.X,edge.B.X),y=edge.A.Y;
            if(joined.Count>0&&Math.Abs(joined[^1].A.Y-y)<.02&&lo<=joined[^1].B.X+.02)
            {var previous=joined[^1];joined[^1]=new(previous.A,new(Math.Max(previous.B.X,hi),previous.B.Y),"VISIBLE");}
            else joined.Add(new(new(lo,y),new(hi,y),"VISIBLE"));
        }
        var steps=joined.Where(e=>e.A.Y>b.MinY+.05&&e.A.Y<b.MaxY-.05&&
            Math.Abs(e.A.X-e.B.X)>.2&&Math.Abs(e.A.X-e.B.X)<=Math.Min(b.Width*.18,b.Height*.5)&&
            (Math.Abs(e.A.X-outer)<.05||Math.Abs(e.B.X-outer)<.05)).ToArray();
        if(steps.Length==0)return null;
        var groups=steps.GroupBy(e=>Math.Round(Math.Abs(e.A.X-e.B.X),2)).OrderByDescending(g=>g.Count());
        var pair=groups.FirstOrDefault(g=>g.Select(e=>Math.Round(e.A.Y,2)).Distinct().Count()>=2);
        if(pair==null)return null;
        double depth=pair.Average(e=>Math.Abs(e.A.X-e.B.X));
        return new(outer,outer+(left?depth:-depth),IncludeEnds(pair.Select(e=>e.A.Y).ToArray(),b.MinY,b.MaxY),left);
    }
    static void DrawGroove(Writer w,Map map,EndGroove groove)
    {
        int[] lanes=ChainLanes(w,groove.Heights);
        for(int i=1;i<groove.Heights.Length;i++)
        {
            double feature=groove.OuterX+map.Dx;
            double x=feature+(groove.Left?-1:1)*w.DimensionRow*(lanes[i-1]+1);
            Vertical(w,feature,x,map.Dy+groove.Heights[i-1],map.Dy+groove.Heights[i],w.FormatDimension(groove.Heights[i]-groove.Heights[i-1]));
        }
        // Keep the depth dimension at the actual upper shoulder of the joint.
        // Its definition points and text stay beside the groove, not below the sheet.
        double shoulder=map.Dy+groove.Heights[^2];
        double opening=groove.Heights[^2]-groove.Heights[1];
        double inset=Math.Min(w.DimensionRow*.5,Math.Max(w.DimensionTextHeight,opening*.25));
        Horizontal(w,map.Dx+groove.OuterX,map.Dx+groove.InnerX,shoulder,shoulder-inset,w.FormatDimension(Math.Abs(groove.InnerX-groove.OuterX)));
    }

    static void DrawPlanGrooves(Writer w,Map map,IReadOnlyList<ArcPath> frontArcs)
    {
        EndGroove? left=FindGroove(map,true),right=FindGroove(map,false);
        var b=map.Projection.Bounds;
        var topEdges=StraightEdges(map.Projection).Where(e=>Math.Abs(e.A.Y-b.MaxY)<.02&&Math.Abs(e.B.Y-b.MaxY)<.02&&Math.Abs(e.A.X-e.B.X)>1).ToArray();
        double bodyMin=topEdges.Length==0?b.MinX:topEdges.Min(e=>Math.Min(e.A.X,e.B.X));
        double bodyMax=topEdges.Length==0?b.MaxX:topEdges.Max(e=>Math.Max(e.A.X,e.B.X));
        // Front-view circles and plan-view bore edges share the aligned X axis.
        // Use fitted circle tangents, never tessellation vertices or unrelated
        // joint lines, for edge-to-bore / bore-width / web-width dimensions.
        var circles=frontArcs.Where(a=>a.Layer=="VISIBLE"&&a.SweepDegrees>=15).ToArray();
        // Match the middle holes to actual plan edges. The two projections can
        // have different clipping bounds at their ends; normalizing both to zero
        // alone shifts the first and last measurements while middle widths agree.
        var planEdges=StraightEdges(map.Projection).Where(e=>Math.Abs(e.A.X-e.B.X)<.01&&Math.Abs(e.A.Y-e.B.Y)>=b.Height*.4)
            .Select(e=>(e.A.X+e.B.X)/2).Where(x=>x>bodyMin+.05&&x<bodyMax-.05&&
                (left==null||Math.Abs(x-left.InnerX)>.05)&&(right==null||Math.Abs(x-right.InnerX)>.05)).Distinct().ToArray();
        var references=circles.Where(a=>a.SweepDegrees>=170).ToArray();
        if(references.Length==0)references=circles;
        var offsets=new List<double>();
        foreach(var arc in references)
            if(planEdges.Length>=2)
            {
                double a=arc.Center.X-arc.Radius+b.MinX,z=arc.Center.X+arc.Radius+b.MinX;
                double nearA=planEdges.MinBy(p=>Math.Abs(p-a)),nearZ=planEdges.MinBy(p=>Math.Abs(p-z));
                double tolerance=Math.Max(.1,arc.Radius*.35);
                if(nearZ-nearA>.2&&Math.Abs((nearZ-nearA)-arc.Radius*2)<.2&&
                    Math.Abs(nearA-a)<=tolerance&&Math.Abs(nearZ-z)<=tolerance)
                    offsets.Add(((nearA-a)+(nearZ-z))/2);
            }
        // A lone projection fragment is not enough to calibrate a hole pair.
        if(offsets.Count==0)planEdges=[];
        offsets.Sort();
        double alignment=offsets.Count==0?0:offsets.Count%2==1?offsets[offsets.Count/2]:(offsets[offsets.Count/2-1]+offsets[offsets.Count/2])/2;
        double[] boreEdges=circles.SelectMany(a=>new[]{a.Center.X-a.Radius+b.MinX+alignment,a.Center.X+a.Radius+b.MinX+alignment})
            // Keep analytic tangents after alignment. A nearby projected joint
            // or clipping edge is not the bore tangent, especially at half holes.
            .Where(x=>x>=bodyMin-.05&&x<=bodyMax+.05).ToArray();
        var marks=(boreEdges.Length>0 ? boreEdges : AxisMarks(map.Projection,true))
            .Where(x=>x>=bodyMin-.05&&x<=bodyMax+.05&&
                (left==null||Math.Abs(x-left.InnerX)>.05||Math.Abs(x-bodyMin)<.05||Math.Abs(x-bodyMax)<.05)&&
                (right==null||Math.Abs(x-right.InnerX)>.05||Math.Abs(x-bodyMin)<.05||Math.Abs(x-bodyMax)<.05)).ToArray();
        marks=IncludeEnds(marks,bodyMin,bodyMax);
        if(marks.Length>2)DrawHorizontalChain(w,map,marks,map.Max.Y+w.DimensionRow);
        DrawLowerDimensions(w,map);
        double heightColumn=left==null?1:2+ChainLanes(w,left.Heights).DefaultIfEmpty(0).Max();
        Vertical(w,map.Min.X,map.Min.X-w.DimensionRow*heightColumn,map.Min.Y,map.Max.Y,w.FormatDimension(map.Height));
        if(left!=null)DrawGroove(w,map,left);
        if(right!=null)DrawGroove(w,map,right);
    }

    static void DrawEndSizeAndBevel(Writer w,Map map)
    {
        var b=map.Projection.Bounds;
        bool On(double value,double edge)=>Math.Abs(value-edge)<.03;
        var bevels=StraightEdges(map.Projection).Where(e=>Math.Abs(e.A.X-e.B.X)>.2&&Math.Abs(e.A.Y-e.B.Y)>.2&&
            ((On(e.A.X,b.MinX)||On(e.A.X,b.MaxX))&&(On(e.B.Y,b.MinY)||On(e.B.Y,b.MaxY))||
             (On(e.B.X,b.MinX)||On(e.B.X,b.MaxX))&&(On(e.A.Y,b.MinY)||On(e.A.Y,b.MaxY))))
            .DistinctBy(e=>(Math.Round(Math.Min(e.A.X,e.B.X),2),Math.Round(Math.Min(e.A.Y,e.B.Y),2),Math.Round(Math.Max(e.A.X,e.B.X),2),Math.Round(Math.Max(e.A.Y,e.B.Y),2))).ToArray();
        bool left=bevels.Any(e=>On(e.A.X,b.MinX)||On(e.B.X,b.MinX));
        bool top=bevels.Any(e=>On(e.A.Y,b.MaxY)||On(e.B.Y,b.MaxY));
        Horizontal(w,map.Min.X,map.Max.X,map.Max.Y,map.Max.Y+w.DimensionRow*(top?2:1),w.FormatDimension(map.Width));
        Vertical(w,map.Min.X,map.Min.X-w.DimensionRow*(left?2:1),map.Min.Y,map.Max.Y,w.FormatDimension(map.Height));
        foreach(var edge in bevels)
        {
            bool atLeft=On(edge.A.X,b.MinX)||On(edge.B.X,b.MinX),atTop=On(edge.A.Y,b.MaxY)||On(edge.B.Y,b.MaxY);
            double featureY=atTop?map.Max.Y:map.Min.Y,featureX=atLeft?map.Min.X:map.Max.X;
            Horizontal(w,map.Dx+edge.A.X,map.Dx+edge.B.X,featureY,featureY+(atTop?1:-1)*w.DimensionRow,w.FormatDimension(Math.Abs(edge.A.X-edge.B.X)));
            Vertical(w,featureX,featureX+(atLeft?-1:1)*w.DimensionRow,map.Dy+edge.A.Y,map.Dy+edge.B.Y,w.FormatDimension(Math.Abs(edge.A.Y-edge.B.Y)));
        }
    }
}
