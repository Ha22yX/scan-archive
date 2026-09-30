using ScanArchive.Server;
using System.Text.Json.Nodes;
using Xunit;

namespace ScanArchive.Server.Tests;

public class PipelineTests
{
    static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(12));
        while(!condition())await Task.Delay(30,timeout.Token);
    }
    [Fact] public async Task DocumentAndPageAnalysisRunInParallelWhileChatRemainsResponsive()
    {
        var f=new LibraryFixture();f.Settings.Save(f.Settings.Current with{AutoOrganize=false,DocumentConcurrency=2,PageConcurrency=2});
        string first=await f.Docs.Import(f.Pdf("one.pdf",2)),second=await f.Docs.Import(f.Pdf("two.pdf",3));
        f.Docs.Enqueue("reindex",first); // Same document must never be processed by two jobs at once.
        f.Fake.ReleasePages=new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var worker=new Worker(f.Settings,f.Db,f.Docs,f.Analyzer,f.Agent,f.Ai,Microsoft.Extensions.Logging.Abstractions.NullLogger<Worker>.Instance);
        await worker.StartAsync(default);
        try {
            await WaitUntil(()=>Volatile.Read(ref f.Fake.PageCalls)==4);
            Assert.Equal(2,f.Db.Rows("SELECT id FROM jobs WHERE status='running' AND kind='index'").Count);
            Assert.Empty(f.Db.Rows("SELECT payload FROM jobs WHERE status='running' GROUP BY payload HAVING count(*)>1"));
            Assert.Equal(0,f.Fake.MetadataCalls);Assert.Empty(f.Db.Rows("SELECT id FROM jobs WHERE kind='organize'"));
            f.Fake.AgentResponses.Enqueue(FakeApi.Message("秘书仍可回复。"));
            var client=new DesktopCommands(f.Settings,f.Db,f.Docs,f.Search);
            var chat=await client.Handle(new(){["command"]="chat",["message"]="你好"},default);
            await WaitUntil(()=>f.Db.Rows("SELECT id FROM jobs WHERE id=$i AND status='done'",("$i",chat.S("job"))).Count==1);
            Assert.False(f.Fake.ReleasePages.Task.IsCompleted);
            f.Fake.ReleasePages.SetResult();
            await WaitUntil(()=>f.Db.Rows("SELECT id FROM jobs WHERE kind='index' AND status='done'").Count==2);
            Assert.Equal(5,f.Fake.PageCalls);
        }finally{f.Fake.ReleasePages.TrySetResult();await worker.StopAsync(default);}
    }
    [Fact] public async Task FullyCoveredArchivedChildrenReplaceParentInBrowseSearchAndCounts()
    {
        var f=new LibraryFixture();string parent=await f.Docs.Import(f.Pdf(count:4));
        string one=await f.Docs.ExtractPages(parent,"1,3","一",default),two=await f.Docs.ExtractPages(parent,"2,4","二",default);
        f.Db.Exec("UPDATE documents SET status='ready'; UPDATE jobs SET status='done'");
        f.Search.AddChunk(parent,1,"测试资料",null,"");f.Search.AddChunk(one,1,"测试资料",null,"");f.Fake.EmbeddingsFail=true;
        await f.Docs.ReconcileSplitBatches();
        Assert.Equal("superseded",f.Db.Doc(parent)!.S("status"));Assert.True(File.Exists(f.Db.Doc(parent)!.S("original")));
        var list=await new DesktopCommands(f.Settings,f.Db,f.Docs,f.Search).Handle(new(){["command"]="browse"},default);
        Assert.Equal(2,list.I("total"));Assert.DoesNotContain(list["documents"]!.AsArray(),d=>d!.S("id")==parent);
        Assert.DoesNotContain((await f.Search.Find("测试资料"))["results"]!.AsArray(),d=>d!.S("doc_id")==parent);
        Assert.DoesNotContain(f.Agent.Overview()["counts"]!.AsArray(),d=>d!.S("status")=="superseded");
        await f.Docs.Trash(two,default);await f.Docs.ReconcileSplitBatches();Assert.Equal("ready",f.Db.Doc(parent)!.S("status"));
        await f.Docs.Restore(two,default);await f.Docs.ReconcileSplitBatches();Assert.Equal("superseded",f.Db.Doc(parent)!.S("status"));
    }
    [Theory][InlineData("missing")][InlineData("overlap")][InlineData("processing")][InlineData("lost_file")]
    public async Task IncompleteOrInvalidSplitsNeverHideTheParent(string issue)
    {
        var f=new LibraryFixture();string parent=await f.Docs.Import(f.Pdf(count:4));
        await f.Docs.ExtractPages(parent,"1-2","一",default);
        string other=await f.Docs.ExtractPages(parent,issue=="missing"?"3":issue=="overlap"?"2-4":"3-4","二",default);
        f.Db.Exec("UPDATE documents SET status='ready'; UPDATE jobs SET status='done'");
        if(issue=="processing")f.Db.Exec("UPDATE documents SET status='analyzed' WHERE id=$i",("$i",other));
        if(issue=="lost_file")File.Move(f.Db.Doc(other)!.S("original"),f.Db.Doc(other)!.S("original")+".missing");
        await f.Docs.ReconcileSplitBatches();Assert.Equal("ready",f.Db.Doc(parent)!.S("status"));
    }
    [Fact] public async Task ConcurrentEnqueueReturnsOneDurableJob()
    {
        var f=new LibraryFixture();string id=await f.Docs.Import(f.Pdf());
        var jobs=await Task.WhenAll(Enumerable.Range(0,12).Select(_=>Task.Run(()=>f.Docs.Enqueue("index",id))));
        Assert.Single(jobs.Distinct());Assert.Single(f.Db.Rows("SELECT id FROM jobs WHERE kind='index'"));
    }
}
