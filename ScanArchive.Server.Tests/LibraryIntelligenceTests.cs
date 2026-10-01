using ScanArchive.Server;
using System.Text.Json.Nodes;
using Xunit;

namespace ScanArchive.Server.Tests;

public sealed class LibraryIntelligenceTests
{
    static LibraryIntelligence Intelligence(LibraryFixture fixture) => new(fixture.Settings, fixture.Db, fixture.Docs);
    static string Add(LibraryFixture f, string title = "Contract REF-731", string category = "Contracts", int count = 1, string status = "ready", string scanned = "2026-10-01T09:00:00Z", bool complete = true)
    {
        string id = Guid.NewGuid().ToString("N");
        f.Db.Exec("""
            INSERT INTO documents(id,hash,path,original,title,category,summary,tags,scanned,created,page_count,status)
            VALUES($i,$h,'test.pdf','test-original.pdf',$title,$category,'Complete document summary','REF-731',$scanned,$scanned,$count,$status)
            """, ("$i", id), ("$h", Guid.NewGuid().ToString("N")), ("$title", title), ("$category", category), ("$count", count), ("$status", status), ("$scanned", scanned));
        if (complete)
        {
            for (int page = 1; page <= count; page++)
            {
                var metadata = new JsonObject { ["summary"] = "A synthetic page", ["entities"] = "REF-731", ["continuity"] = $"Printed page {page}; reference REF-731" };
                f.Db.Exec("INSERT INTO pages VALUES($i,$p,$text,$summary); INSERT INTO analyses VALUES($i,$p,'test',$time)", ("$i", id), ("$p", page), ("$text", $"Reference REF-731 page {page}"), ("$summary", metadata.ToJsonString()), ("$time", scanned));
                f.Search.AddChunk(id, page, $"Reference REF-731 page {page}", [1, 0, 0], f.Settings.Current.EmbeddingModel);
            }
            f.Db.Exec("INSERT INTO analyses VALUES($i,0,'test',$time)", ("$i", id), ("$time", scanned));
        }
        return id;
    }
    static string Snapshot(LibraryFixture f) => string.Join("\n", new[] { "documents", "pages", "analyses", "chunks", "search_fts", "jobs", "operations", "activity", "document_sources" }.Select(table => new JsonArray(f.Db.Rows("SELECT * FROM " + table).Select(x => (JsonNode)x).ToArray()).ToJsonString()));
    static string[] Issues(JsonNode item) => item["issues"]!.AsArray().Select(x => x!.ToString()).ToArray();

    [Fact]
    public void HealthyLibraryHasNoInventedIssuesAndAuditDoesNotWriteOrCallAi()
    {
        var f = new LibraryFixture(); Add(f); Add(f, "Another contract", count: 3);
        string before = Snapshot(f);
        var result = Intelligence(f).Audit();
        Assert.Equal(2, result["totals"]!.I("active_documents"));
        Assert.Equal(0, result.I("total_matching_documents"));
        Assert.Empty(result["items"]!.AsArray());
        Assert.False(result["has_more"]!.GetValue<bool>());
        Assert.Null(result["next_offset"]);
        Assert.Equal(before, Snapshot(f));
        Assert.Empty(f.Fake.Requests);
    }

    [Fact]
    public void PaginationFindsOldAndLateRecordsWithAccurateTotalsAndExcludesHiddenOriginals()
    {
        var f = new LibraryFixture();
        for (int n = 0; n < 57; n++) Add(f, "Document", "", complete: false);
        Add(f, "Hidden source", "", status: "superseded", complete: false);
        Add(f, "Trash", "", status: "deleted", complete: false);
        var intelligence = Intelligence(f); var first = intelligence.Audit("all", 0, 25); var second = intelligence.Audit("all", 25, 25); var last = intelligence.Audit("all", 50, 25);
        Assert.Equal(57, first.I("total_matching_documents")); Assert.Equal(57, first["totals"]!.I("incomplete_analysis"));
        Assert.Equal(57, first["totals"]!.I("uncategorized"));
        Assert.Equal(25, first["items"]!.AsArray().Count); Assert.Equal(7, last["items"]!.AsArray().Count);
        Assert.True(first["has_more"]!.GetValue<bool>()); Assert.Equal(25, first.I("next_offset")); Assert.False(last["has_more"]!.GetValue<bool>());
        var ids = new[] { first, second, last }.SelectMany(x => x["items"]!.AsArray()).Select(x => x!.S("id")).ToArray();
        Assert.Equal(57, ids.Distinct().Count());
        Assert.Empty(intelligence.Audit("all", 999)["items"]!.AsArray());
        Assert.Equal(50, intelligence.Audit("all", -1, 500)["items"]!.AsArray().Count);
    }

