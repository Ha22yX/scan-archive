using ScanArchive.Server;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using PdfSharp.Pdf;
using PdfSharp.Drawing;
using Xunit;

namespace ScanArchive.Server.Tests;

public sealed class LibraryFixture
{
    public string Root {get;}=Path.Combine(Path.GetTempPath(),"scan-archive-tests-"+Guid.NewGuid().ToString("N"));
    public AppSettings Settings {get;}
    public Database Db {get;}
    public Documents Docs {get;}
    public FakeApi Fake {get;}=new();
    public OpenAi Ai {get;}
    public Search Search {get;}
    public Analyzer Analyzer {get;}
    public SecretaryAgent Agent {get;}
    public LibraryFixture()
    {
        Settings=new AppSettings(Path.Combine(Root,"data"));Settings.Save(Settings.Current with{LibraryRoot=Path.Combine(Root,"library"),ScheduleEnabled=false});Settings.SaveApiKey("test-not-a-real-key");
        Directory.CreateDirectory(Settings.Current.LibraryRoot);Db=new Database(Settings);Docs=new Documents(Settings,Db);Ai=new OpenAi(Settings,Db,new HttpClient(Fake));Search=new Search(Settings,Db,Ai);Analyzer=new Analyzer(Settings,Db,Docs,Ai,Search);Agent=new SecretaryAgent(Settings,Db,Docs,Search,Ai);
    }
    public string Pdf(string name="input.pdf",int count=2)
    {
        string file=Path.Combine(Settings.Current.LibraryRoot,name);using var pdf=new PdfDocument();
        for(int n=0;n<count;n++){var p=pdf.AddPage();using var g=XGraphics.FromPdfPage(p);g.DrawRectangle(n==0?XBrushes.Green:XBrushes.Blue,10,10,100+n,100);}
        pdf.Save(file);return file;
    }
}

public sealed class FakeApi:HttpMessageHandler
{
    public int PageCalls,MetadataCalls,EmbeddingCalls;
    public bool EmbeddingsFail;
    public readonly List<JsonObject> Requests=[];
    public readonly Queue<JsonObject> AgentResponses=[];
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
    {
        var body=JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject();Requests.Add(body);
        JsonObject result;
        if(request.RequestUri!.AbsolutePath.EndsWith("embeddings"))
        {
            EmbeddingCalls++;if(EmbeddingsFail)return new(HttpStatusCode.BadRequest){Content=new StringContent("{}")};
            result=new(){["data"]=new JsonArray(new JsonObject{["embedding"]=new JsonArray(1,0,0)}),["usage"]=new JsonObject{["total_tokens"]=10}};
        }
        else if(body["tools"]!=null)result=AgentResponses.Dequeue();
        else
        {
            string name=body["text"]!["format"]!.S("name");JsonObject value;
            if(name=="page_analysis")
            {
                PageCalls++;value=new(){["text"]=$"第 {PageCalls} 页 运动学 velocity 发票编号 INV-00317 保修 warranty",["summary"]="详细描述位移速度计算和保修信息",["topics"]="物理,运动学,velocity,warranty",["entities"]="Brother",["dates_and_numbers"]="INV-00317 775km",["document_title"]="练习资料",["readability"]="清晰"};
            }
            else {MetadataCalls++;value=new(){["title"]="运动学与保修资料",["summary"]="包含两份独立资料，运动学计算与设备保修证明。",["tags"]="位移,速度,velocity,保修,warranty,INV-00317",["document_date"]="",["mixed_content"]=true};}
            result=Message(value.ToJsonString());
        }
        return new(HttpStatusCode.OK){Content=new StringContent(result.ToJsonString(),Encoding.UTF8,"application/json")};
    }
    public static JsonObject Message(string text)=>new(){["status"]="completed",["output"]=new JsonArray(new JsonObject{["type"]="message",["role"]="assistant",["content"]=new JsonArray(new JsonObject{["type"]="output_text",["text"]=text})})};
}

