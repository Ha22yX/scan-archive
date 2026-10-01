using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ScanArchive.Server;

/// <summary>Read-only, bounded discovery for an agent. Metadata is not verified page evidence.</summary>
public sealed class LibraryIntelligence(AppSettings settings, Database db, Documents docs)
{
    const string Active = "status NOT IN ('deleted','superseded')";
    const string Notice = "只读诊断与关联线索，不代表已修改文档或已核实原页。共享姓名、商家、分类、日期及编号候选不能单独证明同一文件；合并或拆分前必须读取原页。用户锁定分类保持不变。";

    public JsonObject Audit(string focus = "all", int offset = 0, int limit = 30)
    {
        focus = focus.Trim().ToLowerInvariant();
        if (focus is not ("all" or "analysis" or "indexing" or "organization" or "taxonomy" or "errors"))
            throw new ArgumentException("focus 支持 all、analysis、indexing、organization、taxonomy、errors。");
        offset = Math.Max(0, offset); limit = Math.Clamp(limit, 1, 50);
        // Group by normalized paths, not every pair of documents/categories. These are
        // spelling/spacing candidates only; the agent decides semantic equivalence.
        var categories = db.Rows($"SELECT category,count(*) document_count,sum(locked=1) locked_count FROM documents WHERE {Active} AND trim(category)<>'' GROUP BY category ORDER BY category");
        var duplicateGroups = categories.GroupBy(x => NormalizeCategory(x.S("category")), StringComparer.Ordinal)
            .Where(g => g.Count() > 1).OrderBy(g => g.Key, StringComparer.Ordinal).ToArray();
        var suspectCategories = duplicateGroups.SelectMany(g => g.Select(x => x.S("category"))).ToArray();
        var parameters = new (string, object?)[] { ("$model", settings.Current.EmbeddingModel), ("$categories", JsonSerializer.Serialize(suspectCategories)) };
        string cte = AuditCte;
        var totals = db.Rows(cte + """
            SELECT count(*) active_documents,coalesce(sum(locked=1),0) locked_documents,
              coalesce(sum(has_error),0) errors,coalesce(sum(needs_analysis),0) incomplete_analysis,
              coalesce(sum(needs_index),0) incomplete_index,coalesce(sum(missing_lexical),0) missing_lexical_index,
              coalesce(sum(missing_vectors),0) missing_current_embeddings,
              coalesce(sum(mixed_content=1),0) mixed_scans,coalesce(sum(uncategorized),0) uncategorized,
              coalesce(sum(poor_title),0) poor_titles,coalesce(sum(taxonomy_candidate),0) taxonomy_candidates,
              coalesce(sum(pending_jobs>0),0) documents_with_active_jobs,
              coalesce(sum(has_error OR needs_analysis OR needs_index OR mixed_content=1 OR uncategorized OR poor_title OR taxonomy_candidate),0) actionable_documents
            FROM health
            """, parameters)[0];
        string predicate = focus switch
        {
            "analysis" => "needs_analysis=1", "indexing" => "needs_index=1", "errors" => "has_error=1",
            "taxonomy" => "taxonomy_candidate=1", "organization" => "mixed_content=1 OR uncategorized=1 OR poor_title=1 OR taxonomy_candidate=1",
            _ => "has_error=1 OR needs_analysis=1 OR needs_index=1 OR mixed_content=1 OR uncategorized=1 OR poor_title=1 OR taxonomy_candidate=1"
        };
        int total = db.Rows(cte + $"SELECT count(*) n FROM health WHERE {predicate}", parameters)[0].I("n");
        var rows = db.Rows(cte + $"""
            SELECT * FROM health WHERE {predicate}
            ORDER BY has_error DESC,needs_analysis DESC,needs_index DESC,mixed_content DESC,scanned,id LIMIT $limit OFFSET $offset
            """, parameters.Append(("$limit", (object?)limit)).Append(("$offset", (object?)offset)).ToArray());
        var items = new JsonArray();
        foreach (var row in rows)
        {
            var issues = new JsonArray(); var suggestions = new JsonArray();
            if (row.I("has_error") == 1) { issues.Add("processing_error"); suggestions.Add("先读取错误信息与处理记录；排除故障后重试对应任务。"); }
            if (row.I("needs_analysis") == 1) { issues.Add("incomplete_analysis"); suggestions.Add("补全缺失页面或文档总体概括；保留已完成的页面分析。"); }
            if (row.I("missing_lexical") == 1) { issues.Add("missing_lexical_index"); suggestions.Add("补齐关键词索引及漏索引页面，避免能预览却无法搜索。"); }
            if (row.I("missing_vectors") == 1) { issues.Add("missing_current_embeddings"); suggestions.Add("补齐当前向量模型索引；保留可用的关键词检索。"); }
            if (row.I("mixed_content") == 1) { issues.Add("mixed_scan"); suggestions.Add("读取全部原页核对独立文档边界，再提出覆盖所有页面的拆分方案。"); }
            if (row.I("uncategorized") == 1) { issues.Add("uncategorized"); suggestions.Add("读取内容后优先复用已有分类。"); }
            if (row.I("poor_title") == 1) { issues.Add("generic_title"); suggestions.Add("使用内容明确的标题，并保留原扫描时间与编号。"); }
            if (row.I("taxonomy_candidate") == 1) { issues.Add("taxonomy_spelling_candidate"); suggestions.Add("核对疑似同义目录中的内容；名称相近不能直接批量移动。"); }
            var item = Summary(row);
            item["issues"] = issues; item["suggested_steps"] = suggestions;
            item["analysis"] = new JsonObject { ["pages_completed"] = row.I("analyzed_pages"), ["metadata_completed"] = row.I("metadata_completed") == 1 };
            item["index"] = new JsonObject { ["chunks"] = row.I("chunk_count"), ["lexical_chunks"] = row.I("lexical_chunks"), ["indexed_pages"] = row.I("indexed_pages"), ["current_model_chunks"] = row.I("current_vectors"), ["current_model"] = settings.Current.EmbeddingModel };
            item["error_excerpt"] = Clip(row.S("error"), 500);
            item["pending_jobs"] = row.I("pending_jobs");
            item["automation_constraint"] = row.I("pending_jobs") > 0 ? "wait_for_active_jobs" : row.I("locked") == 1 ? "preserve_user_locked_organization" : "inspect_before_mutation";
            items.Add(item);
        }
        var taxonomy = new JsonArray(duplicateGroups.Take(10).Select(group => (JsonNode)new JsonObject
        {
            ["signal"] = "category_spelling_or_spacing", ["proof_of_equivalence"] = false,
            ["category_count"] = group.Count(), ["document_count"] = group.Sum(x => x.I("document_count")),
            ["locked_documents"] = group.Sum(x => x.I("locked_count")),
            ["categories"] = new JsonArray(group.Take(8).Select(x => (JsonNode)x.DeepClone()).ToArray()), ["categories_truncated"] = group.Count() > 8
        }).ToArray());
        return new JsonObject
        {
            ["read_only"] = true, ["notice"] = Notice, ["focus"] = focus, ["totals"] = totals,
            ["total_matching_documents"] = total, ["offset"] = offset, ["limit"] = limit,
            ["has_more"] = (long)offset + rows.Count < total, ["next_offset"] = (long)offset + rows.Count < total ? offset + rows.Count : null,
            ["items"] = items, ["taxonomy_group_count"] = duplicateGroups.Length, ["taxonomy_groups"] = taxonomy,
            ["taxonomy_groups_truncated"] = duplicateGroups.Length > 10,
            ["index_coverage_note"] = "按已存储的页面分析、关键词索引及当前向量模型记录诊断；未调用 AI 或重新读取扫描图像。"
        };
    }