    [Fact]
    public void AnalysisRequiresEachRealPageAndOverallMetadataRecord()
    {
        var f = new LibraryFixture(); string missingPage = Add(f, count: 2); string missingMetadata = Add(f);
        f.Db.Exec("DELETE FROM analyses WHERE doc_id=$i AND page=2; INSERT INTO analyses VALUES($i,9,'test','2026-10-01')", ("$i", missingPage));
        f.Db.Exec("DELETE FROM analyses WHERE doc_id=$i AND page=0", ("$i", missingMetadata));
        var result = Intelligence(f).Audit("analysis");
        Assert.Equal(2, result.I("total_matching_documents"));
        var missing = result["items"]!.AsArray().Single(x => x!.S("id") == missingPage)!;
        Assert.Equal(1, missing["analysis"]!.I("pages_completed")); Assert.Contains("incomplete_analysis", Issues(missing));
    }

    [Fact]
    public void IndexAuditDetectsMissingPageLexicalRowsAndWrongOrMalformedVectors()
    {
        var f = new LibraryFixture(); string id = Add(f, count: 4);
        f.Db.Exec("DELETE FROM search_fts WHERE chunk_id IN (SELECT id FROM chunks WHERE doc_id=$i AND page=2); UPDATE chunks SET model='old-model' WHERE doc_id=$i AND page=2; UPDATE chunks SET embedding='[\"not-a-number\"]' WHERE doc_id=$i AND page=3; DELETE FROM search_fts WHERE chunk_id IN (SELECT id FROM chunks WHERE doc_id=$i AND page=4); DELETE FROM chunks WHERE doc_id=$i AND page=4", ("$i", id));
        var result = Intelligence(f).Audit("indexing"); var item = Assert.Single(result["items"]!.AsArray())!;
        Assert.Equal(3, item["index"]!.I("chunks")); Assert.Equal(3, item["index"]!.I("indexed_pages"));
        Assert.Equal(2, item["index"]!.I("lexical_chunks")); Assert.Equal(1, item["index"]!.I("current_model_chunks"));
        Assert.Contains("missing_lexical_index", Issues(item)); Assert.Contains("missing_current_embeddings", Issues(item));
        Assert.Equal(1, result["totals"]!.I("incomplete_index"));
        f.Db.Exec("UPDATE chunks SET embedding='not-json' WHERE doc_id=$i AND page=3", ("$i", id));
        Assert.Equal(1, Intelligence(f).Audit("indexing").I("total_matching_documents"));
    }

    [Fact]
    public void LockedAndBusyRecordsRemainVisibleAsConstrainedSuggestions()
    {
        var f = new LibraryFixture(); string locked = Add(f, "2026-10-01_10-05-03-001", ""); string busy = Add(f, "Mixed", "Contracts", status: "error");
        f.Db.Exec("UPDATE documents SET locked=1 WHERE id=$i", ("$i", locked));
        f.Db.Exec("UPDATE documents SET mixed_content=1,error='Synthetic retry error' WHERE id=$i", ("$i", busy));
        f.Docs.Enqueue("index", busy);
        string before = Snapshot(f); var result = Intelligence(f).Audit();
        var lockedItem = result["items"]!.AsArray().Single(x => x!.S("id") == locked)!;
        var busyItem = result["items"]!.AsArray().Single(x => x!.S("id") == busy)!;
        Assert.Equal("preserve_user_locked_organization", lockedItem.S("automation_constraint"));
        Assert.Contains("generic_title", Issues(lockedItem)); Assert.Equal("wait_for_active_jobs", busyItem.S("automation_constraint"));
        Assert.Contains("processing_error", Issues(busyItem)); Assert.Contains("mixed_scan", Issues(busyItem));
        Assert.Equal(1, Intelligence(f).Audit("errors").I("total_matching_documents"));
        Assert.Equal(before, Snapshot(f));
    }

