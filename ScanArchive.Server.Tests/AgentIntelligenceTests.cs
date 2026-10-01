using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ScanArchive.Server;
using Xunit;

namespace ScanArchive.Server.Tests;

public sealed class AgentIntelligenceTests
{
    static JsonObject Call(string name, JsonObject arguments, string callId) => new()
    {
        ["type"] = "function_call", ["name"] = name, ["call_id"] = callId, ["arguments"] = arguments.ToJsonString()
    };
    static JsonObject Calls(params JsonObject[] calls) => new() { ["output"] = new JsonArray(calls.Select(x => (JsonNode)x).ToArray()) };

    static async Task<string> Document(LibraryFixture f, string name = "contract.pdf", int count = 2)
    {
        string id = await f.Docs.Import(f.Pdf(name, count), "2026-10-01T09:00:00.0000000Z");
        for (int page = 1; page <= count; page++)
        {
            string text = $"Contract AX-731 original page {page}. The warranty covers 24 months. Scan time is not the document date.";
            var summary = new JsonObject { ["text"] = text, ["summary"] = "### Evidence\n\n- Warranty 24 months", ["document_title"] = "Contract AX-731" };
            f.Db.Exec("INSERT INTO pages VALUES($i,$p,$t,$s); INSERT INTO analyses VALUES($i,$p,'synthetic-model',$time)",
                ("$i", id), ("$p", page), ("$t", text), ("$s", summary.ToJsonString()), ("$time", Database.Now));
            f.Search.AddChunk(id, page, text, [1, 0, 0], f.Settings.Current.EmbeddingModel);
        }
        f.Db.Exec("UPDATE documents SET status='ready',title='Contract AX-731',summary='24 month warranty',tags='warranty,保修',document_date='2025-06-15',mixed_content=0 WHERE id=$i; UPDATE jobs SET status='done' WHERE payload=$i", ("$i", id));
        return id;
    }

    static JsonObject ToolResult(IEnumerable<JsonObject> requests, string callId)
    {
        var last = requests.Last(r => r["tools"] != null);
        var output = last["input"]!.AsArray().Last(n => n?.S("type") == "function_call_output" && n.S("call_id") == callId)!;
        return JsonNode.Parse(output.S("output"))!.AsObject();
    }

    static IEnumerable<JsonNode> Walk(JsonNode? node)
    {
        if (node == null) yield break;
        yield return node;
        if (node is JsonObject obj)
            foreach (var property in obj)
                foreach (var child in Walk(property.Value)) yield return child;
        if (node is JsonArray array)
            foreach (var item in array)
                foreach (var child in Walk(item)) yield return child;
    }

    [Fact]
    public async Task InspectPageSendsTheRealPageImageAndOnlyVerifiesThatPhysicalPage()
    {
        var f = new LibraryFixture(); string id = await Document(f);
        string before = f.Db.Rows("SELECT * FROM pages WHERE doc_id=$i ORDER BY number", ("$i", id)).ToJson();
        f.Fake.AgentResponses.Enqueue(Calls(Call("inspect_page", new() { ["id"] = id, ["page"] = 1 }, "vision")));
        f.Fake.AgentResponses.Enqueue(FakeApi.Message($"第一页证据 [[{id}:1]]。未读第二页 [[{id}:2]]。"));
        string answer = await f.Agent.Run("查看这份扫描首页的真实图像。", null, default);
        Assert.Contains($"[[{id}:1]]", answer); Assert.DoesNotContain($"[[{id}:2]]", answer);
        Assert.False(ToolResult(f.Fake.Requests, "vision").ContainsKey("error"));
        var next = f.Fake.Requests.Last(r => r["tools"] != null);
        var image = Assert.Single(Walk(next["input"]).OfType<JsonObject>(), n => n.S("type") == "input_image");
        Assert.StartsWith("data:image/", image.S("image_url"));
        Assert.True(image.S("image_url").Length > 100);
        Assert.Equal(before, f.Db.Rows("SELECT * FROM pages WHERE doc_id=$i ORDER BY number", ("$i", id)).ToJson());
    }

