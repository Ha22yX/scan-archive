using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;

namespace ScanArchive.Server;

/// <summary>Preserves an existing library when moving out of a launcher's redirected AppData view.</summary>
public static class LegacyDataMigration
{
    const string Marker = ".legacy-migration-complete.json";
    static readonly string[] SecretaryFiles = ["settings.json", "openai.secret", "web-password.json"];
    sealed record Candidate(string Root, string Database, long DocumentCount, bool HasDatabase, int RecoveryFiles, bool HasPreferences);

    public static void EnsureMigrated(string destinationRoot)
    {
        string destination = Path.GetFullPath(destinationRoot);
        // A completed stable library is independent of the environment used to launch the app.
        if (File.Exists(Path.Combine(destination, Marker)) && ValidDatabase(Path.Combine(destination, "Secretary", "library.db"))) return;
        string legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScanArchive", "Secretary", "library.db");
        if (OperatingSystem.IsWindows() && File.Exists(legacy))
        {
            using var handle = File.OpenHandle(legacy, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var physical = new StringBuilder(32768);
            uint length = GetFinalPathNameByHandle(handle, physical, (uint)physical.Capacity, 2); // VOLUME_NAME_NT exposes redirection hidden by DOS paths.
            if (length == 0 || length >= physical.Capacity)
                throw new IOException("无法确认旧数据目录是否被重定向。请从 Windows 文件资源管理器首次启动程序，以便安全检查完整旧文档库。");
            string path = physical.ToString();
            if (path.Contains(@"\Packages\OpenAI.Codex_", StringComparison.OrdinalIgnoreCase) && path.Contains(@"\LocalCache\Local\ScanArchive\", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("当前启动环境将旧 AppData 数据重定向到了 Codex 缓存，无法同时检查用户的真实旧文档库。尚未迁移或修改数据。请关闭此窗口，然后在 Windows 文件资源管理器中双击 ScanArchive.exe，完成首次迁移；完成后可从任何入口启动。");
        }
        EnsureMigrated(destination, DefaultCandidates());
    }

    // Explicit candidates keep migration regression tests isolated from the user's actual account.
    public static void EnsureMigrated(string destinationRoot, IEnumerable<string> candidateRoots)
    {
        string destination = Path.GetFullPath(destinationRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(Path.GetFileName(destination))) throw new ArgumentException("文档库数据目录不能是磁盘根目录。", nameof(destinationRoot));
        string parent = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(parent);
        using var migrationLock = AcquireLock(destination + ".migration.lock");
        string destinationDb = Path.Combine(destination, "Secretary", "library.db");
        if (File.Exists(Path.Combine(destination, Marker)) && ValidDatabase(destinationDb)) return;

        var candidates = new List<Candidate>();
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string root in candidateRoots)
        {
            string full = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(full, destination, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(full)) continue;
            var candidate = Inspect(full);
            string identity = candidate.HasDatabase ? FileIdentity(candidate.Database) : full;
            if (identities.Add(identity)) candidates.Add(candidate);
        }

        var populated = candidates.Where(c => c.DocumentCount > 0).ToArray();
        if (populated.Length > 1) throw Conflict("发现多个分别包含文档的旧文档库，无法自动判断应使用哪一个", populated.Select(c => c.Root));
        Candidate? source = populated.SingleOrDefault();
        if (source != null)
        {
            var otherRecovery = candidates.Where(c => c.Root != source.Root && c.RecoveryFiles > 0).ToArray();
            if (otherRecovery.Length > 0)
                throw Conflict("包含文档的旧库之外，还有旧目录保留未交接的扫描或恢复文件，不能遗漏这些扫描", new[] { source.Root }.Concat(otherRecovery.Select(c => c.Root)));
        }
        if (source == null)
        {
            // Do not discard unacknowledged scans simply because their documents are not registered yet.
            var recovery = candidates.Where(c => c.RecoveryFiles > 0).ToArray();
            if (recovery.Length > 1) throw Conflict("多个旧目录均包含尚需保留的扫描或交接文件", recovery.Select(c => c.Root));
            source = recovery.SingleOrDefault();
            if (source == null)
            {
                var configured = candidates.Where(c => c.HasPreferences).ToArray();
                if (configured.Length > 1) throw Conflict("多个旧目录均有配置但没有已登记文档，无法自动选择配置来源", configured.Select(c => c.Root));
                source = configured.SingleOrDefault();
            }
        }

        Candidate? existing = Directory.Exists(destination) ? Inspect(destination) : null;
        if (existing?.DocumentCount > 0)
        {
            if (source?.DocumentCount > 0 && FileIdentity(existing.Database) != FileIdentity(source.Database))
                throw Conflict("新数据目录已经包含文档，不会用旧文档库覆盖", [destination, source.Root]);
            WriteMarker(destination, null, existing.DocumentCount, "existing-library");
            return;
        }
        if (source == null)
        {
            Directory.CreateDirectory(Path.Combine(destination, "Secretary"));
            WriteMarker(destination, null, 0, "no-legacy-library");
            return;
        }

        // Prevent an old core from accepting another scan after the snapshot was taken.
        // SQLite backup still handles independent readers and an existing WAL without copying sidecars.
        using var sourceLease = AcquireSourceLease(source.Root);

        string staging = destination + ".migration-stage-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Path.Combine(staging, "Secretary"));
        try
        {
            CopyKnownFiles(source.Root, staging);
            if (source.HasDatabase) BackupDatabase(source.Database, Path.Combine(staging, "Secretary", "library.db"));
            // An empty target may already contain a queued scan or local preferences from an interrupted first run.
            if (existing != null) CopyKnownFiles(existing.Root, staging, preserveExisting: true);
            long copiedCount = DocumentCount(Path.Combine(staging, "Secretary", "library.db"));
            if (copiedCount != source.DocumentCount)
                throw new IOException("旧文档库在迁移期间发生变化，已保留旧库。请关闭旧程序后再次启动。");
            WriteMarker(staging, source.Root, copiedCount, "migrated");
            Publish(staging, destination);
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            // Keep the source and the incomplete staging directory intact for inspection and safe retry.
            throw new IOException($"文档库迁移未完成，原数据未删除。请关闭旧程序后重试。临时副本：{staging}", ex);
        }
    }

    static IEnumerable<string> DefaultCandidates()
    {
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScanArchive");
        string packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Local", "Packages");
        if (!Directory.Exists(packages)) yield break;
        foreach (string package in Directory.EnumerateDirectories(packages, "OpenAI.Codex_*", SearchOption.TopDirectoryOnly).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            yield return Path.Combine(package, "LocalCache", "Local", "ScanArchive");
    }

    static FileStream AcquireLock(string path)
    {
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) { throw new IOException("另一个程序正在迁移文档库，请稍后重试。", ex); }
    }

    static FileStream? AcquireSourceLease(string source)
    {
        string secretary = Path.Combine(source, "Secretary");
        if (!Directory.Exists(secretary)) return null;
        try { return new FileStream(Path.Combine(secretary, "service.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) { throw new InvalidOperationException($"旧文档库服务仍在运行，尚未迁移。请先关闭使用此目录的旧程序，再启动新版：{source}。所有原数据均已保留。", ex); }
    }

    static Candidate Inspect(string root)
    {
        string database = Path.Combine(root, "Secretary", "library.db");
        bool exists = File.Exists(database);
        long count = exists ? DocumentCount(database) : 0;
        int recovery = CountFiles(Path.Combine(root, "Pending")) + CountFiles(Path.Combine(root, "Secretary", "outbox"));
        bool preferences = File.Exists(Path.Combine(root, "settings.json")) || SecretaryFiles.Any(name => File.Exists(Path.Combine(root, "Secretary", name)));
        return new Candidate(root, database, count, exists, recovery, preferences);
    }

    static int CountFiles(string directory)
    {
        if (!Directory.Exists(directory)) return 0;
        AssertRegularDirectory(directory);
        int count = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly).Count();
        foreach (string child in Directory.EnumerateDirectories(directory)) count = checked(count + CountFiles(child));
        return count;
    }

    static SqliteConnection OpenReadOnly(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 30 }.ToString());
        try { connection.Open(); return connection; }
        catch { connection.Dispose(); throw; }
    }

    static long DocumentCount(string path)
    {
        if (!File.Exists(path)) return 0;
        try
        {
            using var connection = OpenReadOnly(path);
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA quick_check";
            if (!string.Equals(command.ExecuteScalar()?.ToString(), "ok", StringComparison.OrdinalIgnoreCase)) throw new IOException("SQLite 完整性检查未通过。");
            command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='documents'";
            if (Convert.ToInt64(command.ExecuteScalar()) == 0) return 0;
            command.CommandText = "SELECT count(*) FROM documents";
            return Convert.ToInt64(command.ExecuteScalar());
        }
        catch (Exception ex) { throw new IOException($"无法安全读取旧文档库：{path}。未覆盖或删除任何数据。", ex); }
    }

    static bool ValidDatabase(string path)
    {
        if (!File.Exists(path)) return false;
        _ = DocumentCount(path);
        using var connection = OpenReadOnly(path);
        using var command = connection.CreateCommand();command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='documents'";
        return Convert.ToInt64(command.ExecuteScalar()) == 1;
    }

    static void BackupDatabase(string source, string target)
    {
        using var input = OpenReadOnly(source);
        using var output = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = target, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false, DefaultTimeout = 30 }.ToString());
        output.Open();input.BackupDatabase(output);
        // Publish a complete standalone database. Source WAL/SHM are deliberately never copied or checkpointed.
        using var command = output.CreateCommand();command.CommandText = "PRAGMA journal_mode=DELETE";command.ExecuteScalar();
    }

