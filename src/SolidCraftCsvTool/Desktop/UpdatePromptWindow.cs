using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SolidCraftCsvTool;

internal sealed class UpdatePromptWindow : Window
{
    internal UpdatePromptWindow(string version,string currentVersion)
    {
        SetResourceReference(BackgroundProperty,"WindowBackground");
        Title="发现新版本";Width=400;MinWidth=400;MaxWidth=400;SizeToContent=SizeToContent.Height;
        ResizeMode=ResizeMode.NoResize;WindowStartupLocation=WindowStartupLocation.CenterOwner;ShowInTaskbar=false;
        FontFamily=new System.Windows.Media.FontFamily("Segoe UI, Microsoft YaHei UI");FontSize=13;

        var root=new Grid{Margin=new Thickness(22,20,22,18)};
        root.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(40)});
        root.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(14)});
        root.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});
        root.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
        root.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
        Content=root;

        var infoText=new TextBlock
        {
            Text="i",FontSize=22,FontWeight=FontWeights.Bold,Foreground=Brushes.White,
            HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center
        };
        var infoIcon=new Border
        {
            Width=36,Height=36,CornerRadius=new CornerRadius(18),
            Background=new SolidColorBrush(Color.FromRgb(0,120,212)),
            Child=infoText,VerticalAlignment=VerticalAlignment.Top
        };
        Grid.SetRow(infoIcon,0);root.Children.Add(infoIcon);

        var message=new TextBlock
        {
            Text=$"发现新版本 {version}（当前 {currentVersion}）。\n下载并更新后程序会自动重新启动。",
            TextWrapping=TextWrapping.Wrap,VerticalAlignment=VerticalAlignment.Center,FontSize=13
        };
        Grid.SetColumn(message,2);root.Children.Add(message);

        var actions=new StackPanel
        {
            Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,
            Margin=new Thickness(0,18,0,0)
        };
        var yes=new Button{Content="是",MinWidth=78,Padding=new Thickness(14,6,14,6),IsDefault=true,Margin=new Thickness(0,0,8,0)};
        var no=new Button{Content="否",MinWidth=78,Padding=new Thickness(14,6,14,6),IsCancel=true};
        yes.Click+=(_,_)=>DialogResult=true;
        no.Click+=(_,_)=>DialogResult=false;
        actions.Children.Add(yes);actions.Children.Add(no);
        Grid.SetRow(actions,1);Grid.SetColumn(actions,2);root.Children.Add(actions);
    }
}
