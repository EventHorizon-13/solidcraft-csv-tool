using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;

namespace SolidCraftCsvTool;

internal sealed record AppRelease(Version Version, string Tag, string PageUrl, Uri PackageUrl, long PackageBytes);

internal static class UpdateService
{
    const string Repository="EventHorizon-13/solidcraft-roller-seal";
    const string PackageName="SolidCraftRollerSeal-win-x64.zip";
    static readonly Uri ReleaseApi=new($"https://api.github.com/repos/{Repository}/releases/latest");
    static readonly Uri ReleasesPage=new($"https://github.com/{Repository}/releases/latest");
    static readonly HttpClient Client=CreateClient();

    static HttpClient CreateClient()
    {
        var client=new HttpClient{Timeout=TimeSpan.FromMinutes(15)};
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("StandardRollerSeal",AppBuildInfo.Version));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    public static async Task<AppRelease> GetLatestAsync(CancellationToken token=default)
    {
        using var response=await Client.GetAsync(ReleaseApi,HttpCompletionOption.ResponseHeadersRead,token);
        response.EnsureSuccessStatusCode();
        await using var stream=await response.Content.ReadAsStreamAsync(token);
        using var json=await JsonDocument.ParseAsync(stream,cancellationToken:token);
        var root=json.RootElement;
        string tag=root.GetProperty("tag_name").GetString()??throw new InvalidDataException("GitHub 发布信息没有版本号。");
        if(!tag.StartsWith("win-",StringComparison.OrdinalIgnoreCase)||!Version.TryParse(tag[4..],out var version))
            throw new InvalidDataException("GitHub 最新发布的版本号格式不正确。");
        string page=root.GetProperty("html_url").GetString()??ReleasesPage.ToString();
        var asset=root.GetProperty("assets").EnumerateArray().FirstOrDefault(a=>string.Equals(a.GetProperty("name").GetString(),PackageName,StringComparison.Ordinal));
        if(asset.ValueKind!=JsonValueKind.Object)throw new InvalidDataException("GitHub 最新发布缺少 Windows 安装包。");
        string url=asset.GetProperty("browser_download_url").GetString()??throw new InvalidDataException("Windows 安装包链接为空。");
        long bytes=asset.GetProperty("size").GetInt64();
        if(bytes<=0||bytes>600L*1024*1024)throw new InvalidDataException("Windows 安装包大小超出安全范围。");
        return new(version,tag,page,new Uri(url),bytes);
    }

    public static bool IsNewer(AppRelease release)=>release.Version>Version.Parse(AppBuildInfo.Version);