    // Aggregate once per table. Joins remain one row per document, so issue totals
    // cannot be multiplied by page/chunk/job counts. Hidden originals are excluded.
    const string AuditCte = """
        WITH page_health AS (
          SELECT p.doc_id,count(*) stored_pages,
            sum(a.page IS NOT NULL AND p.number BETWEEN 1 AND d.page_count) analyzed_pages
          FROM pages p JOIN documents d ON d.id=p.doc_id LEFT JOIN analyses a ON a.doc_id=p.doc_id AND a.page=p.number
          WHERE d.status NOT IN ('deleted','superseded') GROUP BY p.doc_id),
        lexical_health AS (SELECT cast(chunk_id AS INTEGER) chunk_id,max(trim(tokens)<>'') has_tokens FROM search_fts GROUP BY cast(chunk_id AS INTEGER)),
        chunk_health AS (
          SELECT c.doc_id,count(*) chunk_count,count(DISTINCT CASE WHEN c.page BETWEEN 1 AND d.page_count THEN c.page END) indexed_pages,
            sum(coalesce(f.has_tokens,0)) lexical_chunks,
            sum(CASE WHEN c.model=$model AND json_valid(c.embedding) THEN CASE WHEN json_type(c.embedding)='array' AND json_array_length(c.embedding)>0
              AND NOT EXISTS(SELECT 1 FROM json_each(c.embedding) v WHERE v.type NOT IN ('integer','real'))
              AND EXISTS(SELECT 1 FROM json_each(c.embedding) v WHERE v.atom<>0) THEN 1 ELSE 0 END ELSE 0 END) current_vectors
          FROM chunks c JOIN documents d ON d.id=c.doc_id LEFT JOIN lexical_health f ON f.chunk_id=c.id
          WHERE d.status NOT IN ('deleted','superseded') GROUP BY c.doc_id),
        job_health AS (SELECT payload,count(*) pending_jobs FROM jobs WHERE status IN ('pending','running') GROUP BY payload),
        base AS (
          SELECT d.*,coalesce(p.analyzed_pages,0) analyzed_pages,coalesce(c.chunk_count,0) chunk_count,
            coalesce(c.indexed_pages,0) indexed_pages,coalesce(c.lexical_chunks,0) lexical_chunks,coalesce(c.current_vectors,0) current_vectors,
            coalesce(j.pending_jobs,0) pending_jobs,
            EXISTS(SELECT 1 FROM analyses a WHERE a.doc_id=d.id AND a.page=0) AND trim(d.summary)<>'' metadata_completed
          FROM documents d LEFT JOIN page_health p ON p.doc_id=d.id LEFT JOIN chunk_health c ON c.doc_id=d.id LEFT JOIN job_health j ON j.payload=d.id
          WHERE d.status NOT IN ('deleted','superseded')),
        health AS (
          SELECT *, status='error' OR trim(error)<>'' has_error,
            page_count=0 OR analyzed_pages<page_count OR metadata_completed=0 needs_analysis,
            chunk_count=0 OR lexical_chunks<chunk_count OR indexed_pages<page_count missing_lexical,
            chunk_count=0 OR current_vectors<chunk_count missing_vectors,
            chunk_count=0 OR lexical_chunks<chunk_count OR indexed_pages<page_count OR current_vectors<chunk_count needs_index,
            trim(category)='' uncategorized,
            trim(title)='' OR title IN ('扫描文件','未命名','Untitled','Scan','Document') OR title GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]_[0-9][0-9]-[0-9][0-9]-[0-9][0-9]*' poor_title,
            category IN (SELECT value FROM json_each($categories)) taxonomy_candidate
          FROM base)
        """;

