using System.Globalization;
using SolidCraftCsvTool;

internal static class PlanAnnotationChecks
{
    public static void Run(string directory)
    {
        var points=new List<Point2>{new(-40,130)};
        foreach(double center in new[]{60.0,180.0})
            for(int i=0;i<=180;i++)
            {
                double angle=Math.PI+i*Math.PI/180;
                points.Add(new(center+45*Math.Cos(angle)-40,130+45*Math.Sin(angle)));
            }
        points.AddRange([new(260,130),new(260,25),new(-40,25)]);
        Projection2 front=new("front",[],[new(points,true,"VISIBLE")],new(-40,25,260,130));
        Point2[] plan=[new(0,0),new(291,0),new(291,60),new(300,60),new(300,140),new(291,140),
            new(291,200),new(0,200),new(0,140),new(9,140),new(9,60),new(0,60)];
        // A misleading projected edge must not replace the actual 15 mm arc tangent.
        Projection2 top=new("top",[new(new(13.89,0),new(13.89,200),"VISIBLE")],
            [new(plan,true,"VISIBLE")],new(0,0,300,200));
        Projection2 end=new("left",[],[new([new(0,0),new(200,0),new(200,105),new(0,105)],true,"VISIBLE")],new(0,0,200,105));
        string file=Path.Combine(directory,"plan-annotation-sample.dxf");
        MachiningDxf.Write(file,new("2",front,top,end,DxfFeatureDetector.Analyze(front,top)));
        var dimensions=ReadDimensions(file);
        double N(Dictionary<int,string> d,int code)=>double.Parse(d[code],CultureInfo.InvariantCulture);
        bool Near(double a,double b)=>Math.Abs(a-b)<.02;
        var horizontal=dimensions.Where(d=>(int.Parse(d[70],CultureInfo.InvariantCulture)&7)==0&&Near(N(d,50),0)).ToArray();
        double Width(Dictionary<int,string> d)=>Math.Abs(N(d,14)-N(d,13));
        var first=horizontal.Single(d=>Near(Width(d),15));
        if(!Near(N(first,14)-N(first,13),15))throw new Exception("First plan dimension must run from the end face to the bore tangent.");
        foreach(double expected in new[]{90.0,30.0,66.0})
            if(!horizontal.Any(d=>Near(Width(d),expected)&&Near(N(d,23),N(first,23))))
                throw new Exception("Missing arc-based plan chain segment: "+expected);
        if(horizontal.Any(d=>Near(Width(d),13.89)))throw new Exception("Projection fragments must not create the first bore dimension.");
        var depth=horizontal.Where(d=>Near(Width(d),9)).ToArray();
        if(depth.Length!=2)throw new Exception("Both joint depths must remain independently dimensioned.");
        foreach(var d in depth)
            if(!Near(N(d,23),N(first,23)-60)||N(d,20)<N(first,23)-100||N(d,20)>N(first,23))
                throw new Exception("Joint depth dimension must stay at the upper shoulder, not below the view.");

        // Middle hole pairs calibrate an end-clipping offset in the projections.
        // Hidden, fragmented left shoulders still form a complete female groove.
        var projected=new List<Line2>();
        for(int i=0;i<plan.Length;i++)
        {
            var a=plan[i];var z=plan[(i+1)%plan.Length];
            if(Math.Abs(a.Y-z.Y)<.01&&Math.Min(a.X,z.X)==0&&Math.Max(a.X,z.X)==9)
            {
                projected.Add(new(new(0,a.Y),new(1.26,a.Y),"VISIBLE"));
                projected.Add(new(new(1.26,a.Y),new(9,a.Y),"HIDDEN"));
            }
            else projected.Add(new(a,z,a.X==9&&z.X==9?"HIDDEN":"VISIBLE"));
        }
        foreach(double x in new[]{25.0,115.0,145.0,235.0})projected.Add(new(new(x,0),new(x,200),"VISIBLE"));
        var shifted=top with{Lines=projected,Polylines=[]};
        string calibrated=Path.Combine(directory,"plan-calibrated-sample.dxf");
        MachiningDxf.Write(calibrated,new("3",front,shifted,end,DxfFeatureDetector.Analyze(front,shifted)));
        var corrected=ReadDimensions(calibrated).Where(d=>(int.Parse(d[70],CultureInfo.InvariantCulture)&7)==0&&Near(N(d,50),0)).ToArray();
        var leading=corrected.Single(d=>Near(Width(d),25));
        if(!corrected.Any(d=>Near(Width(d),56)&&Near(N(d,23),N(leading,23))))
            throw new Exception("First and last plan gaps must share the actual middle-hole alignment.");
        if(corrected.Count(d=>Near(Width(d),9))!=2)
            throw new Exception("Hidden or split left female groove must retain its depth dimension.");
        var female=ReadDimensions(calibrated).Where(d=>(int.Parse(d[70],CultureInfo.InvariantCulture)&7)==0&&Near(N(d,50),90)).ToArray();
        if(!female.Any(d=>Near(Math.Abs(N(d,24)-N(d,23)),80)&&N(d,10)<N(leading,13)))
            throw new Exception("Left female groove opening needs its own height dimension.");

        // End strips at the top/bottom borders are not joint shoulders. In the
        // user's drawing these were mistaken for a groove and merged 15+90=105.
        var borders=new List<Line2>{new(new(0,0),new(0,200),"VISIBLE"),new(new(300,0),new(300,200),"VISIBLE")};
        double[] stations=[0,15,105,135,225,300];
        foreach(double y in new[]{0.0,200.0})
            for(int i=1;i<stations.Length;i++)borders.Add(new(new(stations[i-1],y),new(stations[i],y),"VISIBLE"));
        foreach(double x in stations[1..^1])borders.Add(new(new(x,0),new(x,200),"VISIBLE"));
        var plain=top with{Lines=borders,Polylines=[]};
        string borderFile=Path.Combine(directory,"plan-border-strips-sample.dxf");
        MachiningDxf.Write(borderFile,new("5",front,plain,end,DxfFeatureDetector.Analyze(front,plain)));
        var borderDimensions=ReadDimensions(borderFile).Where(d=>(int.Parse(d[70],CultureInfo.InvariantCulture)&7)==0&&Near(N(d,50),0)).ToArray();
        var firstBorder=borderDimensions.Single(d=>Near(Width(d),15));
        if(!borderDimensions.Any(d=>Near(Width(d),75)&&Near(N(d,23),N(firstBorder,23)))||
            borderDimensions.Any(d=>Near(Width(d),105)&&Near(N(d,23),N(firstBorder,23))))
            throw new Exception("Border strips must retain the 15 mm leading and 75 mm trailing bore gaps, never merge into 105.");

        string input=Path.Combine(directory,"batch-inputs");Directory.CreateDirectory(input);
        try
        {
            foreach(string name in new[]{"2_辊密封.step","1_辊密封.STP","辊密封_组合.step","辊密封_组合.STP","notes.txt"})
                File.WriteAllText(Path.Combine(input,name),"fixture");
            var selected=BatchDrawingInputs.FromFolder(input).Select(Path.GetFileName).ToArray();
            if(!selected.SequenceEqual(new[]{"1_辊密封.STP","2_辊密封.step"}))
                throw new Exception("One-click input must include sorted individual STEP files and exclude the combined STEP.");
        }
        finally{Directory.Delete(input,true);}
        Console.WriteLine("PASS: arc-based 15/90/30/90/66 plan chain; both groove depths beside shoulders; hidden split female groove retained; first/last calibrated to middle pairs; border strips do not merge 15+90 into 105; one-click STEP selection excludes assembly.");
    }