    public static async Task<string> DownloadAndStageAsync(AppRelease release,IProgress<int>? progress=null,CancellationToken token=default)
    {
        string root=Path.Combine(Path.GetTempPath(),"StandardRollerSeal-Update",Guid.NewGuid().ToString("N"));
        string stage=Path.Combine(root,"package"),zip=Path.Combine(root,"update.zip");
        Directory.CreateDirectory(stage);
        try
        {
            using var response=await Client.GetAsync(release.PackageUrl,HttpCompletionOption.ResponseHeadersRead,token);
            response.EnsureSuccessStatusCode();
            long? total=response.Content.Headers.ContentLength;
            if(total is <=0 or >600L*1024*1024)throw new InvalidDataException("下载包大小异常。");
            await using(var input=await response.Content.ReadAsStreamAsync(token))
            await using(var output=new FileStream(zip,FileMode.CreateNew,FileAccess.Write,FileShare.None,1024*1024,true))
            {
                byte[] buffer=new byte[1024*1024];long received=0;int previous=-1;
                while(true)
                {
                    int count=await input.ReadAsync(buffer,token);if(count==0)break;
                    received+=count;if(received>600L*1024*1024)throw new InvalidDataException("下载包超过大小限制。");
                    await output.WriteAsync(buffer.AsMemory(0,count),token);
                    if(total.HasValue){int percent=(int)Math.Clamp(received*100/total.Value,0,100);if(percent!=previous){progress?.Report(percent);previous=percent;}}
                }
                await output.FlushAsync(token);
                if(total.HasValue&&received!=total.Value)throw new EndOfStreamException("Windows 安装包未完整下载。");
            }
            using(var archive=ZipFile.OpenRead(zip))
            {
                string prefix=Path.GetFullPath(stage)+Path.DirectorySeparatorChar;
                foreach(var entry in archive.Entries)
                {
                    string destination=Path.GetFullPath(Path.Combine(stage,entry.FullName.Replace('/',Path.DirectorySeparatorChar)));
                    if(!destination.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("更新包包含无效路径。");
                }
                ZipFile.ExtractToDirectory(zip,stage);
            }
            string stagedExe=Path.Combine(stage,AppBuildInfo.ProductName+".exe");
            if(!File.Exists(stagedExe))throw new InvalidDataException("更新包中找不到应用程序。");
            File.Delete(zip);
            return stage;
        }
        catch{try{Directory.Delete(root,true);}catch(IOException){}catch(UnauthorizedAccessException){}throw;}
    }

    public static void StartInstaller(string stage)
    {
        string exe=Environment.ProcessPath??throw new InvalidOperationException("找不到当前程序路径。");
        string target=Path.GetDirectoryName(exe)??throw new InvalidOperationException("找不到程序目录。");
        string stagedExe=Path.Combine(stage,AppBuildInfo.ProductName+".exe");
        var start=new ProcessStartInfo(stagedExe){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=stage};
        start.ArgumentList.Add("--apply-update");start.ArgumentList.Add(Environment.ProcessId.ToString());start.ArgumentList.Add(target);start.ArgumentList.Add(stage);
        Process.Start(start)??throw new InvalidOperationException("无法启动更新程序。");
    }

    public static string ReleasesUrl=>ReleasesPage.ToString();
}

internal static class UpdateInstaller
{
    public static int Apply(string[] args)
    {
        if(args.Length!=4||!int.TryParse(args[1],out int parentPid))throw new ArgumentException("更新参数无效。");
        string target=Path.GetFullPath(args[2]),stage=Path.GetFullPath(args[3]);
        WaitForParent(parentPid);
        string expectedExe=AppBuildInfo.ProductName+".exe";
        string stagedExe=Path.Combine(stage,expectedExe);
        if(!File.Exists(stagedExe))throw new FileNotFoundException("更新包中缺少程序文件。",stagedExe);
        Directory.CreateDirectory(target);
        var files=Directory.GetFiles(stage,"*",SearchOption.AllDirectories)
            .OrderBy(path=>string.Equals(Path.GetFileName(path),expectedExe,StringComparison.OrdinalIgnoreCase)?1:0).ToArray();
        foreach(string source in files)
        {
            string relative=Path.GetRelativePath(stage,source);
            string destination=Path.GetFullPath(Path.Combine(target,relative));
            if(!destination.StartsWith(target.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("更新文件路径无效。");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source,destination,true);
        }
        string installedExe=Path.Combine(target,expectedExe);
        if(!File.Exists(installedExe))throw new IOException("更新后未找到程序文件。");
        int updaterPid=Environment.ProcessId;
        var start=new ProcessStartInfo(installedExe){UseShellExecute=false,WorkingDirectory=target};
        start.ArgumentList.Add("--cleanup-update");start.ArgumentList.Add(stage);start.ArgumentList.Add(updaterPid.ToString());
        Process.Start(start)??throw new InvalidOperationException("更新后无法重新打开程序。");
        return 0;
    }

    public static void CleanupStage(string[] args)
    {
        if(args.Length!=3||!int.TryParse(args[2],out int updaterPid))return;
        WaitForParent(updaterPid);
        try{Directory.Delete(Path.GetFullPath(args[1]),true);}catch(IOException){}catch(UnauthorizedAccessException){}
    }

    static void WaitForParent(int pid)
    {
        try
        {
            using var process=Process.GetProcessById(pid);
            if(!process.WaitForExit(180000))throw new TimeoutException("等待旧程序退出超时，请手动关闭旧程序后重试。");
        }
        catch(ArgumentException){/* already exited */}
    }
}
