using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ScanArchive.Server;

/// <summary>Bounded retrieval hints for cross-scan reasoning, never a merge decision.</summary>
public static class ScanRelations
{
    public static JsonObject Find(Database db,string id,int windowMinutes=30,int limit=12)
    {
        var origin=db.Doc(id)??throw new ArgumentException("文档不存在。");
        if(origin.S("status") is "deleted" or "superseded")throw new ArgumentException("请从当前文档库中的文档查找关联扫描。");
        windowMinutes=Math.Clamp(windowMinutes<=0?30:windowMinutes,1,1440);
        limit=Math.Clamp(limit<=0?12:limit,1,20);
        var originalSources=Sources(db,origin);
        var times=originalSources.Select(x=>Time(x!.S("scanned"))).Where(x=>x.HasValue).Select(x=>x!.Value).ToArray();
        if(times.Length==0)return Result(id,windowMinutes,[],false);
        var sourceIds=originalSources.Select(x=>x!.S("id")).ToHashSet(StringComparer.Ordinal);
        var originalPages=Snippets(db,id);
        var originalIds=Identifiers(origin,originalPages);
        // Use persisted scan times (not import/analysis completion times). SQL bounds the
        // candidate pool before reading page metadata; no full-library pairwise matching.
        var pool=db.Rows("""
            WITH stamps(t) AS (SELECT value FROM json_each($times)), moments(doc_id,t) AS (
                SELECT id,scanned FROM documents
                UNION ALL SELECT merged_doc_id,scanned FROM document_sources
                UNION ALL SELECT doc_id,scanned FROM scan_submissions WHERE doc_id<>'')
            SELECT d.*,min(abs(julianday(m.t)-julianday(s.t))*1440) gap
            FROM moments m JOIN documents d ON d.id=m.doc_id JOIN stamps s
            WHERE d.id<>$id AND d.status NOT IN ('deleted','superseded')
              AND abs(julianday(m.t)-julianday(s.t))*1440 <= $window+0.001
            GROUP BY d.id ORDER BY gap,d.scanned,d.id LIMIT 61
            """,("$times",System.Text.Json.JsonSerializer.Serialize(times.Select(x=>x.ToString("O",CultureInfo.InvariantCulture)))),("$id",id),("$window",windowMinutes));
        var results=new List<(JsonObject item,double score)>();
        foreach(var candidate in pool.Take(60))
        {
            var sources=Sources(db,candidate);
            var candidateTimes=sources.Select(x=>Time(x!.S("scanned"))).Where(x=>x.HasValue).Select(x=>x!.Value).ToArray();
            if(candidateTimes.Length==0)continue;
            double gap=candidateTimes.Min(x=>times.Min(t=>Math.Abs((x-t).TotalMinutes)));
            if(gap>windowMinutes)continue;
            bool sameSource=sources.Any(x=>sourceIds.Contains(x!.S("id")));
            // Existing compositions and their inputs may briefly coexist until archival
            // finishes. They are provenance, not another opportunity to combine them.
            if(sources.Any(x=>x!.S("id")==id)||originalSources.Any(x=>x!.S("id")==candidate.S("id")))continue;
            var pages=Snippets(db,candidate.S("id"));
            string[] shared=Identifiers(candidate,pages).Intersect(originalIds,StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).Take(12).ToArray();
            bool pending=candidate.S("status") is not ("ready" or "analyzed");
            var signals=new JsonArray("扫描时间接近，仅用于寻找候选，不能作为合并依据");
            if(sameSource)signals.Add("来自同一次原始扫描的不同片段，需核实是否本来属于同一文件");
            if(shared.Length>0)signals.Add("存在相同字母数字编号候选，需回读原页核实编号含义和页序");
            if(pending)signals.Add("分析尚未完成，后续该文件整理时会重新检查邻近扫描");
            var item=new JsonObject{
                ["id"]=candidate.S("id"),["title"]=candidate.S("title"),["status"]=candidate.S("status"),
                ["analysis_pending"]=pending,["locked"]=candidate.I("locked")==1,["mixed_content"]=candidate.I("mixed_content")==1,
                ["page_count"]=candidate.I("page_count"),["scanned"]=candidate.S("scanned"),["scan_gap_minutes"]=Math.Round(gap,2),
                ["category"]=candidate.S("category"),["summary_excerpt"]=Clip(candidate.S("summary"),1200),
                ["same_scan_source"]=sameSource,["relation_signals"]=signals,
                ["shared_identifiers"]=new JsonArray(shared.Select(x=>(JsonNode?)JsonValue.Create(x)).ToArray()),
                ["page_snippets"]=pages,["scan_sources"]=new JsonArray(sources.Take(20).Select(x=>x!.DeepClone()).ToArray()),["scan_sources_truncated"]=sources.Count>20
            };
            results.Add((item,shared.Length*100+(pending?0:10)-gap/windowMinutes));
        }
        return Result(id,windowMinutes,results.OrderByDescending(x=>x.score).ThenBy(x=>x.item.S("id"),StringComparer.Ordinal).Take(limit).Select(x=>x.item).ToArray(),pool.Count>60||results.Count>limit);
    }

