using netDxf;
using netDxf.Blocks;
using netDxf.Entities;
using SolidCraftCsvTool;

internal static class AnonymousTitleChecks
{
    public static void Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var source=new DxfDocument(netDxf.Header.DxfVersion.AutoCad2010);
        source.DimensionStyles.Add(DrawingResources.DefaultStyle());
        var border=new Block("FRAME",[new Polyline2D(new[]{new Vector2(0,0),new Vector2(1000,0),new Vector2(1000,700),new Vector2(0,700)},true)]);
        var logo=new Block("EDITED_LOGO",[new Circle(new Vector3(20,60,0),8)]);
        var title=new Block("TITLE_TO_ANONYMOUS",[
            new Polyline2D(new[]{new Vector2(0,0),new Vector2(400,0),new Vector2(400,90),new Vector2(0,90)},true),
            new Insert(logo),new Text("Company",new Vector3(50,60,0),5)]);
        title.AttributeDefinitions.Add(new AttributeDefinition("TITLE"){Value="old title",Position=new(10,25,0),Height=5});
        source.Entities.Add(new Insert(border,new Vector3(1000,500,0)));
        source.Entities.Add(new Insert(title,new Vector3(1550,520,0)));
        source.Entities.Add(new Insert(new Block("UNRELATED_PART",[new Circle(Vector3.Zero,10)]),new Vector3(1300,1000,0)));
        string template=Path.Combine(directory,"anonymous-title-template.dxf");source.Save(template);
        File.WriteAllText(template,File.ReadAllText(template).Replace("TITLE_TO_ANONYMOUS","*U92"));
        Dxf2010Compatibility.NormalizeRasterOwner(template);
        var options=new DrawingOptions{TemplatePath=template};
        var resources=DrawingResources.Load(options);
        if(resources.Frame?.Components.Count!=2||!resources.Frame.Components.Any(i=>i.Block.Name=="*U92"))
            throw new Exception("Anonymous title block must be retained inside the title band.");
        Projection2 Rect(string name,double width,double height)=>new(name,[],
            [new([new(0,0),new(width,0),new(width,height),new(0,height)],true,"VISIBLE")],new(0,0,width,height));
        var front=Rect("front",300,100);var top=Rect("top",300,200);var end=Rect("left",200,100);
        var page=new StepDrawingPage("1",front,top,end,DxfFeatureDetector.Analyze(front,top));
        string output=Path.Combine(directory,"anonymous-title-result.dxf");
        MachiningDxf.WriteCombined(output,[page,page with{Name="2"}],options);
        var saved=DxfDocument.Load(output)??throw new Exception("Cannot reopen generated drawing.");
        var titles=saved.Entities.Inserts.Where(i=>i.Block.Entities.OfType<Insert>().Any(n=>n.Block.Name=="EDITED_LOGO")).ToArray();
        if(titles.Length!=2||saved.Entities.Inserts.Count()!=4)
            throw new Exception("Each sheet must keep its frame, title and nested logo; unrelated parts must be excluded.");
        if(!titles.All(i=>i.Attributes.Any(a=>Convert.ToString(a.Value)=="辊密封1"||Convert.ToString(a.Value)=="辊密封2"))||
            titles.SelectMany(i=>i.Attributes).Select(a=>Convert.ToString(a.Value)).Distinct().Count()!=2)
            throw new Exception("Each anonymous title must receive its own part number.");
        Console.WriteLine("PASS: anonymous title and nested edited logo retained on two sheets with distinct part numbers; unrelated block excluded; saved DXF reopens.");
    }
}
