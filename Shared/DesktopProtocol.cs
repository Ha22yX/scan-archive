using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace ScanArchive.Integration;

public static class DesktopProtocol
{
    public static string PipeName(string dataRoot) => "ScanArchive-"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(dataRoot).ToUpperInvariant())))[..24];
    public static async Task<JsonObject> Request(string dataRoot,JsonObject request,CancellationToken ct=default)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var pipe=new NamedPipeClientStream(".",PipeName(dataRoot),PipeDirection.InOut,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(3000,timeout.Token);
        using var writer=new StreamWriter(pipe,new UTF8Encoding(false),leaveOpen:true){AutoFlush=true};
        using var reader=new StreamReader(pipe,Encoding.UTF8,leaveOpen:true);
        await writer.WriteLineAsync(request.ToJsonString().AsMemory(),timeout.Token);
        string line=await reader.ReadLineAsync(timeout.Token)??throw new IOException("文档库连接已中断。");
        var response=JsonNode.Parse(line)!.AsObject();
        if(response["error"]!=null)throw new InvalidOperationException(response["error"]!.ToString());
        return response;
    }
}
