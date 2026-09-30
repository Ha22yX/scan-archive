using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ScanArchive.Server;

public sealed class OpenAi(AppSettings settings, Database db, HttpClient http)
{
    public bool Available => !string.IsNullOrWhiteSpace(settings.ApiKey);
    public async Task<JsonObject> Post(string endpoint, JsonObject request, CancellationToken ct)
    {
        if (!Available) throw new InvalidOperationException("请先在设置中填写 OpenAI API Key。");
        for (int attempt = 0; ; attempt++)
        {
            string day = DateTime.Today.ToString("yyyy-MM-dd");
            bool reserved = false;
            db.Transaction((c, tx) =>
            {
                using var cmd = c.CreateCommand(); cmd.Transaction = tx;
                cmd.CommandText = "INSERT OR IGNORE INTO usage(day) VALUES($d); UPDATE usage SET requests=requests+1 WHERE day=$d AND requests<$limit;";
                cmd.Parameters.AddWithValue("$d",day); cmd.Parameters.AddWithValue("$limit",settings.Current.DailyRequestLimit);
                cmd.ExecuteNonQuery(); cmd.CommandText = "SELECT changes()"; reserved = Convert.ToInt32(cmd.ExecuteScalar()) == 1;
            });
            if (!reserved) throw new InvalidOperationException("已达到今日 API 请求上限，可在设置中调整。");
            using var msg = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/" + endpoint);
            msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
            msg.Content = new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await http.SendAsync(msg, ct);
            if (((int)response.StatusCode == 429 || (int)response.StatusCode >= 500) && attempt < 3)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, response.Headers.RetryAfter?.Delta?.TotalSeconds ?? Math.Pow(2, attempt + 1))),ct); continue;
            }
            string body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                string code = "";
                try { code = JsonNode.Parse(body)?["error"]?["code"]?.ToString() ?? ""; } catch (JsonException) { }
                throw new InvalidOperationException($"OpenAI 请求失败：HTTP {(int)response.StatusCode} {code}。请检查密钥、模型权限或额度。");
            }
            var json = JsonNode.Parse(body)!.AsObject();
            int input = json["usage"]?.I("input_tokens") ?? 0, output = json["usage"]?.I("output_tokens") ?? 0;
            if (endpoint == "embeddings") input = json["usage"]?.I("total_tokens") ?? 0;
            db.Exec("UPDATE usage SET input_tokens=input_tokens+$i,output_tokens=output_tokens+$o WHERE day=$d",("$d",day),("$i",input),("$o",output));
            return json;
        }
    }
    public static string Text(JsonObject response) => string.Join("\n", response["output"]?.AsArray().Where(x => x?.S("type") == "message")
        .SelectMany(x => x!["content"]!.AsArray()).Where(x => x?.S("type") == "output_text").Select(x => x!.S("text")) ?? []);
    public async Task<JsonObject> Structured(string instructions, JsonArray content, JsonObject schema, string name, CancellationToken ct)
    {
        var response = await Post("responses",new JsonObject {
            ["model"]=settings.Current.Model,["store"]=false,["instructions"]=instructions,
            ["input"]=new JsonArray(new JsonObject{["role"]="user",["content"]=content}),
            ["text"]=new JsonObject{["format"]=new JsonObject{["type"]="json_schema",["name"]=name,["strict"]=true,["schema"]=schema}},
            ["max_output_tokens"]=12000
        },ct);
        if (response.S("status") != "completed") throw new InvalidOperationException("AI 输出未完成，请重试或调整模型。");
        return JsonNode.Parse(Text(response))?.AsObject() ?? throw new InvalidOperationException("AI 未返回有效结果。");
    }
    public async Task<float[]> Embed(string text, CancellationToken ct)
    {
        var response = await Post("embeddings",new JsonObject{["model"]=settings.Current.EmbeddingModel,["input"]=text,["encoding_format"]="float"},ct);
        return response["data"]![0]!["embedding"]!.AsArray().Select(x => x!.GetValue<float>()).ToArray();
    }
    public static JsonObject Schema(params (string name,string type)[] fields)
    {
        var properties = new JsonObject(); var required = new JsonArray();
        foreach(var (name,type) in fields) { properties[name] = new JsonObject{["type"]=type}; required.Add(name); }
        return new JsonObject{["type"]="object",["properties"]=properties,["required"]=required,["additionalProperties"]=false};
    }
}
