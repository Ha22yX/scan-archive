using Microsoft.Data.Sqlite;
using ScanArchive.Integration;
using System.Text.Json;
using Xunit;

namespace ScanArchive.Server.Tests;

[CollectionDefinition("Isolated data directory environment", DisableParallelization = true)]
public sealed class DataDirectoryEnvironmentCollection { }

[Collection("Isolated data directory environment")]
public sealed class LegacyDataMigrationTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "scan-archive-migration-tests-" + Guid.NewGuid().ToString("N"));
    string PathFor(string name) => Path.Combine(root, name);

    static SqliteConnection OpenDatabase(string scanRoot, int documentCount)
    {
        string directory = Path.Combine(scanRoot, "Secretary");
        Directory.CreateDirectory(directory);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory, "library.db"), Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE documents(id TEXT PRIMARY KEY,title TEXT,summary TEXT);";
        command.ExecuteNonQuery();
        for (int n = 0; n < documentCount; n++)
        {
            command.CommandText = "INSERT INTO documents VALUES($id,$title,$summary)";
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$id", "document-" + n);
            command.Parameters.AddWithValue("$title", "Synthetic document " + n);
            command.Parameters.AddWithValue("$summary", "Saved analysis " + n);
            command.ExecuteNonQuery();
        }
        return connection;
    }

    static int CountDocuments(string scanRoot)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(scanRoot, "Secretary", "library.db"), Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM documents";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    static void Write(string scanRoot, string relativePath, string content)
    {
        string path = Path.Combine(scanRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public void StablePathsUseUserProfileInsteadOfRedirectedAppData()
    {
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".scan-archive"), UserDataPaths.Root);
        Assert.Equal(Path.Combine(UserDataPaths.Root, "Secretary"), UserDataPaths.Secretary);
    }

    [Fact]
    public void MigrationKeepsWalDocumentsConfigurationAndOriginalSources()
    {
        string source = PathFor("source"), destination = PathFor("stable");
        using var liveSource = OpenDatabase(source, 3);
        var files = new Dictionary<string, string>
        {
            ["settings.json"] = "{\"Root\":\"C:\\\\synthetic-library\"}",
            ["Secretary/settings.json"] = "{\"model\":\"synthetic-model\"}",
            ["Secretary/openai.secret"] = "synthetic-encrypted-key-bytes",
            ["Secretary/web-password.json"] = "{\"hash\":\"synthetic-only\"}",
            ["Secretary/keys/key-test.xml"] = "<synthetic-key />",
            ["Secretary/outbox/scan-test.json"] = "{\"scanId\":\"synthetic-scan\"}",
            ["Pending/scan-test/page-1.png"] = "synthetic-page-bytes"
        };
        foreach (var (path, content) in files) Write(source, path, content);

        // The connection stays open so the rows can remain in the WAL rather than the main DB file.
        LegacyDataMigration.EnsureMigrated(destination, [source]);

        Assert.Equal(3, CountDocuments(destination));
        Assert.Equal(3, CountDocuments(source));
        foreach (var (path, content) in files)
        {
            Assert.Equal(content, File.ReadAllText(Path.Combine(destination, path)));
            Assert.Equal(content, File.ReadAllText(Path.Combine(source, path)));
        }
        Assert.True(File.Exists(Path.Combine(source, "Secretary", "library.db")));
    }

    [Fact]
    public void MigrationChoosesDocumentsOverANewerEmptyDatabase()
    {
        string populated = PathFor("populated"), empty = PathFor("newer-empty"), destination = PathFor("stable");
        using (OpenDatabase(populated, 2)) { }
        using (OpenDatabase(empty, 0)) { }
        File.SetLastWriteTimeUtc(Path.Combine(empty, "Secretary", "library.db"), DateTime.UtcNow.AddHours(1));

        LegacyDataMigration.EnsureMigrated(destination, [empty, populated]);

        Assert.Equal(2, CountDocuments(destination));
        Assert.Equal(2, CountDocuments(populated));
        Assert.Equal(0, CountDocuments(empty));
    }

    [Fact]
    public void TwoDifferentLibrariesRequireExplicitRecoveryInsteadOfGuessing()
    {
        string first = PathFor("first"), second = PathFor("second"), destination = PathFor("stable");
        using (OpenDatabase(first, 1)) { }
        using (OpenDatabase(second, 4)) { }

        Assert.Throws<InvalidOperationException>(() => LegacyDataMigration.EnsureMigrated(destination, [first, second]));

        Assert.Equal(1, CountDocuments(first));
        Assert.Equal(4, CountDocuments(second));
        Assert.False(File.Exists(Path.Combine(destination, "Secretary", "library.db")));
    }

    [Fact]
    public void RepeatedMigrationDoesNotReplaceAnEstablishedDestination()
    {
        string source = PathFor("source"), destination = PathFor("stable");
        using (OpenDatabase(source, 2)) { }
        LegacyDataMigration.EnsureMigrated(destination, [source]);
        Write(destination, "Secretary/user-note.txt", "written after migration");
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(destination, "Secretary", "library.db"), Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE documents SET title='Updated after migration'";
            command.ExecuteNonQuery();
        }

        LegacyDataMigration.EnsureMigrated(destination, [source]);

        Assert.Equal(2, CountDocuments(destination));
        Assert.Equal("written after migration", File.ReadAllText(Path.Combine(destination, "Secretary", "user-note.txt")));
        using var verification = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(destination, "Secretary", "library.db"), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        verification.Open();
        using var verify = verification.CreateCommand();
        verify.CommandText = "SELECT COUNT(*) FROM documents WHERE title='Updated after migration'";
        Assert.Equal(2L, verify.ExecuteScalar());
    }

    [Fact]
    public void DefaultEntryReusesCompletedDestinationWithoutInspectingAccountSources()
    {
        string source = PathFor("source"), destination = PathFor("stable");
        using (OpenDatabase(source, 2)) { }
        LegacyDataMigration.EnsureMigrated(destination, [source]);
        string database = Path.Combine(destination, "Secretary", "library.db");
        byte[] before = File.ReadAllBytes(database);
        Write(destination, "Secretary/user-note.txt", "keep completed migration");

        // A completed valid target must return before looking up the account's actual legacy paths.
        LegacyDataMigration.EnsureMigrated(destination);

        Assert.Equal(before, File.ReadAllBytes(database));
        Assert.Equal(2, CountDocuments(destination));
        Assert.Equal("keep completed migration", File.ReadAllText(Path.Combine(destination, "Secretary", "user-note.txt")));
    }

    [Fact]
    public void ExistingDestinationDocumentsCannotBeOverwrittenByLegacySource()
    {
        string source = PathFor("source"), destination = PathFor("stable");
        using (OpenDatabase(source, 5)) { }
        using (OpenDatabase(destination, 1)) { }

        Assert.Throws<InvalidOperationException>(() => LegacyDataMigration.EnsureMigrated(destination, [source]));

        Assert.Equal(1, CountDocuments(destination));
        Assert.Equal(5, CountDocuments(source));
    }

    [Fact]
    public void EmptyDestinationUnacknowledgedScansArePreservedAlongsideMigratedLibrary()
    {
        string source = PathFor("source"), destination = PathFor("stable");
        using (OpenDatabase(source, 2)) { }
        using (OpenDatabase(destination, 0)) { }
        Write(source, "Secretary/outbox/source-scan.json", "source scan receipt");
        Write(destination, "Secretary/outbox/new-scan.json", "unacknowledged scan receipt");

        LegacyDataMigration.EnsureMigrated(destination, [source]);

        Assert.Equal(2, CountDocuments(destination));
        Assert.Equal("source scan receipt", File.ReadAllText(Path.Combine(destination, "Secretary", "outbox", "source-scan.json")));
        Assert.Equal("unacknowledged scan receipt", File.ReadAllText(Path.Combine(destination, "Secretary", "outbox", "new-scan.json")));
        Assert.Single(Directory.GetDirectories(root, "stable.before-migration-*"));
    }

    [Fact]
    public void ConflictingScanReceiptsAbortWithoutOverwritingEitherCopy()
    {
        string source = PathFor("source"), destination = PathFor("stable");
        using (OpenDatabase(source, 2)) { }
        using (OpenDatabase(destination, 0)) { }
        Write(source, "Secretary/outbox/same-scan.json", "source receipt");
        Write(destination, "Secretary/outbox/same-scan.json", "different receipt");

        Assert.Throws<InvalidOperationException>(() => LegacyDataMigration.EnsureMigrated(destination, [source]));

        Assert.Equal(2, CountDocuments(source));
        Assert.Equal(0, CountDocuments(destination));
        Assert.Equal("source receipt", File.ReadAllText(Path.Combine(source, "Secretary", "outbox", "same-scan.json")));
        Assert.Equal("different receipt", File.ReadAllText(Path.Combine(destination, "Secretary", "outbox", "same-scan.json")));
    }

    [Fact]
    public void RunningLegacyServiceBlocksMigrationBeforeAnyLibraryIsPublished()
    {
        string source = PathFor("source"), destination = PathFor("stable");
        using (OpenDatabase(source, 2)) { }
        using var serviceLease = new FileStream(Path.Combine(source, "Secretary", "service.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        Assert.Throws<InvalidOperationException>(() => LegacyDataMigration.EnsureMigrated(destination, [source]));

        Assert.Equal(2, CountDocuments(source));
        Assert.False(File.Exists(Path.Combine(destination, "Secretary", "library.db")));
    }

    [Fact]
    public void PopulatedSourceCannotSilentlyDiscardAnotherSourcesUnacknowledgedScan()
    {
        string populated = PathFor("populated"), unacknowledged = PathFor("other-source"), destination = PathFor("stable");
        using (OpenDatabase(populated, 2)) { }
        using (OpenDatabase(unacknowledged, 0)) { }
        Write(unacknowledged, "Secretary/outbox/new-scan.json", "unacknowledged scan in another source");

        Assert.Throws<InvalidOperationException>(() => LegacyDataMigration.EnsureMigrated(destination, [populated, unacknowledged]));

        Assert.Equal(2, CountDocuments(populated));
        Assert.Equal("unacknowledged scan in another source",
            File.ReadAllText(Path.Combine(unacknowledged, "Secretary", "outbox", "new-scan.json")));
        Assert.False(File.Exists(Path.Combine(destination, "Secretary", "library.db")));
    }

    [Fact]
    public void ExplicitRootWinsOverEnvironmentAndReadsOnlyItsOwnScannerSettings()
    {
        string configured = PathFor("explicit"), environment = PathFor("environment");
        string library = PathFor("explicit-library");
        Write(configured, "settings.json", JsonSerializer.Serialize(new { Root = library }));
        string? previous = Environment.GetEnvironmentVariable("SCANARCHIVE_DATA_DIR");
        try
        {
            Environment.SetEnvironmentVariable("SCANARCHIVE_DATA_DIR", Path.Combine(environment, "Secretary"));
            var settings = new AppSettings(Path.Combine(configured, "Secretary"));
            Assert.Equal(Path.Combine(configured, "Secretary"), settings.DataRoot);
            Assert.Equal(library, settings.Current.LibraryRoot);
            Assert.False(Directory.Exists(environment));
            Assert.False(File.Exists(Path.Combine(configured, "Secretary", "library.db")));
        }
        finally { Environment.SetEnvironmentVariable("SCANARCHIVE_DATA_DIR", previous); }
    }

    [Fact]
    public void EnvironmentOverrideIsAnIsolatedRootWithoutDefaultMigration()
    {
        string configured = PathFor("environment"), library = PathFor("environment-library");
        Write(configured, "settings.json", JsonSerializer.Serialize(new { Root = library }));
        string? previous = Environment.GetEnvironmentVariable("SCANARCHIVE_DATA_DIR");
        try
        {
            Environment.SetEnvironmentVariable("SCANARCHIVE_DATA_DIR", Path.Combine(configured, "Secretary"));
            var settings = new AppSettings();
            Assert.Equal(Path.Combine(configured, "Secretary"), settings.DataRoot);
            Assert.Equal(library, settings.Current.LibraryRoot);
            Assert.Empty(Directory.GetFiles(settings.Outbox));
            Assert.Equal(new[] { "settings.json" }, Directory.GetFiles(settings.DataRoot).Select(Path.GetFileName).ToArray());
        }
        finally { Environment.SetEnvironmentVariable("SCANARCHIVE_DATA_DIR", previous); }
    }

    [Fact]
    public void EmptyExplicitRootIsRejectedInsteadOfFallingBackToRealUserData()
    {
        Assert.Throws<ArgumentException>(() => new AppSettings(""));
    }

    public void Dispose()
    {
        string resolved = Path.GetFullPath(root), temporary = Path.GetFullPath(Path.GetTempPath());
        if (!resolved.StartsWith(temporary, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(resolved).StartsWith("scan-archive-migration-tests-", StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing to remove a path outside this test's temporary directory.");
        if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
    }
}
