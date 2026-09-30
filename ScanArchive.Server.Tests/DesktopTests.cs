using ScanArchive.Server;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace ScanArchive.Server.Tests;

public class DesktopTests
{
    static DesktopCommands Client(LibraryFixture f)=>new(f.Settings,f.Db,f.Docs,f.Search);
    [Fact] public async Task DesktopBrowseDetailsAndRestoreShareLibraryRecords()
    {
        var f=new LibraryFixture();var client=Client(f);string id=await f.Docs.Import(f.Pdf());
        await client.Handle(new(){["command"]="move",["id"]=id,["title"]="测试讲义",["category"]="学习/物理"},default);
        var list=await client.Handle(new(){["command"]="browse",["category"]="学习"},default);
        Assert.Equal(1,list.I("total"));Assert.Equal(id,list["documents"]![0]!.S("id"));
        var detail=await client.Handle(new(){["command"]="document",["id"]=id},default);
        Assert.Equal(1,detail["document"]!.I("locked"));
        await f.Docs.Trash(id,default);Assert.Equal(0,(await client.Handle(new(){["command"]="browse"},default)).I("total"));
        await client.Handle(new(){["command"]="restore",["id"]=id},default);
        Assert.Equal(1,(await client.Handle(new(){["command"]="browse"},default)).I("total"));
    }
    [Fact] public async Task ChatQueuesOnceAndKeepsConversationHistoryAcrossClients()
    {
        var f=new LibraryFixture();var client=Client(f);
        var receipt=await client.Handle(new(){["command"]="chat",["message"]="找一下物理笔记"},default);string id=receipt.S("conversation");
        await Assert.ThrowsAsync<ArgumentException>(()=>client.Handle(new(){["command"]="chat",["id"]=id,["message"]="重复发送"},default));
        var state=await client.Handle(new(){["command"]="conversation",["id"]=id},default);
        Assert.Single(state["messages"]!.AsArray());Assert.Equal("pending",state["jobs"]![0]!.S("status"));
        Assert.Equal(id,(await client.Handle(new(){["command"]="conversations"},default))["conversations"]![0]!.S("id"));
    }
    [Fact] public async Task NativeSettingsValidateLibraryRootAndNeverReturnKey()
    {
        var f=new LibraryFixture();var client=Client(f);await f.Docs.Import(f.Pdf());
        var options=f.Settings.Current with{DailyWakeTime="06:15",AgentMaxSteps=25};
        await client.Handle(new(){["command"]="save_settings",["options"]=JsonSerializer.SerializeToNode(options,AppSettings.Json),["apiKey"]="another-test-secret"},default);
        var state=await client.Handle(new(){["command"]="settings"},default);
        Assert.Equal("06:15",state["options"]!.S("dailyWakeTime"));Assert.DoesNotContain("another-test-secret",state.ToJsonString());
        await Assert.ThrowsAsync<ArgumentException>(()=>client.Handle(new(){["command"]="save_settings",["options"]=JsonSerializer.SerializeToNode(options with{LibraryRoot=Path.Combine(f.Root,"wrong")},AppSettings.Json)},default));
    }
    [Fact] public async Task MixedBatchTagsDoNotMakeUnrelatedPagesMatch()
    {
        var f=new LibraryFixture();string id=await f.Docs.Import(f.Pdf());
        f.Db.Exec("UPDATE documents SET title='多份资料',tags='保修,矢量分解' WHERE id=$i",("$i",id));
        f.Db.Exec("INSERT INTO pages VALUES($i,1,'设备保修凭证 WARRANTY-992','保修条款'); INSERT INTO pages VALUES($i,2,'矢量分解 horizontal velocity','物理向量练习');",("$i",id));
        f.Fake.EmbeddingsFail=true;await f.Analyzer.Reindex(id,default);
        var hits=(await f.Search.Find("矢量分解"))["results"]!.AsArray();
        Assert.Single(hits);Assert.Equal(2,hits[0]!.I("page"));
        Assert.DoesNotContain("矢量分解",f.Db.Rows("SELECT text FROM chunks WHERE doc_id=$i AND page=1",("$i",id))[0].S("text"));
    }
    [Fact] public async Task SlowSearchDoesNotBlockDesktopLibraryOrScanHandoff()
    {
        var f=new LibraryFixture();var client=Client(f);var coordinator=new CaptureCoordinator(f.Settings,f.Db,f.Docs,client);
        using var bridge=new DesktopBridge(f.Settings,coordinator,Microsoft.Extensions.Logging.Abstractions.NullLogger<DesktopBridge>.Instance);
        f.Fake.EmbeddingEntered=new(TaskCreationOptions.RunContinuationsAsynchronously);f.Fake.ReleaseEmbedding=new(TaskCreationOptions.RunContinuationsAsynchronously);
        await bridge.StartAsync(default);
        try{
            var searching=ScanArchive.Integration.DesktopProtocol.Request(f.Settings.DataRoot,new(){["command"]="search",["q"]="test"});
            await f.Fake.EmbeddingEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var browsing=await ScanArchive.Integration.DesktopProtocol.Request(f.Settings.DataRoot,new(){["command"]="browse"}).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(0,browsing.I("total"));f.Fake.ReleaseEmbedding.SetResult();await searching;
        }finally{f.Fake.ReleaseEmbedding.TrySetResult();await bridge.StopAsync(default);}
    }
    [Fact] public async Task CancelledReindexKeepsPreviousSearchCoverage()
    {
        var f=new LibraryFixture();string id=await f.Docs.Import(f.Pdf());f.Search.AddChunk(id,1,"原有可查内容",null,"");
        f.Db.Exec("INSERT INTO pages VALUES($i,1,'新版内容','')",("$i",id));
        using var cancelled=new CancellationTokenSource();cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>f.Analyzer.Reindex(id,cancelled.Token));
        Assert.Equal("原有可查内容",f.Db.Rows("SELECT text FROM chunks WHERE doc_id=$i",("$i",id)).Single().S("text"));
    }
}