    static void CopyKnownFiles(string source, string target, bool preserveExisting = false)
    {
        CopyFile(Path.Combine(source, "settings.json"), Path.Combine(target, "settings.json"), preserveExisting);
        foreach (string name in SecretaryFiles) CopyFile(Path.Combine(source, "Secretary", name), Path.Combine(target, "Secretary", name), preserveExisting);
        foreach (string directory in new[] { "keys", "outbox" })
            CopyDirectory(Path.Combine(source, "Secretary", directory), Path.Combine(target, "Secretary", directory));
        CopyDirectory(Path.Combine(source, "Pending"), Path.Combine(target, "Pending"));
    }

    static void CopyFile(string source, string target, bool preserveExisting)
    {
        if (!File.Exists(source)) return;
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) throw new IOException($"迁移不会跟随文件链接：{source}");
        if (File.Exists(target))
        {
            if (preserveExisting) return;
            if (FilesEqual(source, target)) return;
            throw Conflict("发现同名但内容不同的扫描恢复文件，不能自动覆盖", [source, target]);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(source, target, false);
    }

    static bool FilesEqual(string first, string second)
    {
        if (new FileInfo(first).Length != new FileInfo(second).Length) return false;
        using var a = File.OpenRead(first);using var b = File.OpenRead(second);
        return System.Security.Cryptography.SHA256.HashData(a).AsSpan().SequenceEqual(System.Security.Cryptography.SHA256.HashData(b));
    }

