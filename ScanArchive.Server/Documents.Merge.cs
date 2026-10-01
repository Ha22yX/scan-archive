using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace ScanArchive.Server;

public sealed partial class Documents
{
    sealed record MergePage(string Id, int Page);
    readonly Dictionary<string, (string Stamp, bool Valid)> replacementFileChecks = new();

    public string? ActiveMergeForSource(string id) => db.Rows("SELECT m.doc_id FROM document_sources s JOIN document_merges m ON m.doc_id=s.merged_doc_id JOIN operations o ON o.id=m.operation_id JOIN documents d ON d.id=m.doc_id WHERE s.source_doc_id=$i AND o.state='applied' AND d.status<>'deleted' LIMIT 1", ("$i", id)).FirstOrDefault()?.S("doc_id");

    static List<MergePage> ParseMergePlan(string plan)
    {
        if (string.IsNullOrWhiteSpace(plan) || plan.Length > 14000) throw new ArgumentException("合并计划不能为空，最多 200 页。");
        var pages = new List<MergePage>();
        foreach (string entry in plan.Split(',', StringSplitOptions.TrimEntries))
        {
            string[] parts = entry.Split(':', StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || !Guid.TryParseExact(parts[0], "N", out var id) || !int.TryParse(parts[1], out int page) || page < 1)
                throw new ArgumentException("合并页序应为 文档ID:页码，以逗号分隔。");
            pages.Add(new(id.ToString("N"), page));
        }
        if (pages.Count > 200 || pages.Distinct().Count() != pages.Count) throw new ArgumentException("合并最多 200 页，每一页只能出现一次。");
        if (pages.Select(x => x.Id).Distinct().Count() is < 2 or > 10) throw new ArgumentException("一次合并需要 2 至 10 份独立文档。");
        return pages;
    }