    [Fact]
    public void TaxonomyGroupsOnlySpellingCandidatesAndPreservesLockedCounts()
    {
        var f = new LibraryFixture(); Add(f, category: "车辆/购车资料"); string locked = Add(f, category: "车辆/购车 资料");
        Add(f, category: "家庭/合同"); Add(f, category: "工作/合同");
        f.Db.Exec("UPDATE documents SET locked=1 WHERE id=$i", ("$i", locked));
        var result = Intelligence(f).Audit("taxonomy");
        Assert.Equal(2, result.I("total_matching_documents")); Assert.Equal(1, result.I("taxonomy_group_count"));
        var group = Assert.Single(result["taxonomy_groups"]!.AsArray())!;
        Assert.Equal(1, group.I("locked_documents")); Assert.False(group["proof_of_equivalence"]!.GetValue<bool>());
    }

    [Fact]
    public void CompareProvidesContinuityAndIdentifierHintsWithoutVerifyingEvidence()
    {
        var f = new LibraryFixture(); string a = Add(f, scanned: "2026-10-01T09:00:00Z"); string b = Add(f, scanned: "2026-10-01T09:03:00Z");
        f.Db.Exec("UPDATE documents SET tags='REF-731,2026-10-01' WHERE id IN ($a,$b)", ("$a", a), ("$b", b));
        string before = Snapshot(f); var result = Intelligence(f).Compare(a + "\n" + b);
        Assert.False(result["verified_page_evidence"]!.GetValue<bool>());
        Assert.All(result["documents"]!.AsArray(), x => Assert.False(x!["verified_page_evidence"]!.GetValue<bool>()));
        var pair = Assert.Single(result["pairs"]!.AsArray())!;
        Assert.Equal(3, pair.I("scan_gap_minutes")); Assert.False(pair["same_registered_byte_hash"]!.GetValue<bool>());
        Assert.Contains("REF-731", pair["shared_identifier_candidates"]!.AsArray().Select(x => x!.ToString()));
        Assert.DoesNotContain("2026-10-01", pair["shared_identifier_candidates"]!.AsArray().Select(x => x!.ToString()));
        Assert.Contains("Printed page 1", result["documents"]![0]!["page_map"]![0]!.S("continuity"));
        Assert.Equal(before, Snapshot(f)); Assert.Empty(f.Fake.Requests);
    }

    [Fact]
    public void CompareResolvesSplitAndMergedPageOriginsWithOriginalScanTimes()
    {
        var f = new LibraryFixture(); string front = Add(f, count: 2, scanned: "2026-10-01T09:00:00Z"); string back = Add(f, scanned: "2026-10-01T09:03:00Z");
        string merged = Add(f, count: 3); string child = Add(f, count: 2);
        f.Db.Exec("INSERT INTO document_sources VALUES($m,1,$f,1,'2026-10-01T09:00:00Z'); INSERT INTO document_sources VALUES($m,2,$b,1,'2026-10-01T09:03:00Z'); INSERT INTO document_sources VALUES($m,3,$f,2,'2026-10-01T09:00:00Z'); UPDATE documents SET parent_id=$m,source_pages='3,2' WHERE id=$child", ("$m", merged), ("$f", front), ("$b", back), ("$child", child));
        var result = Intelligence(f).Compare(child + "," + front);
        var pages = result["documents"]![0]!["page_map"]!.AsArray();
        Assert.Equal(front, pages[0]!["scan_origin"]!.S("source_doc_id")); Assert.Equal(2, pages[0]!["scan_origin"]!.I("source_page"));
        Assert.Equal(back, pages[1]!["scan_origin"]!.S("source_doc_id")); Assert.Equal("2026-10-01T09:03:00Z", pages[1]!["scan_origin"]!.S("scanned"));
        Assert.True(result["pairs"]![0]!["shared_scan_origin"]!.GetValue<bool>());
    }