    static void CopyDirectory(string source, string target)
    {
        if (!Directory.Exists(source)) return;
        AssertRegularDirectory(source);Directory.CreateDirectory(target);
        foreach (string file in Directory.EnumerateFiles(source)) CopyFile(file, Path.Combine(target, Path.GetFileName(file)), false);
        foreach (string child in Directory.EnumerateDirectories(source)) CopyDirectory(child, Path.Combine(target, Path.GetFileName(child)));
    }

    static void AssertRegularDirectory(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException($"迁移不会跟随目录链接：{path}");
    }

    static void Publish(string staging, string destination)
    {
        string parent = Path.GetDirectoryName(destination)!;
        if (!string.Equals(Path.GetDirectoryName(staging), parent, StringComparison.OrdinalIgnoreCase) || !staging.StartsWith(destination + ".migration-stage-", StringComparison.OrdinalIgnoreCase))
            throw new IOException("迁移临时目录不在预期位置。");
        string? backup = null;
        if (Directory.Exists(destination))
        {
            AssertRegularDirectory(destination);
            backup = destination + ".before-migration-" + Guid.NewGuid().ToString("N");
            Directory.Move(destination, backup);
        }
        try { Directory.Move(staging, destination); }
        catch
        {
            if (backup != null && !Directory.Exists(destination)) Directory.Move(backup, destination);
            throw;
        }
    }

    static void WriteMarker(string destination, string? source, long documents, string result)
    {
        Directory.CreateDirectory(destination);
        string marker = Path.Combine(destination, Marker), temporary = marker + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new { version = 1, completedUtc = DateTimeOffset.UtcNow, source, documents, result }));
        File.Move(temporary, marker, true);
    }

    static InvalidOperationException Conflict(string reason, IEnumerable<string> paths) => new(reason + "。已保留所有文件，请先确认迁移来源后再启动。\n" + string.Join("\n", paths));

    static string FileIdentity(string path)
    {
        if (!OperatingSystem.IsWindows()) return Path.GetFullPath(path);
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (!GetFileInformationByHandle(handle, out var info)) throw new IOException($"无法确认文档库文件身份：{path}", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
        return $"{info.VolumeSerialNumber:X8}:{info.FileIndexHigh:X8}{info.FileIndexLow:X8}";
    }

    [StructLayout(LayoutKind.Sequential)]
    struct FileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
}
