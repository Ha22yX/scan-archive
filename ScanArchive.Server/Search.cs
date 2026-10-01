using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ScanArchive.Server;

public sealed class Search(AppSettings settings,Database db,OpenAi ai)
{
    public static string Tokens(string text)
    {
        text=text.Normalize(NormalizationForm.FormKC).ToLowerInvariant();var tokens=new List<string>();
        foreach(Match m in Regex.Matches(text,@"[\p{IsCJKUnifiedIdeographs}]+|[\p{L}\p{N}]+"))
        {
            string s=m.Value;
            if(s.Any(c=>c is >= '\u4e00' and <= '\u9fff'))
            {
                if(s.Length==1)tokens.Add(s);
                for(int i=0;i<s.Length-1;i++)tokens.Add(s.Substring(i,2));
            }
            else tokens.Add(s);
        }
        return string.Join(' ',tokens.Distinct());
    }
    public static IEnumerable<string> Chunk(string text,int size=1600,int overlap=200)
    {
        if(string.IsNullOrWhiteSpace(text))yield break;
        for(int i=0;i<text.Length;i+=size-overlap){yield return text.Substring(i,Math.Min(size,text.Length-i));if(i+size>=text.Length)break;}
    }
    public void AddChunk(string doc,int page,string text,float[]? vector,string model)
    {
        db.Transaction((c,tx)=>{
            using var cmd=c.CreateCommand();cmd.Transaction=tx;
            cmd.CommandText="INSERT INTO chunks(doc_id,page,text,embedding,model) VALUES($d,$p,$t,$v,$m); SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$d",doc);cmd.Parameters.AddWithValue("$p",page);cmd.Parameters.AddWithValue("$t",text);cmd.Parameters.AddWithValue("$v",vector==null?DBNull.Value:JsonSerializer.Serialize(vector));cmd.Parameters.AddWithValue("$m",model);
            long id=Convert.ToInt64(cmd.ExecuteScalar());cmd.Parameters.Clear();cmd.CommandText="INSERT INTO search_fts(chunk_id,tokens) VALUES($id,$tokens)";
            cmd.Parameters.AddWithValue("$id",id);cmd.Parameters.AddWithValue("$tokens",Tokens(text));cmd.ExecuteNonQuery();
        });
    }
    public void ReplaceChunks(string doc,IEnumerable<(int page,string text,float[]? vector,string model)> chunks)
    {
        db.Transaction((c,tx)=>{
            using var cmd=c.CreateCommand();cmd.Transaction=tx;
            cmd.CommandText="DELETE FROM search_fts WHERE chunk_id IN (SELECT id FROM chunks WHERE doc_id=$d); DELETE FROM chunks WHERE doc_id=$d;";cmd.Parameters.AddWithValue("$d",doc);cmd.ExecuteNonQuery();
            foreach(var chunk in chunks){
                cmd.Parameters.Clear();cmd.CommandText="INSERT INTO chunks(doc_id,page,text,embedding,model) VALUES($d,$p,$t,$v,$m); SELECT last_insert_rowid();";
                cmd.Parameters.AddWithValue("$d",doc);cmd.Parameters.AddWithValue("$p",chunk.page);cmd.Parameters.AddWithValue("$t",chunk.text);cmd.Parameters.AddWithValue("$v",chunk.vector==null?DBNull.Value:JsonSerializer.Serialize(chunk.vector));cmd.Parameters.AddWithValue("$m",chunk.model);
                long id=Convert.ToInt64(cmd.ExecuteScalar());cmd.Parameters.Clear();cmd.CommandText="INSERT INTO search_fts(chunk_id,tokens) VALUES($i,$t)";cmd.Parameters.AddWithValue("$i",id);cmd.Parameters.AddWithValue("$t",Tokens(chunk.text));cmd.ExecuteNonQuery();
            }
        });
    }
    public void Clear(string id)
    {
        db.Transaction((c,tx)=>{
            using var cmd=c.CreateCommand();cmd.Transaction=tx;
            cmd.CommandText="DELETE FROM search_fts WHERE chunk_id IN (SELECT id FROM chunks WHERE doc_id=$id); DELETE FROM chunks WHERE doc_id=$id;";cmd.Parameters.AddWithValue("$id",id);cmd.ExecuteNonQuery();
        });
    }
    public static double Cosine(float[] a,float[] b)
    {
        if(a.Length!=b.Length||a.Length==0)return -1;
        double dot=0,aa=0,bb=0;for(int i=0;i<a.Length;i++){dot+=a[i]*b[i];aa+=a[i]*a[i];bb+=b[i]*b[i];}return aa==0||bb==0?-1:dot/Math.Sqrt(aa*bb);
    }

    // Existing callers retain results/mode/warning and the best-page fields; discovery now groups by document.
    public Task<JsonObject> Find(string query,string category="",string from="",string to="",int limit=12,CancellationToken ct=default)
        =>Research(query,category,from,to,"scanned",limit,0,ct);

    public async Task<JsonObject> Research(string queries,string category="",string from="",string to="",string dateField="scanned",int limit=12,int offset=0,CancellationToken ct=default)
    {
        var texts=queries.Split('\n',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if(texts.Length>6)throw new ArgumentException("一次最多搜索 6 个互补查询。");
        if(texts.Any(q=>q.Length>1000))throw new ArgumentException("搜索词过长。");
        if(dateField is not ("scanned" or "document_date"))throw new ArgumentException("日期字段必须是 scanned 或 document_date。");
        ValidateDate(from);ValidateDate(to);if(from.Length>0&&to.Length>0&&string.CompareOrdinal(from,to)>0)throw new ArgumentException("开始日期不能晚于结束日期。");
        limit=Math.Clamp(limit,1,40);offset=Math.Max(0,offset);
        if(texts.Length==0)return new(){["results"]=new JsonArray(),["mode"]="empty",["warning"]="",["total"]=0,["has_more"]=false};
        var queryPlans=texts.Select(q=>new Query(q)).ToArray();
        string filter=$" WHERE d.status NOT IN ('deleted','superseded') AND ($cat='' OR d.category=$cat OR substr(d.category,1,length($cat)+1)=$cat||'/') AND ($from='' OR substr(d.{dateField},1,10)>=$from) AND ($to='' OR (d.{dateField}<>'' AND substr(d.{dateField},1,10)<=$to)) ";
        var args=new (string,object?)[]{("$cat",category),("$from",from),("$to",to)};
        // Deliberately no newest-N or top-chunk cutoff: long documents cannot crowd older documents out.
        var documents=db.Rows("SELECT d.id,d.title,d.category,d.summary,d.tags,d.scanned,d.document_date,d.page_count,d.status FROM documents d"+filter,args);
        var pages=db.Rows("SELECT p.doc_id,p.number page,p.text,p.summary FROM pages p JOIN documents d ON d.id=p.doc_id"+filter,args);
        var chunks=db.Rows("SELECT c.id chunk_id,c.doc_id,c.page,c.text,c.embedding,c.model FROM chunks c JOIN documents d ON d.id=c.doc_id"+filter,args);
        int active=db.Rows("SELECT count(*) n FROM documents WHERE status NOT IN ('deleted','superseded')")[0].I("n");
        var warnings=new List<string>();
        var vectors=new float[]?[texts.Length];int embedded=0;
        if(ai.Available&&chunks.Any(c=>c.S("embedding").Length>0&&c.S("model")==settings.Current.EmbeddingModel))
        {
            using var slots=new SemaphoreSlim(3);
            var failures=new string?[texts.Length];
            await Task.WhenAll(texts.Select(async(q,i)=>{
                await slots.WaitAsync(ct);
                try{var vector=await ai.Embed(q,ct);if(vector.Length==0||vector.Any(v=>!float.IsFinite(v))||vector.All(v=>v==0))throw new InvalidDataException("查询语义向量无效。");vectors[i]=vector;Interlocked.Increment(ref embedded);}
                catch(OperationCanceledException){throw;}
                catch(Exception ex){failures[i]=ex.Message;}
                finally{slots.Release();}
            }));
            if(failures.Any(f=>f!=null))warnings.Add("部分语义查询不可用，已保留全库文字、编号和元数据搜索。"+string.Join("；",failures.Where(f=>f!=null).Distinct()));
        }
        else if(!ai.Available)warnings.Add("未配置语义搜索；已搜索全库文字、编号和元数据。");
        else if(documents.Count>0)warnings.Add("当前范围尚无可用语义索引；已搜索全库文字、编号和元数据。");
        var byDoc=documents.ToDictionary(d=>d.S("id"),d=>new DocumentHit(d));
        void Match(string doc,int page,string text,string source,long chunk=0,float[]? vector=null)
        {
            if(!byDoc.TryGetValue(doc,out var hit)||string.IsNullOrWhiteSpace(text))return;
            for(int i=0;i<queryPlans.Length;i++)
            {
                var q=queryPlans[i];var match=q.Match(text);
                if(vector!=null&&vectors[i]!=null&&!q.IdentifierOnly)
                {
                    double cosine=Cosine(vector,vectors[i]!);
                    if(double.IsFinite(cosine)&&cosine>=0.25)match=match with{Score=match.Score+cosine*18,Semantic=true};
                }
                if(match.Score<=0)continue;
                hit.Queries.Add(i);
                var entry=new PageHit(page,text,source,chunk,match,q);
                if(!hit.Pages.TryGetValue(page,out var current)||entry.Match.Score>current.Match.Score)hit.Pages[page]=entry;
            }
        }
        foreach(var row in documents){ct.ThrowIfCancellationRequested();Match(row.S("id"),0,string.Join('\n',row.S("title"),row.S("category"),row.S("tags"),row.S("summary")),"metadata");}
        foreach(var row in pages){ct.ThrowIfCancellationRequested();Match(row.S("doc_id"),row.I("page"),row.S("text")+"\n"+SummaryText(row.S("summary")),"page_text");}
        int validVectors=0,invalidVectors=0;int dimensions=vectors.FirstOrDefault(v=>v!=null)?.Length??0;
        var vectorPages=new HashSet<(string,int)>();
        foreach(var row in chunks)
        {
            ct.ThrowIfCancellationRequested();float[]? vector=null;
            if(row.S("embedding").Length>0&&row.S("model")==settings.Current.EmbeddingModel)
            {
                try{vector=JsonSerializer.Deserialize<float[]>(row.S("embedding"));if(vector==null||vector.Length==0||vector.Any(v=>!float.IsFinite(v))||vector.All(v=>v==0)||(dimensions>0&&vector.Length!=dimensions))throw new JsonException();validVectors++;vectorPages.Add((row.S("doc_id"),row.I("page")));}
                catch(JsonException){invalidVectors++;vector=null;}
            }
            Match(row.S("doc_id"),row.I("page"),row.S("text"),"indexed_page",long.Parse(row.S("chunk_id")),vector);
        }
        if(invalidVectors>0)warnings.Add($"{invalidVectors} 个语义索引无效；对应文字仍参与搜索。");
        var readable=pages.Where(p=>!string.IsNullOrWhiteSpace(p.S("text"))||!string.IsNullOrWhiteSpace(p.S("summary"))).Select(p=>(p.S("doc_id"),p.I("page"))).Concat(chunks.Where(c=>!string.IsNullOrWhiteSpace(c.S("text"))).Select(c=>(c.S("doc_id"),c.I("page")))).Distinct().ToArray();
        var readableDocs=readable.Select(p=>p.Item1).ToHashSet();
        int expected=documents.Sum(d=>d.I("page_count")),missing=Math.Max(0,expected-readable.Length);
        if(missing>0)warnings.Add($"{missing} 页尚无可搜索文字，未找到不代表这些页面不存在相关内容。");
        if(vectorPages.Count<readable.Length)warnings.Add($"{readable.Length-vectorPages.Count} 页尚无当前模型的语义索引，仍可按文字和编号查找。");
        var ranked=byDoc.Values.Where(d=>d.Pages.Count>0).OrderByDescending(d=>d.Score).ThenBy(d=>d.Doc.S("id"),StringComparer.Ordinal).ToArray();
        var results=new JsonArray();foreach(var hit in ranked.Skip(offset).Take(limit))results.Add(Result(hit));
        bool more=(long)offset+results.Count<ranked.Length;
        return new(){
            ["results"]=results,["mode"]=embedded>0?"hybrid":"keyword",["warning"]=string.Join("\n",warnings),["warnings"]=Strings(warnings),
            ["queries"]=Strings(texts),["total"]=ranked.Length,["offset"]=offset,["limit"]=limit,["has_more"]=more,["next_offset"]=more?offset+results.Count:null,["truncated"]=more,
            ["scope"]="All active documents matching the filters; OCR, page summaries, metadata and current-model embeddings. Search hits are discovery candidates, not verified original-page evidence.",
            ["date_field"]=dateField,["coverage"]=new JsonObject{["active_documents"]=active,["filtered_documents"]=documents.Count,["expected_pages"]=expected,["searchable_pages"]=readable.Length,["missing_text_pages"]=missing,["indexed_pages"]=chunks.Select(c=>(c.S("doc_id"),c.I("page"))).Distinct().Count(),["semantic_pages"]=vectorPages.Count,["semantic_chunks"]=validVectors,["semantic_queries_completed"]=embedded,["queries_requested"]=texts.Length,["metadata_only_documents"]=documents.Count(d=>!readableDocs.Contains(d.S("id"))),["full_library_scanned"]=true}
        };
    }

    public JsonObject FindInDocument(string id,string query,int offset=0,int limit=10)
    {
        var doc=db.Doc(id)??throw new KeyNotFoundException("文档不存在。");
        if(doc.S("status")=="deleted")throw new InvalidOperationException("文档已删除，请先恢复。");
        query=query.Trim();if(query.Length is 0 or >1000)throw new ArgumentException("请输入 1–1000 字符的文档内搜索词。");
        offset=Math.Max(0,offset);limit=Math.Clamp(limit,1,40);var q=new Query(query);
        var hits=new Dictionary<int,PageHit>();
        var pages=db.Rows("SELECT number page,text,summary FROM pages WHERE doc_id=$i ORDER BY number",("$i",id));
        var chunks=db.Rows("SELECT id chunk_id,page,text FROM chunks WHERE doc_id=$i ORDER BY page,id",("$i",id));
        foreach(var row in pages.Concat(chunks))
        {
            string text=row.S("text")+"\n"+SummaryText(row.S("summary"));var match=q.Match(text);int page=row.I("page");
            if(page<1||match.Score<=0)continue;
            var hit=new PageHit(page,text,"page_text",row.I("chunk_id"),match,q);
            if(!hits.TryGetValue(page,out var prior)||hit.Match.Score>prior.Match.Score)hits[page]=hit;
        }
        var ordered=hits.Values.OrderBy(h=>h.Page).ToArray();var results=new JsonArray();foreach(var hit in ordered.Skip(offset).Take(limit))results.Add(PageResult(id,hit));
        int searchable=pages.Where(p=>(p.S("text")+p.S("summary")).Trim().Length>0).Select(p=>p.I("page")).Concat(chunks.Where(c=>c.S("text").Trim().Length>0).Select(c=>c.I("page"))).Distinct().Count();
        bool more=(long)offset+results.Count<ordered.Length;
        return new(){["doc_id"]=id,["query"]=query,["results"]=results,["total"]=ordered.Length,["offset"]=offset,["has_more"]=more,["next_offset"]=more?offset+results.Count:null,["searched_pages"]=searchable,["page_count"]=doc.I("page_count"),["missing_text_pages"]=Math.Max(0,doc.I("page_count")-searchable),["requires_original_verification"]=true,["scope"]="All stored page text and page analyses; document metadata is not treated as matching page evidence."};
    }

    sealed class DocumentHit(JsonObject doc)
    {
        public JsonObject Doc=doc;
        public Dictionary<int,PageHit> Pages=[];
        public HashSet<int> Queries=[];
        public double Score=>Pages.Values.Max(p=>p.Match.Score)+(Queries.Count-1)*2;
    }
    sealed record PageHit(int Page,string Text,string Source,long Chunk,TextMatch Match,Query Query);
    readonly record struct TextMatch(double Score,bool Identifier=false,bool Phrase=false,bool Lexical=false,bool Semantic=false);
    sealed class Query
    {
        public string Text {get;}
        public string[] Terms {get;}
        public Regex[] Identifiers {get;}
        public bool IdentifierOnly {get;}
        readonly string normalized;
        static readonly HashSet<string> StopWords=new("a an the my me find show where is are of for to in and or please".Split(' '));
        public Query(string text)
        {
            Text=text;normalized=Normalize(text);Terms=Tokens(text).Split(' ',StringSplitOptions.RemoveEmptyEntries).Where(t=>!StopWords.Contains(t)).ToArray();
            var ids=Regex.Matches(normalized,@"(?<![a-z0-9])[a-z0-9]+(?:[-./_][a-z0-9]+)*(?![a-z0-9])").Select(m=>m.Value).Concat(Regex.IsMatch(normalized,@"^[a-z]{1,5}\s+[0-9][a-z0-9]*$")?[normalized]:[]).Where(v=>v.Any(char.IsDigit)&&v.Count(char.IsLetterOrDigit)>=3).Distinct().Take(12).ToArray();
            Identifiers=ids.Select(v=>new Regex(@"(?<![a-z0-9])"+string.Join(@"[\s\p{P}]*",v.Where(char.IsLetterOrDigit).Select(c=>Regex.Escape(c.ToString())))+@"(?![a-z0-9])",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(250))).ToArray();
            IdentifierOnly=ids.Any(id=>Regex.Replace(normalized,@"[^a-z0-9]","")==Regex.Replace(id,@"[^a-z0-9]",""))&&!normalized.Any(c=>c>'\u007f'&&char.IsLetter(c));
        }
        public TextMatch Match(string text)
        {
            string value=Normalize(text);int identifiers=Identifiers.Count(id=>id.IsMatch(value));
            if(IdentifierOnly&&identifiers==0)return default;
            bool phrase=normalized.Length>1&&ContainsPhrase(value,normalized);
            var tokens=Tokens(value).Split(' ',StringSplitOptions.RemoveEmptyEntries).ToHashSet();int matches=Terms.Count(tokens.Contains);
            // Exact identifiers and complete phrases outrank semantic similarity; common words alone cannot beat them.
            double score=identifiers>0?110+10.0*identifiers/Identifiers.Length:0;
            if(phrase)score+=55;
            if(matches>0)score+=24.0*matches/Math.Max(1,Terms.Length);
            return new(score,identifiers>0,phrase,matches>0);
        }
        public int Position(string text)
        {
            foreach(var id in Identifiers){var match=id.Match(Normalize(text));if(match.Success)return match.Index;}
            int position=Normalize(text).IndexOf(normalized,StringComparison.Ordinal);if(position>=0)return position;
            return Terms.Select(t=>Normalize(text).IndexOf(t,StringComparison.Ordinal)).Where(i=>i>=0).DefaultIfEmpty(0).Min();
        }
    }
    static string Normalize(string value)=>Regex.Replace(Regex.Replace(value.Normalize(NormalizationForm.FormKC).ToLowerInvariant(),@"\p{Pd}","-"),@"\s+"," ");
    static bool ContainsPhrase(string text,string query)
    {
        int start=0;
        while(start<=text.Length-query.Length)
        {
            int at=text.IndexOf(query,start,StringComparison.Ordinal);if(at<0)return false;
            bool left=at==0||!AsciiWord(query[0])||!AsciiWord(text[at-1]);int end=at+query.Length;
            bool right=end==text.Length||!AsciiWord(query[^1])||!AsciiWord(text[end]);if(left&&right)return true;start=at+1;
        }
        return false;
    }
    static bool AsciiWord(char c)=>c is >= 'a' and <= 'z' or >= '0' and <= '9';
    static void ValidateDate(string value){if(value.Length>0&&!DateOnly.TryParseExact(value,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out _))throw new ArgumentException("日期请使用 yyyy-MM-dd 格式。");}
    static string SummaryText(string value)
    {
        try{if(JsonNode.Parse(value) is JsonObject obj)return string.Join('\n',obj.Select(p=>p.Value?.ToString()??""));}catch(JsonException){}
        return value;
    }
    static JsonArray Strings(IEnumerable<string> values)=>new(values.Select(v=>(JsonNode?)JsonValue.Create(v)).ToArray());
    static JsonArray Reasons(TextMatch match)
    {
        var items=new List<string>();if(match.Identifier)items.Add("exact_identifier");if(match.Phrase)items.Add("exact_phrase");if(match.Lexical)items.Add("lexical");if(match.Semantic)items.Add("semantic");return Strings(items);
    }
    static JsonObject PageResult(string id,PageHit hit)=>new(){["page"]=hit.Page,["chunk_id"]=hit.Chunk,["snippet"]=Snippet(hit.Text,hit.Query),["citation"]=hit.Page>0?$"{id}:{hit.Page}":"",["score"]=Math.Round(hit.Match.Score,4),["match_scope"]=hit.Page>0?"page":"document",["match_source"]=hit.Source,["match_reasons"]=Reasons(hit.Match),["requires_original_verification"]=true};
    static JsonObject Result(DocumentHit hit)
    {
        // Metadata-only matches have no page number/citation. Page evidence must be read explicitly.
        var pages=hit.Pages.Values.Where(p=>p.Page>0).OrderByDescending(p=>p.Match.Score).ThenBy(p=>p.Page).ToArray();
        var best=pages.FirstOrDefault();
        if(hit.Pages.TryGetValue(0,out var metadataHit)&&(best==null||metadataHit.Match.Score>best.Match.Score))best=metadataHit;
        var result=PageResult(hit.Doc.S("id"),best!);
        foreach(string field in new[]{"title","category","scanned","document_date","status","page_count"})result[field]=hit.Doc[field]?.DeepClone();
        result["doc_id"]=hit.Doc.S("id");result["score"]=Math.Round(hit.Score,4);result["matched_query_count"]=hit.Queries.Count;
        result["matched_queries"]=new JsonArray(hit.Queries.Order().Select(q=>(JsonNode?)JsonValue.Create(q+1)).ToArray());
        result["pages"]=new JsonArray(pages.Take(5).Select(p=>(JsonNode)PageResult(hit.Doc.S("id"),p)).ToArray());result["matched_page_count"]=pages.Length;result["pages_truncated"]=pages.Length>5;
        result["metadata_match"]=hit.Pages.ContainsKey(0);result["metadata_match_detail"]=hit.Pages.TryGetValue(0,out var metadata)?PageResult(hit.Doc.S("id"),metadata):null;return result;
    }
    static string Snippet(string text,Query query)
    {
        // Position uses normalized text, so return the same normalized whitespace while preserving readable case.
        text=Regex.Replace(text.Normalize(NormalizationForm.FormKC),@"\s+"," ").Trim();int start=Math.Max(0,query.Position(text)-100);start=Math.Min(start,text.Length);
        return (start>0?"…":"")+text.Substring(start,Math.Min(550,text.Length-start))+(text.Length-start>550?"…":"");
    }
}