    [Theory]
    [InlineData("research_documents")]
    [InlineData("find_in_document")]
    [InlineData("compare_documents")]
    [InlineData("audit_library")]
    public async Task DiscoveryAndComparisonAreNotSourcePageVerification(string name)
    {
        var f = new LibraryFixture(); string id = await Document(f); string second = await Document(f, "other.pdf");
        JsonObject arguments = name switch
        {
            "research_documents" => new() { ["queries"] = "AX-731\nwarranty", ["category"] = "", ["from"] = "", ["to"] = "", ["date_field"] = "scanned", ["limit"] = 8, ["offset"] = 0 },
            "find_in_document" => new() { ["id"] = id, ["query"] = "warranty", ["offset"] = 0, ["limit"] = 8 },
            "compare_documents" => new() { ["ids"] = id + "," + second },
            _ => new() { ["focus"] = "all", ["offset"] = 0, ["limit"] = 8 }
        };
        f.Fake.AgentResponses.Enqueue(Calls(Call(name, arguments, "discovery")));
        f.Fake.AgentResponses.Enqueue(FakeApi.Message($"发现相关内容，但这条引用未经原页核实 [[{id}:1]]。"));
        string answer = await f.Agent.Run("查找保修资料。", null, default);
        Assert.False(ToolResult(f.Fake.Requests, "discovery").ContainsKey("error"));
        Assert.DoesNotContain($"[[{id}:1]]", answer);
        Assert.Empty(f.Db.Rows("SELECT * FROM operations"));
    }

    [Fact]
    public async Task ComparingDocumentsDoesNotAuthorizePhysicalMergeWithoutPageReads()
    {
        var f = new LibraryFixture(); string one = await Document(f, "front.pdf", 1), two = await Document(f, "back.pdf", 1);
        f.Fake.AgentResponses.Enqueue(Calls(Call("compare_documents", new() { ["ids"] = one + "," + two }, "compare")));
        f.Fake.AgentResponses.Enqueue(Calls(Call("merge_documents", new() { ["page_plan"] = one + ":1," + two + ":1", ["title"] = "Combined", ["reason"] = "Unverified metadata similarity" }, "merge")));
        f.Fake.AgentResponses.Enqueue(FakeApi.Message("检查结束。"));
        await f.Agent.Run("核实并合并正反面。", null, default);
        Assert.False(ToolResult(f.Fake.Requests, "compare").ContainsKey("error"));
        Assert.Contains("read_document", ToolResult(f.Fake.Requests, "merge").S("error"));
        Assert.Empty(f.Db.Rows("SELECT * FROM document_merges"));
        Assert.Equal(2, f.Db.Rows("SELECT id FROM documents").Count);
    }

    [Theory]
    [InlineData("read_document")]
    [InlineData("inspect_page")]
    public async Task MergeMustWaitForTheModelToReceiveEvidenceFromThePreviousRound(string reader)
    {
        var f = new LibraryFixture(); string one = await Document(f, "front.pdf", 1), two = await Document(f, "back.pdf", 1);
        JsonObject ReadArgs(string id) => reader == "read_document" ? new() { ["id"] = id, ["start"] = 1, ["end"] = 1 } : new() { ["id"] = id, ["page"] = 1 };
        f.Fake.AgentResponses.Enqueue(Calls(
            Call(reader, ReadArgs(one), "front"), Call(reader, ReadArgs(two), "back"),
            Call("merge_documents", new() { ["page_plan"] = one + ":1," + two + ":1", ["title"] = "Premature merge", ["reason"] = "The model has not received either page yet" }, "merge")));
        f.Fake.AgentResponses.Enqueue(FakeApi.Message($"现在已收到首页证据 [[{one}:1]]，尚未合并。"));
        string answer = await f.Agent.Run("核对之后再合并。", null, default);
        Assert.False(ToolResult(f.Fake.Requests, "front").ContainsKey("error"));
        Assert.False(ToolResult(f.Fake.Requests, "back").ContainsKey("error"));
        Assert.True(ToolResult(f.Fake.Requests, "merge").ContainsKey("error"));
        Assert.Empty(f.Db.Rows("SELECT * FROM document_merges"));
        Assert.Contains($"[[{one}:1]]", answer);
    }

