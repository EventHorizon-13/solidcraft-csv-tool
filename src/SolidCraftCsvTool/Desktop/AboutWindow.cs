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
        Title="关于";Width=520;SizeToContent=SizeToContent.Height;
        ResizeMode=ResizeMode.NoResize;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        FontFamily=new System.Windows.Media.FontFamily("Segoe UI, Microsoft YaHei UI");FontSize=13;

        var root=new Grid{Margin=new Thickness(24,20,24,20)};
        root.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(46)});
        root.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(16)});
        root.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});
        for(int i=0;i<3;i++)root.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
        Content=root;

        var icon=new Image
        {
            Source=new BitmapImage(new Uri("pack://application:,,,/Assets/AppIcon.png")),
            Width=46,Height=46,VerticalAlignment=VerticalAlignment.Top
        };
        Grid.SetColumn(icon,0);Grid.SetRowSpan(icon,2);root.Children.Add(icon);

        var header=new StackPanel{VerticalAlignment=VerticalAlignment.Center};
        header.Children.Add(new TextBlock{Text=AppBuildInfo.ProductName,FontWeight=FontWeights.SemiBold,FontSize=17});
        header.Children.Add(new TextBlock{Text="版本 "+AppBuildInfo.Version,FontSize=12,Opacity=.70,Margin=new Thickness(0,5,0,0)});
        Grid.SetColumn(header,2);root.Children.Add(header);

        var details=new Grid{Margin=new Thickness(0,12,0,0)};
        details.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(84)});
        details.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});
        for(int i=0;i<4;i++)details.RowDefinitions.Add(new RowDefinition{Height=new GridLength(30)});
        Grid.SetRow(details,1);Grid.SetColumn(details,2);root.Children.Add(details);
        static TextBlock Label(string text)=>new()
        {
            Text=text,Opacity=.68,VerticalAlignment=VerticalAlignment.Center
        };
        void AddDetail(int row,string label,TextBlock value)
        {
            var caption=Label(label);Grid.SetRow(caption,row);details.Children.Add(caption);
            Grid.SetColumn(value,1);Grid.SetRow(value,row);details.Children.Add(value);
        }
        AddDetail(0,"作者",Link("EventHorizon-13","https://github.com/EventHorizon-13"));
        AddDetail(1,"构建时间",new TextBlock{Text=AppBuildInfo.BuildDate,VerticalAlignment=VerticalAlignment.Center});
        AddDetail(2,"项目",Link("GitHub 项目主页","https://github.com/EventHorizon-13/solidcraft-roller-seal"));
        var notices=new TextBlock{VerticalAlignment=VerticalAlignment.Center};
        var noticeLink=new Hyperlink(new Run("组件与许可"));
        noticeLink.Click+=(_,_)=>ShowNotices();
        notices.Inlines.Add(noticeLink);
        AddDetail(3,"许可",notices);

        var actions=new Grid{Margin=new Thickness(0,18,0,0)};
        actions.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
        actions.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});
        actions.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
        Grid.SetRow(actions,2);Grid.SetColumn(actions,2);root.Children.Add(actions);

        var check=new Button{Content="检查更新",Padding=new Thickness(14,6,14,6)};
        var status=new TextBlock{Margin=new Thickness(12,0,12,0),VerticalAlignment=VerticalAlignment.Center,TextTrimming=TextTrimming.CharacterEllipsis};
        var close=new Button{Content="关闭",Padding=new Thickness(20,6,20,6),IsCancel=true};
        close.Click+=(_,_)=>Close();
        check.Click+=async(_,_)=>await CheckUpdateAsync(check,status);
        Grid.SetColumn(status,1);Grid.SetColumn(close,2);
        actions.Children.Add(check);actions.Children.Add(status);actions.Children.Add(close);
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
            var prompt=new UpdatePromptWindow(release.Version,AppBuildInfo.Version){Owner=this};
            if(prompt.ShowDialog()!=true)return;
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
