using System.Text;

namespace SolidCraftCsvTool;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if(args.Length==4&&args[0]=="--apply-update")
        {
            try{return UpdateInstaller.Apply(args);}catch(Exception ex){MessageBox.Show("更新未能完成：\n"+ex.Message,"软件更新",MessageBoxButtons.OK,MessageBoxIcon.Error);return 1;}
        }
        if(args.Length==3&&args[0]=="--cleanup-update")
        {
            UpdateInstaller.CleanupStage(args);args=[];
        }
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        ApplicationConfiguration.Initialize();
        try
        {
            using MainForm form = new();
            if (args.Contains("--smoke-test"))
            {
                form.Show(); Application.DoEvents(); form.VerifyUi();
                var initialTheme=AppTheme.Mode;
                foreach(var mode in Enum.GetValues<ThemeMode>())
                {
                    AppTheme.SetMode(mode);Application.DoEvents();form.VerifyUi();
                }
                AppTheme.SetMode(initialTheme);
                int darkIndex=Array.IndexOf(args,"--dark-screenshot");
                if(darkIndex>=0&&darkIndex+1<args.Length)
                {
                    AppTheme.SetMode(ThemeMode.Dark);Application.DoEvents();
                    using Bitmap shot=new(form.Width,form.Height);form.DrawToBitmap(shot,new Rectangle(Point.Empty,form.Size));
                    shot.Save(args[darkIndex+1],System.Drawing.Imaging.ImageFormat.Png);AppTheme.SetMode(initialTheme);
                }
                var index = Array.IndexOf(args, "--screenshot");
                if (index >= 0 && index + 1 < args.Length)
                {
                    using Bitmap screenshot = new(form.Width, form.Height);
                    form.DrawToBitmap(screenshot, new Rectangle(Point.Empty, form.Size));
                    screenshot.Save(args[index + 1], System.Drawing.Imaging.ImageFormat.Png);
                }
                int aboutIndex=Array.IndexOf(args,"--about-screenshot");
                if(aboutIndex>=0&&aboutIndex+1<args.Length)
                {
                    using var about=new AboutForm();about.Show(form);Application.DoEvents();
                    about.VerifyUi();
                    using Bitmap shot=new(about.Width,about.Height);about.DrawToBitmap(shot,new Rectangle(Point.Empty,about.Size));
                    shot.Save(args[aboutIndex+1],System.Drawing.Imaging.ImageFormat.Png);about.Close();
                }
                int settingsIndex=Array.IndexOf(args,"--settings-screenshot");
                if(settingsIndex>=0&&settingsIndex+1<args.Length)
                {
                    using var drawing=new DrawingSettingsForm(new());drawing.Show(form);Application.DoEvents();
                    using Bitmap shot=new(drawing.Width,drawing.Height);drawing.DrawToBitmap(shot,new Rectangle(Point.Empty,drawing.Size));
                    shot.Save(args[settingsIndex+1],System.Drawing.Imaging.ImageFormat.Png);drawing.Close();
                }
                int minimumIndex=Array.IndexOf(args,"--minimum-screenshot");
                if(minimumIndex>=0&&minimumIndex+1<args.Length)
                {
                    form.Size=form.MinimumSize;Application.DoEvents();form.VerifyUi();
                    using Bitmap shot=new(form.Width,form.Height);form.DrawToBitmap(shot,new Rectangle(Point.Empty,form.Size));
                    shot.Save(args[minimumIndex+1],System.Drawing.Imaging.ImageFormat.Png);
                }
                form.Close(); return 0;
            }
            Application.Run(form);
            return 0;
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error); return 1; }
    }
}