    [Fact]
    public async Task ResearchCanDiscoverMetadataWhenTheDocumentHasNoSearchChunks()
    {
        var f = new LibraryFixture(); string id = await Document(f);
        f.Search.Clear(id);
        f.Db.Exec("UPDATE documents SET title='WARRANTY-AX7 metadata-only certificate' WHERE id=$i", ("$i", id));
        var result = await f.Agent.Execute("research_documents", new() { ["queries"] = "WARRANTY-AX7\ncertificate", ["category"] = "", ["from"] = "", ["to"] = "", ["date_field"] = "scanned", ["limit"] = 8, ["offset"] = 0 }, default);
        Assert.Contains(id, result.ToJsonString());
        Assert.Empty(f.Db.Rows("SELECT * FROM chunks WHERE doc_id=$i", ("$i", id)));
        Assert.Empty(f.Db.Rows("SELECT * FROM jobs WHERE status='pending'"));
    }

    [Fact]
    public async Task ResearchDocumentDateFilterDoesNotConfuseTheLaterScanDate()
    {
        var f = new LibraryFixture(); string id = await Document(f);
        JsonObject Arguments(string dateField) => new() { ["queries"] = "AX-731", ["category"] = "", ["from"] = "2025-06-01", ["to"] = "2025-06-30", ["date_field"] = dateField, ["limit"] = 8, ["offset"] = 0 };
        var documentDate = await f.Agent.Execute("research_documents", Arguments("document_date"), default);
        var scanDate = await f.Agent.Execute("research_documents", Arguments("scanned"), default);
        Assert.Contains(id, documentDate.ToJsonString()); Assert.DoesNotContain(id, scanDate.ToJsonString());
    }

    [Fact]
    public async Task ComparisonResolvesRangeBasedSplitProvenance()
    {
        var f = new LibraryFixture(); string parent = await Document(f, count: 3);
        string child = await f.Docs.ExtractPages(parent, "2-3", "Range fragment", default);
        Assert.Equal("2-3", f.Db.Doc(child)!.S("source_pages"));
        var result = await f.Agent.Execute("compare_documents", new() { ["ids"] = parent + "," + child }, default);
        var profile = Assert.Single(result["documents"]!.AsArray(), d => d!.S("id") == child)!;
        Assert.Equal(new[] { 2, 3 }, profile["page_map"]!.AsArray().Select(p => p!["scan_origin"]!.I("source_page")).ToArray());
        Assert.All(profile["page_map"]!.AsArray(), p => Assert.Equal(parent, p!["scan_origin"]!.S("source_doc_id")));
        Assert.True(Assert.Single(result["pairs"]!.AsArray())!["shared_scan_origin"]!.GetValue<bool>());
    }

