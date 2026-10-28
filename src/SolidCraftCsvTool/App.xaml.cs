using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;

namespace SolidCraftCsvTool;

internal enum AppearanceMode { Automatic, Dark, Light }

public partial class DesktopApp : Application
{
    // Microsoft documents ThemeMode as the Fluent theme entry point; .NET 10 still marks the API as evaluation.
#pragma warning disable WPF0001
    internal static void SetAppearance(AppearanceMode mode)=>Current.ThemeMode=mode switch
    {
        AppearanceMode.Dark=>System.Windows.ThemeMode.Dark,
        AppearanceMode.Light=>System.Windows.ThemeMode.Light,
        _=>System.Windows.ThemeMode.System
    };
#pragma warning restore WPF0001
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        if(e.Args.Length==4&&e.Args[0]=="--apply-update")
        {try{Shutdown(UpdateInstaller.Apply(e.Args));}catch(Exception ex){MessageBox.Show(ex.Message,"更新失败");Shutdown(1);}return;}
        if(e.Args.Length==3&&e.Args[0]=="--cleanup-update")UpdateInstaller.CleanupStage(e.Args);
        try
        {
            var settings=DesktopSettings.Load();SetAppearance(settings.Appearance);
            var window=new MainWindow(settings);MainWindow=window;
            DispatcherUnhandledException+=(_,args)=>
            {
                if(e.Args.Contains("--smoke-test")){File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"smoke-error.txt"),args.Exception.ToString());Shutdown(1);}
                else MessageBox.Show(window,args.Exception.Message,"操作失败",MessageBoxButton.OK,MessageBoxImage.Error);
                args.Handled=true;
            };
            if(e.Args.Contains("--smoke-test"))window.Loaded+=async(_,_)=>
            {
                try{await DesktopSmoke.Run(window,e.Args);Shutdown(0);}
                catch(Exception ex){File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"smoke-error.txt"),ex.ToString());Shutdown(1);}
            };
            window.Show();
        }
        catch(Exception ex)
        {
            if(e.Args.Contains("--smoke-test"))File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"smoke-error.txt"),ex.ToString());
            else MessageBox.Show(ex.Message,"启动失败");Shutdown(1);
        }
    }
}

internal sealed class DesktopSettings
{
    public string output {get;set;}=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),"SolidCraft输出");
    public bool combined {get;set;}=true;
    public bool taskCsv {get;set;}=true;
    public bool overwrite {get;set;}
    public bool openDxf {get;set;}
    public bool exportPdf {get;set;}
    public string themeMode {get;set;}="Automatic";
    public string? dxfBase {get;set;}
    public string? stepBase {get;set;}
    public DrawingOptions drawing {get;set;}=new();
    [System.Text.Json.Serialization.JsonIgnore]
    public AppearanceMode Appearance=>Enum.TryParse<AppearanceMode>(themeMode,out var mode)&&Enum.IsDefined(mode)?mode:AppearanceMode.Automatic;
    static string SettingsPath=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"SolidCraft","csv-tool","settings.json");
    public static DesktopSettings Load()
    {
        try{return File.Exists(SettingsPath)?JsonSerializer.Deserialize<DesktopSettings>(File.ReadAllText(SettingsPath))??new():new();}
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or JsonException){return new();}
    }
    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        string stage=SettingsPath+".tmp";File.WriteAllText(stage,JsonSerializer.Serialize(this));File.Move(stage,SettingsPath,true);
    }
}
