using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;
namespace WebApi.EndToEnd.Tests;
public sealed class ObservabilityScenario : IAsyncDisposable
{
    private ExternalScenario core=null!;private JsonElement context;private string credential="";
    private readonly HttpClient api=new(new HttpClientHandler{CookieContainer=new CookieContainer()}){BaseAddress=new("http://control-plane:8080")};
    private readonly HttpClient source=new(){BaseAddress=new("http://prometheus:9090")};
    public Guid EnvironmentId=>context.GetProperty("environmentId").GetGuid();public Guid ApiId=>context.GetProperty("apiId").GetGuid();
    public string Scope=>$"organizationId={context.GetProperty("organizationId").GetString()}&projectId={context.GetProperty("projectId").GetString()}&environmentId={EnvironmentId}";
    public static async Task<ObservabilityScenario> CreateAsync()
    {
        var file=System.Environment.GetEnvironmentVariable("WEBAPI_E2E_CONTEXT_FILE");Assert.True(file is not null&&File.Exists(file),"Run check-observability.sh for a disposable fixture.");
        var s=new ObservabilityScenario{context=JsonDocument.Parse(await File.ReadAllTextAsync(file!)).RootElement.Clone()};s.core=await ExternalScenario.ConnectAsync();
        await ExternalScenario.WriteAsync(s.api,"/auth/login",new{username="e2e-admin",password=await File.ReadAllTextAsync(s.context.GetProperty("passwordFile").GetString()!)});
        s.credential=await File.ReadAllTextAsync(s.context.GetProperty("credentialFile").GetString()!);return s;
    }
    public Task<JsonElement> PublishAsync(string backend)=>core.PublishAsync(backend,"/orders/{**path}");
    public async Task<JsonElement> QueryAsync(string path){using var r=await api.GetAsync("/api/v1"+path);if(r.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.ServiceUnavailable)throw new HttpRequestException("Observation ingestion pending or source unavailable",null,r.StatusCode);Assert.True(r.IsSuccessStatusCode,$"Query {path.Split('?')[0]} HTTP {(int)r.StatusCode}");return await r.Content.ReadFromJsonAsync<JsonElement>();}
    public Task<JsonElement> CommandAsync(string path,object body,long? revision=null)=>ExternalScenario.WriteAsync(api,path,body,revision is null?null:$"\"{revision}\"");
    public async Task<(int Status,JsonElement Body)> GatewayRequestAsync(int node,string path="/orders",string? trace=null)
    {
        using var client=new HttpClient{BaseAddress=new($"http://gateway-{(node==0?"a":"b")}:8080")};using var request=new HttpRequestMessage(HttpMethod.Get,path);request.Headers.Add("X-API-Key",credential);if(trace is not null)request.Headers.Add("traceparent",$"00-{trace}-0123456789abcdef-01");using var response=await client.SendAsync(request);return ((int)response.StatusCode,await response.Content.ReadFromJsonAsync<JsonElement>());
    }
    public async Task<double> RawCounterAsync(){using var r=await source.GetAsync("/api/v1/query?query="+Uri.EscapeDataString($"sum(webapi_gateway_requests_total{{webapi_environment_id=\"{EnvironmentId}\"}})"));r.EnsureSuccessStatusCode();var j=await r.Content.ReadFromJsonAsync<JsonElement>();return j.GetProperty("data").GetProperty("result").EnumerateArray().Sum(x=>double.Parse(x.GetProperty("value")[1].GetString()!,System.Globalization.CultureInfo.InvariantCulture));}
    public static async Task<T> WaitForConditionAsync<T>(Func<Task<T>> action,Func<T,bool> condition,int seconds=120){var end=DateTimeOffset.UtcNow.AddSeconds(seconds);while(true){try{var value=await action();if(condition(value))return value;}catch(HttpRequestException e)when(e.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.ServiceUnavailable){if(DateTimeOffset.UtcNow>=end)throw;}if(DateTimeOffset.UtcNow>=end)Assert.Fail("Real observation condition did not converge.");await Task.Delay(500);}}
    public string Range(DateTimeOffset start,DateTimeOffset end)=>Scope+$"&start={Uri.EscapeDataString(start.ToString("O"))}&end={Uri.EscapeDataString(end.ToString("O"))}";
    public async ValueTask DisposeAsync(){api.Dispose();source.Dispose();await core.DisposeAsync();}
}
