using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ScanArchive.Integration;

namespace ScanArchive;

static class SecretaryIntegration
{
    static readonly SemaphoreSlim submissions=new(1,1);
    static readonly SemaphoreSlim startup=new(1,1);
    static Process? startedCore;
    static Task startedCoreErrors=Task.CompletedTask;
    static CoreErrorTail? startedCoreErrorTail;
    public static string DataRoot => UserDataPaths.Secretary;
    public static JsonObject Preferences()
    {
        string path=Path.Combine(DataRoot,"settings.json");
        return File.Exists(path)?JsonNode.Parse(File.ReadAllText(path))!.AsObject():new JsonObject();
    }
    public static bool HasKey => File.Exists(Path.Combine(DataRoot,"openai.secret")) || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY"));
    public static void Save(string root,string key,string wakeTime,bool schedule,bool automatic)
    {
        Directory.CreateDirectory(DataRoot);var options=Preferences();
        if(options["libraryRoot"]==null)options["libraryRoot"]=root;
        else if(!string.Equals(Path.GetFullPath(options["libraryRoot"]!.ToString()),Path.GetFullPath(root),StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("扫描归档目录与文档秘书的库目录不同，请先在网页设置中确认文档库位置。");
        options["dailyWakeTime"]=wakeTime;options["scheduleEnabled"]=schedule;options["autoOrganize"]=automatic;
        string path=Path.Combine(DataRoot,"settings.json"),temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        File.WriteAllText(temp,options.ToJsonString());File.Move(temp,path,true);
        if(!string.IsNullOrWhiteSpace(key))
        {
            byte[] encrypted=ProtectedData.Protect(Encoding.UTF8.GetBytes(key.Trim()),null,DataProtectionScope.CurrentUser);
            path=Path.Combine(DataRoot,"openai.secret");File.WriteAllBytes(path+".tmp",encrypted);File.Move(path+".tmp",path,true);
        }
    }
    public static string QueueScan(string path,DateTime scanned,string device,string source)
    {
        string outbox=Path.Combine(DataRoot,"outbox");Directory.CreateDirectory(outbox);
        string id=Guid.NewGuid().ToString("N"),file=Path.Combine(outbox,id+".json");
        File.WriteAllText(file+".tmp",new JsonObject{["command"]="register_scan",["scanId"]=id,["path"]=path,["scanned"]=scanned.ToUniversalTime().ToString("O"),["device"]=device,["source"]=source}.ToJsonString());File.Move(file+".tmp",file);
        return id;
    }
    public static async Task FlushScans()
    {
        string outbox=Path.Combine(DataRoot,"outbox");
        if(!Directory.Exists(outbox)||!await submissions.WaitAsync(0))return;
        try
        {
            // Replay explicit scanner events, never discover files in the archive.
            foreach(string file in Directory.EnumerateFiles(outbox,"*.json"))
            {
                var request=JsonNode.Parse(await File.ReadAllTextAsync(file))!.AsObject();
                request["command"]="register_scan";request["scanId"]??=Path.GetFileNameWithoutExtension(file);
                var receipt=await DesktopProtocol.Request(DataRoot,request);
                if(receipt["acknowledged"]?.GetValue<bool>()==true)File.Delete(file);
            }
        }
        finally{submissions.Release();}
    }
    public static int PendingCount => Directory.Exists(Path.Combine(DataRoot,"outbox"))?Directory.GetFiles(Path.Combine(DataRoot,"outbox"),"*.json").Length:0;
    public static async Task<List<JsonObject>> Library()
    {
        var result=new List<JsonObject>();
        for(int offset=0;;offset+=100)
        {
            var page=await DesktopProtocol.Request(DataRoot,new JsonObject{["command"]="library",["offset"]=offset});
            var rows=page["documents"]!.AsArray().Select(x=>x!.AsObject()).ToList();result.AddRange(rows);
            if(rows.Count<100)break;
        }
        return result;
    }
    public static Task<JsonObject> Trash(string id)=>DesktopProtocol.Request(DataRoot,new JsonObject{["command"]="trash",["id"]=id});
    public static async Task EnsureStarted()
    {
        await startup.WaitAsync();
        try
        {
            using var client=new HttpClient(new HttpClientHandler{UseProxy=false}){Timeout=TimeSpan.FromSeconds(2)};
            if(await CoreReady(client))return;
            string exe=Path.Combine(AppContext.BaseDirectory,"server","ScanArchive.Server.exe");
            if(!File.Exists(exe))throw new IOException("未找到文档核心程序，请从完整安装目录启动 Scan Archive。");
            // A slow first migration must not spawn another core on every UI retry.
            if(startedCore==null||startedCore.HasExited)
            {
                startedCore?.Dispose();
                startedCore=Process.Start(new ProcessStartInfo(exe){WorkingDirectory=Path.GetDirectoryName(exe),UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardError=true})
                    ??throw new IOException("无法启动文档核心程序。");
                var errorTail=new CoreErrorTail();startedCoreErrorTail=errorTail;
                var errorStream=startedCore.StandardError;
                startedCoreErrors=Task.Run(()=>ReadCoreErrors(errorStream,errorTail));
            }
            for(int i=0;i<60;i++)
            {
                await Task.Delay(500);
                await ThrowIfCoreExited();
                if(await CoreReady(client))return;
            }
            await ThrowIfCoreExited();
            throw new IOException("文档库核心仍未就绪。首次迁移可能需要更长时间，请稍后重试或检查服务日志。");
        }
        finally{startup.Release();}
    }
    static async Task ThrowIfCoreExited()
    {
        if(startedCore==null||!startedCore.HasExited)return;
        // Drain the final error text only after exit, with a bound in case another
        // inherited process keeps the pipe open. A healthy core is never awaited.
        await Task.WhenAny(startedCoreErrors,Task.Delay(300));
        string details=startedCoreErrorTail?.Text.Trim()??"";
        throw new IOException($"文档核心启动失败（退出代码 {startedCore.ExitCode}）。"+
            (details.Length>0?"\n\n"+details:"请检查文档核心日志后重试。"));
    }
    static async Task ReadCoreErrors(StreamReader reader,CoreErrorTail output)
    {
        var buffer=new char[1024];
        try
        {
            int count;
            while((count=await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false))>0)
                output.Append(buffer,count);
        }
        catch(IOException){}
        catch(ObjectDisposedException){}
    }
    sealed class CoreErrorTail
    {
        const int Limit=4000;
        readonly object gate=new();
        readonly StringBuilder text=new();
        public void Append(char[] buffer,int count)
        {
            lock(gate){text.Append(buffer,0,count);if(text.Length>Limit)text.Remove(0,text.Length-Limit);}
        }
        public string Text { get { lock(gate)return text.ToString(); } }
    }
    static async Task<bool> CoreReady(HttpClient client)
    {
        try
        {
            using var response=await client.GetAsync("http://127.0.0.1:5278/health");
            if(!response.IsSuccessStatusCode)
                throw new IOException("本机 5278 端口上的服务尚未就绪。请检查或重启文档核心后重试。");
            JsonNode? health;
            try{health=JsonNode.Parse(await response.Content.ReadAsStringAsync());}
            catch(JsonException){throw new IOException("本机 5278 端口未提供兼容的文档核心，请关闭旧核心或其他占用程序后重新启动 Scan Archive。");}
            if(health is not JsonObject details || details["dataLayout"]?.ToString()!="user-profile-v1")
                throw new IOException("旧版文档核心仍在运行，尚未使用统一数据目录。请先关闭旧核心，再重新启动 Scan Archive；原有文档不会被删除。");
            return true;
        }
        catch(HttpRequestException){return false;}
        catch(TaskCanceledException){return false;}
    }
    public static Task<JsonObject> Command(string command, JsonObject? values=null, CancellationToken ct=default)
    {
        values??=new();values["command"]=command;return DesktopProtocol.Request(DataRoot,values,ct);
    }
    public static async Task OpenPanel()
    {
        await EnsureStarted();Process.Start(new ProcessStartInfo("http://localhost:5278"){UseShellExecute=true});
    }
}
