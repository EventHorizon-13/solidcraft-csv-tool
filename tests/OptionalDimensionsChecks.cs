using System.Globalization;
using SolidCraftCsvTool;

internal static class OptionalDimensionsChecks
{
    static readonly Dictionary<string,string> Defaults=new()
    { ["子头宽"]="82",["子头突出"]="9",["母槽宽"]="子头宽+2*配合间隙",["母槽深"]="10",["配合间隙"]="1.5" };
    public static void Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var old=ModelRules.DefaultRow("old")[..ModelRules.RequiredColumnCount];
        if(ModelRules.Validate([old])[0].Parameters.Count!=13)throw new Exception("Old 14-column rows must stay compatible.");
        string legacy=Path.Combine(directory,"legacy.csv");
        File.WriteAllText(legacy,string.Join(',',ModelRules.Columns.Take(14))+"\n"+string.Join(',',old));
        if(CsvFiles.Read(legacy)[0].Length!=19)throw new Exception("Old CSV must receive blank optional dimensions.");
        var plain=ModelRules.Validate([ModelRules.DefaultRow()])[0];
        if(ModelRules.ChangesWithDefaults(plain,Defaults)["母槽宽"]!="子头宽+2*配合间隙")throw new Exception("Default expression must stay linked.");
        var custom=ModelRules.DefaultRow();custom[14]="80";custom[15]="8";custom[17]="12";custom[18]="2";
        var explicitValues=ModelRules.ChangesWithDefaults(ModelRules.Validate([custom])[0],Defaults);
        if(explicitValues["子头宽"]!="80"||explicitValues["配合间隙"]!="2"||explicitValues["母槽宽"]!=Defaults["母槽宽"])
            throw new Exception("Only filled dimensions may override the template.");
        if(ModelRules.ChangesWithDefaults(plain,Defaults)["子头宽"]!="82")throw new Exception("A blank next row must reset the prior override.");
        var invalid=ModelRules.DefaultRow();invalid[17]="-1";bool rejected=false;
        try{ModelRules.Validate([invalid]);}catch(InvalidDataException){rejected=true;}
        if(!rejected)throw new Exception("Negative groove depth must be rejected.");
        var templateRows=CsvFiles.Read("examples/辊密封参数模板.csv");
        if(templateRows.Count!=6||templateRows.Any(r=>r.Length!=19)||ModelRules.Validate(templateRows).Count!=6)throw new Exception("Updated template must contain 19 columns and six valid rows.");
        string app=Path.Combine(directory,"app"),work=Path.Combine(directory,"working");Directory.CreateDirectory(Path.Combine(app,"模板"));Directory.CreateDirectory(Path.Combine(work,"模板"));
        File.WriteAllText(Path.Combine(app,"模板","V13.solidcraft"),"");File.WriteAllText(Path.Combine(app,"模板","图框.dxf"),"");File.WriteAllText(Path.Combine(work,"模板","old.solidcraft"),"");
        var found=LocalTemplates.Find(app,work);
        if(found.Native!=Path.Combine(app,"模板","V13.solidcraft")||found.Dxf!=Path.Combine(app,"模板","图框.dxf"))throw new Exception("Application-local template folder must take priority.");
        Console.WriteLine("PASS: old CSV compatibility, five optional dimensions, blank defaults and formula preservation, local 模板 folder detection.");
    }
    public static async Task HeadlessAsync(string directory,string executable)
    {
        Directory.CreateDirectory(directory);ControlClient.HeadlessExecutable=Path.GetFullPath(executable);
        try
        {
            string seed=Path.GetFullPath(Path.Combine(directory,"optional-seed.solidcraft"));
            using(var client=await ControlClient.ConnectAsync(7878))
            {
                await client.ExecuteAsync("file.new",new{name="optional dimension fixture"});
                foreach(var p in ModelRules.Validate([ModelRules.DefaultRow()])[0].Parameters)
                    await client.ExecuteAsync("parameters.change",new{name=p.Key,expression=p.Value});
                foreach(var p in Defaults.OrderBy(p=>p.Key=="母槽宽"?1:0))await client.ExecuteAsync("parameters.change",new{name=p.Key,expression=p.Value,unit="mm"});
                await client.ExecuteAsync("parameters.change",new{name="半孔校验",expression="0"});
                await client.ExecuteAsync("solid.box",new{length="子头宽",width="母槽宽",height="母槽深"});
                await client.ExecuteAsync("file.save_as",new{path=seed,cache=true});
            }
            var custom=ModelRules.DefaultRow("custom");custom[14]="80";custom[15]="8";custom[17]="12";custom[18]="2";
            var blank=ModelRules.DefaultRow("default");var explicitMother=ModelRules.DefaultRow("mother");explicitMother[16]="100";
            string[][] rows=[custom,blank,explicitMother];string folder=Path.GetFullPath(Path.Combine(directory,"parts"));
            await Exporter.RunAsync(new(ModelRules.Validate(rows),seed,folder,7878,10,false,false,"1027",null,true,rows),new Progress<ExportProgress>(p=>Console.WriteLine(p.Message)),CancellationToken.None);
            string[][] expected=[["80","8","84","12","2"],["82","9","85","10","1.5"],["82","9","100","10","1.5"]];
            var snapshot=CsvFiles.Read(Path.Combine(folder,"辊密封_1027.csv"));
            for(int i=0;i<rows.Length;i++)
            {
                if(!snapshot[i][14..].SequenceEqual(expected[i]))throw new Exception("Task CSV must store the actual per-row dimensions: "+string.Join(',',snapshot[i][14..]));
                using var client=await ControlClient.ConnectAsync(7878);
                await client.ExecuteAsync("doc.open",new{path=Path.Combine(folder,OutputNames.Part(rows[i][0],"1027")+".solidcraft")});
                var bounds=ModelRules.ReadBounds(await client.InspectAsync());
                double[] sizes=[bounds.Max[0]-bounds.Min[0],bounds.Max[1]-bounds.Min[1],bounds.Max[2]-bounds.Min[2]];
                double[] wanted=[double.Parse(expected[i][0],CultureInfo.InvariantCulture),double.Parse(expected[i][2],CultureInfo.InvariantCulture),double.Parse(expected[i][3],CultureInfo.InvariantCulture)];
                if(sizes.Zip(wanted).Any(p=>Math.Abs(p.First-p.Second)>.001))throw new Exception("CSV dimensions did not change the exported geometry.");
                if(i==1)
                {
                    var result=await client.ExecuteAsync("parameters.list",new{filter="user"});
                    var mother=result.GetProperty("parameters").EnumerateArray().Single(p=>p.GetProperty("name").GetString()=="母槽宽");
                    if(!mother.GetProperty("expression").GetString()!.Contains("子头宽"))throw new Exception("Blank mother width must restore the original formula.");
                }
            }
            Console.WriteLine("PASS: actual CLI geometry and task CSV match all five dimensions; blank next row resets original defaults; mother width formula restored.");
        }
        finally{ControlClient.HeadlessExecutable=null;}
    }
}
