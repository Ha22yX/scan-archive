using ScanArchive.Server;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Xunit;

namespace ScanArchive.Server.Tests;

public class CrossScanTests
{
    const string FirstScan = "2026-10-01T09:01:00.0000000Z";
    const string SecondScan = "2026-10-01T09:03:00.0000000Z";

    static async Task<string> AddScan(LibraryFixture f, string name, int count, string scanned)
    {
        var receipt = await new CaptureCoordinator(f.Settings, f.Db, f.Docs).Handle(new JsonObject
        {
            ["command"] = "register_scan", ["scanId"] = Guid.NewGuid().ToString("N"),
            ["path"] = f.Pdf(name, count), ["scanned"] = scanned, ["device"] = "Synthetic scanner", ["source"] = "feeder"
        }, default);
        string id = receipt.S("id");
        CompleteAnalysis(f, id, name, count, scanned);
        return id;
    }

    static void CompleteAnalysis(LibraryFixture f, string id, string name, int count, string scanned)
    {
        for (int page = 1; page <= count; page++)
        {
            string text = $"Synthetic {name} page {page}: contract REF-731, page-specific evidence.";
            var analysis = new JsonObject { ["text"] = text, ["summary"] = $"### {name}\n\n- Page {page}", ["document_title"] = "Synthetic contract REF-731" };
            f.Db.Exec("INSERT INTO pages(doc_id,number,text,summary) VALUES($i,$p,$t,$s); INSERT INTO analyses(doc_id,page,model,analyzed_at) VALUES($i,$p,'synthetic-model',$time)",
                ("$i", id), ("$p", page), ("$t", text), ("$s", analysis.ToJsonString()), ("$time", scanned));
        }
        f.Db.Exec("UPDATE documents SET status='analyzed',title='Synthetic contract REF-731',summary='Complete synthetic evidence',mixed_content=0 WHERE id=$i; UPDATE jobs SET status='done' WHERE payload=$i; INSERT INTO analyses(doc_id,page,model,analyzed_at) VALUES($i,0,'synthetic-model',$time)",
            ("$i", id), ("$time", scanned));
        f.Docs.WriteMetadata(id);
    }

    static async Task<(LibraryFixture fixture, string front, string back)> TwoScans(int pages = 2)
    {
        var f = new LibraryFixture();
        string front = await AddScan(f, "fronts.pdf", pages, FirstScan);
        string back = await AddScan(f, "backs.pdf", pages, SecondScan);
        return (f, front, back);
    }

    static string Plan(string front, string back) => $"{front}:1,{back}:2,{front}:2,{back}:1";
    static byte[] Hash(string file) => SHA256.HashData(File.ReadAllBytes(file));
    static void Ready(LibraryFixture f, string id) => f.Db.Exec("UPDATE documents SET status='ready' WHERE id=$i; UPDATE jobs SET status='done' WHERE payload=$i", ("$i", id));

