using SolidCraftCsvTool;
using netDxf;
using netDxf.Blocks;
using netDxf.Entities;

internal static class BatchOptionsChecks
{
    public static void Run(string directory)
    {
        Directory.CreateDirectory(directory);
        if(OutputNames.FileName(null,".dxf","1027")!="辊密封_1027.dxf" ||
            OutputNames.FileName("定制图.DXF",".dxf","1027")!="定制图.dxf" ||
            OutputNames.Part("A01","1027")!="辊密封_1027_A01")throw new Exception("Daily/default/custom file names are incorrect.");
        foreach(string invalid in new[]{"../escape","CON","trailing.","a/b"})
        {
            bool rejected=false;try{OutputNames.FileName(invalid,".step");}catch(InvalidDataException){rejected=true;}
            if(!rejected)throw new Exception("Unsafe output filename accepted.");
        }
        string csv=Path.Combine("examples","辊密封参数模板.csv");
        var rows=CsvFiles.Read(csv);if(ModelRules.Validate(rows).Count!=6)throw new Exception("CSV template needs six valid examples.");
        string copied=Path.Combine(directory,"辊密封_1027.csv");CsvFiles.Write(copied,rows);
        if(!CsvFiles.Read(copied).SelectMany(r=>r).SequenceEqual(rows.SelectMany(r=>r)))throw new Exception("Current task CSV cannot round-trip.");
        var source=new DxfDocument(netDxf.Header.DxfVersion.AutoCad2010);source.DimensionStyles.Add(DrawingResources.DefaultStyle());
        var border=new Block("OPTIONS_FRAME",[new Polyline2D(new[]{new Vector2(0,0),new Vector2(1000,0),new Vector2(1000,700),new Vector2(0,700)},true)]);
        var title=new Block("OPTIONS_TITLE",[new Polyline2D(new[]{new Vector2(0,0),new Vector2(260,0),new Vector2(260,90),new Vector2(0,90)},true)]);
        title.AttributeDefinitions.Add(new AttributeDefinition("TITLE"){Value="old",Position=new(10,25,0),Height=5});
        source.Entities.Add(new Insert(border,new Vector3(1000,500,0)));source.Entities.Add(new Insert(title,new Vector3(1700,520,0)));
        string template=Path.Combine(directory,"options-frame-template.dxf");source.Save(template);Dxf2010Compatibility.NormalizeRasterOwner(template);
        Projection2 Rectangle(string name,double width,double height)=>new(name,[],[new([new(0,0),new(width,0),new(width,height),new(0,height)],true,"VISIBLE")],new(0,0,width,height));
        var front=Rectangle("front",300,100);var top=Rectangle("top",300,200);var end=Rectangle("left",200,100);
        string result=Path.Combine(directory,"titlebar-only.dxf");
        MachiningDxf.Write(result,new("辊密封_1027_A01",front,top,end,DxfFeatureDetector.Analyze(front,top)),new(){TemplatePath=template});
        var saved=DxfDocument.Load(result)??throw new Exception("Cannot reopen titlebar-only drawing.");
        if(saved.Entities.Texts.Any() || !saved.Entities.Inserts.SelectMany(i=>i.Attributes).Any(a=>Convert.ToString(a.Value)=="辊密封A01"))
            throw new Exception("Root drawing name must be removed while the titlebar keeps the real part number.");
        Console.WriteLine("PASS: daily/custom naming, six-row UTF-8 CSV, task CSV round trip, titlebar-only part name.");
    }

