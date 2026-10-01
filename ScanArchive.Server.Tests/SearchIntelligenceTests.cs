using ScanArchive.Server;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace ScanArchive.Server.Tests;

public class SearchIntelligenceTests
{
    static string Doc(LibraryFixture f,string title,string summary="",int count=1,string scanned="2026-10-01T10:00:00Z",string date="",string category="")
    {
        string id=Guid.NewGuid().ToString("N");
        f.Db.Exec("INSERT INTO documents(id,hash,path,original,title,summary,scanned,document_date,category,page_count,status,created) VALUES($i,$i,'test.pdf','test.pdf',$t,$s,$scan,$date,$cat,$count,'ready',$scan)",("$i",id),("$t",title),("$s",summary),("$scan",scanned),("$date",date),("$cat",category),("$count",count));return id;
    }
    static void Page(LibraryFixture f,string id,int page,string text,bool indexed=false,float[]? vector=null)
    {
        f.Db.Exec("INSERT INTO pages(doc_id,number,text,summary) VALUES($i,$p,$t,'')",("$i",id),("$p",page),("$t",text));
        if(indexed)f.Search.AddChunk(id,page,text,vector,vector==null?"":f.Settings.Current.EmbeddingModel);
    }
    static JsonArray Results(JsonObject result)=>result["results"]!.AsArray();

