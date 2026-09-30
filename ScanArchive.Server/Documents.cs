using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Docnet.Core;
using Docnet.Core.Models;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace ScanArchive.Server;

public sealed class Documents(AppSettings settings, Database db)
{
    readonly System.Collections.Concurrent.ConcurrentDictionary<string,object> metadataLocks=new();
    static readonly object PdfGate=new();
    public readonly SemaphoreSlim Mutation = new(1,1);
    public string Root => Path.GetFullPath(settings.Current.LibraryRoot);
    public static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase){".pdf",".png",".jpg",".jpeg"};
    public string SafePath(string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Split(['/', '\\']).Any(x => x is "." or ".." || x.StartsWith('.'))) throw new ArgumentException("目录必须位于文档库内。");
        string path = Path.GetFullPath(Path.Combine(Root,relative));
        if (!path.StartsWith(Root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("无效路径。");
        string? current = path;
        while (current != null && !string.Equals(current,Root,StringComparison.OrdinalIgnoreCase))
        {
            if ((Directory.Exists(current) || File.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) throw new ArgumentException("不支持通过链接访问文档库外的路径。");
            current = Path.GetDirectoryName(current);
        }
        return path;
    }
    public static string Clean(string text)
    {
        var cleaned = new string(text.Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim(' ','.');
        if (string.IsNullOrEmpty(cleaned)) cleaned="未命名";
        if (cleaned.Length>100) cleaned=cleaned[..100];
        if (System.Text.RegularExpressions.Regex.IsMatch(cleaned,@"^(CON|PRN|AUX|NUL|COM\d|LPT\d)(\.|$)",System.Text.RegularExpressions.RegexOptions.IgnoreCase)) cleaned="_"+cleaned;
        return cleaned;
    }
    public string Category(string value)
    {
        var parts = value.Replace('\\','/').Split('/',StringSplitOptions.RemoveEmptyEntries);
        if(parts.Length==0 || parts.Length>6 || parts.Any(x=>x is "." or ".." || x.StartsWith('.'))) throw new ArgumentException("分类应为 1 至 6 层有效目录。");
        string category=string.Join('/',parts.Select(Clean)); SafePath("Library/"+category); return category;
    }
    public async Task<string> Import(string path,string? scanned = null,string parentId="",CancellationToken ct=default,bool enqueue=true)
    {
        path=Path.GetFullPath(path);
        string relative=Path.GetRelativePath(Root,path); SafePath(relative);
        if(!Extensions.Contains(Path.GetExtension(path))) throw new ArgumentException("支持 PDF、PNG 和 JPEG。");
        await Mutation.WaitAsync(ct);
        try
        {
            await using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
            string hash=Convert.ToHexString(await SHA256.HashDataAsync(input,ct));
            var existing=db.Rows("SELECT id FROM documents WHERE hash=$h",("$h",hash)).FirstOrDefault();
            if(existing!=null) return existing.S("id");
            string id=Guid.NewGuid().ToString("N"), extension=Path.GetExtension(path).ToLowerInvariant();
            string originals=Path.Combine(Root,".scanarchive-originals"); Directory.CreateDirectory(originals);
            if(File.GetAttributes(originals).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("原始扫描目录不能是链接。");
            string original=Path.Combine(originals,hash+extension);
            if(!File.Exists(original))
            {
                input.Position=0; string temp=original+".partial";
                await using(var output=new FileStream(temp,FileMode.Create,FileAccess.Write,FileShare.None)) await input.CopyToAsync(output,ct);
                File.Move(temp,original,true);
            }
            db.Exec("INSERT INTO documents(id,hash,path,original,title,scanned,created,parent_id) VALUES($i,$h,$p,$o,$t,$s,$c,$parent)",
                ("$i",id),("$h",hash),("$p",path),("$o",original),("$t",Path.GetFileNameWithoutExtension(path)),("$s",scanned??File.GetCreationTimeUtc(path).ToString("O")),("$c",Database.Now),("$parent",parentId));
            try { db.Exec("UPDATE documents SET page_count=$n WHERE id=$i",("$n",PageCount(db.Doc(id)!)),("$i",id)); }
            catch(Exception ex) { db.Log("preview",ex.Message); }
            WriteMetadata(id);if(enqueue)Enqueue("index",id); db.Log("import","已接收文件："+Path.GetFileName(path)); return id;
        }
        finally{Mutation.Release();}
    }
    public string Enqueue(string kind,string payload)
    {
        if(kind is "index" or "organize" or "embeddings" or "reindex" or "format_summary" or "reanalyze" && db.Doc(payload)?.S("status") is "deleted" or "superseded")throw new InvalidOperationException("此文档已删除或已由拆分文件替代。");
        string id="";
        db.Transaction((c,tx)=>{
            using var cmd=c.CreateCommand();cmd.Transaction=tx;
            cmd.CommandText="SELECT id FROM jobs WHERE kind=$k AND payload=$p AND status IN ('pending','running') LIMIT 1";
            cmd.Parameters.AddWithValue("$k",kind);cmd.Parameters.AddWithValue("$p",payload);
            var existing=cmd.ExecuteScalar();if(existing!=null){id=existing.ToString()!;return;}
            id=Guid.NewGuid().ToString("N");cmd.CommandText="INSERT INTO jobs(id,kind,payload,status,next_run,created,updated) VALUES($id,$k,$p,'pending',$t,$t,$t)";
            cmd.Parameters.AddWithValue("$id",id);cmd.Parameters.AddWithValue("$t",Database.Now);cmd.ExecuteNonQuery();
        });return id;
    }

    public JsonArray Folders() => new(db.Rows("SELECT category,count(*) count FROM documents WHERE status NOT IN ('deleted','superseded') AND category<>'' GROUP BY category ORDER BY category").Select(x=>(JsonNode)x).ToArray());
    public async Task<JsonObject> Move(string id,string title,string category,string reason,bool locked=false,CancellationToken ct=default)
    {
        await Mutation.WaitAsync(ct);
        try
        {
            var doc=db.Doc(id)??throw new KeyNotFoundException("文档不存在");
            if(doc.S("status") is "deleted" or "superseded")throw new InvalidOperationException("此文档已删除或已由拆分文件替代，请操作独立文档。");
            if(doc.I("locked")==1 && !locked) throw new InvalidOperationException("此文件的分类已由用户锁定。");
            if(doc.S("status") is "analyzing" or "indexing")throw new InvalidOperationException("请等待内容分析完成后再调整归档。");
            title=Clean(title); category=Category(category);
            string destination=SafePath("Library/"+category+"/"+title+"__"+id[..8]+Path.GetExtension(doc.S("path")));
            if(string.Equals(destination,doc.S("path"),StringComparison.OrdinalIgnoreCase)){
                if(locked){db.Exec("UPDATE documents SET locked=1 WHERE id=$i",("$i",id));WriteMetadata(id);}
                return db.Doc(id)!;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            string op=Guid.NewGuid().ToString("N");
            var after=new JsonObject{["title"]=title,["category"]=category,["locked"]=locked?1:0};
            db.Exec("INSERT INTO operations VALUES($id,$doc,'move',$old,$new,$before,$after,'prepared',$time,$reason)",("$id",op),("$doc",id),("$old",doc.S("path")),("$new",destination),("$before",doc.ToJsonString()),("$after",after.ToJsonString()),("$time",Database.Now),("$reason",reason));
            File.Move(doc.S("path"),destination,false);
            ApplyMove(op,id,destination,after,"applied");
            WriteMetadata(id);db.Log("organize",$"{doc.S("title")} → {category}/{title}");return db.Doc(id)!;
        }
        finally{Mutation.Release();}
    }
    void ApplyMove(string op,string id,string path,JsonObject info,string state)
    {
        db.Transaction((c,tx)=>{
            using var cmd=c.CreateCommand();cmd.Transaction=tx;
            cmd.CommandText="UPDATE documents SET path=$p,title=$t,category=$c,locked=$l WHERE id=$i; UPDATE operations SET state=$s WHERE id=$op;";
            foreach(var (key,value) in new (string,object)[]{("$p",path),("$t",info.S("title")),("$c",info.S("category")),("$l",info.I("locked")),("$i",id),("$s",state),("$op",op)})cmd.Parameters.AddWithValue(key,value);
            cmd.ExecuteNonQuery();
        });
    }
    public void RecoverMoves()
    {
        foreach(var op in db.Rows("SELECT * FROM operations WHERE state IN ('prepared','undoing')"))
        {
            bool undo=op.S("state")=="undoing";string target=op.S(undo?"old_path":"new_path"); string source=op.S(undo?"new_path":"old_path");
            if(File.Exists(target)&&!File.Exists(source))ApplyMove(op.S("id"),op.S("doc_id"),target,JsonNode.Parse(op.S(undo?"before_json":"after_json"))!.AsObject(),undo?"undone":"applied");
            else if(File.Exists(source)&&!File.Exists(target))db.Exec("UPDATE operations SET state=$s WHERE id=$i",("$s",undo?"applied":"failed"),("$i",op.S("id")));
            else db.Log("error","移动恢复需要检查："+op.S("id"));
        }
    }
    public async Task Undo(string id,CancellationToken ct)
    {
        await Mutation.WaitAsync(ct);try{
            var op=db.Rows("SELECT * FROM operations WHERE id=$i AND state='applied'",("$i",id)).FirstOrDefault()??throw new ArgumentException("该操作不能撤销。");
            var doc=db.Doc(op.S("doc_id"))!;
            if(doc.S("path")!=op.S("new_path"))throw new ArgumentException("请先撤销此文件后续的移动。");
            SafePath(Path.GetRelativePath(Root,op.S("old_path")));Directory.CreateDirectory(Path.GetDirectoryName(op.S("old_path"))!);
            db.Exec("UPDATE operations SET state='undoing' WHERE id=$i",("$i",id));
            File.Move(op.S("new_path"),op.S("old_path"),false);
            ApplyMove(id,op.S("doc_id"),op.S("old_path"),JsonNode.Parse(op.S("before_json"))!.AsObject(),"undone");
            WriteMetadata(op.S("doc_id"));
            db.Log("undo","已撤销文件移动："+doc.S("title"));
        }finally{Mutation.Release();}
    }
    public int PageCount(JsonObject doc)
    {lock(PdfGate)return PageCountCore(doc);}
    int PageCountCore(JsonObject doc)
    {
        if(!doc.S("original").EndsWith(".pdf",StringComparison.OrdinalIgnoreCase))return 1;
        using var reader=DocLib.Instance.GetDocReader(File.ReadAllBytes(doc.S("original")),new PageDimensions(1200,1600));return reader.GetPageCount();
    }
    public async Task Trash(string id,CancellationToken ct)
    {
        await Mutation.WaitAsync(ct);
        try
        {
            var doc=db.Doc(id)??throw new KeyNotFoundException("文档不存在。");
            if(doc.S("status")=="deleted")return;
            if(db.Rows("SELECT id FROM jobs WHERE payload=$i AND status='running'",("$i",id)).Count>0)throw new InvalidOperationException("此文件正在处理，请等待当前任务完成后再删除。");
            db.Exec("INSERT OR REPLACE INTO trash VALUES($i,$s,$t); UPDATE documents SET status='deleted' WHERE id=$i; UPDATE jobs SET status='cancelled' WHERE payload=$i AND status='pending'",("$i",id),("$s",doc.S("status")),("$t",Database.Now));
            WriteMetadata(id);db.Log("trash","已移入应用回收站："+doc.S("title"));
        }
        finally{Mutation.Release();}
    }
    public async Task Restore(string id,CancellationToken ct)
    {
        await Mutation.WaitAsync(ct);
        try
        {
            var item=db.Rows("SELECT * FROM trash WHERE doc_id=$i",("$i",id)).FirstOrDefault()??throw new ArgumentException("文件不在回收站。");
            db.Exec("UPDATE documents SET status=$s WHERE id=$i; DELETE FROM trash WHERE doc_id=$i; UPDATE jobs SET status='pending' WHERE payload=$i AND status='cancelled'",("$i",id),("$s",item.S("previous_status")));
            WriteMetadata(id);db.Log("restore","已从回收站恢复文档。");
        }
        finally{Mutation.Release();}
    }
    public void WriteMetadata(string id)
    {
        lock(metadataLocks.GetOrAdd(id,_=>new object()))WriteMetadataCore(id);
    }
    void WriteMetadataCore(string id)
    {
        var doc=db.Doc(id)??throw new KeyNotFoundException();
        var pages=db.Rows("SELECT number,text,summary FROM pages WHERE doc_id=$i ORDER BY number",("$i",id));
        foreach(var page in pages) { page["analysis"]=JsonNode.Parse(page.S("summary"));page.Remove("summary"); }
        var data=new JsonObject{["schema_version"]=1,["document"]=doc,["pages"]=new JsonArray(pages.Select(x=>(JsonNode)x).ToArray()),["analysis_history"]=new JsonArray(db.Rows("SELECT page,model,analyzed_at FROM analyses WHERE doc_id=$i ORDER BY page",("$i",id)).Select(x=>(JsonNode)x).ToArray()),["updated_at"]=Database.Now};
        data["summary_revisions"]=new JsonArray(db.Rows("SELECT before_summary,after_summary,model,created FROM summary_revisions WHERE doc_id=$i ORDER BY created",("$i",id)).Select(x=>(JsonNode)x).ToArray());
        data["scans"]=new JsonArray(db.Rows("SELECT scan_id,scanned,device,source FROM scan_submissions WHERE doc_id=$i ORDER BY scanned",("$i",id)).Select(x=>(JsonNode)x).ToArray());
        string directory=Path.Combine(Root,".scanarchive-metadata");Directory.CreateDirectory(directory);
        if(File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint))throw new IOException("元数据目录不能是链接。");
        string file=Path.Combine(directory,id+".json");File.WriteAllText(file+".tmp",data.ToJsonString(AppSettings.Json));File.Move(file+".tmp",file,true);
    }
    public byte[] PageImage(JsonObject doc,int number)
    {lock(PdfGate)return PageImageCore(doc,number);}
    byte[] PageImageCore(JsonObject doc,int number)
    {
        if(number<1)throw new ArgumentException("页码错误。");
        if(!doc.S("original").EndsWith(".pdf",StringComparison.OrdinalIgnoreCase))
        {
            if(number!=1)throw new ArgumentException("页码错误。");
            using var image=Image.FromFile(doc.S("original")); double scale=Math.Min(1,1600d/Math.Max(image.Width,image.Height));
            using var bitmap=new Bitmap(image,(int)(image.Width*scale),(int)(image.Height*scale));using var ms=new MemoryStream();bitmap.Save(ms,ImageFormat.Png);return ms.ToArray();
        }
        using var reader=DocLib.Instance.GetDocReader(File.ReadAllBytes(doc.S("original")),new PageDimensions(1400,1800));
        if(number>reader.GetPageCount())throw new ArgumentException("页码错误。");
        using var page=reader.GetPageReader(number-1);byte[] pixels=page.GetImage();
        using var bmp=new Bitmap(page.GetPageWidth(),page.GetPageHeight(),PixelFormat.Format32bppArgb);
        var data=bmp.LockBits(new Rectangle(0,0,bmp.Width,bmp.Height),ImageLockMode.WriteOnly,bmp.PixelFormat);
        try{Marshal.Copy(pixels,0,data.Scan0,pixels.Length);}finally{bmp.UnlockBits(data);}
        using var output=new MemoryStream();bmp.Save(output,ImageFormat.Png);return output.ToArray();
    }
    public async Task<string> Split(string id,int first,int last,string title,CancellationToken ct)
    {
        if(first<1||last<first||last-first>10000)throw new ArgumentException("页码范围无效。");
        return await ExtractPages(id,string.Join(',',Enumerable.Range(first,last-first+1)),title,ct);
    }
    public static int[] ParsePages(string selection,int count)
    {
        var pages=new List<int>();
        foreach(string part in selection.Split(',',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries))
        {
            var bounds=part.Split('-');
            if(bounds.Length>2||!int.TryParse(bounds[0],out int first))throw new ArgumentException("页码格式示例：1,6 或 2-3。");
            int last=first;
            if(bounds.Length==2&&!int.TryParse(bounds[1],out last))throw new ArgumentException("页码格式无效。");
            if(first<1||last<first||last>count)throw new ArgumentException("页码越界。");
            pages.AddRange(Enumerable.Range(first,last-first+1));
        }
        if(pages.Count==0||pages.Distinct().Count()!=pages.Count)throw new ArgumentException("页码不能为空或重复。");
        return pages.ToArray();
    }
    public async Task ReconcileSplitBatches(CancellationToken ct=default)
    {
        await Mutation.WaitAsync(ct);
        try
        {
            foreach(var parent in db.Rows("SELECT * FROM documents WHERE status IN ('ready','superseded') AND EXISTS (SELECT 1 FROM documents child WHERE child.parent_id=documents.id) ORDER BY created DESC"))
            {
                ct.ThrowIfCancellationRequested();
                string id=parent.S("id");bool hidden=parent.S("status")=="superseded";
                // A source batch stays visible until its own organization has finished.
                if(!hidden&&db.Rows("SELECT id FROM jobs WHERE payload=$i AND status IN ('pending','running')",("$i",id)).Count>0)continue;
                var children=db.Rows("SELECT * FROM documents WHERE parent_id=$i AND status<>'deleted'",("$i",id));
                int count=parent.I("page_count");var covered=new HashSet<int>();
                bool complete=children.Count>=2&&count>0&&File.Exists(parent.S("original"));
                foreach(var child in children)
                {
                    if(!complete)break;
                    if(child.S("status") is not ("ready" or "superseded")||!File.Exists(child.S("original"))||!File.Exists(child.S("path"))){complete=false;break;}
                    try
                    {
                        var pages=ParsePages(child.S("source_pages"),count);
                        // Validate physical outputs before hiding a batch, not just AI assertions.
                        if(pages.Length!=child.I("page_count")||(!hidden&&PageCount(child)!=pages.Length)){complete=false;break;}
                        foreach(int page in pages)if(!covered.Add(page)){complete=false;break;}
                    }
                    catch(Exception ex)when(ex is ArgumentException or IOException or InvalidDataException){complete=false;}
                }
                complete&=covered.Count==count;
                if(complete==hidden)continue;
                db.Exec("UPDATE documents SET status=$s WHERE id=$i",("$s",complete?"superseded":"ready"),("$i",id));
                WriteMetadata(id);
                db.Log("split",complete?$"已验证 {count} 页完整拆分，原始合集已从文档库隐藏：{parent.S("title")}":$"拆分文件不再完整可用，已恢复原始合集以免遗漏：{parent.S("title")}");
            }
        }
        finally{Mutation.Release();}
    }
    public async Task<string> ExtractPages(string id,string selection,string title,CancellationToken ct)
    {
        var doc=db.Doc(id)??throw new KeyNotFoundException();
        if(doc.S("status") is "deleted" or "superseded")throw new InvalidOperationException("此文档已删除或已由拆分文件替代，请操作独立文档。");
        if(!doc.S("original").EndsWith(".pdf",StringComparison.OrdinalIgnoreCase))throw new ArgumentException("只有 PDF 可拆分页。");
        using var source=PdfReader.Open(doc.S("original"),PdfDocumentOpenMode.Import);
        int[] pages=ParsePages(selection,source.PageCount);
        bool consecutive=pages.SequenceEqual(Enumerable.Range(pages[0],pages.Length));
        string pageMap=consecutive?$"{pages[0]}-{pages[^1]}":string.Join(',',pages);
        if(doc.S("parent_id")!=""&&pages.SequenceEqual(Enumerable.Range(1,source.PageCount)))throw new ArgumentException("不能完整复制已拆分的子文件。");
        var existing=db.Rows("SELECT id FROM documents WHERE parent_id=$i AND source_pages=$p",("$i",id),("$p",pageMap)).FirstOrDefault();
        if(existing!=null)return existing.S("id");
        string path=SafePath("Inbox/"+Clean(title)+"__"+Guid.NewGuid().ToString("N")[..8]+".pdf");Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using(var output=new PdfDocument()){foreach(int p in pages)output.AddPage(source.Pages[p-1]);output.Save(path);}
        string child=await Import(path,doc.S("scanned"),id,ct,enqueue:false);
        db.Exec("UPDATE documents SET source_pages=$p WHERE id=$i",("$p",pageMap),("$i",child));
        // The bytes of each extracted page are unchanged, so reuse its completed analysis.
        for(int index=0;index<pages.Length;index++)
        {
            db.Exec("INSERT OR IGNORE INTO pages(doc_id,number,text,summary) SELECT $child,$n,text,summary FROM pages WHERE doc_id=$parent AND number=$p; INSERT OR IGNORE INTO analyses(doc_id,page,model,analyzed_at) SELECT $child,$n,model,analyzed_at FROM analyses WHERE doc_id=$parent AND page=$p",("$child",child),("$n",index+1),("$parent",id),("$p",pages[index]));
        }
        WriteMetadata(child);Enqueue("index",child);
        db.Log("split",$"{doc.S("title")} 第 {pageMap} 页已生成独立文件，原文件保留。");return child;
    }
}
