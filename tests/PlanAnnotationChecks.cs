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
        Console.WriteLine("PASS: arc-based 15/90/30/90/66 plan chain; both groove depths beside shoulders; one-click STEP selection excludes assembly.");
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