    [Fact]
    public async Task PlanProgressIsScopedToTheCurrentConversation()
    {
        var f = new LibraryFixture(); string conversation = Guid.NewGuid().ToString("N");
        f.Db.Exec("INSERT INTO conversations VALUES($i,'Find warranty',$t); INSERT INTO messages(conversation,role,text,created) VALUES($i,'user','查找保修条款',$t)", ("$i", conversation), ("$t", Database.Now));
        f.Fake.AgentResponses.Enqueue(Calls(Call("update_work_plan", new() { ["goal"] = "核实保修条款", ["steps"] = "检索候选\n检查原页\n回答并引用", ["completed"] = 1, ["next"] = "检查原页" }, "plan")));
        f.Fake.AgentResponses.Enqueue(FakeApi.Message("本轮已完成候选检索。"));
        await f.Agent.Run("", conversation, default);
        Assert.False(ToolResult(f.Fake.Requests, "plan").ContainsKey("error"));
        string progress = Assert.IsType<string>(f.Db.State("agent_progress:" + conversation));
        Assert.Contains("核实保修条款", JsonNode.Parse(progress)!.ToJsonString(new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        Assert.Null(f.Db.State("agent_progress:background"));
        Assert.Empty(f.Db.Rows("SELECT * FROM operations"));
    }

    [Fact]
    public async Task QueuedIndexRepairIsReportedAsPendingRatherThanAlreadyRepaired()
    {
        var f = new LibraryFixture(); string id = await Document(f);
        f.Fake.AgentResponses.Enqueue(Calls(Call("repair_search_index", new() { ["id"] = id }, "repair")));
        f.Fake.AgentResponses.Enqueue(FakeApi.Message("已提交索引修复任务。"));
        string answer = await f.Agent.Run("修复这个文件的搜索索引。", null, default);
        var result = ToolResult(f.Fake.Requests, "repair");
        string job = result.S("job"); Assert.False(string.IsNullOrWhiteSpace(job));
        Assert.Equal("pending", Assert.Single(f.Db.Rows("SELECT status FROM jobs WHERE id=$i", ("$i", job))).S("status"));
        Assert.Contains("0 已完成", answer); Assert.Contains("1 等待或执行中", answer);
        Assert.Contains("排队不代表修复完成", answer);
        Assert.Equal(0, f.Fake.PageCalls);
    }

    [Fact]
    public async Task OversizedToolBatchExecutesOnlyEightMutationsAndReturnsRejectedResults()
    {
        var f = new LibraryFixture();
        f.Fake.AgentResponses.Enqueue(Calls(Enumerable.Range(0, 10).Select(n => Call("create_category", new() { ["category"] = "Budget/C" + n }, "category" + n)).ToArray()));
        f.Fake.AgentResponses.Enqueue(FakeApi.Message("已处理本轮允许的分类。"));
        await f.Agent.Run("创建测试分类。", null, default);
        Assert.Equal(8, Directory.GetDirectories(Path.Combine(f.Docs.Root, "Library", "Budget")).Length);
        Assert.All(Enumerable.Range(0, 8), n => Assert.False(ToolResult(f.Fake.Requests, "category" + n).ContainsKey("error")));
        Assert.All(new[] { 8, 9 }, n => Assert.True(ToolResult(f.Fake.Requests, "category" + n).ContainsKey("error")));
    }

    [Fact]
    public async Task TotalToolBudgetStopsFurtherMutationsAndPublishesLimitedProgress()
    {
        var f = new LibraryFixture(); f.Settings.Save(f.Settings.Current with { AgentMaxSteps = 3 });
        for (int round = 0; round < 3; round++)
        {
            int first = round * 8;
            f.Fake.AgentResponses.Enqueue(Calls(Enumerable.Range(first, 8).Select(n => Call("create_category", new() { ["category"] = "Budget/C" + n }, "category" + n)).ToArray()));
        }
        string answer = await f.Agent.Run("创建测试分类。", null, default);
        Assert.Equal(20, Directory.GetDirectories(Path.Combine(f.Docs.Root, "Library", "Budget")).Length);
        Assert.Contains("操作预算", answer);
        Assert.Equal("limited", JsonNode.Parse(f.Db.State("agent_progress:background")!)!.S("status"));
    }

    [Fact]
    public async Task ReadOnlyToolBatchUsesBoundedParallelismAndCorrelatesAllResults()
    {
        var f = new LibraryFixture(); await Document(f); using var handler = new BlockingAgentApi();
        var ai = new OpenAi(f.Settings, f.Db, new HttpClient(handler)); var search = new Search(f.Settings, f.Db, ai);
        var agent = new SecretaryAgent(f.Settings, f.Db, f.Docs, search, ai);
        handler.Responses.Enqueue(Calls(Enumerable.Range(0, 6).Select(n => Call("search_documents", new() { ["query"] = "query-" + n, ["category"] = "", ["from"] = "", ["to"] = "" }, "q" + n)).ToArray()));
        handler.Responses.Enqueue(FakeApi.Message("检索结束。"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Task<string> run = agent.Run("比较不同检索线索。", null, timeout.Token);
        try
        {
            await handler.ThreeEntered.Task.WaitAsync(timeout.Token);
            Assert.Equal(3, Volatile.Read(ref handler.Maximum));
            handler.Release.TrySetResult(); await run;
            Assert.All(Enumerable.Range(0, 6), n => Assert.False(ToolResult(handler.Requests, "q" + n).ContainsKey("error")));
            Assert.True(handler.Requests.First(r => r["tools"] != null)["parallel_tool_calls"]!.GetValue<bool>());
            Assert.InRange(handler.Maximum, 2, 3);
        }
        finally { handler.Release.TrySetResult(); timeout.Cancel(); try { await run; } catch (OperationCanceledException) { } }
    }

    [Fact]
    public async Task MixedToolBatchDoesNotMoveAFileWhileThePriorReadIsWaiting()
    {
        var f = new LibraryFixture(); string id = await Document(f); using var handler = new BlockingAgentApi();
        var ai = new OpenAi(f.Settings, f.Db, new HttpClient(handler)); var search = new Search(f.Settings, f.Db, ai);
        var agent = new SecretaryAgent(f.Settings, f.Db, f.Docs, search, ai);
        handler.Responses.Enqueue(Calls(
            Call("search_documents", new() { ["query"] = "warranty", ["category"] = "", ["from"] = "", ["to"] = "" }, "read"),
            Call("move_document", new() { ["id"] = id, ["title"] = "Confirmed warranty", ["category"] = "Warranty", ["reason"] = "User-authorized organization" }, "move")));
        handler.Responses.Enqueue(FakeApi.Message("已整理标题。"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Task<string> run = agent.Run("查找并整理这份保修资料。", null, timeout.Token);
        try
        {
            await handler.Entered.Task.WaitAsync(timeout.Token);
            Assert.Equal("Contract AX-731", f.Db.Doc(id)!.S("title"));
            Assert.Empty(f.Db.Rows("SELECT * FROM operations"));
            handler.Release.TrySetResult(); await run;
            Assert.Equal("Confirmed warranty", f.Db.Doc(id)!.S("title"));
            Assert.Equal("", ToolResult(handler.Requests, "move").S("error"));
        }
        finally { handler.Release.TrySetResult(); timeout.Cancel(); try { await run; } catch (OperationCanceledException) { } }
    }

    sealed class BlockingAgentApi : HttpMessageHandler
    {
        public readonly Queue<JsonObject> Responses = new();
        public readonly List<JsonObject> Requests = new();
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource ThreeEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Maximum;
        int current;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject(); lock (Requests) Requests.Add(body);
            JsonObject result;
            if (request.RequestUri!.AbsolutePath.EndsWith("embeddings"))
            {
                int active = Interlocked.Increment(ref current);
                int before; do { before = Maximum; } while (active > before && Interlocked.CompareExchange(ref Maximum, active, before) != before);
                Entered.TrySetResult(); if (active >= 3) ThreeEntered.TrySetResult();
                try { await Release.Task.WaitAsync(ct); }
                finally { Interlocked.Decrement(ref current); }
                result = new() { ["data"] = new JsonArray(new JsonObject { ["embedding"] = new JsonArray(1, 0, 0) }), ["usage"] = new JsonObject { ["total_tokens"] = 5 } };
            }
            else { lock (Responses) result = Responses.Dequeue(); }
            return new(HttpStatusCode.OK) { Content = new StringContent(result.ToJsonString(), Encoding.UTF8, "application/json") };
        }
    }
}