    public async Task<string> MergeDocuments(string pagePlan, string title, string reason, CancellationToken ct)
    {
        var plan = ParseMergePlan(pagePlan);
        string canonical = string.Join(',', plan.Select(x => $"{x.Id}:{x.Page}"));
        string planHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("请提供合并后的标题和依据。");
        if (reason.Length > 6000) throw new ArgumentException("合并依据过长。");
        await Mutation.WaitAsync(ct);
        try
        {
            var replay = db.Rows("SELECT m.doc_id,o.state FROM document_merges m JOIN operations o ON o.id=m.operation_id WHERE m.plan_hash=$h", ("$h", planHash)).FirstOrDefault();
            if (replay != null)
            {
                if (replay.S("state") != "applied" || db.Doc(replay.S("doc_id"))?.S("status") == "deleted")
                    throw new InvalidOperationException("此合并已撤销或移入回收站；请先恢复已有文档，不能自动重复创建。");
                return replay.S("doc_id");
            }
            var sources = new Dictionary<string, JsonObject>();
            foreach (string id in plan.Select(x => x.Id).Distinct())
            {
                var doc = db.Doc(id) ?? throw new ArgumentException("源文档不存在。");
                if (doc.S("status") is not ("analyzed" or "ready") || doc.I("locked") != 0 || doc.I("mixed_content") != 0)
                    throw new InvalidOperationException("仅能合并已完成分析、未锁定、未被替代的独立文档；混合扫描请先拆分。");
                if (db.Rows("SELECT id FROM jobs WHERE payload=$i AND status IN ('pending','running') AND kind<>'organize'", ("$i", id)).Count != 0)
                    throw new InvalidOperationException("源文档仍有分析或索引任务，请完成后再合并。");
                if (ActiveMergeForSource(id) != null)
                    throw new InvalidOperationException("源文档已参与另一项合并，请处理已有合并结果。");
                if (!File.Exists(doc.S("original")) || !File.Exists(doc.S("path"))) throw new IOException("源文档文件不可用，未执行合并。");
                SafePath(Path.GetRelativePath(Root, doc.S("path")));
                if (!MatchesDocumentHash(doc.S("original"), doc.S("hash")) || !MatchesDocumentHash(doc.S("path"), doc.S("hash")))
                    throw new IOException("源文件内容已发生变化，请先重新导入核对，未执行合并。");
                int count = PageCount(doc);
                if (count != doc.I("page_count") || !plan.Where(x => x.Id == id).Select(x => x.Page).Order().SequenceEqual(Enumerable.Range(1, count)))
                    throw new ArgumentException("合并必须包含每份源文档的全部页面，每页恰好一次。");
                var analyzed = db.Rows("SELECT p.number,p.summary FROM pages p JOIN analyses a ON a.doc_id=p.doc_id AND a.page=p.number WHERE p.doc_id=$i ORDER BY p.number", ("$i", id));
                if (!analyzed.Select(x => x.I("number")).SequenceEqual(Enumerable.Range(1, count)) || analyzed.Any(x => JsonNode.Parse(x.S("summary")) is not JsonObject))
                    throw new InvalidOperationException("源文档的逐页内容分析尚未完整。");
                sources.Add(id, doc);
            }
            var provenance = new HashSet<string>();
            foreach (var page in plan)
            {
                var leaf = ResolveSourcePage(page.Id, page.Page, new HashSet<string>()) ?? throw new InvalidDataException("无法核对源页面的完整来源。");
                if (!provenance.Add(leaf.S("source_doc_id") + ":" + leaf.I("source_page")))
                    throw new InvalidOperationException("这些文档包含重叠的原始页面，不能重复合并同一页。");
            }

            string mergedId = Guid.NewGuid().ToString("N"), operationId = Guid.NewGuid().ToString("N");
            string path = SafePath("Inbox/" + Clean(title) + "__" + mergedId[..8] + ".pdf");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string stage = path + ".partial";
            // Source PDFs are imported directly, preserving page size, rotation and vector contents.
            lock (PdfGate)
            {
                var pdfs = new Dictionary<string, PdfDocument>();
                try
                {
                    using var output = new PdfDocument();
                    foreach (var item in plan)
                    {
                        ct.ThrowIfCancellationRequested();
                        var source = sources[item.Id];
                        if (source.S("original").EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                        {
                            if (!pdfs.TryGetValue(item.Id, out var pdf)) pdfs[item.Id] = pdf = PdfReader.Open(source.S("original"), PdfDocumentOpenMode.Import);
                            output.AddPage(pdf.Pages[item.Page - 1]);
                        }
                        else
                        {
                            using var image = XImage.FromFile(source.S("original"));
                            var page = output.AddPage();
                            page.Width = XUnit.FromPoint(image.PointWidth); page.Height = XUnit.FromPoint(image.PointHeight);
                            using var graphics = XGraphics.FromPdfPage(page);
                            graphics.DrawImage(image, 0, 0, image.PointWidth, image.PointHeight);
                        }
                    }
                    output.Save(stage);
                    using var check = Docnet.Core.DocLib.Instance.GetDocReader(File.ReadAllBytes(stage), new Docnet.Core.Models.PageDimensions(160, 200));
                    if (check.GetPageCount() != plan.Count) throw new InvalidDataException("合并文件页数不完整。");
                    for (int page = 0; page < plan.Count; page++)
                    {
                        ct.ThrowIfCancellationRequested();
                        using var rendered = check.GetPageReader(page);
                        if (rendered.GetImage().Length == 0) throw new InvalidDataException("合并文件包含不可读取的页面。");
                    }
                }
                finally { foreach (var pdf in pdfs.Values) pdf.Dispose(); }
            }
            string hash;
            await using (var input = File.OpenRead(stage)) hash = Convert.ToHexString(await SHA256.HashDataAsync(input, ct));
            if (db.Rows("SELECT id FROM documents WHERE hash=$h", ("$h", hash)).Count > 0) throw new InvalidOperationException("已有相同内容的文档，未创建重复合并。");
            string originals = Path.Combine(Root, ".scanarchive-originals"); Directory.CreateDirectory(originals);
            if (File.GetAttributes(originals).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("原始扫描目录不能是链接。");
            string original = Path.Combine(originals, hash + ".pdf");
            if (!File.Exists(original)) { File.Copy(stage, original + ".partial", true); File.Move(original + ".partial", original); }
            File.Move(stage, path, false);
            ct.ThrowIfCancellationRequested();
            string scanned = sources.Values.OrderBy(x => DateTimeOffset.Parse(x.S("scanned"))).First().S("scanned");
            string now = Database.Now;
            var before = new JsonObject { ["sources"] = new JsonArray(sources.Values.Select(x => (JsonNode)x.DeepClone()).ToArray()) };
            var after = new JsonObject { ["page_plan"] = canonical, ["plan_hash"] = planHash, ["page_count"] = plan.Count };
            // Publication is immutable; source visibility and data remain untouched on interruption.
            db.Transaction((connection, transaction) =>
            {
                Run(connection, transaction, "INSERT INTO documents(id,hash,path,original,title,scanned,created,page_count) VALUES($i,$h,$p,$o,$t,$s,$c,$n)", ("$i", mergedId), ("$h", hash), ("$p", path), ("$o", original), ("$t", Clean(title)), ("$s", scanned), ("$c", now), ("$n", plan.Count));
                for (int index = 0; index < plan.Count; index++)
                {
                    var item = plan[index];
                    Run(connection, transaction, "INSERT INTO document_sources VALUES($i,$n,$s,$p,$t); INSERT INTO pages SELECT $i,$n,text,summary FROM pages WHERE doc_id=$s AND number=$p; INSERT INTO analyses SELECT $i,$n,model,analyzed_at FROM analyses WHERE doc_id=$s AND page=$p;", ("$i", mergedId), ("$n", index + 1), ("$s", item.Id), ("$p", item.Page), ("$t", sources[item.Id].S("scanned")));
                }
                Run(connection, transaction, "INSERT INTO operations VALUES($i,$d,'merge','',$p,$b,$a,'applied',$t,$r); INSERT INTO document_merges VALUES($d,$h,$plan,$i); INSERT INTO jobs(id,kind,payload,status,next_run,created,updated) VALUES($job,'index',$d,'pending',$t,$t,$t)", ("$i", operationId), ("$d", mergedId), ("$p", path), ("$b", before.ToJsonString()), ("$a", after.ToJsonString()), ("$t", now), ("$r", reason.Trim()), ("$h", planHash), ("$plan", canonical), ("$job", Guid.NewGuid().ToString("N")));
            });
            WriteMetadata(mergedId);
            db.Log("merge",$"已按页序合并 {sources.Count} 份扫描为 {plan.Count} 页：{Clean(title)}。原始扫描与时间已保留；完成归档后隐藏重复来源。");
            return mergedId;
        }
        finally { Mutation.Release(); }
    }

    static void Run(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string, object)[] args)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        foreach (var (key, value) in args) command.Parameters.AddWithValue(key, value);
        command.ExecuteNonQuery();
    }

