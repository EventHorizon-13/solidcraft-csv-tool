using System.Text;

namespace SolidCraftCsvTool;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        ApplicationConfiguration.Initialize();
        try
        {
            using MainForm form = new();
            if (args.Contains("--smoke-test"))
            {
                form.Show(); Application.DoEvents(); form.VerifyUi();
                var index = Array.IndexOf(args, "--screenshot");
                if (index >= 0 && index + 1 < args.Length)
                {
                    using Bitmap screenshot = new(form.Width, form.Height);
                    form.DrawToBitmap(screenshot, new Rectangle(Point.Empty, form.Size));
                    screenshot.Save(args[index + 1], System.Drawing.Imaging.ImageFormat.Png);
                }
                form.Close(); return 0;
            }
            Application.Run(form);
            return 0;
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error); return 1; }
    }
}
