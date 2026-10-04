using System.Collections.Concurrent;
namespace WebApi.Gateway.Observability;
public sealed class TelemetryDropTracker
{
    private readonly ConcurrentDictionary<string,long> dropped=new(new[]{"metrics","logs","traces"}.Select(s=>new KeyValuePair<string,long>(s,0)));
    private readonly ConcurrentDictionary<string,long> failed=new(new[]{"metrics","logs","traces"}.Select(s=>new KeyValuePair<string,long>(s,0)));
    private readonly ConcurrentDictionary<string,double> lastSuccess=new();
    private readonly ConcurrentDictionary<string,double> lastLoss=new(new[]{"metrics","logs","traces"}.Select(s=>new KeyValuePair<string,double>(s,0)));
    public long DroppedCount=>dropped.Values.Sum();
    public long ExportFailedCount=>failed.Values.Sum();
    public IEnumerable<KeyValuePair<string,long>> Drops=>dropped.ToArray();
    public IEnumerable<KeyValuePair<string,long>> Failures=>failed.ToArray();
    public IEnumerable<KeyValuePair<string,double>> LastSuccess=>lastSuccess.ToArray();
    public IEnumerable<KeyValuePair<string,double>> LastLoss=>lastLoss.ToArray();
    public void Record(string signal,long count)=>Add(dropped,signal,count);
    public void Failed(string signal,long count)=>Add(failed,signal,count);
    public void Succeeded(string signal)=>lastSuccess[signal]=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()/1000d;
    private void Add(ConcurrentDictionary<string,long> target,string signal,long count)
    {if(signal is not("metrics" or "logs" or "traces")||count<=0)return;target.AddOrUpdate(signal,count,(_,prior)=>prior+count);lastLoss[signal]=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()/1000d;}
}