    static bool MatchesDocumentHash(string path, string expected)
    {
        using var input = File.OpenRead(path);
        return string.Equals(Convert.ToHexString(SHA256.HashData(input)), expected, StringComparison.OrdinalIgnoreCase);
    }

    public JsonArray SourcePages(string id)
    {
        var doc = db.Doc(id) ?? throw new KeyNotFoundException("文档不存在。");
        bool derived = doc.S("parent_id") != "" || db.Rows("SELECT 1 FROM document_sources WHERE merged_doc_id=$i LIMIT 1", ("$i", id)).Count > 0;
        var result = new JsonArray();
        if (!derived) return result;
        for (int page = 1; page <= doc.I("page_count"); page++)
        {
            var leaf = ResolveSourcePage(id, page, new HashSet<string>());
            if (leaf != null) { leaf["merged_page"] = page; result.Add(leaf); }
        }
        return result;
    }

    JsonObject? ResolveSourcePage(string id, int page, HashSet<string> visited)
    {
        if (visited.Count > 64 || !visited.Add(id + ":" + page)) return null;
        var doc = db.Rows("SELECT id,title,scanned,parent_id,source_pages,page_count FROM documents WHERE id=$i", ("$i", id)).FirstOrDefault(); if (doc == null) return null;
        var source = db.Rows("SELECT * FROM document_sources WHERE merged_doc_id=$i AND merged_page=$p", ("$i", id), ("$p", page)).FirstOrDefault();
        if (source != null) return ResolveSourcePage(source.S("source_doc_id"), source.I("source_page"), visited);
        if (doc.S("parent_id") != "" && db.Rows("SELECT id,page_count FROM documents WHERE id=$i", ("$i", doc.S("parent_id"))).FirstOrDefault() is { } parent)
        {
            try { int[] pages = ParsePages(doc.S("source_pages"), parent.I("page_count")); if (page <= pages.Length) return ResolveSourcePage(parent.S("id"), pages[page - 1], visited); }
            catch (ArgumentException) { }
        }
        return new JsonObject { ["source_doc_id"] = id, ["source_page"] = page, ["scanned"] = doc.S("scanned"), ["title"] = doc.S("title") };
    }