    public static void RunHalfHoleChecks()
    {
        string directory=Path.Combine(Path.GetTempPath(),"half-hole-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            foreach(double radius in new[]{35.0,45.0,47.5})
            foreach(double shift in new[]{0.0,-400.0})
            {
                Point2 P(double x,double y)=>new(x+shift,y+shift);
                var contour=new List<Point2>();
                void Arc(double center,int start,int finish)
                {
                    for(int i=start;i<=finish;i++)contour.Add(P(center+radius*Math.Cos(i*Math.PI/180),105+radius*Math.Sin(i*Math.PI/180)));
                }
                Arc(0,270,360);
                foreach(double center in new[]{120.0,240.0,360.0,480.0})Arc(center,180,360);
                Arc(600,180,270);
                contour.AddRange([P(600,0),P(0,0)]);
                Projection2 front=new("front",[],[new(contour,true,"VISIBLE")],new(shift,shift,shift+600,shift+105));
                var edges=new List<Line2>();
                foreach(double center in new[]{120.0,240.0,360.0,480.0})
                    foreach(double x in new[]{center-radius,center+radius})edges.Add(new(new(x,0),new(x,200),"VISIBLE"));
                edges.Add(new(new(radius,0),new(radius,200),"VISIBLE"));
                // A misleading end projection lies near the real right half-hole tangent.
                edges.Add(new(new(600-radius+1.11,0),new(600-radius+1.11,200),"VISIBLE"));
                // The real top boundary may end with a 1 mm collinear fragment. Keep it in
                // the body span so the final half-hole tangent is dimensioned to the true end.
                Projection2 top=new("top",edges,[new([new(0,0),new(600,0),new(600,200),new(599,200),new(0,200)],true,"VISIBLE")],new(0,0,600,200));
                Projection2 end=new("left",[],[new([new(0,0),new(200,0),new(200,105),new(0,105)],true,"VISIBLE")],new(0,0,200,105));
                string file=Path.Combine(directory,"sample.dxf");
                MachiningDxf.Write(file,new("half-hole",front,top,end,DxfFeatureDetector.Analyze(front,top)));
                double N(Dictionary<int,string> d,int c)=>double.Parse(d[c],CultureInfo.InvariantCulture);
                var dims=ReadDimensions(file).Where(d=>(int.Parse(d[70])&7)==0&&Math.Abs(N(d,50))<.001).ToArray();
                var overall=dims.Single(d=>Math.Abs(N(d,14)-N(d,13)-600)<.001&&Math.Abs(N(d,23)-N(d,24))<.001&&N(d,23)==dims.Min(x=>N(x,23)));
                double origin=N(overall,13),baseline=N(overall,23);
                if(!dims.Any(d=>Math.Abs(N(d,13)-(origin+600-radius))<.01&&Math.Abs(N(d,14)-(origin+600))<.01&&Math.Abs(N(d,23)-baseline-200)<.01))
                    throw new Exception($"Right half-hole dimension must equal actual radius {radius}, not snap to a nearby edge");
                var chain=dims.Where(d=>Math.Abs(N(d,23)-baseline-200)<.01&&Math.Abs(N(d,24)-baseline-200)<.01&&
                    N(d,13)>=origin-.01&&N(d,14)<=origin+600+.01).ToArray();
                if(Math.Abs(chain.Sum(d=>Math.Abs(N(d,14)-N(d,13)))-600)>.01)
                    throw new Exception("A 1 mm right boundary fragment must be included so the 600 mm plan chain closes.");
            }
            Check660RightHalfHoleBoundaryFragment(directory);
            Console.WriteLine("PASS: half-hole plan tangents use radii 35, 45 and 47.5; 1 mm right boundary fragments remain in the body span; 600/660 mm plan chains close.");
        }
        finally{Directory.Delete(directory,true);}
    }

