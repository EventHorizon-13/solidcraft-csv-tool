using SolidCraftCsvTool;

internal static class FrontTongueDimensionChecks
{
    public static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "front-tongue-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            foreach (var sizes in new[] { (Hole:60.0, Tongue:9.0), (Hole:48.0, Tongue:6.0), (Hole:72.0, Tongue:12.0), (Hole:55.5, Tongue:8.5) })
            foreach (bool tongue in new[] { false, true })
            foreach (double shift in new[] { 0.0, -400.0 })
            {
                Point2 P(double x,double y) => new(x+shift,y+shift);
                var outline=new List<Point2>{P(0,100)};
                for(int i=0;i<=180;i++)
                {
                    double angle=(180+i)*Math.PI/180;
                    outline.Add(P(120+45*Math.Cos(angle),100+45*Math.Sin(angle)));
                }
                double root=120+sizes.Hole;
                double width=root+(tongue?sizes.Tongue:0);
                outline.AddRange([P(width,100),P(width,0),P(0,0)]);
                Projection2 front=new("front",tongue?[new(P(root,0),P(root,100),"VISIBLE")]:[],
                    [new(outline,true,"VISIBLE")],new(shift,shift,shift+width,shift+100));
                Projection2 Rect(string name)=>new(name,[],[new([new(0,0),new(width,0),new(width,80),new(0,80)],true,"VISIBLE")],new(0,0,width,80));
                var top=Rect("top");var end=Rect("left");
                string path=Path.Combine(directory,"sample.dxf");
                MachiningDxf.Write(path,new("tongue",front,top,end,DxfFeatureDetector.Analyze(front,top)));
                var doc=netDxf.DxfDocument.Load(path);
                var raw=File.ReadAllLines(path);
                var dimensions=new List<Dictionary<int,string>>();
                Dictionary<int,string>? current=null;
                for(int i=0;i<raw.Length;i+=2)
                {
                    int code=int.Parse(raw[i],System.Globalization.CultureInfo.InvariantCulture);
                    if(code==0){current=raw[i+1]=="DIMENSION"?new():null;if(current!=null)dimensions.Add(current);}
                    else if(current!=null)current[code]=raw[i+1];
                }
                double N(Dictionary<int,string> d,int c)=>double.Parse(d[c],System.Globalization.CultureInfo.InvariantCulture);
                var hole=doc.Entities.Arcs.Single(a=>Math.Abs(a.Radius-45)<.001);
                var chain=dimensions.Where(d=>(int.Parse(d[70])&7)==0 && Math.Abs(N(d,50))<.001 &&
                    Math.Abs(N(d,23)-hole.Center.Y)<.001 && Math.Abs(N(d,24)-hole.Center.Y)<.001)
                    .Select(d=>(A:N(d,13),B:N(d,14))).ToArray();
                bool Has(double a,double b)=>chain.Any(d=>Math.Abs(d.A-a)<.001&&Math.Abs(d.B-b)<.001);
                // Layout shifts the front's minimum to the same page origin.
                double origin=doc.Entities.Arcs.Single(a=>Math.Abs(a.Radius-45)<.001).Center.X-120;
                if(!Has(origin+120,origin+root))throw new Exception($"Missing independent {sizes.Hole} mm hole-position dimension");
                if(tongue && (!Has(origin+root,origin+width)||Has(origin+120,origin+width)))
                    throw new Exception($"Expected independent {sizes.Hole} + {sizes.Tongue}, not combined {sizes.Hole+sizes.Tongue}");
            }
            Console.WriteLine("PASS: geometry-driven functional dimensions 60+9, 48+6, 72+12, 55.5+8.5; no combined substitution; no-tongue and translated geometry.");
        }
        finally {Directory.Delete(directory,true);}
    }
}