    JsonArray SourceCaptures(string id)
    {
        var ids = SourcePages(id).Select(x => x!.S("source_doc_id")).Append(id).Distinct();
        var captures = ids.SelectMany(source => db.Rows("SELECT scan_id,doc_id,scanned,device,source FROM scan_submissions WHERE doc_id=$i ORDER BY scanned", ("$i", source)))
            .DistinctBy(x => x.S("scan_id")).OrderBy(x => x.S("scanned"));
        return new JsonArray(captures.Select(x => (JsonNode)x).ToArray());
    }

    public async Task ReconcileMergedDocuments(CancellationToken ct = default)
    {
        await Mutation.WaitAsync(ct);
        try { ReconcileReplacementsCore(ct); }
        finally { Mutation.Release(); }
    }

    void ReconcileReplacementsCore(CancellationToken ct)
    {
        var all = db.Rows("SELECT id,hash,parent_id,source_pages,page_count,status,path,original,title,locked FROM documents").ToDictionary(x => x.S("id"));
        var sources = db.Rows("SELECT s.*,o.state FROM document_sources s JOIN document_merges m ON m.doc_id=s.merged_doc_id JOIN operations o ON o.id=m.operation_id");
        var childrenByParent = all.Values.Where(x => x.S("parent_id") != "").ToLookup(x => x.S("parent_id"));
        var sourceIds = sources.Select(x => x.S("source_doc_id")).ToHashSet();
        var busyIds = db.Rows("SELECT DISTINCT payload FROM jobs WHERE status IN ('pending','running')").Select(x => x.S("payload")).ToHashSet();
        var physical = new Dictionary<string, bool>();
        bool Available(JsonObject doc)
        {
            string id = doc.S("id");
            if (physical.TryGetValue(id, out bool valid)) return valid;
            valid = false;
            try
            {
                if (doc.S("status") is "ready" or "superseded" && File.Exists(doc.S("original")) && File.Exists(doc.S("path")))
                {
                    var original = new FileInfo(doc.S("original")); var file = new FileInfo(doc.S("path"));
                    string stamp = $"{original.FullName}|{original.Length}|{original.LastWriteTimeUtc.Ticks}|{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}|{doc.I("page_count")}|{doc.S("hash")}";
                    if (replacementFileChecks.TryGetValue(id, out var checkedFile) && checkedFile.Stamp == stamp) valid = checkedFile.Valid;
                    else
                    {
                        valid = MatchesDocumentHash(doc.S("original"), doc.S("hash")) && MatchesDocumentHash(doc.S("path"), doc.S("hash")) && PageCount(doc) == doc.I("page_count");
                        replacementFileChecks[id] = (stamp, valid);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { valid = false; }
            return physical[id] = valid;
        }
        var mergedCoverage = sources.Where(x => x.S("state") == "applied").GroupBy(x => x.S("merged_doc_id"));
        var mergedSources = new HashSet<string>();
        foreach (var group in mergedCoverage)
        {
            ct.ThrowIfCancellationRequested();
            if (!all.TryGetValue(group.Key, out var output) || !Available(output)) continue;
            var map = group.OrderBy(x => x.I("merged_page")).ToArray();
            if (!map.Select(x => x.I("merged_page")).SequenceEqual(Enumerable.Range(1, output.I("page_count")))) continue;
            bool complete = map.GroupBy(x => x.S("source_doc_id")).All(items => all.TryGetValue(items.Key, out var source) && source.I("locked") == 0 && items.Select(x => x.I("source_page")).Order().SequenceEqual(Enumerable.Range(1, source.I("page_count"))));
            if (complete) foreach (var source in map) mergedSources.Add(source.S("source_doc_id"));
        }
        var candidates = all.Values.Where(x => childrenByParent.Contains(x.S("id")) || sourceIds.Contains(x.S("id"))).ToArray();
        foreach (var doc in candidates)
        {
            ct.ThrowIfCancellationRequested(); string id = doc.S("id"); bool hidden = doc.S("status") == "superseded";
            if (doc.S("status") is not ("ready" or "analyzed" or "superseded")) continue;
            var children = childrenByParent[id].Where(child => child.S("status") != "deleted").ToArray();
            var covered = new HashSet<int>();
            bool split = children.Length >= 2 && doc.I("page_count") > 0 && File.Exists(doc.S("original"));
            if (!hidden && busyIds.Contains(id)) split = false;
            foreach (var child in children)
            {
                if (!split) break;
                try
                {
                    int[] pages = ParsePages(child.S("source_pages"), doc.I("page_count"));
                    if (!Available(child) || pages.Length != child.I("page_count") || pages.Any(page => !covered.Add(page))) split = false;
                }
                catch (ArgumentException) { split = false; }
            }
            split &= covered.Count == doc.I("page_count");
            bool replace = split || mergedSources.Contains(id);
            if (replace == hidden) continue;
            string key = "replacement_status:" + id;
            string status = replace ? "superseded" : db.State(key) ?? "ready";
            if (replace) db.State(key, doc.S("status"));
            db.Exec("UPDATE documents SET status=$s WHERE id=$i", ("$s", status), ("$i", id));
            WriteMetadata(id);
            db.Log("reconcile", replace ? "已验证完整替代文档，隐藏重复来源：" + doc.S("title") : "替代文档不再完整可用，恢复来源：" + doc.S("title"));
        }
    }

    void UndoMergeCore(JsonObject operation)
    {
        string id = operation.S("doc_id"); var doc = db.Doc(id) ?? throw new KeyNotFoundException();
        if (db.Rows("SELECT 1 FROM document_sources s JOIN document_merges m ON m.doc_id=s.merged_doc_id JOIN operations o ON o.id=m.operation_id JOIN documents d ON d.id=m.doc_id WHERE s.source_doc_id=$i AND o.state='applied' AND d.status<>'deleted'", ("$i", id)).Count > 0 ||
            db.Rows("SELECT id FROM documents WHERE parent_id=$i AND status<>'deleted'", ("$i", id)).Count > 0)
            throw new InvalidOperationException("请先撤销后续合并或将拆分结果移入回收站，再撤销此合并。");
        if (db.Rows("SELECT id FROM jobs WHERE payload=$i AND status='running'", ("$i", id)).Count > 0) throw new InvalidOperationException("合并文档正在处理，请完成后再撤销。");
        db.Transaction((connection, transaction) =>
        {
            Run(connection, transaction, "UPDATE operations SET state='undone' WHERE id=$op; INSERT OR IGNORE INTO trash VALUES($i,$s,$t); UPDATE documents SET status='deleted' WHERE id=$i; UPDATE jobs SET status='cancelled' WHERE payload=$i AND status='pending'", ("$op", operation.S("id")), ("$i", id), ("$s", doc.S("status")), ("$t", Database.Now));
        });
        ReconcileReplacementsCore(CancellationToken.None); WriteMetadata(id);
        db.Log("undo", "已撤销跨扫描合并并恢复来源：" + doc.S("title"));
    }

    void ValidateMergeRestore(string id)
    {
        foreach (var source in db.Rows("SELECT DISTINCT source_doc_id FROM document_sources WHERE merged_doc_id=$i", ("$i", id)))
        {
            string sourceId = source.S("source_doc_id"); var doc = db.Doc(sourceId);
            if (doc == null || doc.S("status") is not ("ready" or "analyzed") || doc.I("locked") != 0 ||
                ActiveMergeForSource(sourceId) is { } active && active != id)
                throw new InvalidOperationException("来源已删除、锁定或被其他整理结果替代。请先恢复来源或撤销后续整理，再恢复此合并。");
        }
    }
}
