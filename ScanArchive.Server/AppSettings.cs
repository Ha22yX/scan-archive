using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ScanArchive.Server;

public sealed record ServerOptions
{
    public string LibraryRoot { get; init; } = "";
    public string Model { get; init; } = "gpt-6-astra";
    public string EmbeddingModel { get; init; } = "text-embedding-3-large";
    public bool AutoOrganize { get; init; } = true;
    public bool ScheduleEnabled { get; init; } = true;
    public int ScheduleMinutes { get; init; } = 60;
    public string DailyWakeTime { get; init; } = "05:00";
    public int DailyRequestLimit { get; init; } = 1000;
    public int DocumentConcurrency { get; init; } = 3;
    public int PageConcurrency { get; init; } = 2;
    public int AgentMaxSteps { get; init; } = 20;
    public string Instructions { get; init; } = "按内容自主组织目录，优先复用已有分类，避免创建含义重复的分类。文件标题使用中文；保留专有名词。";
}

public sealed class AppSettings
{
    readonly object gate = new();
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public string DataRoot { get; }
    public ServerOptions Current { get; private set; }
    public string Outbox => Path.Combine(DataRoot, "outbox");
    public AppSettings(string? dataRoot = null)
    {
        DataRoot = dataRoot ?? Environment.GetEnvironmentVariable("SCANARCHIVE_DATA_DIR") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScanArchive", "Secretary");
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(Outbox);
        string path = Path.Combine(DataRoot, "settings.json");
        Current = File.Exists(path) ? JsonSerializer.Deserialize<ServerOptions>(File.ReadAllText(path), Json)! : new();
        if (string.IsNullOrEmpty(Current.LibraryRoot))
        {
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "扫描归档");
            string scannerSettings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScanArchive", "settings.json");
            if (File.Exists(scannerSettings)) root = JsonDocument.Parse(File.ReadAllText(scannerSettings)).RootElement.GetProperty("Root").GetString()!;
            Save(Current with { LibraryRoot = root });
        }
    }
    public void Save(ServerOptions options)
    {
        if (!Path.IsPathFullyQualified(options.LibraryRoot)) throw new ArgumentException("文档库必须是完整路径。");
        if (options.ScheduleMinutes < 5 || options.ScheduleMinutes > 10080) throw new ArgumentException("整理间隔应为 5 至 10080 分钟。");
        if (!TimeOnly.TryParseExact(options.DailyWakeTime, "HH:mm", out _)) throw new ArgumentException("唤醒时间格式为 HH:mm。");
        if (options.DailyRequestLimit is < 1 or > 100000 || options.AgentMaxSteps is < 1 or > 50) throw new ArgumentException("请求上限或 Agent 步数不正确。");
        if(options.DocumentConcurrency is <1 or >6 || options.PageConcurrency is <1 or >4)throw new ArgumentException("文档并发范围 1–6，单文件页面并发范围 1–4。");
        if (string.IsNullOrWhiteSpace(options.Model) || string.IsNullOrWhiteSpace(options.EmbeddingModel)) throw new ArgumentException("请填写模型名称。");
        lock (gate)
        {
            string path = Path.Combine(DataRoot, "settings.json");
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(options, Json));
            File.Move(path + ".tmp", path, true);
            Current = options;
        }
    }
    public void Reload()
    {
        lock (gate)
        {
            var options = JsonSerializer.Deserialize<ServerOptions>(File.ReadAllText(Path.Combine(DataRoot, "settings.json")), Json);
            if (options != null) Current = options;
        }
    }
    public string ApiKey
    {
        get
        {
            string path = Path.Combine(DataRoot, "openai.secret");
            if (File.Exists(path)) return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser));
            return Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? "";
        }
    }
    public void SaveApiKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        var encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(key.Trim()), null, DataProtectionScope.CurrentUser);
        string path = Path.Combine(DataRoot, "openai.secret");
        File.WriteAllBytes(path + ".tmp", encrypted);
        File.Move(path + ".tmp", path, true);
    }
}