public class LibraryTests
{
    [Fact] public async Task ScanHandoffSurvivesLostAcknowledgementAndFileRename()
    {
        var f=new LibraryFixture();var coordinator=new CaptureCoordinator(f.Settings,f.Db,f.Docs);
        var request=new JsonObject{["command"]="register_scan",["scanId"]=Guid.NewGuid().ToString("N"),["path"]=f.Pdf(),["scanned"]="2026-09-30T00:22:53.8402090Z",["device"]="Brother test",["source"]="feeder"};
        string id=(await coordinator.Handle(request,CancellationToken.None)).S("id");
        await f.Docs.Move(id,"讲义","学习","test");
        f.Db.Exec("UPDATE scan_submissions SET doc_id='' WHERE scan_id=$i",("$i",request.S("scanId")));
        Assert.Equal(id,(await coordinator.Handle(request,CancellationToken.None)).S("id"));
        Assert.Single(f.Db.Rows("SELECT * FROM documents"));Assert.Single(f.Db.Rows("SELECT * FROM jobs WHERE kind='index'"));
        var metadata=JsonNode.Parse(File.ReadAllText(Path.Combine(f.Docs.Root,".scanarchive-metadata",id+".json")))!;
        Assert.Equal("feeder",metadata["scans"]![0]!.S("source"));Assert.Equal(request.S("scanned"),metadata["scans"]![0]!.S("scanned"));
    }
    [Fact] public async Task DesktopPipeReturnsTheSameRegisteredLibraryAndTrashIsReversible()
    {
        var f=new LibraryFixture();var coordinator=new CaptureCoordinator(f.Settings,f.Db,f.Docs);
        using var bridge=new DesktopBridge(f.Settings,coordinator,Microsoft.Extensions.Logging.Abstractions.NullLogger<DesktopBridge>.Instance);
        await bridge.StartAsync(CancellationToken.None);
        try
        {
            var receipt=await ScanArchive.Integration.DesktopProtocol.Request(f.Settings.DataRoot,new JsonObject{["command"]="register_scan",["scanId"]=Guid.NewGuid().ToString("N"),["path"]=f.Pdf(),["scanned"]=DateTimeOffset.UtcNow.ToString("O")});string id=receipt.S("id");
            await f.Analyzer.Analyze(id,CancellationToken.None);
            var library=await ScanArchive.Integration.DesktopProtocol.Request(f.Settings.DataRoot,new JsonObject{["command"]="library"});Assert.Equal(id,library["documents"]![0]!.S("id"));
            await ScanArchive.Integration.DesktopProtocol.Request(f.Settings.DataRoot,new JsonObject{["command"]="trash",["id"]=id});
            Assert.Empty((await f.Search.Find("INV-00317"))["results"]!.AsArray());
            Assert.Empty((await coordinator.Handle(new JsonObject{["command"]="library"},CancellationToken.None))["documents"]!.AsArray());
            await f.Docs.Restore(id,CancellationToken.None);Assert.NotEmpty((await f.Search.Find("INV-00317"))["results"]!.AsArray());Assert.True(File.Exists(f.Db.Doc(id)!.S("original")));
        }
        finally{await bridge.StopAsync(CancellationToken.None);}
    }
    [Fact] public async Task BackgroundWorkerDoesNotDiscoverUnregisteredFiles()
    {
        var f=new LibraryFixture();f.Pdf();
        using var worker=new Worker(f.Settings,f.Db,f.Docs,f.Analyzer,f.Agent,f.Ai,Microsoft.Extensions.Logging.Abstractions.NullLogger<Worker>.Instance);
        await worker.StartAsync(CancellationToken.None);await Task.Delay(2300);await worker.StopAsync(CancellationToken.None);
        Assert.Empty(f.Db.Rows("SELECT * FROM documents"));Assert.Equal(0,f.Fake.PageCalls);
    }
    [Fact] public async Task MismatchedArchiveRootRejectsHandoffWithoutAcknowledging()
    {
        var f=new LibraryFixture();var coordinator=new CaptureCoordinator(f.Settings,f.Db,f.Docs);
        await Assert.ThrowsAsync<ArgumentException>(()=>coordinator.Handle(new JsonObject{["command"]="register_scan",["scanId"]=Guid.NewGuid().ToString("N"),["path"]=Path.Combine(f.Root,"outside.pdf"),["scanned"]=DateTimeOffset.UtcNow.ToString("O")},CancellationToken.None));
        Assert.Empty(f.Db.Rows("SELECT * FROM scan_submissions"));
    }
    [Fact] public async Task ImportDeduplicatesAndPreservesOriginalAndScanTimestamp()
    {
        var f=new LibraryFixture();string path=f.Pdf();string scan="2026-09-29T23:00:00.0000000Z";string id=await f.Docs.Import(path,scan);
        Assert.Equal(id,await f.Docs.Import(path));var d=f.Db.Doc(id)!;Assert.Equal(scan,d.S("scanned"));Assert.True(File.Exists(d.S("original")));Assert.Single(f.Db.Rows("SELECT * FROM documents"));
        Assert.Equal(File.ReadAllBytes(path),File.ReadAllBytes(d.S("original")));
    }
    [Fact] public async Task MoveAndUndoPreserveIdentityAndRecoverJournalAfterCrash()
    {
        var f=new LibraryFixture();string path=f.Pdf();string id=await f.Docs.Import(path);await f.Docs.Move(id,"运动学练习","学习/物理","test");
        Assert.False(File.Exists(path));Assert.True(File.Exists(f.Db.Doc(id)!.S("path")));var op=f.Db.Rows("SELECT * FROM operations").Single();
        await f.Docs.Undo(op.S("id"),CancellationToken.None);Assert.True(File.Exists(path));Assert.Equal(id,f.Db.Doc(id)!.S("id"));
        await f.Docs.Move(id,"练习","学习","test");op=f.Db.Rows("SELECT * FROM operations WHERE state='applied'").Single();
        f.Db.Exec("UPDATE operations SET state='prepared' WHERE id=$i",("$i",op.S("id")));f.Db.Exec("UPDATE documents SET path=$p WHERE id=$i",("$p",path),("$i",id));
        f.Docs.RecoverMoves();Assert.Equal(op.S("new_path"),f.Db.Doc(id)!.S("path"));
    }
    [Theory][InlineData("../outside")][InlineData("C:/Windows")][InlineData("/absolute")][InlineData("Library/../../escape")][InlineData(".scanarchive-originals/file")]
    public void PathTraversalRejected(string path){var f=new LibraryFixture();Assert.Throws<ArgumentException>(()=>f.Docs.SafePath(path));}
    [Fact] public async Task UserLockedClassificationCannotBeChangedByAgent()
    {
        var f=new LibraryFixture();string id=await f.Docs.Import(f.Pdf());await f.Docs.Move(id,"手动标题","我的分类","manual",true);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Docs.Move(id,"AI标题","别的分类","agent"));
    }
    [Fact] public async Task AnalysisFinishesAllPagesBeforeAgentIsQueuedAndSavesMetadata()
    {
        var f=new LibraryFixture();string id=await f.Docs.Import(f.Pdf());Assert.Empty(f.Db.Rows("SELECT * FROM jobs WHERE kind='organize'"));
        await f.Analyzer.Analyze(id,CancellationToken.None);Assert.Equal(2,f.Fake.PageCalls);Assert.Equal(1,f.Fake.MetadataCalls);
        Assert.Equal(2,f.Db.Doc(id)!.I("page_count"));Assert.Equal("analyzed",f.Db.Doc(id)!.S("status"));Assert.Single(f.Db.Rows("SELECT * FROM jobs WHERE kind='organize'"));
        var metadata=JsonNode.Parse(File.ReadAllText(Path.Combine(f.Settings.Current.LibraryRoot,".scanarchive-metadata",id+".json")))!;
        Assert.Equal(2,metadata["pages"]!.AsArray().Count);Assert.NotEmpty(metadata["document"]!.S("scanned"));
        await f.Analyzer.Analyze(id,CancellationToken.None);Assert.Equal(2,f.Fake.PageCalls);
    }
    [Fact] public async Task SearchFindsChineseExactIdentifierAndSemanticSynonym()
    {
        var f=new LibraryFixture();string id=await f.Docs.Import(f.Pdf());await f.Analyzer.Analyze(id,CancellationToken.None);
        foreach(string query in new[]{"运动学","INV-00317","equipment guarantee"})
        {
            var result=await f.Search.Find(query);Assert.Contains(result["results"]!.AsArray(),x=>x!.S("doc_id")==id);Assert.Equal("hybrid",result.S("mode"));
        }
        Assert.Empty((await f.Search.Find("运动学",from:"2099-01-01"))["results"]!.AsArray());
    }
    [Fact] public async Task KeywordSearchWorksWhenEmbeddingsAreUnavailable()
    {
        var f=new LibraryFixture();string id=await f.Docs.Import(f.Pdf());f.Fake.EmbeddingsFail=true;await f.Analyzer.Analyze(id,CancellationToken.None);
        Assert.NotEmpty((await f.Search.Find("INV-00317"))["results"]!.AsArray());Assert.Single(f.Db.Rows("SELECT * FROM jobs WHERE kind='embeddings'"));
    }
    [Fact] public async Task SplitInheritsScanTimeAndSourcePageRangeWithoutDeletingParent()
    {
        var f=new LibraryFixture();string path=f.Pdf();string id=await f.Docs.Import(path,"2026-01-01T10:20:30.0000000Z");string child=await f.Docs.Split(id,2,2,"独立讲义",CancellationToken.None);
        var d=f.Db.Doc(child)!;Assert.Equal(id,d.S("parent_id"));Assert.Equal("2-2",d.S("source_pages"));Assert.Equal(f.Db.Doc(id)!.S("scanned"),d.S("scanned"));Assert.True(File.Exists(path));Assert.Equal(1,f.Docs.PageCount(d));
    }
    [Fact] public async Task AgentExecutesToolsAndOnlyCitesPagesItRead()
    {
        var f=new LibraryFixture();string id=await f.Docs.Import(f.Pdf());await f.Analyzer.Analyze(id,CancellationToken.None);
        f.Fake.AgentResponses.Enqueue(new JsonObject{["output"]=new JsonArray(new JsonObject{["type"]="function_call",["name"]="read_document",["call_id"]="call_1",["arguments"]=new JsonObject{["id"]=id,["start"]=1,["end"]=1}.ToJsonString()})});
        f.Fake.AgentResponses.Enqueue(FakeApi.Message($"证据 [[{id}:1]] 错误引用 [[{id}:2]]"));
        string answer=await f.Agent.Run("查找资料",null,CancellationToken.None);Assert.Contains($"[[{id}:1]]",answer);Assert.DoesNotContain($"[[{id}:2]]",answer);
        Assert.Contains(f.Fake.Requests,r=>r["input"] is JsonArray items&&items.Any(x=>x?.S("type")=="function_call_output"));
    }
    [Fact] public void DailyScheduleRunsAtFiveAndCatchesUpOnlyOnce()
    {
        var options=new ServerOptions{ScheduleEnabled=true,DailyWakeTime="05:00"};
        Assert.Null(Worker.DueWake(options,null,new DateTime(2026,9,29,4,59,0)));
        Assert.Equal("2026-09-29T05:00",Worker.DueWake(options,null,new DateTime(2026,9,29,5,0,0)));
        Assert.Null(Worker.DueWake(options,"2026-09-29T05:00",new DateTime(2026,9,29,20,0,0)));
        Assert.Equal("2026-09-29T05:00",Worker.DueWake(options,"2026-09-28T05:00",new DateTime(2026,9,29,8,0,0)));
    }
    [Fact] public async Task ApiBudgetIsEnforcedAndSecretsAreEncrypted()
    {
        var f=new LibraryFixture();f.Settings.Save(f.Settings.Current with{DailyRequestLimit=1});await f.Ai.Embed("test",CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Ai.Embed("test",CancellationToken.None));
        Assert.DoesNotContain("test-not-a-real-key",Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(f.Settings.DataRoot,"openai.secret"))));
    }
}
