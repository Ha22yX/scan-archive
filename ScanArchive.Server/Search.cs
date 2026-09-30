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
    public async Task<JsonObject> Find(string query,string category="",string from="",string to="",int limit=12,CancellationToken ct=default)
    {
        query=query.Trim();if(query.Length==0)return new JsonObject{["results"]=new JsonArray(),["mode"]="empty"};
        if(query.Length>1000)throw new ArgumentException("搜索词过长。");limit=Math.Clamp(limit,1,40);
        string filter=" WHERE d.status<>'deleted' AND ($cat='' OR d.category=$cat OR substr(d.category,1,length($cat)+1)=$cat||'/') AND ($from='' OR d.scanned >= $from) AND ($to='' OR substr(d.scanned,1,10)<=$to) ";
        var args=new (string,object?)[]{("$cat",category),("$from",from),("$to",to)};
        var hits=new Dictionary<long,(JsonObject row,double score)>();
        void Rank(IEnumerable<JsonObject> list,double weight){int rank=0;foreach(var row in list){long id=long.Parse(row.S("chunk_id"));rank++;if(!hits.ContainsKey(id))hits[id]=(row,0);hits[id]=(hits[id].row,hits[id].score+weight/(60+rank));}}
        string select="SELECT c.id chunk_id,c.doc_id,c.page,c.text,d.title,d.category,d.scanned FROM chunks c JOIN documents d ON d.id=c.doc_id";
        var exact=db.Rows(select+filter+" AND (instr(lower(c.text),lower($q))>0 OR instr(lower(d.title||' '||d.tags),lower($q))>0) ORDER BY instr(lower(d.title),lower($q))>0 DESC LIMIT 100",args.Append(("$q",(object?)query)).ToArray());Rank(exact,2);
        string tokens=Tokens(query);string match=string.Join(" OR ",tokens.Split(' ',StringSplitOptions.RemoveEmptyEntries).Take(40).Select(t=>"\""+t.Replace("\"","\"\"")+"\""));
        if(match.Length>0)
        {
            var lexical=db.Rows("SELECT c.id chunk_id,c.doc_id,c.page,c.text,d.title,d.category,d.scanned FROM search_fts f JOIN chunks c ON c.id=f.chunk_id JOIN documents d ON d.id=c.doc_id"+filter+" AND search_fts MATCH $q ORDER BY bm25(search_fts) LIMIT 100",args.Append(("$q",(object?)match)).ToArray());Rank(lexical,1);
        }
        bool semantic=false;string warning="";
        if(ai.Available)
        {
            try{
                var vector=await ai.Embed(query,ct);
                // No arbitrary newest-N cutoff: every indexed chunk is eligible.
                var candidates=db.Rows("SELECT c.id chunk_id,c.doc_id,c.page,c.text,c.embedding,d.title,d.category,d.scanned FROM chunks c JOIN documents d ON d.id=c.doc_id"+filter+" AND c.embedding IS NOT NULL AND c.model=$model",args.Append(("$model",(object?)settings.Current.EmbeddingModel)).ToArray());
                var matches=candidates.Select(row=>(row,score:Cosine(vector,JsonSerializer.Deserialize<float[]>(row.S("embedding"))!))).Where(x=>x.score>=0.25).OrderByDescending(x=>x.score).Take(100).ToArray();
                foreach(var candidate in matches)candidate.row.Remove("embedding");Rank(matches.Select(x=>x.row),1);semantic=true;
            }catch(OperationCanceledException){throw;}
            catch(Exception ex){warning=ex.Message;}
        }
        var ranked=hits.Values.OrderByDescending(x=>x.score).GroupBy(x=>(x.row.S("doc_id"),x.row.I("page"))).Select(g=>g.First()).Take(limit).ToArray();
        var results=new JsonArray();foreach(var hit in ranked){var row=hit.row;row["score"]=Math.Round(hit.score,6);row["snippet"]=Snippet(row.S("text"),query);row.Remove("text");row["citation"]=$"{row.S("doc_id")}:{row.I("page")}";results.Add(row);}
        return new JsonObject{["results"]=results,["mode"]=semantic?"hybrid":"keyword",["warning"]=warning};
    }
    static string Snippet(string text,string query){int position=text.IndexOf(query,StringComparison.OrdinalIgnoreCase);int start=Math.Max(0,position-100);return text.Substring(start,Math.Min(550,text.Length-start));}
}