    [Fact]
    public async Task ReversedBackStackBecomesOrderedPdfWithEveryScanTimeAndPageAnalysisRetained()
    {
        var (f, front, back) = await TwoScans();
        var frontDoc = f.Db.Doc(front)!; var backDoc = f.Db.Doc(back)!;
        var sources = new[] { (front, 1, FirstScan), (back, 2, SecondScan), (front, 2, FirstScan), (back, 1, SecondScan) };
        var originalHashes = new[] { Hash(frontDoc.S("original")), Hash(backDoc.S("original")) };

        string merged = await f.Docs.MergeDocuments(Plan(front, back), "Complete synthetic contract", "Same identifier and complementary numbered pages", default);
        var output = f.Db.Doc(merged)!;
        Assert.Equal(4, f.Docs.PageCount(output));
        Assert.Equal(FirstScan, output.S("scanned"));
        Assert.Equal("queued", output.S("status"));
        Assert.Single(f.Db.Rows("SELECT id FROM jobs WHERE kind='index' AND payload=$i AND status='pending'", ("$i", merged)));

        var provenance = f.Db.Rows("SELECT * FROM document_sources WHERE merged_doc_id=$i ORDER BY merged_page", ("$i", merged));
        Assert.Equal(4, provenance.Count);
        for (int n = 0; n < sources.Length; n++)
        {
            var (source, page, scanned) = sources[n];
            Assert.Equal(n + 1, provenance[n].I("merged_page"));
            Assert.Equal(source, provenance[n].S("source_doc_id"));
            Assert.Equal(page, provenance[n].I("source_page"));
            Assert.Equal(scanned, provenance[n].S("scanned"));
            Assert.Equal(f.Db.Rows("SELECT text,summary FROM pages WHERE doc_id=$i AND number=$p", ("$i", source), ("$p", page))[0].ToJsonString(),
                f.Db.Rows("SELECT text,summary FROM pages WHERE doc_id=$i AND number=$p", ("$i", merged), ("$p", n + 1))[0].ToJsonString());
            Assert.Equal(f.Db.Rows("SELECT model,analyzed_at FROM analyses WHERE doc_id=$i AND page=$p", ("$i", source), ("$p", page))[0].ToJsonString(),
                f.Db.Rows("SELECT model,analyzed_at FROM analyses WHERE doc_id=$i AND page=$p", ("$i", merged), ("$p", n + 1))[0].ToJsonString());
            Assert.Equal(f.Docs.PageImage(f.Db.Doc(source)!, page), f.Docs.PageImage(output, n + 1));
        }

        Assert.Equal(originalHashes[0], Hash(frontDoc.S("original")));
        Assert.Equal(originalHashes[1], Hash(backDoc.S("original")));
        Assert.True(File.Exists(frontDoc.S("path"))); Assert.True(File.Exists(backDoc.S("path")));
        Assert.Equal(2, f.Db.Rows("SELECT * FROM scan_submissions").Count);
        Assert.Equal(0, f.Fake.PageCalls);

        var metadata = JsonNode.Parse(File.ReadAllText(Path.Combine(f.Docs.Root, ".scanarchive-metadata", merged + ".json")))!;
        Assert.Equal(4, metadata["document_sources"]!.AsArray().Count);
        Assert.Equal(new[] { FirstScan, SecondScan }, metadata["scans"]!.AsArray().Select(x => x!.S("scanned")).Order().ToArray());
        Assert.All(metadata["scans"]!.AsArray(), x => Assert.False(string.IsNullOrWhiteSpace(x!.S("scan_id"))));

        // A merged document needs its own overall summary/search index, not another paid OCR pass.
        await f.Analyzer.Analyze(merged, default);
        Assert.Equal(0, f.Fake.PageCalls); Assert.Equal(1, f.Fake.MetadataCalls);
        Assert.Equal(4, f.Db.Rows("SELECT * FROM pages WHERE doc_id=$i", ("$i", merged)).Count);
    }

