using netDxf;
using netDxf.Blocks;
using netDxf.Entities;
using SolidCraftCsvTool;

internal static class TemplateChecks
{
    public static int Run(string directory,StepDrawingPage page)
    {
        int checks=0;void Check(bool condition,string message){if(!condition)throw new Exception(message);checks++;}
        var source=new DxfDocument(netDxf.Header.DxfVersion.AutoCad2010);
        var style=DrawingResources.DefaultStyle();style.TextHeight=.8;source.DimensionStyles.Add(style);
        var border=new Block("SYNTHETIC_FRAME",[new Polyline2D(new[]{new Vector2(0,0),new Vector2(1000,0),new Vector2(1000,700),new Vector2(0,700)},true)]);
        var title=new Block("SYNTHETIC_TITLE",[new Polyline2D(new[]{new Vector2(0,0),new Vector2(260,0),new Vector2(260,90),new Vector2(0,90)},true)]);
        title.AttributeDefinitions.Add(new AttributeDefinition("TITLE"){Value="old title",Position=new(10,25,0),Height=5});
        source.Entities.Add(new Insert(border,new Vector3(1000,500,0)));
        source.Entities.Add(new Insert(title,new Vector3(1700,520,0)));
        source.Entities.Add(new Text("DO_NOT_IMPORT_PART",new Vector3(1300,1000,0),10));
        string template=Path.Combine(directory,"synthetic-template.dxf");source.Save(template);Dxf2010Compatibility.NormalizeRasterOwner(template);
        var options=new DrawingOptions{TemplatePath=template,DimensionStyleName="_5",HiddenLineScale=2};
        DrawingResources resource=DrawingResources.Load(options);
        Check(resource.Frame?.Components.Count==2,"Import only the frame and its title block");
        Check(Math.Abs(resource.TextHeight-16)<1e-8,"Custom style effective text size must include overall scale");
        string output=Path.Combine(directory,"template-sample.dxf");
        MachiningDxf.WriteCombined(output,[page,page with{Name="template-page-2"}],options);
        var saved=DxfDocument.Load(output)??throw new Exception("Cannot reopen template output");
        Check(saved.Entities.Inserts.Count()==4,"Each drawing needs its own frame and title block");
        Check(saved.Entities.Texts.All(t=>t.Value!="DO_NOT_IMPORT_PART"),"A template's existing part drawings must not be copied");
        Check(saved.Entities.Inserts.SelectMany(i=>i.Attributes).Any(a=>Convert.ToString(a.Value)=="template-page-2"),"Title attribute must identify the actual part");
        Check(Math.Abs(saved.DimensionStyles["_5"].TextHeight-.8)<1e-8 && Math.Abs(saved.DimensionStyles["_5"].DimScaleOverall-20)<1e-8,"Imported dimension style must retain its values");
        Check(saved.Entities.Polylines2D.Where(p=>p.Layer.Name=="HIDDEN").All(p=>Math.Abs(p.LinetypeScale-2)<1e-8&&p.LinetypeGeneration),"Hidden line scale and continuity must survive saving");
        bool rejected=false;
        try{MachiningDxf.Write(Path.Combine(directory,"wrong-angle.dxf"),page with{End=page.End with{Name="right"}});}catch(InvalidDataException){rejected=true;}
        Check(rejected,"First-angle drawing must refuse a right-side projection on the right");
        return checks;
    }
}
