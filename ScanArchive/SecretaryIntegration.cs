using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace ScanArchive;

static class SecretaryIntegration
{
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
    public static void Notify(string path,DateTime scanned)
    {
        string outbox=Path.Combine(DataRoot,"outbox");Directory.CreateDirectory(outbox);
        string file=Path.Combine(outbox,Guid.NewGuid().ToString("N")+".json");
        File.WriteAllText(file+".tmp",new JsonObject{["path"]=path,["scanned"]=scanned.ToUniversalTime().ToString("O")}.ToJsonString());File.Move(file+".tmp",file);
    }
    public static async Task EnsureStarted()
    {
        using var client=new HttpClient{Timeout=TimeSpan.FromSeconds(2)};
        try{var r=await client.GetAsync("http://localhost:5278/health");if(r.IsSuccessStatusCode)return;}catch(HttpRequestException){}catch(TaskCanceledException){}
        string exe=Path.Combine(AppContext.BaseDirectory,"server","ScanArchive.Server.exe");
        if(File.Exists(exe))Process.Start(new ProcessStartInfo(exe){WorkingDirectory=Path.GetDirectoryName(exe),UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden});
    }
    public static async Task OpenPanel()
    {
        await EnsureStarted();Process.Start(new ProcessStartInfo("http://localhost:5278"){UseShellExecute=true});
    }
}