    public JsonObject Compare(string ids)
    {
        if (ids.Length > 1000) throw new ArgumentException("最多比较 6 份文档。");
        var requested = ids.Split([',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (requested.Length is < 2 or > 6 || requested.Distinct(StringComparer.Ordinal).Count() != requested.Length)
            throw new ArgumentException("请提供 2 至 6 个不同的文档 ID，以逗号或换行分隔。");
        var records = requested.Select(id => db.Doc(id) ?? throw new KeyNotFoundException("文档不存在：" + id)).ToArray();
        var profiles = new JsonArray(); var identifiers = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var origins = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var document in records)
        {
            string id = document.S("id");
            int pageRows = db.Rows("SELECT count(*) n FROM pages WHERE doc_id=$i", ("$i", id))[0].I("n");
            // Long scans include both boundaries and declare omitted interior pages.
            var pages = db.Rows("""
                SELECT number,substr(text,1,900) text,substr(summary,1,16000) summary FROM pages WHERE doc_id=$i
                AND (number IN (SELECT number FROM pages WHERE doc_id=$i ORDER BY number LIMIT 20)
                  OR number IN (SELECT number FROM pages WHERE doc_id=$i ORDER BY number DESC LIMIT 20)) ORDER BY number
                """, ("$i", id));
            var pageMap = new JsonArray(); var sourceIds = new HashSet<string>(StringComparer.Ordinal);
            var identifierSet = IdentifierCandidates(document.S("title") + " " + Clip(document.S("tags"), 3000) + " " + Clip(document.S("summary"), 6000));
            foreach (var page in pages)
            {
                JsonObject? analysis = null;
                try { analysis = JsonNode.Parse(page.S("summary")) as JsonObject; } catch (JsonException) { }
                string continuity = Clip(analysis?.S("continuity") ?? "", 700);
                identifierSet.UnionWith(IdentifierCandidates(page.S("text") + " " + continuity + " " + Clip(analysis?.S("entities") ?? "", 1500) + " " + Clip(analysis?.S("dates_and_numbers") ?? "", 1000)));
                var origin = PageOrigin(id, page.I("number"));
                if (origin != null) sourceIds.Add(origin.S("source_doc_id"));
                pageMap.Add(new JsonObject { ["page"] = page.I("number"), ["text_excerpt"] = Clip(page.S("text"), 450), ["summary_excerpt"] = Clip(analysis?.S("summary") ?? page.S("summary"), 450), ["continuity"] = continuity, ["scan_origin"] = origin });
            }
            identifiers[id] = identifierSet; origins[id] = sourceIds;
            var profile = Summary(document);
            profile["document_date"] = document.S("document_date"); profile["summary_excerpt"] = Clip(document.S("summary"), 1800);
            profile["identifier_candidates"] = new JsonArray(identifierSet.Order(StringComparer.Ordinal).Take(40).Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
            profile["identifiers_truncated"] = identifierSet.Count > 40;
            profile["page_map"] = pageMap; profile["analyzed_page_records"] = pageRows; profile["pages_omitted"] = Math.Max(0, pageRows - pages.Count);
            profile["pages_without_analysis"] = Math.Max(0, document.I("page_count") - pageRows);
            int activeJobCount = db.Rows("SELECT count(*) n FROM jobs WHERE payload=$i AND status IN ('pending','running')", ("$i", id))[0].I("n");
            profile["active_job_count"] = activeJobCount;
            profile["active_jobs"] = new JsonArray(db.Rows("SELECT kind,status FROM jobs WHERE payload=$i AND status IN ('pending','running') ORDER BY created LIMIT 10", ("$i", id)).Select(x => (JsonNode)x).ToArray());
            profile["active_jobs_truncated"] = activeJobCount > 10;
            profile["merge_reserved_by"] = docs.ActiveMergeForSource(id);
            profile["verified_page_evidence"] = false;
            profiles.Add(profile);
        }
        var pairs = new JsonArray();
        for (int i = 0; i < records.Length; i++)
            for (int j = i + 1; j < records.Length; j++)
            {
                var a = records[i]; var b = records[j];
                var shared = identifiers[a.S("id")].Intersect(identifiers[b.S("id")], StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                var pair = new JsonObject
                {
                    ["left_id"] = a.S("id"), ["right_id"] = b.S("id"),
                    ["same_registered_byte_hash"] = a.S("hash").Length > 0 && string.Equals(a.S("hash"), b.S("hash"), StringComparison.OrdinalIgnoreCase),
                    ["shared_identifier_candidates"] = new JsonArray(shared.Take(16).Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()), ["shared_identifiers_truncated"] = shared.Length > 16,
                    ["same_category"] = a.S("category") != "" && string.Equals(a.S("category"), b.S("category"), StringComparison.Ordinal),
                    ["shared_scan_origin"] = origins[a.S("id")].Overlaps(origins[b.S("id")]),
                    ["merge_eligibility_verified"] = false,
                    ["next_step"] = "读取待比较的全部原页，核对编号含义、页序、内容边界及正反面结构；相同字节哈希仅来自登记记录，未重新读取文件。"
                };
                if (DateTimeOffset.TryParse(a.S("scanned"), out var ta) && DateTimeOffset.TryParse(b.S("scanned"), out var tb)) pair["scan_gap_minutes"] = Math.Round(Math.Abs((ta - tb).TotalMinutes), 2);
                pairs.Add(pair);
            }
        return new JsonObject { ["read_only"] = true, ["notice"] = Notice, ["verified_page_evidence"] = false, ["documents"] = profiles, ["pairs"] = pairs };
    }

    // Resolve only the bounded displayed pages, rather than materializing provenance
    // for every page in a very large scan. Cycles/incomplete historical lineage stop.
    JsonObject? PageOrigin(string id, int page)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        for (int depth = 0; depth < 24 && visited.Add(id + ":" + page); depth++)
        {
            var document = db.Rows("SELECT id,title,scanned,page_count,parent_id,source_pages FROM documents WHERE id=$i", ("$i", id)).FirstOrDefault();
            if (document == null || page < 1 || page > document.I("page_count")) return null;
            var source = db.Rows("SELECT source_doc_id,source_page FROM document_sources WHERE merged_doc_id=$i AND merged_page=$p", ("$i", id), ("$p", page)).FirstOrDefault();
            if (source != null) { id = source.S("source_doc_id"); page = source.I("source_page"); continue; }
            if (document.S("parent_id") != "")
            {
                string parentId = document.S("parent_id");
                var parent = db.Rows("SELECT page_count FROM documents WHERE id=$i", ("$i", parentId)).FirstOrDefault();
                if (parent == null) return null;
                try
                {
                    int[] mapping = Documents.ParsePages(document.S("source_pages"), parent.I("page_count"));
                    if (page > mapping.Length) return null;
                    id = parentId; page = mapping[page - 1]; continue;
                }
                catch (ArgumentException) { return null; }
            }
            return new JsonObject { ["source_doc_id"] = id, ["source_page"] = page, ["scanned"] = document.S("scanned"), ["title"] = Clip(document.S("title"), 200) };
        }
        return null;
    }
    static JsonObject Summary(JsonObject document) => new() { ["id"] = document.S("id"), ["title"] = Clip(document.S("title"), 250), ["category"] = Clip(document.S("category"), 300), ["status"] = document.S("status"), ["locked"] = document.I("locked") == 1, ["mixed_content"] = document.I("mixed_content") == 1, ["page_count"] = document.I("page_count"), ["scanned"] = document.S("scanned") };
    static string NormalizeCategory(string value) => Regex.Replace(value.Normalize(NormalizationForm.FormKC).Replace('\\', '/').ToLowerInvariant(), @"[\s_\-]+", "", RegexOptions.CultureInvariant);
    static HashSet<string> IdentifierCandidates(string value) => Regex.Matches(value.Normalize(NormalizationForm.FormKC), @"(?<![A-Za-z0-9])[A-Za-z0-9][A-Za-z0-9\-]{5,39}(?![A-Za-z0-9])", RegexOptions.CultureInvariant)
        .Select(x => x.Value.ToUpperInvariant()).Where(x => x.Any(char.IsDigit) && !Regex.IsMatch(x, @"^\d{4}-\d{2}-\d{2}$", RegexOptions.CultureInvariant)).ToHashSet(StringComparer.Ordinal);
    static string Clip(string value, int length) => value.Length <= length ? value : value[..length] + "…";
}
