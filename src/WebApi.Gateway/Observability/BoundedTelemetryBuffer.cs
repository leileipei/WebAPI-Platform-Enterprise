using System.Text.Json;
using System.Threading.Channels;
namespace WebApi.Gateway.Observability;
public sealed class BoundedTelemetryBuffer : BackgroundService
{
    private readonly Channel<(string Signal,JsonElement Item)> queue;
    private readonly ITelemetryBatchSink sink;
    private readonly TelemetryDropTracker tracker;
    private readonly TelemetrySettings settings;
    public BoundedTelemetryBuffer(ITelemetryBatchSink sink,TelemetryDropTracker tracker,TelemetrySettings settings)
    {
        this.sink=sink;this.tracker=tracker;this.settings=settings;
        queue=Channel.CreateBounded<(string,JsonElement)>(new BoundedChannelOptions(settings.QueueCapacity){SingleReader=true,SingleWriter=false,FullMode=BoundedChannelFullMode.Wait,AllowSynchronousContinuations=false});
    }
    // This method owns the JSON storage, never retaining Activity, MetricPoint or LogRecord buffers.
    public bool TryWrite(string signal,JsonElement item)
    {if(queue.Writer.TryWrite((signal,item)))return true;tracker.Record(signal,1);return false;}
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while(await queue.Reader.WaitToReadAsync(stoppingToken))
            {
                await Task.Delay(settings.BatchDelayMs,stoppingToken);
                var batch=new List<(string Signal,JsonElement Item)>(512);
                while(batch.Count<512&&queue.Reader.TryRead(out var item))batch.Add(item);
                foreach(var group in batch.GroupBy(x=>x.Signal))
                {
                    var items=group.Select(x=>x.Item).ToArray();
                    using var timeout=CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);timeout.CancelAfter(TimeSpan.FromSeconds(5));
                    try{await sink.ExportAsync(group.Key,items,timeout.Token);tracker.Succeeded(group.Key);}
                    catch(OperationCanceledException)when(stoppingToken.IsCancellationRequested){return;}
                    catch(Exception){tracker.Failed(group.Key,items.Length);}
                }
            }
        }
        catch(OperationCanceledException)when(stoppingToken.IsCancellationRequested){}
    }
    public override async Task StopAsync(CancellationToken ct)
    {queue.Writer.TryComplete();await base.StopAsync(ct);while(queue.Reader.TryRead(out var item))tracker.Record(item.Signal,1);}
}
