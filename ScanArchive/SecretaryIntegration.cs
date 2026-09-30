using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ScanArchive.Integration;

namespace ScanArchive;

static class SecretaryIntegration
{
    static readonly SemaphoreSlim submissions=new(1,1);
    public static string DataRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ScanArchive","Secretary");
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
        using var client=new HttpClient{Timeout=TimeSpan.FromSeconds(2)};
        try{var r=await client.GetAsync("http://localhost:5278/health");if(r.IsSuccessStatusCode)return;}catch(HttpRequestException){}catch(TaskCanceledException){}
        string exe=Path.Combine(AppContext.BaseDirectory,"server","ScanArchive.Server.exe");
        if(File.Exists(exe))Process.Start(new ProcessStartInfo(exe){WorkingDirectory=Path.GetDirectoryName(exe),UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden});
        for(int i=0;i<20;i++)
        {
            await Task.Delay(300);
            try{var r=await client.GetAsync("http://localhost:5278/health");if(r.IsSuccessStatusCode)return;}catch(HttpRequestException){}catch(TaskCanceledException){}
        }
        throw new IOException("文档库核心未能启动，请检查服务日志。");
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