    [Fact]
    public void CompareBoundsLargeScansAndDeclaresOmittedPagesAndJobs()
    {
        var f = new LibraryFixture(); string a = Add(f, count: 85); string b = Add(f);
        for (int n = 0; n < 12; n++) f.Docs.Enqueue("synthetic" + n, a);
        var result = Intelligence(f).Compare(a + "," + b); var profile = result["documents"]![0]!;
        var pages = profile["page_map"]!.AsArray(); Assert.Equal(40, pages.Count); Assert.Equal(1, pages[0]!.I("page")); Assert.Equal(85, pages[^1]!.I("page"));
        Assert.Equal(45, profile.I("pages_omitted")); Assert.Equal(12, profile.I("active_job_count")); Assert.True(profile["active_jobs_truncated"]!.GetValue<bool>());
        Assert.Equal(10, profile["active_jobs"]!.AsArray().Count);
    }

    [Theory]
    [InlineData("1-2", 2, "1,2")]
    [InlineData("3-3", 1, "3")]
    [InlineData("3-3,1-2", 3, "3,1,2")]
    public void CompareResolvesRangeMappingsUsingParentPageNumbers(string mapping, int childPages, string expected)
    {
        var f = new LibraryFixture(); string original = Add(f, count: 4, scanned: "2026-10-01T09:00:00Z");
        string child = Add(f, count: childPages, scanned: "2026-10-01T12:00:00Z");
        f.Db.Exec("UPDATE documents SET parent_id=$parent,source_pages=$mapping WHERE id=$child", ("$parent", original), ("$mapping", mapping), ("$child", child));
        string before = Snapshot(f); var result = Intelligence(f).Compare(child + "," + original);
        var pages = result["documents"]![0]!["page_map"]!.AsArray();
        Assert.Equal(expected.Split(',').Select(int.Parse), pages.Select(x => x!["scan_origin"]!.I("source_page")));
        Assert.All(pages, page => { Assert.Equal(original, page!["scan_origin"]!.S("source_doc_id")); Assert.Equal("2026-10-01T09:00:00Z", page["scan_origin"]!.S("scanned")); });
        Assert.Equal(before, Snapshot(f));
    }

    [Fact]
    public void CompareResolvesNestedRangesAndRejectsOutOfRangeLineage()
    {
        var f = new LibraryFixture(); string original = Add(f, count: 4); string child = Add(f, count: 3); string grandchild = Add(f, count: 2);
        f.Db.Exec("UPDATE documents SET parent_id=$original,source_pages='2-4' WHERE id=$child; UPDATE documents SET parent_id=$child,source_pages='2-3' WHERE id=$grandchild", ("$original", original), ("$child", child), ("$grandchild", grandchild));
        var intelligence = Intelligence(f);
        var pages = intelligence.Compare(grandchild + "," + original)["documents"]![0]!["page_map"]!.AsArray();
        Assert.Equal(new[] { 3, 4 }, pages.Select(x => x!["scan_origin"]!.I("source_page")));
        Assert.All(pages, page => Assert.Equal(original, page!["scan_origin"]!.S("source_doc_id")));
        f.Db.Exec("UPDATE documents SET source_pages='4-5' WHERE id=$child", ("$child", child));
        Assert.All(intelligence.Compare(grandchild + "," + original)["documents"]![0]!["page_map"]!.AsArray(), page => Assert.Null(page!["scan_origin"]));
    }

    [Fact]
    public void InvalidRequestsAndBrokenLineageFailWithoutMutationsOrLoops()
    {
        var f = new LibraryFixture(); string a = Add(f); string b = Add(f);
        f.Db.Exec("UPDATE documents SET parent_id=$b,source_pages='1' WHERE id=$a; UPDATE documents SET parent_id=$a,source_pages='1' WHERE id=$b", ("$a", a), ("$b", b));
        var intelligence = Intelligence(f); string before = Snapshot(f);
        Assert.Throws<ArgumentException>(() => intelligence.Audit("unknown")); Assert.Throws<ArgumentException>(() => intelligence.Compare(a));
        Assert.Throws<ArgumentException>(() => intelligence.Compare(a + "," + a)); Assert.Throws<KeyNotFoundException>(() => intelligence.Compare(a + ",missing"));
        Assert.Throws<ArgumentException>(() => intelligence.Compare(string.Join(',', Enumerable.Range(0, 7))));
        var result = intelligence.Compare(a + "," + b);
        Assert.Null(result["documents"]![0]!["page_map"]![0]!["scan_origin"]);
        Assert.Equal(before, Snapshot(f));
    }
}