    [Theory]
    [InlineData("INV-00317","Receipt ＩＮＶ－００３１７")]
    [InlineData("INV00317","Receipt INV / 00317")]
    [InlineData("INV 00317","Receipt INV00317")]
    [InlineData("INV–00317","Receipt INV-00317")]
    public async Task IdentifiersNormalizeWidthPunctuationAndSpacingWithoutSubstringMatches(string query,string text)
    {
        var f=new LibraryFixture();f.Fake.EmbeddingsFail=true;
        string wanted=Doc(f,"target"),wrong=Doc(f,"competing");Page(f,wanted,1,text);Page(f,wrong,1,"Receipt INV-003170 and XINV00317");
        var result=await f.Search.Research(query);
        Assert.Equal(wanted,Assert.Single(Results(result))!.S("doc_id"));
        Assert.Contains(Results(result)[0]!["match_reasons"]!.AsArray(),r=>r!.ToString()=="exact_identifier");
    }
    [Fact] public async Task NumericLookupDoesNotMatchLongerIdentifiersEvenWithSemanticSimilarity()
    {
        var f=new LibraryFixture();string exact=Doc(f,"Exact"),wrong=Doc(f,"Wrong");
        Page(f,exact,1,"Order number 36610",true,[1,0,0]);Page(f,wrong,1,"Order number 1366109",true,[1,0,0]);
        Assert.Equal(exact,Assert.Single(Results(await f.Search.Research("36610")))!.S("doc_id"));
    }
    [Fact] public async Task ExactIdentifierOutranksSemanticallySimilarDocuments()
    {
        var f=new LibraryFixture();string wrong=Doc(f,"Vehicle sale"),exact=Doc(f,"Actual record");
        Page(f,wrong,1,"Vehicle sale order 99999",true,[1,0,0]);Page(f,exact,1,"Order 36610 vehicle purchase",true,[0,1,0]);
        var result=await f.Search.Research("my vehicle order 36610");
        Assert.Equal(exact,Results(result)[0]!.S("doc_id"));Assert.Equal("hybrid",result.S("mode"));
    }
    [Fact] public async Task ManyPagesInOneDocumentCannotHideAnotherOlderDocument()
    {
        var f=new LibraryFixture();f.Fake.EmbeddingsFail=true;
        string large=Doc(f,"Large",count:125),old=Doc(f,"Older",scanned:"2000-01-01T00:00:00Z");
        for(int i=1;i<=125;i++)Page(f,large,i,"shared scientific material",true);
        Page(f,old,1,"shared scientific material",true);
        var result=await f.Search.Research("scientific material",limit:2);
        Assert.Equal(2,Results(result).Count);Assert.Contains(Results(result),r=>r!.S("doc_id")==old);
        var big=Results(result).Single(r=>r!.S("doc_id")==large)!;Assert.Equal(125,big.I("matched_page_count"));Assert.True(big["pages_truncated"]!.GetValue<bool>());Assert.Equal(5,big["pages"]!.AsArray().Count);
    }
    [Fact] public async Task MetadataWithoutAnyPageIndexIsDiscoverableButHasNoPageEvidence()
    {
        var f=new LibraryFixture();f.Fake.EmbeddingsFail=true;string id=Doc(f,"家庭资料","保险单编号 POLICY-4182",count:4);
        var hit=Assert.Single(Results(await f.Search.Research("POLICY-4182")))!;
        Assert.Equal(id,hit.S("doc_id"));Assert.Equal(0,hit.I("page"));Assert.Equal("",hit.S("citation"));Assert.Equal("document",hit.S("match_scope"));Assert.Empty(hit["pages"]!.AsArray());
        var inside=f.Search.FindInDocument(id,"POLICY-4182");Assert.Empty(Results(inside));Assert.Equal(4,inside.I("missing_text_pages"));
    }
    [Fact] public async Task UnindexedOcrAndSummaryAreSearchedWhileIncompleteCoverageIsReported()
    {
        var f=new LibraryFixture();f.Fake.EmbeddingsFail=true;string id=Doc(f,"Untitled",count:4);
        Page(f,id,3,"The uncommon warranty claim is reference WTY-59382");
        f.Db.Exec("UPDATE pages SET summary=$s WHERE doc_id=$i",("$i",id),("$s","{\"summary\":\"压缩机延长保修\",\"entities\":\"Brother\"}"));
        foreach(string query in new[]{"WTY-59382","压缩机延长保修"})
        {
            var result=await f.Search.Research(query);var hit=Assert.Single(Results(result))!;Assert.Equal(3,hit.I("page"));Assert.Equal(id+":3",hit.S("citation"));
            Assert.Equal(3,result["coverage"]!.I("missing_text_pages"));Assert.Equal(0,result["coverage"]!.I("indexed_pages"));Assert.NotEmpty(result.S("warning"));
        }
    }
    [Fact] public async Task DateFieldAndCategoryFiltersUseExplicitSemantics()
    {
        var f=new LibraryFixture();f.Fake.EmbeddingsFail=true;
        string scan=Doc(f,"Warranty",scanned:"2026-10-01T00:00:00Z",date:"2020-01-01",category:"家庭/设备");
        string date=Doc(f,"Warranty",scanned:"2020-01-01T00:00:00Z",date:"2026-10-01",category:"家庭/设备");
        string unknown=Doc(f,"Warranty",scanned:"2020-01-01T00:00:00Z",category:"家庭/设备");
        Assert.Equal(scan,Assert.Single(Results(await f.Search.Research("Warranty",category:"家庭",from:"2026-10-01",to:"2026-10-01")))!.S("doc_id"));
        Assert.Equal(date,Assert.Single(Results(await f.Search.Research("Warranty",category:"家庭",from:"2026-10-01",to:"2026-10-01",dateField:"document_date")))!.S("doc_id"));
        Assert.DoesNotContain(Results(await f.Search.Research("Warranty",to:"2026-10-01",dateField:"document_date")),r=>r!.S("doc_id")==unknown);
        Assert.Empty(Results(await f.Search.Research("Warranty",category:"家庭/设")));
        await Assert.ThrowsAsync<ArgumentException>(()=>f.Search.Research("Warranty",dateField:"created"));
        await Assert.ThrowsAsync<ArgumentException>(()=>f.Search.Research("Warranty",from:"tomorrow"));
    }
    [Fact] public async Task MultiQueryFusionKeepsComplementaryHitsAndPaginationHasNoDuplicates()
    {
        var f=new LibraryFixture();f.Fake.EmbeddingsFail=true;
        string both=Doc(f,"Both"),one=Doc(f,"English"),two=Doc(f,"Chinese");
        Page(f,both,1,"equipment guarantee 设备保修");Page(f,one,1,"equipment guarantee");Page(f,two,1,"设备保修");
        var first=await f.Search.Research("equipment guarantee\n设备保修",limit:2);var second=await f.Search.Research("equipment guarantee\n设备保修",limit:2,offset:first.I("next_offset"));
        Assert.Equal(both,Results(first)[0]!.S("doc_id"));Assert.Equal(2,Results(first)[0]!.I("matched_query_count"));Assert.Equal(3,first.I("total"));Assert.True(first["has_more"]!.GetValue<bool>());Assert.False(second["has_more"]!.GetValue<bool>());
        Assert.Equal(3,Results(first).Concat(Results(second)).Select(r=>r!.S("doc_id")).Distinct().Count());
        await Assert.ThrowsAsync<ArgumentException>(()=>f.Search.Research("a\nb\nc\nd\ne\nf\ng"));
    }
    [Fact] public void FindWithinDocumentSearchesAllPagesAndPaginatesActualPageNumbers()
    {
        var f=new LibraryFixture();string id=Doc(f,"Summary only needle",count:7);
        foreach(int i in new[]{1,3,7})Page(f,id,i,"needle on this page");Page(f,id,2,"Different material");
        var first=f.Search.FindInDocument(id,"needle",limit:2);var last=f.Search.FindInDocument(id,"needle",offset:first.I("next_offset"),limit:2);
        Assert.Equal(new[]{1,3},Results(first).Select(r=>r!.I("page")));Assert.Equal(7,Assert.Single(Results(last))!.I("page"));Assert.Equal(3,first.I("total"));Assert.Equal(4,first.I("searched_pages"));
    }
    [Fact] public async Task InvalidOrWrongModelEmbeddingsDoNotRemoveKeywordResults()
    {
        var f=new LibraryFixture();string id=Doc(f,"Record");Page(f,id,1,"special long warranty",true,[1,0,0]);
        f.Db.Exec("UPDATE chunks SET embedding='broken'");var malformed=await f.Search.Research("warranty");Assert.Single(Results(malformed));Assert.Contains("无效",malformed.S("warning"));
        f.Db.Exec("UPDATE chunks SET embedding='[1,0,0]',model='old-model'");var mismatch=await f.Search.Research("warranty");Assert.Single(Results(mismatch));Assert.Equal(0,mismatch["coverage"]!.I("semantic_pages"));
    }
    [Fact] public async Task DeletedAndSupersededDocumentsAreExcludedFromDiscovery()
    {
        var f=new LibraryFixture();f.Fake.EmbeddingsFail=true;string live=Doc(f,"Warranty"),deleted=Doc(f,"Warranty"),replaced=Doc(f,"Warranty");
        f.Db.Exec("UPDATE documents SET status='deleted' WHERE id=$i",("$i",deleted));f.Db.Exec("UPDATE documents SET status='superseded' WHERE id=$i",("$i",replaced));
        Assert.Equal(live,Assert.Single(Results(await f.Search.Research("Warranty")))!.S("doc_id"));
    }
    [Fact] public async Task EmbeddingQueriesRunWithAtMostThreeConcurrentRequests()
    {
        var f=new LibraryFixture();string id=Doc(f,"test");Page(f,id,1,"sample content",true,[1,0,0]);using var handler=new ConcurrentEmbeddings();using var http=new HttpClient(handler);
        var search=new Search(f.Settings,f.Db,new OpenAi(f.Settings,f.Db,http));
        var pending=search.Research("one\ntwo\nthree\nfour\nfive\nsix");
        await handler.ThreeEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));Assert.Equal(3,handler.Maximum);handler.Release.TrySetResult();
        var result=await pending;Assert.Equal(6,result["coverage"]!.I("semantic_queries_completed"));Assert.Equal(3,handler.Maximum);
    }
    [Fact] public async Task ExactMetadataMatchDoesNotBorrowCitationFromWeakSemanticPage()
    {
        var f=new LibraryFixture();string id=Doc(f,"Invoice INV-88021");Page(f,id,1,"Unrelated photographic landscape",true,[1,0,0]);
        var hit=Assert.Single(Results(await f.Search.Research("find invoice INV-88021")))!;
        Assert.Equal("document",hit.S("match_scope"));Assert.Equal(0,hit.I("page"));Assert.Equal("",hit.S("citation"));
        Assert.Contains(hit["match_reasons"]!.AsArray(),r=>r!.ToString()=="exact_identifier");
        Assert.Single(hit["pages"]!.AsArray());Assert.True(hit["pages"]![0]!["requires_original_verification"]!.GetValue<bool>());
    }
    [Fact] public async Task ActualMatchingPageCanOutrankMetadataAndRetainsItsCitation()
    {
        var f=new LibraryFixture();string id=Doc(f,"Warranty claim");Page(f,id,1,"Warranty claim reference WTY-59382",true,[1,0,0]);
        var hit=Assert.Single(Results(await f.Search.Research("warranty claim WTY-59382")))!;
        Assert.Equal("page",hit.S("match_scope"));Assert.Equal(id+":1",hit.S("citation"));
    }
    [Fact] public async Task ZeroStoredVectorsDoNotCountAsSemanticCoverage()
    {
        var f=new LibraryFixture();string id=Doc(f,"Warranty");Page(f,id,1,"Warranty claim",true,[0,0,0]);
        var result=await f.Search.Research("warranty claim");Assert.Single(Results(result));
        Assert.Equal(0,result["coverage"]!.I("semantic_pages"));Assert.Equal(0,result["coverage"]!.I("semantic_chunks"));Assert.Contains("无效",result.S("warning"));
    }
    [Fact] public async Task ZeroQueryVectorFallsBackToKeywordMode()
    {
        var f=new LibraryFixture();string id=Doc(f,"Warranty");Page(f,id,1,"Warranty claim",true,[1,0,0]);
        using var http=new HttpClient(new ZeroEmbeddings());var search=new Search(f.Settings,f.Db,new OpenAi(f.Settings,f.Db,http));
        var result=await search.Research("warranty claim");Assert.Single(Results(result));Assert.Equal("keyword",result.S("mode"));
        Assert.Equal(0,result["coverage"]!.I("semantic_queries_completed"));Assert.Contains("查询语义向量无效",result.S("warning"));
    }
    sealed class ZeroEmbeddings:HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
            =>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("{\"data\":[{\"embedding\":[0,0,0]}]}",Encoding.UTF8,"application/json")});
    }
    sealed class ConcurrentEmbeddings:HttpMessageHandler
    {
        int current,maximum;
        public int Maximum=>Volatile.Read(ref maximum);
        public TaskCompletionSource ThreeEntered=new(TaskCreationOptions.RunContinuationsAsynchronously),Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            int entered=Interlocked.Increment(ref current);int seen;
            do{seen=maximum;if(seen>=entered)break;}while(Interlocked.CompareExchange(ref maximum,entered,seen)!=seen);
            if(entered==3)ThreeEntered.TrySetResult();
            try{await Release.Task.WaitAsync(ct);return new(HttpStatusCode.OK){Content=new StringContent("{\"data\":[{\"embedding\":[1,0,0]}]}",Encoding.UTF8,"application/json")};}
            finally{Interlocked.Decrement(ref current);}
        }
    }
}
