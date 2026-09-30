using ScanArchive.Server;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Collections.Concurrent;
using System.Net;

var builder=WebApplication.CreateBuilder(args);
var settings=new AppSettings();
// A file lock follows the process lifetime and prevents concurrent queue recovery.
FileStream instanceLock;
try{instanceLock=new FileStream(Path.Combine(settings.DataRoot,"service.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}
catch(IOException){return;}
using var instanceLease=instanceLock;
builder.WebHost.UseUrls(Environment.GetEnvironmentVariable("SCANARCHIVE_URLS")??"http://0.0.0.0:5278");
builder.WebHost.ConfigureKestrel(o=>o.Limits.MaxRequestBodySize=100*1024*1024);
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton<Database>();builder.Services.AddSingleton<Documents>();builder.Services.AddSingleton<Search>();builder.Services.AddSingleton<Analyzer>();builder.Services.AddSingleton<SecretaryAgent>();
builder.Services.AddHttpClient<OpenAi>(http=>http.Timeout=TimeSpan.FromMinutes(4));
builder.Services.AddSingleton<Worker>();builder.Services.AddHostedService(sp=>sp.GetRequiredService<Worker>());
builder.Services.AddSingleton<DesktopCommands>();builder.Services.AddSingleton<CaptureCoordinator>();builder.Services.AddHostedService<DesktopBridge>();
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(settings.DataRoot,"keys"))).ProtectKeysWithDpapi();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options=>{
    options.Cookie.Name="scanarchive_session";options.Cookie.HttpOnly=true;options.Cookie.SameSite=SameSiteMode.Strict;
    options.ExpireTimeSpan=TimeSpan.FromDays(7);options.SlidingExpiration=true;
    options.Events.OnRedirectToLogin=ctx=>{ctx.Response.StatusCode=401;return Task.CompletedTask;};
    options.Events.OnRedirectToAccessDenied=ctx=>{ctx.Response.StatusCode=403;return Task.CompletedTask;};
});
builder.Services.AddAuthorization();
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o=>o.MultipartBodyLengthLimit=100*1024*1024);
var app=builder.Build();
app.Use(async(ctx,next)=>{
    ctx.Response.Headers["X-Content-Type-Options"]="nosniff";
    ctx.Response.Headers["Content-Security-Policy"]="default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; frame-src 'self'; object-src 'none'; frame-ancestors 'self'; base-uri 'self'";
    if(ctx.Request.Path.StartsWithSegments("/api"))ctx.Response.Headers.CacheControl="no-store";
    if(ctx.Request.Method is not ("GET" or "HEAD" or "OPTIONS"))
    {
        string origin=ctx.Request.Headers.Origin.ToString();
        if(ctx.Request.Headers["X-ScanArchive"]!="1" || (origin!="" && (!Uri.TryCreate(origin,UriKind.Absolute,out var uri)||!string.Equals(uri.Authority,ctx.Request.Host.Value,StringComparison.OrdinalIgnoreCase))))
        {ctx.Response.StatusCode=403;await ctx.Response.WriteAsJsonAsync(new{error="请求来源不匹配。"});return;}
    }
    try{await next();}
    catch(OperationCanceledException)when(ctx.RequestAborted.IsCancellationRequested){ }
    catch(Exception ex)
    {
        app.Logger.LogError(ex,"Request failed");
        if(!ctx.Response.HasStarted){ctx.Response.StatusCode=ex is ArgumentException?400:ex is KeyNotFoundException?404:500;await ctx.Response.WriteAsJsonAsync(new{error=ex.Message});}
    }
});
var uiFiles=new StaticFileOptions{
    // Revalidate the local UI after an application update, including its Markdown renderer.
    OnPrepareResponse=context=>context.Context.Response.Headers.CacheControl="no-cache, must-revalidate"
};
app.UseDefaultFiles();app.UseStaticFiles(uiFiles);
app.UseAuthentication();app.UseAuthorization();
string passwordFile=Path.Combine(settings.DataRoot,"web-password.json");
var attempts=new ConcurrentDictionary<string,(DateTime start,int count)>();
bool Verify(string password)
{
    if(!File.Exists(passwordFile))return false;
    var stored=JsonNode.Parse(File.ReadAllText(passwordFile))!;byte[] salt=Convert.FromBase64String(stored.S("salt"));
    var candidate=Rfc2898DeriveBytes.Pbkdf2(password,salt,210000,HashAlgorithmName.SHA256,32);
    return CryptographicOperations.FixedTimeEquals(candidate,Convert.FromBase64String(stored.S("hash")));
}
app.MapGet("/health",()=>Results.Ok(new{service="ScanArchive.Secretary",version="1.0"}));
app.MapGet("/api/session",(HttpContext ctx)=>new{authenticated=ctx.User.Identity?.IsAuthenticated==true,setupRequired=!File.Exists(passwordFile),local=IPAddress.IsLoopback(ctx.Connection.RemoteIpAddress??IPAddress.None)});
app.MapPost("/api/login",async(HttpContext ctx,JsonObject body)=>{
    string ip=ctx.Connection.RemoteIpAddress?.ToString()??"unknown";
    var attempt=attempts.AddOrUpdate(ip,(DateTime.UtcNow,1),(_,x)=>DateTime.UtcNow-x.start>TimeSpan.FromMinutes(1)?(DateTime.UtcNow,1):(x.start,x.count+1));
    if(attempt.count>10)return Results.Json(new{error="尝试次数过多，请稍后再试。"},statusCode:429);
    string password=body.S("password");
    if(!File.Exists(passwordFile))
    {
        if(!IPAddress.IsLoopback(ctx.Connection.RemoteIpAddress??IPAddress.None))return Results.Json(new{error="首次请在服务器电脑打开 localhost:5278 设置访问密码。"},statusCode:403);
        if(password.Length<10)return Results.BadRequest(new{error="首次设置密码至少 10 个字符。"});
        byte[] salt=RandomNumberGenerator.GetBytes(24);var hash=Rfc2898DeriveBytes.Pbkdf2(password,salt,210000,HashAlgorithmName.SHA256,32);
        using var file=new FileStream(passwordFile,FileMode.CreateNew,FileAccess.Write);
        JsonSerializer.Serialize(file,new{salt=Convert.ToBase64String(salt),hash=Convert.ToBase64String(hash)});
    }
    else if(!Verify(password))return Results.Json(new{error="密码不正确。"},statusCode:401);
    await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name,"owner")],CookieAuthenticationDefaults.AuthenticationScheme)));
    return Results.Ok(new{ok=true});
});
var api=app.MapGroup("/api").RequireAuthorization();
api.MapPost("/logout",async(HttpContext ctx)=>{await ctx.SignOutAsync();return Results.Ok();});
api.MapGet("/status",(Database db,OpenAi ai,SecretaryAgent agent)=>new{configured=ai.Available,settings=settings.Current,overview=agent.Overview(),pending=db.Rows("SELECT kind,status,count(*) count FROM jobs WHERE status<>'done' GROUP BY kind,status"),usage=db.Rows("SELECT * FROM usage ORDER BY day DESC LIMIT 7"),lastWake=db.State("last_wake"),addresses=System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().Where(x=>x.OperationalStatus==System.Net.NetworkInformation.OperationalStatus.Up).SelectMany(x=>x.GetIPProperties().UnicastAddresses).Where(x=>x.Address.AddressFamily==System.Net.Sockets.AddressFamily.InterNetwork).Select(x=>$"http://{x.Address}:5278").ToArray()});
api.MapGet("/settings",(OpenAi ai)=>new{options=settings.Current,hasApiKey=ai.Available});
api.MapPost("/settings",(JsonObject body,Database db)=>{
    var options=body["options"]!.Deserialize<ServerOptions>(AppSettings.Json)??throw new ArgumentException("设置无效。");
    if(!string.Equals(Path.GetFullPath(options.LibraryRoot),Path.GetFullPath(settings.Current.LibraryRoot),StringComparison.OrdinalIgnoreCase)&&db.Rows("SELECT id FROM documents LIMIT 1").Count>0)throw new ArgumentException("已有文档时不能直接更改库根目录，请先迁移数据。");
    bool changedEmbedding=options.EmbeddingModel!=settings.Current.EmbeddingModel;
    settings.Save(options);settings.SaveApiKey(body.S("apiKey"));
    if(changedEmbedding)db.Log("index","嵌入模型已更改，请执行索引修复以重建向量。");
    return Results.Ok(new{ok=true});
});
api.MapPost("/test-api",async(OpenAi ai,CancellationToken ct)=>{
    var result=await ai.Structured("Return ok=true.",new JsonArray(new JsonObject{["type"]="input_text",["text"]="Connection test"}),new JsonObject{["type"]="object",["properties"]=new JsonObject{["ok"]=new JsonObject{["type"]="boolean"}},["required"]=new JsonArray("ok"),["additionalProperties"]=false},"connection_test",ct);return result;
});
api.MapGet("/documents",(string? q,string? status,string? category,string? from,string? to,int? offset,Database db)=>db.Rows("SELECT id,title,category,summary,tags,scanned,document_date,status,error,page_count,parent_id,source_pages,mixed_content,locked FROM documents WHERE status NOT IN ('deleted','superseded') AND ($s='' OR status=$s) AND ($c='' OR category=$c OR substr(category,1,length($c)+1)=$c||'/') AND ($from='' OR substr(scanned,1,10)>=$from) AND ($to='' OR substr(scanned,1,10)<=$to) AND ($q='' OR instr(lower(title||' '||summary||' '||tags),lower($q))>0) ORDER BY scanned DESC LIMIT 100 OFFSET $o",("$s",status??""),("$c",category??""),("$q",q??""),("$from",from??""),("$to",to??""),("$o",Math.Max(0,offset??0))));
api.MapGet("/documents/{id}",(string id,Database db)=>new{document=db.Doc(id)??throw new KeyNotFoundException(),pages=db.Rows("SELECT number,text,summary FROM pages WHERE doc_id=$i ORDER BY number",("$i",id)),children=db.Rows("SELECT id,title,source_pages,status FROM documents WHERE parent_id=$i",("$i",id))});
api.MapGet("/documents/{id}/file",(string id,Database db)=>{
    var doc=db.Doc(id)??throw new KeyNotFoundException();string path=doc.S("original");string type=path.EndsWith(".pdf",StringComparison.OrdinalIgnoreCase)?"application/pdf":path.EndsWith(".png",StringComparison.OrdinalIgnoreCase)?"image/png":"image/jpeg";
    return Results.File(path,type,enableRangeProcessing:true);
});
api.MapGet("/documents/{id}/pages/{page:int}",(string id,int page,Database db,Documents docs)=>Results.File(docs.PageImage(db.Doc(id)??throw new KeyNotFoundException(),page),"image/png"));
api.MapGet("/documents/{id}/metadata",(string id,Database db)=>Results.Json(new{document=db.Doc(id)??throw new KeyNotFoundException(),pages=db.Rows("SELECT number,text,summary FROM pages WHERE doc_id=$i ORDER BY number",("$i",id))}));
api.MapPost("/documents/{id}/move",async(string id,JsonObject body,Documents docs,CancellationToken ct)=>await docs.Move(id,body.S("title"),body.S("category"),"用户手动调整",true,ct));
api.MapPost("/documents/{id}/unlock",(string id,Database db)=>{db.Exec("UPDATE documents SET locked=0 WHERE id=$i",("$i",id));return Results.Ok();});
api.MapPost("/documents/{id}/retry",(string id,Documents docs)=>new{job=docs.Enqueue("index",id)});
api.MapPost("/documents/{id}/organize",(string id,Documents docs,Database db)=>{
    var doc=db.Doc(id)??throw new KeyNotFoundException();
    if(doc.S("status") is not ("ready" or "analyzed"))throw new ArgumentException("请等待内容分析完成后再整理。");
    return new{job=docs.Enqueue("organize",id)};
});
api.MapPost("/upload",async(HttpRequest request,Documents docs,CancellationToken ct)=>{
    var form=await request.ReadFormAsync(ct);var ids=new List<string>();
    foreach(var file in form.Files)
    {
        string ext=Path.GetExtension(file.FileName);if(!Documents.Extensions.Contains(ext))throw new ArgumentException("文件格式不支持。");
        string path=docs.SafePath("Inbox/"+Documents.Clean(Path.GetFileNameWithoutExtension(file.FileName))+"__"+Guid.NewGuid().ToString("N")[..8]+ext);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using(var output=new FileStream(path+".partial",FileMode.CreateNew,FileAccess.Write))await file.CopyToAsync(output,ct);
        File.Move(path+".partial",path);ids.Add(await docs.Import(path,ct:ct));
    }
    return new{ids};
}).DisableAntiforgery();
api.MapGet("/search",async(string q,string? category,string? from,string? to,Search search,CancellationToken ct)=>await search.Find(q,category??"",from??"",to??"",20,ct));
api.MapGet("/categories",(Documents docs)=>docs.Folders());
api.MapGet("/trash",(Database db)=>db.Rows("SELECT d.id,d.title,t.deleted_at FROM documents d JOIN trash t ON t.doc_id=d.id ORDER BY t.deleted_at DESC"));
api.MapPost("/documents/{id}/trash",async(string id,Documents docs,CancellationToken ct)=>{await docs.Trash(id,ct);return Results.Ok();});
api.MapPost("/documents/{id}/restore",async(string id,Documents docs,CancellationToken ct)=>{await docs.Restore(id,ct);return Results.Ok();});
api.MapGet("/activity",(Database db)=>new{counts=db.JobCounts(),activity=db.Rows("SELECT * FROM activity ORDER BY id DESC LIMIT 150"),operations=db.Rows("SELECT * FROM operations ORDER BY created DESC LIMIT 100"),jobs=db.Rows("SELECT j.*,d.title document_title FROM jobs j LEFT JOIN documents d ON d.id=j.payload ORDER BY CASE j.status WHEN 'running' THEN 0 WHEN 'pending' THEN 1 WHEN 'failed' THEN 2 ELSE 3 END,j.created DESC LIMIT 100")});
api.MapPost("/operations/{id}/undo",async(string id,Documents docs,CancellationToken ct)=>{await docs.Undo(id,ct);return Results.Ok();});
api.MapPost("/jobs/{id}/retry",(string id,Database db)=>{db.Exec("UPDATE jobs SET status='pending',next_run=$t,error='' WHERE id=$i AND status='failed'",("$t",Database.Now),("$i",id));return Results.Ok();});
api.MapPost("/maintenance",(Documents docs)=>new{job=docs.Enqueue("review","manual:"+Guid.NewGuid().ToString("N"))});
api.MapPost("/discover",async(Worker worker,CancellationToken ct)=>new{found=await worker.Discover(ct)});
api.MapGet("/conversations",(Database db)=>db.Rows("SELECT * FROM conversations ORDER BY created DESC"));
api.MapGet("/conversations/{id}",(string id,Database db)=>new{messages=db.Rows("SELECT role,text,created FROM messages WHERE conversation=$i ORDER BY id",("$i",id)),jobs=db.Rows("SELECT status,error FROM jobs WHERE kind='chat' AND payload=$i ORDER BY created DESC LIMIT 1",("$i",id))});
api.MapPost("/chat",async(JsonObject body,DesktopCommands desktop,CancellationToken ct)=>{
    body["command"]="chat";body["id"]=body.S("conversation");return await desktop.Handle(body,ct);
});
api.MapGet("/memories",(Database db)=>db.Rows("SELECT * FROM memories ORDER BY created DESC"));
api.MapDelete("/memories/{id}",(string id,Database db)=>{db.Exec("DELETE FROM memories WHERE id=$i",("$i",id));return Results.Ok();});
app.MapFallbackToFile("index.html",uiFiles);
app.Run();
public partial class Program { }
