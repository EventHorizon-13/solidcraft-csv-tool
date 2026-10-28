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
        }
        DesktopApp.SetAppearance(AppearanceMode.Light);await Settle(window);
        if(Arg("--screenshot") is string normal)Capture(window,normal);
        if(Arg("--dark-screenshot") is string dark){DesktopApp.SetAppearance(AppearanceMode.Dark);await Settle(window);VerifyContrast(window);VerifyGridHeaders(window,AppearanceMode.Dark);Capture(window,dark);}
        if(Arg("--about-screenshot") is string about)
        {
            var dialog=new AboutWindow{Owner=window};dialog.Show();
            foreach(var mode in new[]{AppearanceMode.Light,AppearanceMode.Dark}){DesktopApp.SetAppearance(mode);await Settle(dialog);VerifyContrast(dialog);Capture(dialog,mode==AppearanceMode.Light?about:Path.ChangeExtension(about,"dark.png"));}
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
                    dialog.Tabs.SelectedIndex=tab;await Settle(dialog);VerifyContrast(dialog);
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
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"smoke-success.txt"),"PASS: six-row data, light/dark/system and repeated switches; text and grid header contrast; main/settings/about and minimum size.");
        window.Close();
    }
}
