using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ScanArchive.Integration;

namespace ScanArchive.Server;

// Explicit scanner commands, never an archive-directory watcher.
public sealed class CaptureCoordinator(AppSettings settings,Database db,Documents docs,DesktopCommands? commands=null)
{
    readonly SemaphoreSlim gate=new(1,1);
    public async Task<JsonObject> Handle(JsonObject request,CancellationToken ct)
    {
        switch(request.S("command"))
        {
            case "register_scan":return await Register(request,ct);
            case "library":
                return new(){["root"]=settings.Current.LibraryRoot,["documents"]=new JsonArray(db.Rows("SELECT id,title,path,original,scanned,status,category,error,page_count FROM documents WHERE status<>'deleted' ORDER BY scanned DESC LIMIT 100 OFFSET $o",("$o",Math.Max(0,request.I("offset")))).Select(d=>(JsonNode)d).ToArray())};
            case "trash":await docs.Trash(request.S("id"),ct);return new(){["ok"]=true};
            default:return commands==null?throw new ArgumentException("未知桌面命令。"):await commands.Handle(request,ct);
        }
    }
    async Task<JsonObject> Register(JsonObject r,CancellationToken ct)
    {
        if(!Guid.TryParseExact(r.S("scanId"),"N",out _)||!DateTimeOffset.TryParse(r.S("scanned"),out _))throw new ArgumentException("扫描事件缺少有效编号或时间。");
        await gate.WaitAsync(ct);
        try
        {
            string scan=r.S("scanId");var receipt=db.Rows("SELECT * FROM scan_submissions WHERE scan_id=$i",("$i",scan)).FirstOrDefault();
            string id=receipt?.S("doc_id")??"";
            if(id!="")return new(){["id"]=id,["scanId"]=scan,["acknowledged"]=true};
            if(receipt==null)
            {
                string path=Path.GetFullPath(r.S("path"));docs.SafePath(Path.GetRelativePath(docs.Root,path));
                await using var input=File.OpenRead(path);string hash=Convert.ToHexString(await SHA256.HashDataAsync(input,ct));
                db.Exec("INSERT INTO scan_submissions(scan_id,path,scanned,hash,device,source) VALUES($i,$p,$s,$h,$d,$f)",("$i",scan),("$p",path),("$s",r.S("scanned")),("$h",hash),("$d",r.S("device")),("$f",r.S("source")));
                receipt=db.Rows("SELECT * FROM scan_submissions WHERE scan_id=$i",("$i",scan)).Single();
            }
            // A response can be lost after import and an agent may already have moved the file.
            id=db.Rows("SELECT id FROM documents WHERE hash=$h",("$h",receipt.S("hash"))).FirstOrDefault()?.S("id")??"";
            if(id=="")id=await docs.Import(receipt.S("path"),receipt.S("scanned"),ct:ct);
            db.Exec("UPDATE scan_submissions SET doc_id=$d WHERE scan_id=$i",("$d",id),("$i",scan));
            if(db.Doc(id)?.S("status")=="queued")docs.Enqueue("index",id);
            docs.WriteMetadata(id);
            return new(){["id"]=id,["scanId"]=scan,["acknowledged"]=true};
        }
        finally{gate.Release();}
    }
}

public sealed class DesktopBridge(AppSettings settings,CaptureCoordinator coordinator,ILogger<DesktopBridge> logger):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // A long search must not block scan acknowledgement, browsing or chat polling.
        using var slots=new SemaphoreSlim(8,8);
        var active=new List<Task>();
        try {
            while(!ct.IsCancellationRequested){
                await slots.WaitAsync(ct);
                var pipe=new NamedPipeServerStream(DesktopProtocol.PipeName(settings.DataRoot),PipeDirection.InOut,8,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
                try{await pipe.WaitForConnectionAsync(ct);}
                catch{pipe.Dispose();slots.Release();throw;}
                active.RemoveAll(t=>t.IsCompleted);
                active.Add(Serve(pipe,slots,ct));
            }
        }catch(OperationCanceledException)when(ct.IsCancellationRequested){}
        finally{await Task.WhenAll(active);}
    }
    async Task Serve(NamedPipeServerStream pipe,SemaphoreSlim slots,CancellationToken ct)
    {
        using(pipe)
        try{
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(28));
            using var reader=new StreamReader(pipe,Encoding.UTF8,leaveOpen:true);
            using var writer=new StreamWriter(pipe,new UTF8Encoding(false),leaveOpen:true){AutoFlush=true};
            string line=await reader.ReadLineAsync(timeout.Token)??"";
            JsonObject response;
            try{if(line.Length>65536)throw new ArgumentException("桌面请求过大。");response=await coordinator.Handle(JsonNode.Parse(line)!.AsObject(),timeout.Token);}
            catch(Exception ex){response=new(){["error"]=ex.Message};}
            await writer.WriteLineAsync(response.ToJsonString().AsMemory(),timeout.Token);
        }
        catch(OperationCanceledException)when(ct.IsCancellationRequested){}
        catch(Exception ex){logger.LogWarning("Desktop connection ended: {Message}",ex.Message);}
        finally{slots.Release();}
    }
}
