using Microsoft.Data.Sqlite;
using System.Text.Json.Nodes;

namespace ScanArchive.Server;

public sealed class Database
{
    readonly string connectionString;
    readonly object gate = new();
    public Database(AppSettings settings)
    {
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(settings.DataRoot, "library.db"), DefaultTimeout = 30 }.ToString();
        Exec("""
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS documents (
                id TEXT PRIMARY KEY, hash TEXT NOT NULL UNIQUE, path TEXT NOT NULL, original TEXT NOT NULL,
                title TEXT NOT NULL, category TEXT NOT NULL DEFAULT '', summary TEXT NOT NULL DEFAULT '', tags TEXT NOT NULL DEFAULT '',
                scanned TEXT NOT NULL, document_date TEXT NOT NULL DEFAULT '', page_count INTEGER NOT NULL DEFAULT 0,
                status TEXT NOT NULL DEFAULT 'queued', error TEXT NOT NULL DEFAULT '', created TEXT NOT NULL,
                locked INTEGER NOT NULL DEFAULT 0, parent_id TEXT NOT NULL DEFAULT '', source_pages TEXT NOT NULL DEFAULT '',mixed_content INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS pages(doc_id TEXT NOT NULL, number INTEGER NOT NULL, text TEXT NOT NULL, summary TEXT NOT NULL DEFAULT '', PRIMARY KEY(doc_id,number));
            CREATE TABLE IF NOT EXISTS analyses(doc_id TEXT NOT NULL,page INTEGER NOT NULL,model TEXT NOT NULL,analyzed_at TEXT NOT NULL,PRIMARY KEY(doc_id,page));
            CREATE TABLE IF NOT EXISTS chunks(id INTEGER PRIMARY KEY AUTOINCREMENT,doc_id TEXT NOT NULL,page INTEGER NOT NULL,text TEXT NOT NULL,embedding TEXT,model TEXT NOT NULL DEFAULT '');
            CREATE INDEX IF NOT EXISTS chunks_doc ON chunks(doc_id);
            CREATE VIRTUAL TABLE IF NOT EXISTS search_fts USING fts5(chunk_id UNINDEXED,tokens,tokenize='unicode61');
            CREATE TABLE IF NOT EXISTS jobs(id TEXT PRIMARY KEY,kind TEXT NOT NULL,payload TEXT NOT NULL,status TEXT NOT NULL,attempts INTEGER NOT NULL DEFAULT 0,next_run TEXT NOT NULL,error TEXT NOT NULL DEFAULT '',created TEXT NOT NULL,updated TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS jobs_status ON jobs(status,next_run);
            CREATE TABLE IF NOT EXISTS operations(id TEXT PRIMARY KEY,doc_id TEXT NOT NULL,kind TEXT NOT NULL,old_path TEXT NOT NULL,new_path TEXT NOT NULL,before_json TEXT NOT NULL,after_json TEXT NOT NULL,state TEXT NOT NULL,created TEXT NOT NULL,reason TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS activity(id INTEGER PRIMARY KEY AUTOINCREMENT,time TEXT NOT NULL,kind TEXT NOT NULL,message TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS conversations(id TEXT PRIMARY KEY,title TEXT NOT NULL,created TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS messages(id INTEGER PRIMARY KEY AUTOINCREMENT,conversation TEXT NOT NULL,role TEXT NOT NULL,text TEXT NOT NULL,created TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS memories(id TEXT PRIMARY KEY,text TEXT NOT NULL,created TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS state(key TEXT PRIMARY KEY,value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS usage(day TEXT PRIMARY KEY,requests INTEGER NOT NULL DEFAULT 0,input_tokens INTEGER NOT NULL DEFAULT 0,output_tokens INTEGER NOT NULL DEFAULT 0);
            """);
        Exec("UPDATE jobs SET status=CASE WHEN kind IN ('index','embeddings','reindex') THEN 'pending' ELSE 'failed' END,error='服务重启，任务未完成；已执行的整理操作保留。' WHERE status='running'");
    }
    public List<JsonObject> Rows(string sql, params (string, object?)[] args)
    {
        lock (gate)
        {
            using var c = new SqliteConnection(connectionString); c.Open();
            using var cmd = c.CreateCommand(); cmd.CommandText = sql;
            foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
            using var reader = cmd.ExecuteReader();
            var rows = new List<JsonObject>();
            while (reader.Read())
            {
                var row = new JsonObject();
                for (int i = 0; i < reader.FieldCount; i++) row[reader.GetName(i)] = reader.IsDBNull(i) ? null : System.Text.Json.JsonSerializer.SerializeToNode(reader.GetValue(i));
                rows.Add(row);
            }
            return rows;
        }
    }
    public int Exec(string sql, params (string, object?)[] args)
    {
        lock (gate)
        {
            using var c = new SqliteConnection(connectionString); c.Open();
            using var cmd = c.CreateCommand(); cmd.CommandText = sql;
            foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
            return cmd.ExecuteNonQuery();
        }
    }
    public void Transaction(Action<SqliteConnection, SqliteTransaction> action)
    {
        lock (gate)
        {
            using var c = new SqliteConnection(connectionString); c.Open(); using var tx = c.BeginTransaction(); action(c, tx); tx.Commit();
        }
    }
    public JsonObject? Doc(string id) => Rows("SELECT * FROM documents WHERE id=$id", ("$id", id)).FirstOrDefault();
    public void Log(string kind, string message) => Exec("INSERT INTO activity(time,kind,message) VALUES($t,$k,$m)", ("$t", Now), ("$k", kind), ("$m", message));
    public string? State(string key) => Rows("SELECT value FROM state WHERE key=$k", ("$k", key)).FirstOrDefault()?.S("value");
    public void State(string key,string value) => Exec("INSERT INTO state VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=excluded.value", ("$k",key),("$v",value));
    public static string Now => DateTimeOffset.UtcNow.ToString("O");
}
public static class JsonHelpers
{
    public static string S(this JsonNode node, string key) => node[key]?.ToString() ?? "";
    public static int I(this JsonNode node,string key) => int.TryParse(node.S(key), out int value) ? value : 0;
}
