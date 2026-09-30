using System.Text.Json;
using System.Text.Json.Nodes;

namespace ScanArchive.Server;

// The Windows client is an authenticated local peer (CurrentUserOnly named pipe).
// Both transports operate on the same document and job services.
public sealed class DesktopCommands(AppSettings settings, Database db, Documents docs, Search search)
{
    static JsonArray Rows(IEnumerable<JsonObject> rows) => new(rows.Select(x => (JsonNode)x).ToArray());
    readonly SemaphoreSlim chatGate = new(1, 1);

    public async Task<JsonObject> Handle(JsonObject r, CancellationToken ct)
    {
        string id = r.S("id");
        switch (r.S("command"))
        {
            case "browse":
                string filter = "WHERE status<>'deleted' AND ($c='' OR category=$c OR substr(category,1,length($c)+1)=$c||'/') AND ($s='' OR status=$s)";
                var args = new (string, object?)[] { ("$c", r.S("category")), ("$s", r.S("status")) };
                return new() {
                    ["documents"] = Rows(db.Rows("SELECT id,title,original,path,scanned,status,category,error,page_count,summary,locked,parent_id,source_pages FROM documents " + filter + " ORDER BY scanned DESC,id LIMIT 60 OFFSET $o", args.Append(("$o", (object?)Math.Max(0,r.I("offset")))).ToArray())),
                    ["total"] = db.Rows("SELECT count(*) n FROM documents " + filter, args)[0].I("n"),
                    ["categories"] = docs.Folders(),
                    ["pending"] = db.Rows("SELECT count(*) n FROM jobs WHERE status IN ('pending','running')")[0].I("n"),
                    ["configured"] = !string.IsNullOrWhiteSpace(settings.ApiKey)
                };
            case "search": return await search.Find(r.S("q"), r.S("category"), r.S("from"), r.S("to"), 40, ct);
            case "document":
                return new() { ["document"] = db.Doc(id) ?? throw new KeyNotFoundException("文档不存在。"),
                    ["pages"] = Rows(db.Rows("SELECT number,text,summary FROM pages WHERE doc_id=$i ORDER BY number", ("$i",id))),
                    ["children"] = Rows(db.Rows("SELECT id,title,source_pages FROM documents WHERE parent_id=$i AND status<>'deleted'", ("$i",id))) };
            case "restore": await docs.Restore(id,ct); return new() { ["ok"] = true };
            case "move": return await docs.Move(id,r.S("title"),r.S("category"),"桌面手动调整",true,ct);
            case "unlock":
                db.Exec("UPDATE documents SET locked=0 WHERE id=$i",("$i",id)); docs.WriteMetadata(id); return new(){["ok"]=true};
            case "organize":
                if(db.Doc(id)?.S("status") is not ("ready" or "analyzed")) throw new ArgumentException("请等待内容分析完成后再整理。");
                return new(){["job"]=docs.Enqueue("organize",id)};
            case "format_summary":
                if(db.Doc(id)?.S("status") is not ("ready" or "analyzed"))throw new ArgumentException("请等待文档分析完成后再整理概括。");
                return new(){["job"]=docs.Enqueue("format_summary",id)};
            case "retry_analysis": return new(){["job"]=docs.Enqueue("index",id)};
            case "maintenance": return new(){["job"]=docs.Enqueue("review","manual:"+Guid.NewGuid().ToString("N"))};
            case "activity": return new(){
                ["jobs"]=Rows(db.Rows("SELECT j.*,d.title document_title FROM jobs j LEFT JOIN documents d ON d.id=j.payload ORDER BY j.created DESC LIMIT 100")),
                ["operations"]=Rows(db.Rows("SELECT * FROM operations ORDER BY created DESC LIMIT 100")),
                ["trash"]=Rows(db.Rows("SELECT d.id,d.title,t.deleted_at FROM documents d JOIN trash t ON t.doc_id=d.id ORDER BY t.deleted_at DESC")),
                ["activity"]=Rows(db.Rows("SELECT * FROM activity ORDER BY id DESC LIMIT 100"))};
            case "retry_job": db.Exec("UPDATE jobs SET status='pending',next_run=$t,error='' WHERE id=$i AND status='failed'",("$t",Database.Now),("$i",id)); return new(){["ok"]=true};
            case "undo": await docs.Undo(id,ct); return new(){["ok"]=true};
            case "conversations": return new(){["conversations"]=Rows(db.Rows("SELECT * FROM conversations ORDER BY created DESC"))};
            case "conversation": return new(){
                ["messages"]=Rows(db.Rows("SELECT role,text,created FROM messages WHERE conversation=$i ORDER BY id",("$i",id))),
                ["jobs"]=Rows(db.Rows("SELECT id,status,error FROM jobs WHERE kind='chat' AND payload=$i ORDER BY created DESC LIMIT 1",("$i",id)))};
            case "chat":
                await chatGate.WaitAsync(ct);
                try {
                    string message=r.S("message").Trim();
                    if(message.Length is <1 or >16000)throw new ArgumentException("消息应为 1 至 16000 字。");
                    if(id==""){id=Guid.NewGuid().ToString("N");db.Exec("INSERT INTO conversations VALUES($i,$t,$d)",("$i",id),("$t",message[..Math.Min(60,message.Length)]),("$d",Database.Now));}
                    else if(db.Rows("SELECT id FROM conversations WHERE id=$i",("$i",id)).Count==0)throw new ArgumentException("会话不存在。");
                    if(db.Rows("SELECT id FROM jobs WHERE kind='chat' AND payload=$i AND status IN ('pending','running')",("$i",id)).Count>0)throw new ArgumentException("秘书正在处理此会话，请等待回复。");
                    db.Exec("INSERT INTO messages(conversation,role,text,created) VALUES($c,'user',$t,$d)",("$c",id),("$t",message),("$d",Database.Now));
                    return new(){["conversation"]=id,["job"]=docs.Enqueue("chat",id)};
                } finally {chatGate.Release();}
            case "settings": return new(){["options"]=JsonSerializer.SerializeToNode(settings.Current,AppSettings.Json),["hasApiKey"]=!string.IsNullOrWhiteSpace(settings.ApiKey)};
            case "save_settings":
                var options=r["options"]!.Deserialize<ServerOptions>(AppSettings.Json)??throw new ArgumentException("设置无效。");
                if(!string.Equals(Path.GetFullPath(options.LibraryRoot),Path.GetFullPath(settings.Current.LibraryRoot),StringComparison.OrdinalIgnoreCase)&&db.Rows("SELECT id FROM documents LIMIT 1").Count>0)
                    throw new ArgumentException("文档库已有文件，不能直接改目录。请先迁移文档库及数据，避免失去关联。");
                bool changed=options.EmbeddingModel!=settings.Current.EmbeddingModel;
                settings.Save(options);settings.SaveApiKey(r.S("apiKey"));
                if(changed)foreach(var doc in db.Rows("SELECT id FROM documents WHERE status<>'deleted'"))docs.Enqueue("embeddings",doc.S("id"));
                return new(){["ok"]=true};
            case "import":
                // User-selected native file picker, not background directory discovery.
                string source=Path.GetFullPath(r.S("path")), ext=Path.GetExtension(source);
                if(!Documents.Extensions.Contains(ext))throw new ArgumentException("请选择 PDF、PNG 或 JPEG 文件。");
                string target=docs.SafePath("Inbox/"+Documents.Clean(Path.GetFileNameWithoutExtension(source))+"__"+Guid.NewGuid().ToString("N")[..8]+ext);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using(var input=File.OpenRead(source))
                await using(var output=new FileStream(target+".partial",FileMode.CreateNew,FileAccess.Write))await input.CopyToAsync(output,ct);
                File.Move(target+".partial",target);
                return new(){["id"]=await docs.Import(target,File.GetCreationTimeUtc(source).ToString("O"),ct:ct)};
            default: throw new ArgumentException("未知桌面命令。");
        }
    }
}
