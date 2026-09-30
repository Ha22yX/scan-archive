using System.Text.Json.Nodes;

namespace ScanArchive.Server;

public sealed class Worker(AppSettings settings,Database db,Documents docs,Analyzer analyzer,SecretaryAgent agent,OpenAi ai,ILogger<Worker> logger):BackgroundService
{
    public static string? DueWake(ServerOptions options,string? last,DateTime now)
    {
        if(!options.ScheduleEnabled||!TimeOnly.TryParseExact(options.DailyWakeTime,"HH:mm",out var time))return null;
        var scheduled=now.Date+time.ToTimeSpan();
        if(now<scheduled)scheduled=scheduled.AddDays(-1);
        string key=scheduled.ToString("yyyy-MM-dd")+"T"+options.DailyWakeTime;
        // At installation, run today's missed wake-up once; do not replay older days.
        if(last==null&&scheduled.Date<now.Date)return null;
        return last==key?null:key;
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        docs.RecoverMoves();
        while(!stoppingToken.IsCancellationRequested)
        {
            try
            {
                settings.Reload();
                string? due=DueWake(settings.Current,db.State("last_wake"),DateTime.Now);
                if(due!=null){docs.Enqueue("review","daily:"+due);db.State("last_wake",due);}
                if(ai.Available)
                {
                    var job=db.Rows("SELECT * FROM jobs WHERE status='pending' AND next_run <= $now ORDER BY CASE kind WHEN 'chat' THEN 0 WHEN 'index' THEN 1 WHEN 'organize' THEN 2 ELSE 3 END,created LIMIT 1",("$now",Database.Now)).FirstOrDefault();
                    if(job!=null){await Process(job,stoppingToken);continue;}
                }
            }
            catch(OperationCanceledException)when(stoppingToken.IsCancellationRequested){break;}
            catch(Exception ex){logger.LogError(ex,"Background processing failed");db.Log("error",ex.Message);}
            await Task.Delay(2000,stoppingToken);
        }
    }
    public async Task<int> Discover(CancellationToken ct)
    {
        if(!Directory.Exists(docs.Root))return 0;int found=0;
        var options=new EnumerationOptions{RecurseSubdirectories=true,IgnoreInaccessible=true,AttributesToSkip=FileAttributes.ReparsePoint};
        foreach(string path in Directory.EnumerateFiles(docs.Root,"*",options))
        {
            ct.ThrowIfCancellationRequested();
            if(Path.GetRelativePath(docs.Root,path).Split(Path.DirectorySeparatorChar).Any(x=>x.StartsWith('.')))continue;
            if(!Documents.Extensions.Contains(Path.GetExtension(path))||DateTime.UtcNow-File.GetLastWriteTimeUtc(path)<TimeSpan.FromSeconds(3))continue;
            if(db.Rows("SELECT id FROM documents WHERE path=$p",("$p",Path.GetFullPath(path))).Count>0)continue;
            try{await docs.Import(path,ct:ct);found++;}catch(IOException){ }
        }
        return found;
    }
    async Task Process(JsonObject job,CancellationToken ct)
    {
        string id=job.S("id"),payload=job.S("payload"),kind=job.S("kind");
        await docs.Mutation.WaitAsync(ct);
        try
        {
            if(db.Exec("UPDATE jobs SET status='running',attempts=attempts+1,updated=$t WHERE id=$i AND status='pending'",("$t",Database.Now),("$i",id))==0)return;
        }
        finally{docs.Mutation.Release();}
        try
        {
            switch(kind)
            {
                case "index":await analyzer.Analyze(payload,ct);break;
                case "embeddings":await analyzer.RepairEmbeddings(payload,ct);break;
                case "reindex":await analyzer.Reindex(payload,ct);break;
                case "organize":
                    await agent.Run("Content analysis completed for document ID "+payload+". Inspect its metadata/pages and the existing library taxonomy, then organize it intelligently. Split truly independent documents if needed. Preserve the original batch and scan time.",null,ct);
                    db.Exec("UPDATE documents SET status='ready' WHERE id=$i AND status='analyzed'",("$i",payload));docs.WriteMetadata(payload);break;
                case "review":
                    await agent.Run("Scheduled library maintenance. Inspect the library, page-analysis/search coverage, category quality, duplicates in terminology, titles and recent changes. Paginate the inventory as needed. Repair missing indexes, consolidate redundant categories and improve organization where evidence supports it. Respect locked user choices. Do not churn existing classifications without a clear benefit. Summarize actions and unresolved issues.",null,ct);break;
                case "chat":await agent.Run("",payload,ct);break;
                default:throw new ArgumentException("未知任务类型。");
            }
            db.Exec("UPDATE jobs SET status='done',error='',updated=$t WHERE id=$i",("$t",Database.Now),("$i",id));
        }
        catch(OperationCanceledException)when(ct.IsCancellationRequested)
        {db.Exec("UPDATE jobs SET status='pending' WHERE id=$i",("$i",id));throw;}
        catch(Exception ex)
        {
            int attempts=job.I("attempts")+1;
            // Never automatically replay a partly executed agent run: its moves/splits may already have succeeded.
            bool retry=kind is "index" or "embeddings" or "reindex" && attempts<4;
            db.Exec("UPDATE jobs SET status=$s,error=$e,next_run=$next,updated=$t WHERE id=$i",("$s",retry?"pending":"failed"),("$e",ex.Message),("$next",DateTimeOffset.UtcNow.AddMinutes(Math.Pow(2,attempts)).ToString("O")),("$t",Database.Now),("$i",id));
            if(kind=="index")db.Exec("UPDATE documents SET status='error',error=$e WHERE id=$i",("$e",ex.Message),("$i",payload));
            db.Log("error",kind+"："+ex.Message);
            if(kind=="chat")db.Exec("INSERT INTO messages(conversation,role,text,created) VALUES($c,'assistant',$t,$d)",("$c",payload),("$t","本次处理未完成："+ex.Message+" 已执行的操作可在活动页查看。"),("$d",Database.Now));
        }
    }
}
