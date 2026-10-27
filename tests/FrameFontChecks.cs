using netDxf;
using netDxf.Blocks;
using netDxf.Entities;
using netDxf.Tables;
using SolidCraftCsvTool;

internal static class FrameFontChecks
{
    public static void Run(string directory)
    {
        var source=new DxfDocument(netDxf.Header.DxfVersion.AutoCad2010);
        source.DimensionStyles.Add(DrawingResources.DefaultStyle());
        var old=new TextStyle("OLD_FRAME_FONT","arial.ttf");
        var child=new Block("NESTED_LABEL",[new Text("Nested grid label",new Vector3(0,0,0),5,old)]);
        var border=new Block("FRAME_FONT_TEST",[
            new Polyline2D(new[]{new Vector2(0,0),new Vector2(1000,0),new Vector2(1000,700),new Vector2(0,700)},true),
            new Text("A",new Vector3(10,300,0),5,old),new Insert(child,new Vector3(20,500,0))]);
        var title=new Block("TITLE_FONT_TEST",[
            new Polyline2D(new[]{new Vector2(0,0),new Vector2(260,0),new Vector2(260,90),new Vector2(0,90)},true),
            new MText(@"{\fArial|b0|i0;Company} / title",new Vector3(10,60,0),5,200,old)]);
        title.AttributeDefinitions.Add(new AttributeDefinition("TITLE"){Value="old title",Position=new(10,25,0),Height=5,Style=old});
        title.AttributeDefinitions.Add(new AttributeDefinition("DESIGNER"){Value="Designer",Position=new(10,10,0),Height=5,Style=old});
        source.Entities.Add(new Insert(border,new Vector3(1000,500,0)));
        source.Entities.Add(new Insert(title,new Vector3(1700,520,0)));
        string template=Path.Combine(directory,"frame-font-template.dxf");
        source.Save(template);Dxf2010Compatibility.NormalizeRasterOwner(template);
        Projection2 Rectangle(string name,double width,double height)=>new(name,[],
            [new([new(0,0),new(width,0),new(width,height),new(0,height)],true,"VISIBLE")],new(0,0,width,height));
        var front=Rectangle("front",300,100);var top=Rectangle("top",300,200);
        var end=Rectangle("left",200,100);
        string output=Path.Combine(directory,"frame-font-sample.dxf");
        MachiningDxf.Write(output,new("4",front,top,end,DxfFeatureDetector.Analyze(front,top)),new(){TemplatePath=template});
        var saved=DxfDocument.Load(output)??throw new Exception("Cannot reopen frame font result.");
        int count=0;var visited=new HashSet<Block>();
        void Style(TextStyle style)
        {
            if(!style.FontFile.Equals("gbenor.shx",StringComparison.OrdinalIgnoreCase)||!style.BigFont.Equals("gbcbig.shx",StringComparison.OrdinalIgnoreCase))
                throw new Exception("Every frame/title font must use gbenor/gbcbig.");
            count++;
        }
        void Check(Insert insert)
        {
            foreach(var attribute in insert.Attributes)Style(attribute.Style);
            if(!visited.Add(insert.Block))return;
            foreach(var definition in insert.Block.AttributeDefinitions.Values)Style(definition.Style);
            foreach(var entity in insert.Block.Entities)
                switch(entity)
                {
                    case Text text:Style(text.Style);break;
                    case MText text:
                        Style(text.Style);
                        if(text.Value.Contains(@"\f",StringComparison.OrdinalIgnoreCase))throw new Exception("Inline MText font overrides must be removed.");
                        if(!text.Value.Contains("Company"))throw new Exception("Frame wording must be preserved.");
                        break;
                    case Insert nested:Check(nested);break;
                }
        }
        foreach(var insert in saved.Entities.Inserts)Check(insert);
        if(count<7)throw new Exception("Frame font fixture must cover nested text, MText, attributes and definitions.");
        Console.WriteLine("PASS: all frame/title TEXT, MTEXT, nested labels, attributes and definitions use gbenor/gbcbig; inline overrides removed; wording preserved.");
    }
}
