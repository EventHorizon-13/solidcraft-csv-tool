using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace SolidCraftCsvTool;

internal static class DesktopSmoke
{
    static async Task Settle(Window window)
    {
        window.UpdateLayout();await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);window.UpdateLayout();
    }
    static IEnumerable<DependencyObject> All(DependencyObject root)
    {
        yield return root;for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)foreach(var item in All(VisualTreeHelper.GetChild(root,i)))yield return item;
    }
    static Color Composite(Color top,Color bottom,double opacity=1)
    {
        double alpha=top.A/255d*opacity;return Color.FromRgb((byte)(top.R*alpha+bottom.R*(1-alpha)),(byte)(top.G*alpha+bottom.G*(1-alpha)),(byte)(top.B*alpha+bottom.B*(1-alpha)));
    }
    static Color Background(DependencyObject? element)
    {
        if(element==null)return Colors.White;
        Color parent=Background(VisualTreeHelper.GetParent(element));
        Brush? brush=element switch{Control control=>control.Background,Panel panel=>panel.Background,Border border=>border.Background,_=>null};
        return brush is SolidColorBrush solid?Composite(solid.Color,parent,solid.Opacity):parent;
    }
    static double Luminance(Color color)
    {
        static double Linear(byte value){double n=value/255d;return n<=.04045?n/12.92:Math.Pow((n+.055)/1.055,2.4);}
        return .2126*Linear(color.R)+.7152*Linear(color.G)+.0722*Linear(color.B);
    }
    static void VerifyContrast(Window window)
    {
        int verified=0;
        foreach(var control in All(window).OfType<Control>().Where(c=>c.IsVisible&&c.IsEnabled&&c is Button or TextBox or ComboBox))
        {
            if(control.Foreground is not SolidColorBrush foreground)continue;
            Color background=Background(control),text=Composite(foreground.Color,background,foreground.Opacity);
            double a=Luminance(text),b=Luminance(background),ratio=(Math.Max(a,b)+.05)/(Math.Min(a,b)+.05);
            if(ratio<4)throw new InvalidOperationException($"文字对比度不足：{control.GetType().Name} {control.Name}, foreground={text}, background={background}, ratio={ratio:F2}");
            verified++;
        }
        if(verified==0)throw new InvalidOperationException("未检查到可见文字控件。");
    }
    static void VerifyGridHeaders(MainWindow window,AppearanceMode mode)
    {
        var headers=All(window).OfType<DataGridColumnHeader>().Where(h=>h.IsVisible).ToArray();
        if(headers.Length==0)throw new InvalidOperationException("未找到可见的参数表列头。");
        foreach(var header in headers)
        {
            if(header.Background is not SolidColorBrush || header.Foreground is not SolidColorBrush foreground)
                throw new InvalidOperationException("参数表列头未绑定主题背景或文字颜色。");
            Color background=Background(header),text=Composite(foreground.Color,background,foreground.Opacity);
            double a=Luminance(text),b=Luminance(background),ratio=(Math.Max(a,b)+.05)/(Math.Min(a,b)+.05);
            if(ratio<4.5)throw new InvalidOperationException($"参数表列头对比度不足：{header.Content} ratio={ratio:F2}");
            if(mode==AppearanceMode.Dark&&b>.20)
                throw new InvalidOperationException($"深色模式列头背景仍过亮：{header.Content} background={background}");
            if(mode==AppearanceMode.Light&&b<.50)
                throw new InvalidOperationException($"浅色模式列头背景过暗：{header.Content} background={background}");
        }
    }
    static void VerifySelectedRow(MainWindow window,AppearanceMode mode)
    {
        var cells=All(window).OfType<DataGridCell>().Where(c=>c.IsVisible&&c.IsSelected).ToArray();
        if(cells.Length==0)throw new InvalidOperationException("未找到选中的参数单元格。");
        foreach(var cell in cells)
        {
            if(cell.Background is not SolidColorBrush || cell.Foreground is not SolidColorBrush foreground)
                throw new InvalidOperationException("选中单元格未应用自定义主题颜色。");
            var background=Background(cell);
            var text=Composite(foreground.Color,background,foreground.Opacity);
            double a=Luminance(text),b=Luminance(background);
            double ratio=(Math.Max(a,b)+.05)/(Math.Min(a,b)+.05);
            if(ratio<4.5)throw new InvalidOperationException($"选中行文字对比度不足：{ratio:F2}");
            if(cell.BorderThickness.Left<1||cell.BorderThickness.Top<1||cell.BorderThickness.Right<1||cell.BorderThickness.Bottom<1)
                throw new InvalidOperationException("选中单元格缺少可见外框。");
            if(mode==AppearanceMode.Dark&&b>.18)
                throw new InvalidOperationException($"深色选中行仍然过亮：{background}");
            if(mode==AppearanceMode.Light&&b<.50)
                throw new InvalidOperationException($"浅色选中行背景过暗：{background}");
        }
        var focused=cells.FirstOrDefault(c=>c.IsKeyboardFocusWithin);
        if(focused==null)throw new InvalidOperationException("选中行没有获得焦点的当前单元格。");
        if(focused.BorderThickness.Left<2||focused.BorderThickness.Top<2||focused.BorderThickness.Right<2||focused.BorderThickness.Bottom<2)
            throw new InvalidOperationException("当前单元格的强调边框未显示。");
        if(focused.BorderBrush is not SolidColorBrush focusBorder||focusBorder.Color.A==0)
            throw new InvalidOperationException("当前单元格的强调边框颜色不可见。");
    }
    static void VerifySelectedTab(SettingsWindow dialog,int index)
    {
        if(dialog.Tabs.ItemContainerGenerator.ContainerFromIndex(index) is not TabItem tab)
            throw new InvalidOperationException($"未找到设置选项卡 {index}。");
        tab.ApplyTemplate();
        if(tab.Background is not SolidColorBrush background||background.Color.A==0)
            throw new InvalidOperationException($"选中的设置选项卡 {index} 没有高亮背景。");
        var indicator=tab.Template.FindName("SelectionIndicator",tab) as Border;
        if(indicator==null||indicator.Visibility!=Visibility.Visible)
            throw new InvalidOperationException($"选中的设置选项卡 {index} 没有底部强调线。");
        if(indicator.Background is not SolidColorBrush accent||accent.Color.A==0)
            throw new InvalidOperationException($"选项卡 {index} 的强调线颜色不可见。");
        if(tab.Foreground is not SolidColorBrush foreground)
            throw new InvalidOperationException($"选项卡 {index} 未设置文字颜色。");
        Color text=Composite(foreground.Color,background.Color,foreground.Opacity);
        double a=Luminance(text),b=Luminance(background.Color),ratio=(Math.Max(a,b)+.05)/(Math.Min(a,b)+.05);
        if(ratio<4.5)throw new InvalidOperationException($"选项卡文字对比度不足：{ratio:F2}");
    }
    static void VerifyUpdatePrompt(Window dialog)
    {
        if(dialog.Title!="发现新版本"||dialog.ActualHeight>240)
            throw new InvalidOperationException("更新确认窗口标题或尺寸不正确。");
        var buttons=All(dialog).OfType<Button>().ToArray();
        if(!buttons.Any(b=>Equals(b.Content,"是"))||!buttons.Any(b=>Equals(b.Content,"否")))
            throw new InvalidOperationException("更新确认窗口缺少“是／否”操作。");
        var message=All(dialog).OfType<TextBlock>().FirstOrDefault(t=>t.Text.Contains("发现新版本")&&t.Text.Contains("自动重新启动"));
        if(message?.Foreground is not SolidColorBrush foreground)
            throw new InvalidOperationException("更新确认窗口缺少可读的版本提示文字。");
        Color background=Background(message),text=Composite(foreground.Color,background,foreground.Opacity);
        double a=Luminance(text),b=Luminance(background),ratio=(Math.Max(a,b)+.05)/(Math.Min(a,b)+.05);
        if(ratio<4.5)throw new InvalidOperationException($"更新提示文字对比度不足：{ratio:F2}");
    }
    static void VerifyAboutLayout(Window dialog)
    {
        if(dialog.ActualHeight>355)throw new InvalidOperationException($"关于窗口高度过大：{dialog.ActualHeight:F0}");
        var controls=All(dialog).OfType<Button>().ToArray();
        if(!controls.Any(b=>Equals(b.Content,"检查更新"))||!controls.Any(b=>Equals(b.Content,"关闭")))
            throw new InvalidOperationException("关于窗口缺少操作按钮。");
    }
    static void Capture(Window window,string path)
    {
        var image=new RenderTargetBitmap(Math.Max(1,(int)Math.Ceiling(window.ActualWidth)),Math.Max(1,(int)Math.Ceiling(window.ActualHeight)),96,96,PixelFormats.Pbgra32);image.Render(window);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using var file=File.Create(path);encoder.Save(file);
    }
    internal static async Task Run(MainWindow window,string[] args)
    {
        string? Arg(string name){int i=Array.IndexOf(args,name);return i>=0&&i+1<args.Length?args[i+1]:null;}
        window.LoadSmokeRows();
        foreach(var mode in new[]{AppearanceMode.Automatic,AppearanceMode.Dark,AppearanceMode.Light})
        {
            DesktopApp.SetAppearance(mode);await Settle(window);window.VerifyUi();VerifyContrast(window);VerifyGridHeaders(window,mode);
            window.SelectSmokeRow();await Settle(window);VerifySelectedRow(window,mode);window.ClearSmokeSelection();
        }
        DesktopApp.SetAppearance(AppearanceMode.Light);await Settle(window);
        if(Arg("--screenshot") is string normal)
        {
            Capture(window,normal);
            window.SelectSmokeRow();await Settle(window);
            VerifySelectedRow(window,AppearanceMode.Light);
            Capture(window,Path.ChangeExtension(normal,"selected.png"));
            window.ClearSmokeSelection();
        }
        if(Arg("--dark-screenshot") is string dark)
        {
            DesktopApp.SetAppearance(AppearanceMode.Dark);window.SelectSmokeRow();await Settle(window);
            VerifyContrast(window);VerifyGridHeaders(window,AppearanceMode.Dark);VerifySelectedRow(window,AppearanceMode.Dark);
            Capture(window,dark);window.ClearSmokeSelection();
        }
        if(Arg("--about-screenshot") is string about)
        {
            var dialog=new AboutWindow{Owner=window};dialog.Show();
            foreach(var mode in new[]{AppearanceMode.Light,AppearanceMode.Dark}){DesktopApp.SetAppearance(mode);await Settle(dialog);VerifyContrast(dialog);VerifyAboutLayout(dialog);Capture(dialog,mode==AppearanceMode.Light?about:Path.ChangeExtension(about,"dark.png"));}
            dialog.Close();
        }
        if(Arg("--update-prompt-screenshot") is string promptPath)
        {
            var dialog=new UpdatePromptWindow("2.2.5","2.2.4"){Owner=window};dialog.Show();
            foreach(var mode in new[]{AppearanceMode.Light,AppearanceMode.Dark})
            {
                DesktopApp.SetAppearance(mode);await Settle(dialog);VerifyContrast(dialog);VerifyUpdatePrompt(dialog);
                Capture(dialog,mode==AppearanceMode.Light?promptPath:Path.ChangeExtension(promptPath,"dark.png"));
            }
            dialog.Close();
        }
        if(Arg("--settings-screenshot") is string settings)
        {
            var dialog=new SettingsWindow(new(),AppearanceMode.Automatic){Owner=window};dialog.Show();
            foreach(var mode in new[]{AppearanceMode.Light,AppearanceMode.Dark})
            {
                DesktopApp.SetAppearance(mode);
                for(int tab=0;tab<dialog.Tabs.Items.Count;tab++)
                {
                    dialog.Tabs.SelectedIndex=tab;await Settle(dialog);VerifyContrast(dialog);VerifySelectedTab(dialog,tab);
                    string suffix=(mode==AppearanceMode.Dark?"dark.":"")+(tab==0?"":$"tab{tab}.")+"png";
                    Capture(dialog,Path.ChangeExtension(settings,suffix));
                }
            }
            dialog.Close();
        }
        if(Arg("--minimum-screenshot") is string minimum)
        {
            window.Width=window.MinWidth;window.Height=window.MinHeight;DesktopApp.SetAppearance(AppearanceMode.Light);await Settle(window);window.VerifyUi();VerifyContrast(window);Capture(window,minimum);
        }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"smoke-success.txt"),"PASS: six-row data; theme contrast; selected-cell outline and focused-cell accent; selected settings-tab background and indicator; themed update prompt; aligned About dialog; adjustable log; main/settings/about/update prompt and minimum size.");
        window.Close();
    }
}