    static void Check660RightHalfHoleBoundaryFragment(string directory)
    {
        const double length=660,radius=45;
        Point2 P(double x,double y)=>new(x,y);
        var contour=new List<Point2>{P(0,105)};
        void Arc(double center,int start,int finish)
        {
            for(int i=start;i<=finish;i++)
            {
                double angle=i*Math.PI/180;
                contour.Add(P(center+radius*Math.Cos(angle),105+radius*Math.Sin(angle)));
            }
        }
        foreach(double center in new[]{60.0,180.0,300.0,420.0,540.0})Arc(center,180,360);
        Arc(length,180,270);
        contour.AddRange([P(length,0),P(0,0)]);
        Projection2 front=new("front",[],[new(contour,true,"VISIBLE")],new(0,0,length,105));
        var edges=new List<Line2>();
        foreach(double center in new[]{60.0,180.0,300.0,420.0,540.0})
            foreach(double x in new[]{center-radius,center+radius})
                edges.Add(new(new(x,0),new(x,200),"VISIBLE"));
        edges.Add(new(new(length-radius+1.11,0),new(length-radius+1.11,200),"VISIBLE"));
        Projection2 top=new("top",edges,
            [new([P(0,0),P(length,0),P(length,200),P(length-1,200),P(0,200)],true,"VISIBLE")],
            new(0,0,length,200));
        Projection2 end=new("left",[],[new([P(0,0),P(200,0),P(200,105),P(0,105)],true,"VISIBLE")],new(0,0,200,105));
        string file=Path.Combine(directory,"half-hole-660-boundary-fragment.dxf");
        MachiningDxf.Write(file,new("half-hole-660",front,top,end,DxfFeatureDetector.Analyze(front,top)));
        double N(Dictionary<int,string> d,int c)=>double.Parse(d[c],CultureInfo.InvariantCulture);
        bool Near(double a,double b)=>Math.Abs(a-b)<.01;
        double Width(Dictionary<int,string> d)=>Math.Abs(N(d,14)-N(d,13));
        var dims=ReadDimensions(file).Where(d=>(int.Parse(d[70],CultureInfo.InvariantCulture)&7)==0&&Near(N(d,50),0)).ToArray();
        double minY=dims.Min(d=>N(d,23));
        var overall=dims.Single(d=>Near(Width(d),length)&&Near(N(d,23),N(d,24))&&Near(N(d,23),minY));
        double origin=N(overall,13),baseline=N(overall,23);
        if(!dims.Any(d=>Near(N(d,13),origin+length-radius)&&Near(N(d,14),origin+length)&&
            Near(N(d,23),baseline+200)&&Near(N(d,24),baseline+200)))
            throw new Exception("The 660 mm case must dimension the Ø90 right half-hole end segment as 45 mm, not 44 mm.");
        if(!dims.Any(d=>Near(N(d,13),origin)&&Near(N(d,14),origin+15)&&
            Near(N(d,23),baseline+200)&&Near(N(d,24),baseline+200)))
            throw new Exception("The 660 mm case must preserve its 15 mm left end segment.");
        var chain=dims.Where(d=>Near(N(d,23),baseline+200)&&Near(N(d,24),baseline+200)&&
            N(d,13)>=origin-.01&&N(d,14)<=origin+length+.01).ToArray();
        if(!Near(chain.Sum(Width),length))
            throw new Exception($"The 660 mm plan dimension chain must close to 660 mm; actual sum: {chain.Sum(Width):0.###}.");
    }

    static List<Dictionary<int,string>> ReadDimensions(string file)
    {
        var result=new List<Dictionary<int,string>>();Dictionary<int,string>? current=null;
        string[] lines=File.ReadAllLines(file);
        for(int i=0;i<lines.Length;i+=2)
        {
            int code=int.Parse(lines[i],CultureInfo.InvariantCulture);
            if(code==0)
            {
                current=lines[i+1]=="DIMENSION"?new():null;
                if(current!=null)result.Add(current);
            }
            else if(current!=null)current[code]=lines[i+1];
        }
        return result;
    }
}
