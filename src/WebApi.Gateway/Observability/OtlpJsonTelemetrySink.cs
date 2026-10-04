using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace WebApi.Gateway.Observability;
public sealed class OtlpJsonTelemetrySink : ITelemetryBatchSink,IDisposable
{
    private readonly HttpClient client;
    private readonly object resource;
    public OtlpJsonTelemetrySink(TelemetrySettings settings,GatewaySettings gateway)
    {
        client=new(new SocketsHttpHandler{AllowAutoRedirect=false,UseCookies=false}){BaseAddress=new Uri(settings.CollectorEndpoint.AbsoluteUri.TrimEnd('/')+"/"),Timeout=Timeout.InfiniteTimeSpan};
        if(settings.CredentialSecretFile is {Length:>0})
        {try{client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",File.ReadAllText(settings.CredentialSecretFile).Trim());}catch(Exception error)when(error is IOException or UnauthorizedAccessException or FormatException){throw new InvalidOperationException("Telemetry credential configuration is invalid.");}}
        resource=new{attributes=new[]{TelemetryAttributes.Attribute("service.name","webapi-gateway"),TelemetryAttributes.Attribute("service.instance.id",gateway.NodeName),TelemetryAttributes.Attribute("webapi.environment.id",gateway.EnvironmentId.ToString())}};
    }
    public async Task ExportAsync(string signal,IReadOnlyList<JsonElement> items,CancellationToken ct)
    {
        var scope=new{name="WebApi.Gateway"};object body=signal switch{
            "logs"=>new{resourceLogs=new[]{new{resource,scopeLogs=new[]{new{scope,logRecords=items}}}}},
            "traces"=>new{resourceSpans=new[]{new{resource,scopeSpans=new[]{new{scope,spans=items}}}}},
            "metrics"=>new{resourceMetrics=new[]{new{resource,scopeMetrics=new[]{new{scope,metrics=MergeMetrics(items)}}}}},
            _=>throw new ArgumentException("Unsupported telemetry signal.")};
        using var request=new HttpRequestMessage(HttpMethod.Post,"v1/"+signal){Content=JsonContent.Create(body)};
        using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);response.EnsureSuccessStatusCode();
        // Do not retain provider responses or expose their error messages.
        const int maximum=64*1024;
        if(response.Content.Headers.ContentLength>maximum)throw new InvalidOperationException("Collector response limit exceeded.");
        await using var stream=await response.Content.ReadAsStreamAsync(ct);using var payload=new MemoryStream();var readBuffer=new byte[4096];
        while(true){var count=await stream.ReadAsync(readBuffer,ct);if(count==0)break;if(payload.Length+count>maximum)throw new InvalidOperationException("Collector response limit exceeded.");payload.Write(readBuffer,0,count);}
        using var document=JsonDocument.Parse(payload.ToArray());var result=document.RootElement;
        if(result.TryGetProperty("partialSuccess",out var partial)&&partial.EnumerateObject().Any(x=>x.Value.ToString() is not("" or "0")))throw new InvalidOperationException("Collector partially rejected telemetry.");
    }
    private static JsonObject[] MergeMetrics(IReadOnlyList<JsonElement> items)
    {
        var result=new Dictionary<string,JsonObject>();
        foreach(var item in items)
        {
            var node=JsonNode.Parse(item.GetRawText())!.AsObject();var name=node["name"]!.GetValue<string>();
            if(!result.TryGetValue(name,out var existing)){result[name]=node;continue;}
            var type=node.ContainsKey("sum")?"sum":node.ContainsKey("histogram")?"histogram":"gauge";
            var points=existing[type]!["dataPoints"]!.AsArray();foreach(var point in node[type]!["dataPoints"]!.AsArray())points.Add(point!.DeepClone());
        }
        return result.Values.ToArray();
    }
    public void Dispose()=>client.Dispose();
}
