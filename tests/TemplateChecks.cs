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
        title.AttributeDefinitions.Add(new AttributeDefinition("MATERIAL"){Value="原材料",Position=new(10,40,0),Height=5});
        title.AttributeDefinitions.Add(new AttributeDefinition("DESIGNER"){Value="原设计",Position=new(10,55,0),Height=5});
        title.AttributeDefinitions.Add(new AttributeDefinition("DATE"){Value="原日期",Position=new(10,70,0),Height=5});
        var nested=new Block("NESTED_TITLE_DATA");
        nested.AttributeDefinitions.Add(new AttributeDefinition("项目"){Value="原项目",Position=new(0,0,0),Height=5});
        nested.AttributeDefinitions.Add(new AttributeDefinition("TITLE"){Value="old nested title",Position=new(0,10,0),Height=5});
        title.Entities.Add(new Insert(nested,new Vector3(80,55,0)));
        source.Entities.Add(new Insert(border,new Vector3(1000,500,0)));
        source.Entities.Add(new Insert(title,new Vector3(1700,520,0)));
        source.Entities.Add(new Text("DO_NOT_IMPORT_PART",new Vector3(1300,1000,0),10));
        string template=Path.Combine(directory,"synthetic-template.dxf");source.Save(template);Dxf2010Compatibility.NormalizeRasterOwner(template);
        var options=new DrawingOptions{TemplatePath=template,DimensionStyleName="_5",HiddenLineScale=2};
        DrawingResources resource=DrawingResources.Load(options);
        Check(resource.Frame?.Components.Count==2,"Import only the frame and its title block");
        Check(Math.Abs(resource.TextHeight-16)<1e-8,"Custom style effective text size must include overall scale");
        var fields=DrawingResources.TitleFields(options);
        Check(fields.Count==5&&fields.Any(f=>f.Key=="项目"&&f.Value=="原项目"),"Settings must discover all title attributes, including nested blocks");
        string output=Path.Combine(directory,"template-sample.dxf");
        MachiningDxf.WriteCombined(output,[page,page with{Name="template-page-2"}],options);
        var saved=DxfDocument.Load(output)??throw new Exception("Cannot reopen template output");
        Check(saved.Entities.Inserts.Count()==4,"Each drawing needs its own frame and title block");
        Check(saved.Entities.Texts.All(t=>t.Value!="DO_NOT_IMPORT_PART"),"A template's existing part drawings must not be copied");
        Check(saved.Entities.Inserts.SelectMany(i=>i.Attributes).Any(a=>Convert.ToString(a.Value)=="辊密封2"),"Title attribute must identify the actual part");
        Check(Math.Abs(saved.DimensionStyles["_5"].TextHeight-.8)<1e-8 && Math.Abs(saved.DimensionStyles["_5"].DimScaleOverall-20)<1e-8,"Imported dimension style must retain its values");
        Check(saved.Entities.Polylines2D.Where(p=>p.Layer.Name=="HIDDEN").All(p=>Math.Abs(p.LinetypeScale-2)<1e-8&&p.LinetypeGeneration),"Hidden line scale and continuity must survive saving");
        var frameInserts=saved.Entities.Inserts.Where(i=>i.Block.Name=="SYNTHETIC_FRAME").ToArray();
        Check(saved.Entities.Inserts.SelectMany(i=>i.Attributes).All(a=>frameInserts.Any(f=>a.Position.X>=f.Position.X&&a.Position.X<=f.Position.X+1000*f.Scale.X&&a.Position.Y>=f.Position.Y&&a.Position.Y<=f.Position.Y+700*f.Scale.Y)),"Title attributes must move and scale with their sheet frame");
        var configured=options with{TitleBlockAttributes=new(){["material"]="陶瓷纤维",["DESIGNER"]="设计甲",["DATE"]="",["项目"]="辊密封项目"}};
        string json=System.Text.Json.JsonSerializer.Serialize(configured);
        configured=System.Text.Json.JsonSerializer.Deserialize<DrawingOptions>(json)??throw new Exception("Settings round trip failed");
        string custom=Path.Combine(directory,"title-attributes.dxf");MachiningDxf.WriteCombined(custom,[page,page with{Name="辊密封_1028_B02"}],configured);
        var customSaved=DxfDocument.Load(custom)??throw new Exception("Cannot read customized title fields");
        var customTitles=customSaved.Entities.Inserts.Where(i=>i.Block.Name.StartsWith("SYNTHETIC_TITLE",StringComparison.Ordinal)).ToArray();
        Check(customTitles.Length==2&&customTitles.All(i=>Convert.ToString(i.Attributes.Single(a=>a.Tag=="MATERIAL").Value)=="陶瓷纤维"),"Case insensitive title overrides must survive settings serialization and apply to every page");
        Check(customTitles.All(i=>Convert.ToString(i.Attributes.Single(a=>a.Tag=="DESIGNER").Value)=="设计甲"&&Convert.ToString(i.Attributes.Single(a=>a.Tag=="DATE").Value)=="原日期"),"Custom title values must be written and blank fields must retain template values");
        Check(customTitles.Any(i=>Convert.ToString(i.Attributes.Single(a=>a.Tag=="TITLE").Value)=="辊密封B02"),"Custom metadata must preserve automatic per-part names");
        Check(customTitles.SelectMany(i=>i.Block.Entities.OfType<Insert>()).All(i=>Convert.ToString(i.Attributes.Single(a=>a.Tag=="项目").Value)=="辊密封项目"),"Nested title fields must be updated in the saved DXF");
        Check(customTitles.All(i=>i.Block.Entities.OfType<Insert>().All(n=>Convert.ToString(n.Attributes.Single(a=>a.Tag=="TITLE").Value)==Convert.ToString(i.Attributes.Single(a=>a.Tag=="TITLE").Value))),"Nested automatic titles must retain each sheet's own part name");
        Check(DrawingResources.TitleFields(options).Any(f=>f.Key=="MATERIAL"&&f.Value=="原材料"),"Title customization must leave the source template unchanged");
        string crowded=Path.Combine(directory,"crowded-sample.dxf");
        var tightTop=page.Top with{Lines=page.Top.Lines.Append(new Line2(new(32,0),new(32,160),"VISIBLE")).ToArray()};
        MachiningDxf.Write(crowded,page with{Top=tightTop});
        var dense=DxfDocument.Load(crowded)??throw new Exception("Cannot read dense annotation fixture");
        Check(dense.Entities.Dimensions.Any(),"Dense dimension output must reopen; serialized spacing is verified independently with ezdxf");
        bool rejected=false;
        try{MachiningDxf.Write(Path.Combine(directory,"wrong-angle.dxf"),page with{End=page.End with{Name="right"}});}catch(InvalidDataException){rejected=true;}
        Check(rejected,"First-angle drawing must refuse a right-side projection on the right");
        return checks;
    }
}
