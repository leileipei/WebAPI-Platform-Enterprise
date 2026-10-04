using Xunit;
using System.Collections.Concurrent;
using System.Text.Json;
using WebApi.Gateway.Observability;
namespace WebApi.Gateway.Tests.Support;
public sealed class RecordingTelemetrySink : ITelemetryBatchSink
{
    public ConcurrentQueue<(string Signal, JsonElement Item)> Items {get;}=new();
    public bool Hold {get;set;}
    public bool Fail {get;set;}
    public TaskCompletionSource Started {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource released=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void Release()=>released.TrySetResult();
    public async Task ExportAsync(string signal,IReadOnlyList<JsonElement> items,CancellationToken ct)
    {
        Started.TrySetResult();
        if(Hold)await released.Task.WaitAsync(ct);
        if(Fail)throw new HttpRequestException("Injected synthetic export failure");
        foreach(var item in items)Items.Enqueue((signal,item.Clone()));
    }
    public JsonElement[] Logs=>Items.Where(x=>x.Signal=="logs").Select(x=>x.Item).ToArray();
    public static string? Attribute(JsonElement item,string key)
    {
        if(!item.TryGetProperty("attributes",out var attributes))return null;
        foreach(var a in attributes.EnumerateArray())if(a.GetProperty("key").GetString()==key){var v=a.GetProperty("value");return v.TryGetProperty("stringValue",out var s)?s.GetString():v.TryGetProperty("intValue",out var n)?n.GetString():v.TryGetProperty("doubleValue",out var d)?d.ToString():null;}
        return null;
    }
    public async Task WaitAsync(Func<bool> predicate)
    {var until=DateTime.UtcNow.AddSeconds(5);while(DateTime.UtcNow<until){if(predicate())return;await Task.Delay(20);}Assert.True(predicate(),"Expected real gateway telemetry was not recorded.");}
}