    public static async Task HeadlessAsync(string directory,string executable)
    {
        Directory.CreateDirectory(directory);ControlClient.HeadlessExecutable=Path.GetFullPath(executable);
        try
        {
            string seed=Path.GetFullPath(Path.Combine(directory,"seed.solidcraft"));
            using(var client=await ControlClient.ConnectAsync(7878))
            {
                if(!client.IsHeadless||client.HasWindow)throw new Exception("CAD background session must have no window.");
                await client.ExecuteAsync("file.new",new{name="headless export fixture"});
                var prepared=ModelRules.Validate([ModelRules.DefaultRow()])[0];
                foreach(var entry in prepared.Parameters)
                    await client.ExecuteAsync("parameters.change",new{name=entry.Key,expression=entry.Value});
                await client.ExecuteAsync("parameters.change",new{name="半孔校验",expression="0"});
                await client.ExecuteAsync("solid.box",new{length=20,width=10,height=5});
                await client.ExecuteAsync("file.save_as",new{path=seed,cache=true});
            }
            var progress=new Progress<ExportProgress>(p=>Console.WriteLine(p.Message));
            string one=Path.GetFullPath(Path.Combine(directory,"single"));var row=ModelRules.DefaultRow("A01");
            await Exporter.RunAsync(new(ModelRules.Validate([row]),seed,one,7878,10,false,false,"1027","组合试件.step",true,[row],ExportStep:false),progress,CancellationToken.None);
            string nativeOnly=Path.Combine(one,"辊密封_1027_A01.solidcraft");
            if(!File.Exists(nativeOnly)||File.Exists(Path.Combine(one,"辊密封_1027_A01.step")))throw new Exception("DXF-only mode must save the native model without exporting an intermediate STEP.");
            await StepDrawingExporter.RunAsync([nativeOnly],one,7878,progress,CancellationToken.None,new(){IncludeFrame=false,OutputFileName="native-only-drawing"});
            var nativeOnlyDxf=DxfDocument.Load(Path.Combine(one,"native-only-drawing.dxf"))??throw new Exception("Direct native SolidCraft to DXF failed.");
            if(nativeOnlyDxf.Entities.Dimensions.Count()==0)throw new Exception("Direct native projection must include editable dimensions.");
            if(File.Exists(Path.Combine(one,"组合试件.step"))||CsvFiles.Read(Path.Combine(one,"辊密封_1027.csv"))[0][0]!="A01")throw new Exception("No-merge/current-task CSV options ignored.");
            string two=Path.GetFullPath(Path.Combine(directory,"combined"));var rows=new[]{ModelRules.DefaultRow("1"),ModelRules.DefaultRow("2")};
            await Exporter.RunAsync(new(ModelRules.Validate(rows),seed,two,7878,10,true,false,"1027","自定义组合.step",false),progress,CancellationToken.None);
            if(!File.Exists(Path.Combine(two,"自定义组合.step"))||Directory.EnumerateFiles(two,"*.csv").Any())throw new Exception("Merge/disable-CSV options ignored.");
            var files=BatchDrawingInputs.FromFolder(two,"自定义组合.step");if(files.Length!=2)throw new Exception("Custom merged STEP must not be projected as a part.");
            var nativeFiles=rows.Select(r=>Path.Combine(two,OutputNames.Part(r[0],"1027")+".solidcraft")).ToArray();
            await StepDrawingExporter.RunAsync(nativeFiles,two,7878,progress,CancellationToken.None,new(){IncludeFrame=false,OutputFileName="native-source-drawing"});
            var nativeDrawing=DxfDocument.Load(Path.Combine(two,"native-source-drawing.dxf"))??throw new Exception("Direct native SolidCraft projection failed.");
            await StepDrawingExporter.RunAsync(files,two,7878,progress,CancellationToken.None,new(){IncludeFrame=false,OutputFileName="step-source-drawing"});
            var drawing=DxfDocument.Load(Path.Combine(two,"step-source-drawing.dxf"))??throw new Exception("STEP projection failed.");
            if(drawing.Entities.Dimensions.Count()==0||nativeDrawing.Entities.Dimensions.Count()==0)throw new Exception("Orthographic projections need editable dimensions.");
            if(nativeDrawing.Entities.Dimensions.Count()!=drawing.Entities.Dimensions.Count())throw new Exception("Native SolidCraft and STEP projections produced different dimension counts.");
            Console.WriteLine("PASS: direct .solidcraft-to-DXF; no intermediate STEP in DXF-only mode; native/STEP dimension counts match; combined STEP path remains supported.");
        }
        finally{ControlClient.HeadlessExecutable=null;}
    }
}
