using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media.Imaging;

namespace SolidCraftCsvTool;

internal sealed class AboutWindow : Window
{
    internal AboutWindow()
    {
        SetResourceReference(BackgroundProperty,"WindowBackground");
        Title="关于";Width=470;Height=330;ResizeMode=ResizeMode.NoResize;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        FontFamily=new System.Windows.Media.FontFamily("Segoe UI, Microsoft YaHei UI");FontSize=13;
        var root=new Grid{Margin=new Thickness(24)};Content=root;
        foreach(double height in new[]{60,28,28,32})root.RowDefinitions.Add(new(){Height=new GridLength(height)});
        root.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});root.RowDefinitions.Add(new(){Height=GridLength.Auto});
        var header=new StackPanel{Orientation=Orientation.Horizontal};
        header.Children.Add(new Image{Source=new BitmapImage(new Uri("pack://application:,,,/Assets/AppIcon.png")),Width=36,Height=36,Margin=new Thickness(0,0,14,0),VerticalAlignment=VerticalAlignment.Top});
        var name=new StackPanel();name.Children.Add(new TextBlock{Text=AppBuildInfo.ProductName,FontWeight=FontWeights.SemiBold,FontSize=15});
        name.Children.Add(new TextBlock{Text="版本 "+AppBuildInfo.Version,Margin=new Thickness(0,5,0,0)});header.Children.Add(name);root.Children.Add(header);
        var author=Link("EventHorizon-13","https://github.com/EventHorizon-13");Grid.SetRow(author,1);root.Children.Add(author);
        var build=new TextBlock{Text="构建时间  "+AppBuildInfo.BuildDate,VerticalAlignment=VerticalAlignment.Center};Grid.SetRow(build,2);root.Children.Add(build);
        var links=new StackPanel{Orientation=Orientation.Horizontal};links.Children.Add(Link("项目主页","https://github.com/EventHorizon-13/solidcraft-roller-seal"));
        var notices=new TextBlock{Margin=new Thickness(18,0,0,0),VerticalAlignment=VerticalAlignment.Center};var noticeLink=new Hyperlink(new Run("组件与许可"));noticeLink.Click+=(_,_)=>ShowNotices();notices.Inlines.Add(noticeLink);links.Children.Add(notices);Grid.SetRow(links,3);root.Children.Add(links);
        var buttons=new StackPanel{Orientation=Orientation.Horizontal};
        var check=new Button{Content="检查更新",Padding=new Thickness(12,5,12,5),Margin=new Thickness(0,0,8,0)};
        var close=new Button{Content="关闭",Padding=new Thickness(18,5,18,5),IsCancel=true};close.Click+=(_,_)=>Close();
        var status=new TextBlock{Margin=new Thickness(12,0,0,0),VerticalAlignment=VerticalAlignment.Center};
        check.Click+=async(_,_)=>await CheckUpdateAsync(check,status);buttons.Children.Add(check);buttons.Children.Add(close);buttons.Children.Add(status);Grid.SetRow(buttons,5);root.Children.Add(buttons);
    }
    static TextBlock Link(string text,string url)
    {
        var block=new TextBlock{VerticalAlignment=VerticalAlignment.Center};var link=new Hyperlink(new Run(text));link.Click+=(_,_)=>OpenLink(url);block.Inlines.Add(link);return block;
    }
    static void OpenLink(string url)
    {
        try{Process.Start(new ProcessStartInfo(url){UseShellExecute=true});}catch(Exception ex){MessageBox.Show(ex.Message,"无法打开浏览器");}
    }
    async Task CheckUpdateAsync(Button button,TextBlock status)
    {
        button.IsEnabled=false;button.Content="正在检查…";
        try
        {
            var release=await UpdateService.GetLatestAsync();
            if(!UpdateService.IsNewer(release)){status.Text="已是最新版本";return;}
            if(!release.Target.CanInstallInPlace){OpenLink(release.PageUrl);return;}
            if(MessageBox.Show(this,$"发现新版本 {release.Version}（当前 {AppBuildInfo.Version}）。\n下载并更新后程序会自动重新启动。","发现新版本",MessageBoxButton.YesNo,MessageBoxImage.Information)!=MessageBoxResult.Yes)return;
            string install=Path.GetDirectoryName(Environment.ProcessPath??"")??AppContext.BaseDirectory;
            string probe=Path.Combine(install,".update-permission-"+Guid.NewGuid().ToString("N"));
            try{using(File.Create(probe)){}File.Delete(probe);}
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){OpenLink(release.PageUrl);return;}
            button.Content="正在下载…";
            string stage=await UpdateService.DownloadAndStageAsync(release,new Progress<int>(value=>status.Text=$"正在下载 {value}%"));
            try{UpdateService.StartInstaller(stage);Application.Current.Shutdown();}
            catch{try{Directory.Delete(Path.GetDirectoryName(stage)!,true);}catch{}throw;}
        }
        catch(Exception ex){status.Text="更新失败";MessageBox.Show(this,ex.Message,"更新失败",MessageBoxButton.OK,MessageBoxImage.Warning);}
        finally{if(IsLoaded){button.IsEnabled=true;button.Content="检查更新";}}
    }
    void ShowNotices()
    {
        string file=Path.Combine(AppContext.BaseDirectory,"THIRD_PARTY_NOTICES.md");
        string text=File.Exists(file)?File.ReadAllText(file):"SolidCraft CLI、netDxf、MLightCAD、Node.js、.NET。许可见软件包。";
        new Window{Owner=this,Title="组件与许可",Width=700,Height=500,WindowStartupLocation=WindowStartupLocation.CenterOwner,
            Content=new TextBox{Text=text,IsReadOnly=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Margin=new Thickness(16),Padding=new Thickness(8)}}.ShowDialog();
    }
}