    [Fact]
    public async Task RepeatedAndConcurrentMergeRequestsReuseOneDurableOutput()
    {
        var (f, front, back) = await TwoScans();
        string plan = Plan(front, back);
        var ids = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => f.Docs.MergeDocuments(plan, "Complete contract", "Matched REF-731", default)));
        Assert.Single(ids.Distinct());
        Assert.Equal(3, f.Db.Rows("SELECT id FROM documents").Count);
        Assert.Equal(4, f.Db.Rows("SELECT * FROM document_sources").Count);
        Assert.Single(f.Db.Rows("SELECT id FROM jobs WHERE kind='index' AND payload=$i", ("$i", ids[0])));
        Ready(f, ids[0]); await f.Docs.ReconcileMergedDocuments();
        Assert.Equal(ids[0], await f.Docs.MergeDocuments(plan, "Changed retry title", "Same retry", default));
    }

    [Fact]
    public async Task LaterSplitOfMergedOutputKeepsEveryRecoverableSourceAndOneVisibleRepresentation()
    {
        var (f, front, back) = await TwoScans();
        Ready(f, front); Ready(f, back);
        string merged = await f.Docs.MergeDocuments(Plan(front, back), "Complete contract", "Matched REF-731", default);
        Ready(f, merged); await f.Docs.ReconcileMergedDocuments();
        string first = await f.Docs.ExtractPages(merged, "1-2", "First attachment", default);
        string second = await f.Docs.ExtractPages(merged, "3-4", "Second attachment", default);
        Ready(f, first); Ready(f, second);
        await f.Docs.ReconcileSplitBatches(); await f.Docs.ReconcileMergedDocuments();
        Assert.All(new[] { front, back, merged }, id => Assert.Equal("superseded", f.Db.Doc(id)!.S("status")));
        var browse = await new DesktopCommands(f.Settings, f.Db, f.Docs, f.Search).Handle(new() { ["command"] = "browse" }, default);
        Assert.Equal(2, browse.I("total"));

        await f.Docs.Trash(first, default); await f.Docs.ReconcileSplitBatches(); await f.Docs.ReconcileMergedDocuments();
        Assert.Equal("ready", f.Db.Doc(merged)!.S("status"));
        Assert.Equal("superseded", f.Db.Doc(front)!.S("status")); Assert.Equal("superseded", f.Db.Doc(back)!.S("status"));
        foreach (string id in new[] { front, back, merged, first, second }) Assert.True(File.Exists(f.Db.Doc(id)!.S("original")));
    }

    [Fact]
    public async Task SourcesStayVisibleUntilOutputReadyAndReturnAfterTrashOrUndo()
    {
        var (f, front, back) = await TwoScans();
        Ready(f, front); Ready(f, back);
        string merged = await f.Docs.MergeDocuments(Plan(front, back), "Complete contract", "Matched REF-731", default);
        await f.Docs.ReconcileMergedDocuments();
        Assert.Equal("ready", f.Db.Doc(front)!.S("status")); Assert.Equal("ready", f.Db.Doc(back)!.S("status"));
        f.Db.Exec("UPDATE documents SET status='analyzed' WHERE id=$i", ("$i", merged));
        await f.Docs.ReconcileMergedDocuments();
        Assert.Equal("ready", f.Db.Doc(front)!.S("status"));

        Ready(f, merged); await f.Docs.ReconcileMergedDocuments();
        Assert.Equal("superseded", f.Db.Doc(front)!.S("status")); Assert.Equal("superseded", f.Db.Doc(back)!.S("status"));
        var browse = await new DesktopCommands(f.Settings, f.Db, f.Docs, f.Search).Handle(new() { ["command"] = "browse" }, default);
        Assert.Equal(1, browse.I("total"));

        await f.Docs.Trash(merged, default);
        Assert.Equal("ready", f.Db.Doc(front)!.S("status")); Assert.Equal("ready", f.Db.Doc(back)!.S("status"));
        await f.Docs.Restore(merged, default); await f.Docs.ReconcileMergedDocuments();
        Assert.Equal("superseded", f.Db.Doc(front)!.S("status"));

        var op = Assert.Single(f.Db.Rows("SELECT * FROM operations WHERE kind='merge' AND doc_id=$i AND state='applied'", ("$i", merged)));
        await f.Docs.Undo(op.S("id"), default);
        Assert.Equal("deleted", f.Db.Doc(merged)!.S("status"));
        Assert.Equal("ready", f.Db.Doc(front)!.S("status")); Assert.Equal("ready", f.Db.Doc(back)!.S("status"));
        await f.Docs.ReconcileMergedDocuments();
        Assert.Equal("ready", f.Db.Doc(front)!.S("status"));
        foreach (string id in new[] { front, back, merged })
        {
            Assert.True(File.Exists(f.Db.Doc(id)!.S("original")));
            Assert.True(File.Exists(f.Db.Doc(id)!.S("path")));
        }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("outside")]
    [InlineData("zero")]
    [InlineData("single")]
    [InlineData("malformed")]
    public async Task InvalidPlanCannotCreateOrHideDocuments(string issue)
    {
        var (f, front, back) = await TwoScans();
        string plan = issue switch
        {
            "missing" => $"{front}:1,{front}:2,{back}:1",
            "duplicate" => $"{front}:1,{front}:1,{back}:1,{back}:2",
            "outside" => $"{front}:1,{front}:3,{back}:1,{back}:2",
            "zero" => $"{front}:0,{front}:2,{back}:1,{back}:2",
            "single" => $"{front}:1,{front}:2",
            _ => "not a page plan"
        };
        await Assert.ThrowsAsync<ArgumentException>(() => f.Docs.MergeDocuments(plan, "Invalid", "Invalid test input", default));
        Assert.Equal(2, f.Db.Rows("SELECT * FROM documents").Count);
        Assert.Empty(f.Db.Rows("SELECT * FROM document_sources"));
        Assert.Empty(f.Db.Rows("SELECT * FROM operations WHERE kind='merge'"));
        Assert.DoesNotContain(f.Db.Rows("SELECT status FROM documents"), d => d.S("status") == "superseded");
    }

    [Theory]
    [InlineData("locked")]
    [InlineData("deleted")]
    [InlineData("mixed")]
    [InlineData("processing")]
    [InlineData("pending_work")]
    [InlineData("missing_analysis")]
    public async Task UnsafeSourceCannotParticipateInAMerge(string issue)
    {
        var (f, front, back) = await TwoScans();
        var frontDoc = f.Db.Doc(front)!; var backDoc = f.Db.Doc(back)!;
        var sourceHashes = new[] { Hash(frontDoc.S("original")), Hash(backDoc.S("original")) };
        switch (issue)
        {
            case "locked": f.Db.Exec("UPDATE documents SET locked=1 WHERE id=$i", ("$i", front)); break;
            case "deleted": await f.Docs.Trash(front, default); break;
            case "mixed": f.Db.Exec("UPDATE documents SET mixed_content=1 WHERE id=$i", ("$i", front)); break;
            case "processing": f.Db.Exec("UPDATE documents SET status='analyzing' WHERE id=$i", ("$i", front)); break;
            case "pending_work": f.Docs.Enqueue("reindex", front); break;
            case "missing_analysis": f.Db.Exec("DELETE FROM pages WHERE doc_id=$i AND number=2; DELETE FROM analyses WHERE doc_id=$i AND page=2", ("$i", front)); break;
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Docs.MergeDocuments(Plan(front, back), "Unsafe", "Invalid test input", default));
        Assert.Equal(2, f.Db.Rows("SELECT * FROM documents").Count);
        Assert.Empty(f.Db.Rows("SELECT * FROM document_sources"));
        Assert.Equal(sourceHashes[0], Hash(frontDoc.S("original"))); Assert.Equal(sourceHashes[1], Hash(backDoc.S("original")));
        Assert.True(File.Exists(frontDoc.S("path"))); Assert.True(File.Exists(backDoc.S("path")));
    }

    [Fact]
    public async Task NearbyScanCandidatesAreSymmetricEvenWhenAnalysisFinishesOutOfOrder()
    {
        var (f, front, back) = await TwoScans(1);
        f.Db.Exec("UPDATE documents SET status='analyzing',summary='' WHERE id=$i; DELETE FROM pages WHERE doc_id=$i; DELETE FROM analyses WHERE doc_id=$i", ("$i", front));
        var whileEarlierPending = ScanRelations.Find(f.Db, back)["candidates"]!.AsArray();
        var pending = Assert.Single(whileEarlierPending, n => n!.S("id") == front)!;
        Assert.True(pending["analysis_pending"]!.GetValue<bool>());

        // Finishing a slower earlier scan must still expose the later, already archived one.
        Ready(f, back);
        var whenEarlierFinishes = ScanRelations.Find(f.Db, front)["candidates"]!.AsArray();
        Assert.Contains(whenEarlierFinishes, n => n!.S("id") == back);
        Assert.Equal(2, f.Db.Rows("SELECT id FROM documents").Count);
        Assert.Empty(f.Db.Rows("SELECT * FROM document_sources"));
    }

    [Fact]
    public async Task TemporalProximityIsOnlyAReviewHintAndDoesNotMergeUnrelatedDocuments()
    {
        var (f, front, back) = await TwoScans(1);
        f.Db.Exec("UPDATE documents SET title='Unrelated grocery receipt',summary='Apples and milk',tags='' WHERE id=$i; UPDATE pages SET text='Apples and milk',summary='{}' WHERE doc_id=$i", ("$i", back));
        string far = await AddScan(f, "old.pdf", 1, "2026-09-01T09:01:00.0000000Z");
        var result = ScanRelations.Find(f.Db, front);
        Assert.Contains(result["candidates"]!.AsArray(), n => n!.S("id") == back);
        Assert.DoesNotContain(result["candidates"]!.AsArray(), n => n!.S("id") == far);
        Assert.False(string.IsNullOrWhiteSpace(result.S("notice")));
        Assert.Empty(f.Db.Rows("SELECT * FROM document_sources"));
        Assert.Empty(f.Db.Rows("SELECT * FROM operations WHERE kind='merge'"));
        Assert.Equal("analyzed", f.Db.Doc(front)!.S("status")); Assert.Equal("analyzed", f.Db.Doc(back)!.S("status"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task AgentMustReadEverySourcePageInTheSameRunBeforeMerging(int pagesRead)
    {
        var (f, front, back) = await TwoScans();
        static JsonObject Call(string name, JsonObject args, string call) => new()
        {
            ["output"] = new JsonArray(new JsonObject { ["type"] = "function_call", ["name"] = name, ["call_id"] = call, ["arguments"] = args.ToJsonString() })
        };
        if (pagesRead > 0)
        {
            f.Fake.AgentResponses.Enqueue(Call("read_document", new() { ["id"] = front, ["start"] = 1, ["end"] = 2 }, "front"));
            f.Fake.AgentResponses.Enqueue(Call("read_document", new() { ["id"] = back, ["start"] = 1, ["end"] = pagesRead - 2 }, "back"));
        }
        f.Fake.AgentResponses.Enqueue(Call("merge_documents", new()
        {
            ["page_plan"] = Plan(front, back), ["title"] = "Complete synthetic contract", ["reason"] = "REF-731 matches with complementary original page numbering."
        }, "merge"));
        f.Fake.AgentResponses.Enqueue(FakeApi.Message("本次检查结束。"));
        await f.Agent.Run("检查并整理两次扫描的正反面。", null, default);

        var outputs = f.Fake.Requests.Last()["input"]!.AsArray().Where(n => n?.S("type") == "function_call_output" && n.S("call_id") == "merge").ToArray();
        var result = JsonNode.Parse(Assert.Single(outputs)!.S("output"))!;
        Assert.Equal(pagesRead == 4 ? 3 : 2, f.Db.Rows("SELECT id FROM documents").Count);
        if (pagesRead == 4) Assert.True(result["created"]!.GetValue<bool>());
        else Assert.Contains("read_document", result.S("error"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedOriginalPagesCannotBeDuplicatedThroughDerivedDocuments(bool overlappingChildren)
    {
        var f = new LibraryFixture();
        string source = await AddScan(f, "source.pdf", 3, FirstScan);
        string child = await f.Docs.ExtractPages(source, "1-2", "First part", default); Ready(f, child);
        string other = overlappingChildren ? await f.Docs.ExtractPages(source, "2-3", "Overlapping part", default) : source;
        Ready(f, other);
        string plan = overlappingChildren ? $"{child}:1,{child}:2,{other}:1,{other}:2" : $"{source}:1,{source}:2,{source}:3,{child}:1,{child}:2";
        int before = f.Db.Rows("SELECT id FROM documents").Count;
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Docs.MergeDocuments(plan, "Duplicate evidence", "Invalid overlapping provenance", default));
        Assert.Equal(before, f.Db.Rows("SELECT id FROM documents").Count);
        Assert.Empty(f.Db.Rows("SELECT * FROM document_merges"));
        Assert.True(File.Exists(f.Db.Doc(source)!.S("original")));
    }

    [Fact]
    public async Task MissingPublishedMergedFileRestoresSourcesAndReappearanceHidesThemAgain()
    {
        var (f, front, back) = await TwoScans(); Ready(f, front); Ready(f, back);
        string merged = await f.Docs.MergeDocuments(Plan(front, back), "Complete contract", "Matched evidence", default);
        Ready(f, merged); await f.Docs.ReconcileMergedDocuments();
        var doc = f.Db.Doc(merged)!; string path = doc.S("path"), moved = path + ".unavailable";
        var immutableHash = Hash(doc.S("original"));
        File.Move(path, moved);
        await f.Docs.ReconcileMergedDocuments();
        Assert.Equal("ready", f.Db.Doc(front)!.S("status")); Assert.Equal("ready", f.Db.Doc(back)!.S("status"));
        Assert.Equal(immutableHash, Hash(doc.S("original")));
        File.Move(moved, path);
        await f.Docs.ReconcileMergedDocuments();
        Assert.Equal("superseded", f.Db.Doc(front)!.S("status")); Assert.Equal("superseded", f.Db.Doc(back)!.S("status"));
    }

    [Fact]
    public async Task PostScanWorkerSuppliesAlreadyArchivedNeighborToItsOrganizingAgent()
    {
        var (f, front, back) = await TwoScans(1); Ready(f, back);
        string job = f.Docs.Enqueue("organize", front);
        f.Fake.AgentResponses.Enqueue(FakeApi.Message("已检查两次独立扫描，证据不足，保持分开。"));
        using var worker = new Worker(f.Settings, f.Db, f.Docs, f.Analyzer, f.Agent, f.Ai, Microsoft.Extensions.Logging.Abstractions.NullLogger<Worker>.Instance);
        await worker.StartAsync(default);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            while (f.Db.Rows("SELECT id FROM jobs WHERE id=$i AND status='done'", ("$i", job)).Count == 0) await Task.Delay(30, timeout.Token);
            var request = Assert.Single(f.Fake.Requests);
            string task = request["input"]![0]!.S("content");
            Assert.Contains(front, task); Assert.Contains(back, task); Assert.Contains("window_minutes", task);
            Assert.Equal("ready", f.Db.Doc(front)!.S("status")); Assert.Equal("ready", f.Db.Doc(back)!.S("status"));
            Assert.Empty(f.Db.Rows("SELECT * FROM document_merges"));
        }
        finally { await worker.StopAsync(default); }
    }

    [Fact]
    public async Task AScanImageAndPdfCanBecomeOneDocumentWithoutChangingEitherSource()
    {
        var f = new LibraryFixture();
        string pdf = await AddScan(f, "front.pdf", 1, FirstScan);
        string imagePath = Path.Combine(f.Docs.Root, "back.png");
        using (var bitmap = new System.Drawing.Bitmap(90, 140))
        {
            using var graphics = System.Drawing.Graphics.FromImage(bitmap);
            graphics.Clear(System.Drawing.Color.MediumPurple);
            bitmap.Save(imagePath, System.Drawing.Imaging.ImageFormat.Png);
        }
        string image = await f.Docs.Import(imagePath, SecondScan);
        CompleteAnalysis(f, image, "back.png", 1, SecondScan);
        byte[] sourceHash = Hash(imagePath);
        string merged = await f.Docs.MergeDocuments($"{pdf}:1,{image}:1", "Combined image and PDF", "Complementary form faces", default);
        Assert.Equal(2, f.Docs.PageCount(f.Db.Doc(merged)!));
        Assert.Equal(sourceHash, Hash(imagePath)); Assert.Equal(sourceHash, Hash(f.Db.Doc(image)!.S("original")));
        using var stream = new MemoryStream(f.Docs.PageImage(f.Db.Doc(merged)!, 2));
        using var rendered = new System.Drawing.Bitmap(stream);
        Assert.InRange(Math.Abs(rendered.Width / (double)rendered.Height - 90d / 140), 0, .01);
        var color = rendered.GetPixel(rendered.Width / 2, rendered.Height / 2);
        Assert.InRange(Math.Abs(color.R - System.Drawing.Color.MediumPurple.R), 0, 2);
        Assert.InRange(Math.Abs(color.G - System.Drawing.Color.MediumPurple.G), 0, 2);
        Assert.InRange(Math.Abs(color.B - System.Drawing.Color.MediumPurple.B), 0, 2);
    }

    [Fact]
    public async Task ThirdScanExtendsAnExistingMergeAndUndoPreservesTheDependencyOrder()
    {
        var (f, front, back) = await TwoScans(1); Ready(f, front); Ready(f, back);
        string first = await f.Docs.MergeDocuments($"{front}:1,{back}:1", "First two pages", "REF-731 pages 1-2", default);
        Ready(f, first); await f.Docs.ReconcileMergedDocuments();
        string third = await AddScan(f, "third.pdf", 1, "2026-10-01T09:05:00.0000000Z"); Ready(f, third);
        string complete = await f.Docs.MergeDocuments($"{first}:1,{first}:2,{third}:1", "Three-page contract", "REF-731 pages 1-3", default);
        Ready(f, complete); await f.Docs.ReconcileMergedDocuments();
        Assert.Equal(new[] { front, back, third }, f.Docs.SourcePages(complete).Select(x => x!.S("source_doc_id")).ToArray());
        var firstOp = Assert.Single(f.Db.Rows("SELECT * FROM operations WHERE kind='merge' AND doc_id=$i", ("$i", first)));
        var finalOp = Assert.Single(f.Db.Rows("SELECT * FROM operations WHERE kind='merge' AND doc_id=$i", ("$i", complete)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Docs.Undo(firstOp.S("id"), default));
        await f.Docs.Undo(finalOp.S("id"), default);
        Assert.Equal("ready", f.Db.Doc(first)!.S("status")); Assert.Equal("ready", f.Db.Doc(third)!.S("status"));
        Assert.Equal("superseded", f.Db.Doc(front)!.S("status"));
        await f.Docs.Undo(firstOp.S("id"), default);
        Assert.Equal("ready", f.Db.Doc(front)!.S("status")); Assert.Equal("ready", f.Db.Doc(back)!.S("status"));
        foreach (string id in new[] { front, back, third, first, complete }) Assert.True(File.Exists(f.Db.Doc(id)!.S("original")));
    }

    [Fact]
    public async Task ChangedReadableOutputWithSamePageCountInvalidatesCacheUntilExactBytesAreRestored()
    {
        var (f, front, back) = await TwoScans(); Ready(f, front); Ready(f, back);
        string merged = await f.Docs.MergeDocuments(Plan(front, back), "Complete contract", "Matched page evidence", default);
        Ready(f, merged);
        await f.Docs.ReconcileMergedDocuments(); await f.Docs.ReconcileMergedDocuments();
        Assert.Equal("superseded", f.Db.Doc(front)!.S("status"));
        var doc = f.Db.Doc(merged)!;
        string path = doc.S("path"); byte[] archivedBytes = File.ReadAllBytes(path);
        DateTime originalStamp = File.GetLastWriteTimeUtc(path);
        byte[] otherPdf = File.ReadAllBytes(f.Pdf("unrelated-four-pages.pdf", 4));
        Assert.NotEqual(SHA256.HashData(archivedBytes), SHA256.HashData(otherPdf));
        File.WriteAllBytes(path, otherPdf); File.SetLastWriteTimeUtc(path, originalStamp.AddSeconds(3));
        var published = doc.DeepClone().AsObject(); published["original"] = path;
        Assert.Equal(4, f.Docs.PageCount(published));

        await f.Docs.ReconcileMergedDocuments();
        Assert.Equal("ready", f.Db.Doc(front)!.S("status")); Assert.Equal("ready", f.Db.Doc(back)!.S("status"));
        Assert.Equal(archivedBytes, File.ReadAllBytes(doc.S("original")));
        File.WriteAllBytes(path, archivedBytes); File.SetLastWriteTimeUtc(path, originalStamp.AddSeconds(6));
        await f.Docs.ReconcileMergedDocuments();
        Assert.Equal("superseded", f.Db.Doc(front)!.S("status")); Assert.Equal("superseded", f.Db.Doc(back)!.S("status"));
    }

    [Fact]
    public async Task UndoCanBeRestoredUnlessItsSourcesHaveEnteredAnotherActiveMerge()
    {
        var (f, front, back) = await TwoScans(1); Ready(f, front); Ready(f, back);
        string merged = await f.Docs.MergeDocuments($"{front}:1,{back}:1", "Original pair", "Matched page evidence", default);
        Ready(f, merged); await f.Docs.ReconcileMergedDocuments();
        var op = Assert.Single(f.Db.Rows("SELECT * FROM operations WHERE doc_id=$i AND kind='merge'", ("$i", merged)));
        await f.Docs.Undo(op.S("id"), default);
        await f.Docs.Restore(merged, default);
        Assert.Equal("applied", Assert.Single(f.Db.Rows("SELECT state FROM operations WHERE id=$i", ("$i", op.S("id")))).S("state"));
        Assert.Equal("superseded", f.Db.Doc(front)!.S("status"));

        await f.Docs.Undo(op.S("id"), default);
        string third = await AddScan(f, "alternative-back.pdf", 1, SecondScan); Ready(f, third);
        string replacement = await f.Docs.MergeDocuments($"{front}:1,{third}:1", "Corrected pair", "Corrected independent page evidence", default);
        Ready(f, replacement); await f.Docs.ReconcileMergedDocuments();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Docs.Restore(merged, default));
        Assert.Equal("deleted", f.Db.Doc(merged)!.S("status"));
        Assert.Equal("undone", Assert.Single(f.Db.Rows("SELECT state FROM operations WHERE id=$i", ("$i", op.S("id")))).S("state"));
        Assert.Equal("superseded", f.Db.Doc(front)!.S("status")); Assert.Equal("ready", f.Db.Doc(back)!.S("status"));
        Assert.Equal("ready", f.Db.Doc(replacement)!.S("status"));
        foreach (string id in new[] { front, back, third, merged, replacement }) Assert.True(File.Exists(f.Db.Doc(id)!.S("original")));
    }

    [Fact]
    public async Task OldConversationRefusalDoesNotPreventCombiningPrintedPagesOneThreeAndTwo()
    {
        var f = new LibraryFixture();
        string original = f.Pdf("synthetic-three-printed-pages.pdf", 3);
        string Part(string name, params int[] pages)
        {
            string path = Path.Combine(f.Docs.Root, name);
            using var input = PdfSharp.Pdf.IO.PdfReader.Open(original, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
            using var output = new PdfSharp.Pdf.PdfDocument();
            foreach (int page in pages) output.AddPage(input.Pages[page - 1]);
            output.Save(path); return path;
        }
        string a = await f.Docs.Import(Part("printed-1-and-3.pdf", 1, 3), FirstScan);
        string b = await f.Docs.Import(Part("printed-2.pdf", 2), SecondScan);
        CompleteAnalysis(f, a, "Agreement REF-731 pages 1,3", 2, FirstScan);
        CompleteAnalysis(f, b, "Agreement REF-731 page 2", 1, SecondScan);
        f.Db.Exec("UPDATE pages SET text=CASE number WHEN 1 THEN 'REF-731 printed page 1 of 3' ELSE 'REF-731 printed page 3 of 3' END WHERE doc_id=$i", ("$i", a));
        f.Db.Exec("UPDATE pages SET text='REF-731 printed page 2 of 3' WHERE doc_id=$i", ("$i", b));
        string conversation = Guid.NewGuid().ToString("N");
        f.Db.Exec("INSERT INTO conversations VALUES($c,'Combine separate scanned faces',$t)", ("$c", conversation), ("$t", Database.Now));
        foreach (var (role, text) in new[]
        {
            ("user", $"第一份 {a} 是第 1、3 页，第二份 {b} 是第 2 页，请合成一份。"),
            ("assistant", "不能合并，工具不支持；我只能统一标题和分类。"),
            ("user", "那你帮我合并啊")
        }) f.Db.Exec("INSERT INTO messages(conversation,role,text,created) VALUES($c,$r,$m,$t)", ("$c", conversation), ("$r", role), ("$m", text), ("$t", Database.Now));
        void Call(string name, JsonObject args, string call) => f.Fake.AgentResponses.Enqueue(new()
        {
            ["output"] = new JsonArray(new JsonObject { ["type"] = "function_call", ["name"] = name, ["call_id"] = call, ["arguments"] = args.ToJsonString() })
        });
        Call("read_document", new() { ["id"] = a, ["start"] = 1, ["end"] = 2 }, "read_a");
        Call("read_document", new() { ["id"] = b, ["start"] = 1, ["end"] = 1 }, "read_b");
        Call("merge_documents", new()
        {
            ["page_plan"] = $"{a}:1,{b}:1,{a}:2", ["title"] = "Complete REF-731 agreement",
            ["reason"] = "Both scans carry REF-731; printed page labels prove the order 1,2,3."
        }, "merge");
        f.Fake.AgentResponses.Enqueue(FakeApi.Message("已执行合并请求，新文件正在处理。"));
        string answer = await f.Agent.Run("", conversation, default);

        var initial = f.Fake.Requests[0];
        Assert.Contains("CURRENT CAPABILITIES ARE AUTHORITATIVE", initial.S("instructions"));
        Assert.Contains(initial["tools"]!.AsArray(), t => t!.S("name") == "merge_documents");
        Assert.Contains(initial["input"]!.AsArray(), m => m!.S("role") == "assistant" && m!.S("content").Contains("工具不支持"));
        var toolOutput = Assert.Single(f.Fake.Requests.Last()["input"]!.AsArray(), n => n?.S("type") == "function_call_output" && n.S("call_id") == "merge")!;
        var receipt = JsonNode.Parse(toolOutput.S("output"))!;
        string merged = receipt.S("id"); var doc = f.Db.Doc(merged)!;
        Assert.True(receipt["created"]!.GetValue<bool>()); Assert.False(receipt["reused"]!.GetValue<bool>());
        Assert.Equal(3, receipt.I("page_count")); Assert.Equal(2, receipt.I("source_count"));
        Assert.Equal(0, receipt.I("hidden_source_count")); Assert.False(receipt["sources_hidden"]!.GetValue<bool>());
        Assert.Equal("applied", receipt.S("operation_state")); Assert.Equal("queued", receipt.S("status"));
        Assert.False(string.IsNullOrWhiteSpace(receipt.S("operation_id")));
        Assert.Equal(3, f.Docs.PageCount(doc));
        Assert.Equal(f.Docs.PageImage(f.Db.Doc(a)!, 1), f.Docs.PageImage(doc, 1));
        Assert.Equal(f.Docs.PageImage(f.Db.Doc(b)!, 1), f.Docs.PageImage(doc, 2));
        Assert.Equal(f.Docs.PageImage(f.Db.Doc(a)!, 2), f.Docs.PageImage(doc, 3));
        Assert.Equal(new[] { "REF-731 printed page 1 of 3", "REF-731 printed page 2 of 3", "REF-731 printed page 3 of 3" },
            f.Db.Rows("SELECT text FROM pages WHERE doc_id=$i ORDER BY number", ("$i", merged)).Select(p => p.S("text")).ToArray());
        Assert.Contains("### 合并执行结果", answer); Assert.Contains($"[[{merged}:1]]", answer);
        Assert.Equal(answer, f.Db.Rows("SELECT text FROM messages WHERE conversation=$c ORDER BY id DESC LIMIT 1", ("$c", conversation))[0].S("text"));
        Assert.Equal("analyzed", f.Db.Doc(a)!.S("status")); Assert.Equal("analyzed", f.Db.Doc(b)!.S("status"));
    }
}