    static JsonObject Result(string id,int minutes,JsonObject[] candidates,bool truncated)=>new(){
        ["document_id"]=id,["window_minutes"]=minutes,["truncated"]=truncated,
        ["notice"]="这些是关联候选，不是合并结论。时间、相同人名、同一商家都不足以证明同一份文件；必须读取全部原页核实编号、页码连续性或正反面结构。",
        ["candidates"]=new JsonArray(candidates.Select(x=>(JsonNode)x).ToArray())
    };

    // Resolve split children and composed documents back to original scan records.
    // A bounded walk also tolerates historical malformed lineage without looping.
    static JsonArray Sources(Database db,JsonObject document)
    {
        var result=new Dictionary<string,JsonObject>(StringComparer.Ordinal);
        var seen=new HashSet<string>(StringComparer.Ordinal);var queue=new Queue<JsonObject>();queue.Enqueue(document);
        while(queue.Count>0&&seen.Count<128)
        {
            var current=queue.Dequeue();string id=current.S("id");if(!seen.Add(id))continue;
            var parents=db.Rows("SELECT DISTINCT source_doc_id FROM document_sources WHERE merged_doc_id=$i LIMIT 128",("$i",id)).Select(x=>x.S("source_doc_id")).ToList();
            if(current.S("parent_id")!="")parents.Add(current.S("parent_id"));
            bool resolved=false;
            foreach(string parent in parents.Distinct(StringComparer.Ordinal))if(db.Doc(parent) is JsonObject source){queue.Enqueue(source);resolved=true;}
            if(resolved)continue;
            var scans=db.Rows("SELECT scanned FROM scan_submissions WHERE doc_id=$i ORDER BY scanned LIMIT 20",("$i",id));
            if(scans.Count==0)scans.Add(new JsonObject{["scanned"]=current.S("scanned")});
            foreach(var scan in scans)result[id+":"+scan.S("scanned")]=new JsonObject{["id"]=id,["scanned"]=scan.S("scanned")};
        }
        return new JsonArray(result.Values.Take(128).Select(x=>(JsonNode)x).ToArray());
    }
    static JsonArray Snippets(Database db,string id)=>new(db.Rows("SELECT number,text,summary FROM pages WHERE doc_id=$i AND (number=(SELECT min(number) FROM pages WHERE doc_id=$i) OR number=(SELECT max(number) FROM pages WHERE doc_id=$i)) ORDER BY number",("$i",id)).Select(x=>(JsonNode)new JsonObject{
        ["number"]=x.I("number"),["text_excerpt"]=Clip(x.S("text"),450),["summary"]=Clip(x.S("summary"),750),["continuity"]=Continuity(x.S("summary"))
    }).ToArray());
    static string Continuity(string summary)
    {
        try{return Clip(JsonNode.Parse(summary)?.S("continuity")??"",900);}catch(System.Text.Json.JsonException){return "";}
    }
    static HashSet<string> Identifiers(JsonObject document,JsonArray pages)
    {
        string text=document.S("title")+" "+Clip(document.S("tags"),4000)+" "+Clip(document.S("summary"),4000)+" "+pages.ToJsonString();
        return Regex.Matches(text,@"(?<![A-Za-z0-9])[A-Za-z0-9][A-Za-z0-9-]{5,39}(?![A-Za-z0-9])",RegexOptions.CultureInvariant)
            .Select(x=>x.Value).Where(x=>x.Any(char.IsDigit)&&!Regex.IsMatch(x,@"^\d{4}-\d{2}-\d{2}$")).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
    static DateTimeOffset? Time(string value)=>DateTimeOffset.TryParse(value,CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out var parsed)?parsed:null;
    static string Clip(string value,int max)=>value.Length<=max?value:value[..max]+"…";
}
