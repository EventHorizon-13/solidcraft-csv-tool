using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace SolidCraftCsvTool;

internal sealed class HeadlessSession : IDisposable
{
    readonly Process process;
    readonly StreamReader reader;
    readonly StreamWriter writer;
    int sequence;
    public bool HasWindow => process.MainWindowHandle!=IntPtr.Zero;
    HeadlessSession(Process process)
    {
        this.process=process;reader=process.StandardOutput;writer=process.StandardInput;
        // Drain diagnostics while the CAD engine works, so a full stderr pipe
        // can never block a long model operation. No GUI process is launched.
        process.ErrorDataReceived+=(_,_)=>{};process.BeginErrorReadLine();
    }
    public static async Task<HeadlessSession> StartAsync(string executable)
    {
        if(!File.Exists(executable))throw new FileNotFoundException("找不到无窗口 SolidCraft CLI，请完整解压软件包。",executable);
        var start=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,
            RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardInputEncoding=new UTF8Encoding(false),StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};
        foreach(string arg in new[]{"mcp","--headless","--allow-any-path"})start.ArgumentList.Add(arg);
        var session=new HeadlessSession(Process.Start(start)??throw new IOException("无法启动 SolidCraft CLI。"));
        try
        {
            await session.RpcAsync("initialize",new{protocolVersion="2025-06-18",capabilities=new{},clientInfo=new{name="roller-seal-batch",version="1"}});
            await session.writer.WriteLineAsync(JsonSerializer.Serialize(new{jsonrpc="2.0",method="notifications/initialized"}));
            await session.writer.FlushAsync();return session;
        }
        catch{session.Dispose();throw;}
    }
    async Task<JsonElement> RpcAsync(string method,object parameters)
    {
        int id=++sequence;
        using var timeout=new CancellationTokenSource(TimeSpan.FromHours(4));
        try
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(new{jsonrpc="2.0",id,method,@params=parameters}).AsMemory(),timeout.Token);
            await writer.FlushAsync(timeout.Token);
            while(true)
            {
                string? line=await reader.ReadLineAsync(timeout.Token);
                if(line==null)throw new IOException("SolidCraft 后台会话已退出。");
                using var json=JsonDocument.Parse(line);var response=json.RootElement;
                if(!response.TryGetProperty("id",out var received))continue;
                if(received.GetInt32()!=id)throw new IOException("SolidCraft 后台响应编号不匹配。");
                if(response.TryGetProperty("error",out var error))throw new IOException("SolidCraft 后台请求失败："+error.GetRawText());
                return response.GetProperty("result").Clone();
            }
        }
        catch(OperationCanceledException){throw new IOException("SolidCraft 后台请求超过4小时，已停止等待。当前任务不会重复提交。");}
    }
    async Task<JsonElement> ToolAsync(string name,object arguments)
    {
        var result=await RpcAsync("tools/call",new{name,arguments});
        var contents=result.GetProperty("content").EnumerateArray().Where(c=>c.GetProperty("type").GetString()=="text").Select(c=>c.GetProperty("text").GetString()??"").ToArray();
        if(result.TryGetProperty("isError",out var failed)&&failed.ValueKind==JsonValueKind.True)
            throw new IOException("SolidCraft 后台执行失败："+string.Join("\n",contents));
        foreach(string text in contents)
            try{using var data=JsonDocument.Parse(text);return data.RootElement.Clone();}catch(JsonException){}
        throw new IOException("SolidCraft 后台没有返回可读取的结果。");
    }
    public Task<JsonElement> RequestAsync(string method,object parameters)=>method switch
    {
        "engine.execute"=>ToolAsync("execute",parameters),
        "document.inspect"=>ToolAsync("execute",new{command="document.inspect",@params=parameters}),
        "control.running"=>ToolAsync("running",new{}),
        _=>throw new IOException("无窗口会话不支持此桌面请求："+method)
    };
    public void Dispose()
    {
        writer.Dispose();
        try{if(!process.WaitForExit(1500))process.Kill(entireProcessTree:true);}catch(InvalidOperationException){}
        reader.Dispose();process.Dispose();
    }
}
