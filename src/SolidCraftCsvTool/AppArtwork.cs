namespace SolidCraftCsvTool;

internal static class AppArtwork
{
    public static Icon LoadIcon()
    {
        using var stream=typeof(AppArtwork).Assembly.GetManifestResourceStream("SolidCraftCsvTool.AppIcon.ico")
            ??throw new InvalidOperationException("软件图标资源缺失。");
        using var icon=new Icon(stream,32,32);return (Icon)icon.Clone();
    }
    public static Image LoadImage()
    {
        using var stream=typeof(AppArtwork).Assembly.GetManifestResourceStream("SolidCraftCsvTool.AppIcon.png")
            ??throw new InvalidOperationException("软件图标资源缺失。");
        using var image=Image.FromStream(stream);return new Bitmap(image);
    }
}
