using System.Globalization;
using SolidCraftCsvTool;

internal static class FrontHeightChecks
{
    public static void Run(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        // These right-side steps used to add a 30/40/30 height chain.
        Point2[] outline = [new(0,0), new(300,0), new(300,30), new(290,30),
            new(290,70), new(300,70), new(300,100), new(0,100)];
        Projection2 Rectangle(string name, double width, double height) => new(name, [],
            [new([new(0,0),new(width,0),new(width,height),new(0,height)],true,"VISIBLE")],new(0,0,width,height));
        Projection2 front = new("front", [], [new(outline,true,"VISIBLE")], new(0,0,300,100));
        var top = Rectangle("top",300,160) with
        {
            Lines = [new(new(1.11,0),new(1.11,160),"VISIBLE"),new(new(3.11,0),new(3.11,160),"VISIBLE"),new(new(298.74,0),new(298.74,160),"VISIBLE")]
        };
        var end = Rectangle("left",160,80);
        string file = Path.Combine(outputDirectory,"front-height-sample.dxf");
        MachiningDxf.Write(file,new("1",front,top,end,DxfFeatureDetector.Analyze(front,top)));
        string[] raw = File.ReadAllLines(file);
        var dimensions = new List<Dictionary<int,string>>();
        Dictionary<int,string>? current = null;
        for (int i=0;i<raw.Length;i+=2)
        {
            int code = int.Parse(raw[i],CultureInfo.InvariantCulture);
            if (code==0)
            {
                current = raw[i+1]=="DIMENSION" ? new() : null;
                if(current!=null) dimensions.Add(current);
            }
            else if(current!=null) current[code]=raw[i+1];
        }
        double N(Dictionary<int,string> d,int code) => double.Parse(d[code],CultureInfo.InvariantCulture);
        bool Near(double a,double b) => Math.Abs(a-b)<.001;
        var vertical = dimensions.Where(d=>(int.Parse(d[70],CultureInfo.InvariantCulture)&7)==0 && Near(N(d,50),90)).ToArray();
        if(vertical.Length!=3 || vertical.Any(d=>!new[]{100.0,160.0,80.0}.Any(h=>Near(Math.Abs(N(d,24)-N(d,23)),h))))
            throw new Exception("Only overall heights may remain; front right-side step heights must be absent.");
        var height = vertical.Single(d=>Near(Math.Abs(N(d,24)-N(d,23)),100));
        if(!Near(N(height,13),N(height,14)) || N(height,10)>=N(height,13))
            throw new Exception("Front overall height must be on the left.");
        if(!dimensions.Any(d=>(int.Parse(d[70],CultureInfo.InvariantCulture)&7)==0 && Near(N(d,50),0) && Near(Math.Abs(N(d,14)-N(d,13)),300) && N(d,20)<N(d,23)))
            throw new Exception("Overall length below the front view must remain.");
        var reopened = netDxf.DxfDocument.Load(file);
        if(reopened==null || reopened.Entities.Dimensions.Count()!=dimensions.Count)
            throw new Exception("Saved dimensions must reopen.");
        if(dimensions.Any(d=>new[]{1.11,1.26}.Any(value=>Near(Math.Abs(N(d,50)==0 ? N(d,14)-N(d,13) : N(d,24)-N(d,23)),value))))
            throw new Exception("Unwanted 1.11 and 1.26 mm plan details must be absent.");
        if(!dimensions.Any(d=>Near(N(d,50),0) && Near(Math.Abs(N(d,14)-N(d,13)),2)))
            throw new Exception("Other small plan dimensions must remain.");
        var titles = reopened.Entities.Texts.Where(t=>t.Value=="辊密封1").ToArray();
        if(titles.Length==0 || titles.Any(title=>!title.Style.FontFile.Equals("gbenor.shx",StringComparison.OrdinalIgnoreCase) ||
            !title.Style.BigFont.Equals("gbcbig.shx",StringComparison.OrdinalIgnoreCase)))
            throw new Exception("Added drawing title must use gbenor/gbcbig.");
        Console.WriteLine("PASS: targeted front-view check; no right-side heights, one left overall height, lower length retained; 1.11/1.26 mm plan details omitted, 2 mm kept; title uses gbenor/gbcbig; DXF reopened.");
    }
}
