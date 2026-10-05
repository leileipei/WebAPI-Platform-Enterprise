using System.Text;using WebApi.Contracts.Common;using WebApi.Contracts.Observability;using WebApi.Contracts.Security;using WebApi.Domain.Observability;
namespace WebApi.Infrastructure.Observability;
public sealed class AccessLogQueryService(ObservationScopeResolver resolver,LokiLogSource source,ObservationCursorCodec cursors,ObservationSignalCoverage coverage)
{
    public async Task<ObservationEnvelope<CursorPage<AccessLogDto>>> QueryAsync(ActorContext actor,ObservationScopeRequest scope,TimeRange range,LogFilter filter,CancellationToken ct)
    {
        Validate(scope,range,filter);var trusted=await resolver.ResolveAsync(actor,"log.read",scope,filter.ApiId,filter.ApplicationId,filter.DestinationId,ct);PolicyObservationProjection.RequireFilter(trusted,filter.PolicyId,filter.PolicyDecision);var prepared=source.Prepare(filter);
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try{var page=await ReadPageAsync(actor,trusted,range,filter,prepared,timeout.Token);await Reauthorize(actor,scope,filter,trusted,ct);return page;}
        catch(OperationCanceledException)when(!ct.IsCancellationRequested){throw ObservationSourceSettings.Unavailable("logs");}
    }
    private async Task<ObservationEnvelope<CursorPage<AccessLogDto>>> ReadPageAsync(ActorContext actor,TrustedObservationScope scope,TimeRange range,LogFilter filter,PreparedLogFilter prepared,CancellationToken ct)
    {
        var cursor=filter.Cursor is null?null:cursors.Decode(filter.Cursor,"logs",actor.UserId,scope,range,prepared.Hash);
        if(scope.EnvironmentIds.Count==0)return new(SourceState.NoData,range,null,new(false,[],"no_accessible_environments",false),null,new([],null,false));
        var batchTask=source.QueryAsync(scope,range,prepared,cursor,ct);var collectionTask=coverage.QueryAsync(scope,range,"logs",ct);await Task.WhenAll(batchTask,collectionTask);var batch=await batchTask;var collection=await collectionTask;
        var take=batch.Rows.Take(filter.Limit).ToArray();var more=batch.Rows.Count>take.Length;var boundary=take.LastOrDefault()?.Nanoseconds;var ids=boundary is null?[]:take.Where(x=>x.Nanoseconds==boundary.Value).Select(x=>x.Value.Id).Concat(cursor?.Nanoseconds==boundary?cursor.BoundaryIds:[]).Distinct().ToArray();
        var ambiguous=batch.Capped&&(boundary is null||batch.OldestNanoseconds>=boundary);var tokenLimit=more&&ids.Length>ObservationCursorCodec.MaximumBoundaryIds;var truncated=ambiguous||tokenLimit;string? next=null;
        if(!truncated&&boundary is long ns&&(more||batch.Capped))next=cursors.Encode("logs",actor.UserId,scope,range,prepared.Hash,ns,ids);
        var reason=ambiguous?"provider_boundary_cap":tokenLimit?"cursor_boundary_limit":batch.RejectedRows?"source_rows_rejected":collection.Coverage.Reason;
        var state=truncated||batch.RejectedRows?SourceState.Partial:collection.State==SourceState.Partial?SourceState.Partial:take.Length==0?SourceState.NoData:SourceState.Available;
        return new(state,range,collection.ObservedAt,collection.Coverage with{Complete=collection.Coverage.Complete&&!truncated&&!batch.RejectedRows,Reason=reason,Truncated=truncated},null,new(take.Select(x=>x.Value).ToArray(),next,truncated));
    }
    public async Task<ObservationEnvelope<AccessLogDto>> DetailAsync(ActorContext actor,ObservationScopeRequest scope,TimeRange range,Guid logId,CancellationToken ct)
    {
        var filter=new LogFilter(Limit:1);Validate(scope,range,filter);var trusted=await resolver.ResolveAsync(actor,"log.read",scope,null,null,null,ct);
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try{var batch=await source.QueryAsync(trusted,range,source.Prepare(filter),null,timeout.Token,logId);var row=batch.Rows.SingleOrDefault()??throw new ApiException(404,"observation_log_not_found","日志不存在或不在当前可访问范围内。");var collection=await coverage.QueryAsync(trusted,range,"logs",timeout.Token);await Reauthorize(actor,scope,filter,trusted,ct);
            return new(batch.RejectedRows||collection.State==SourceState.Partial?SourceState.Partial:SourceState.Available,range,collection.ObservedAt,collection.Coverage with {Complete=collection.Coverage.Complete&&!batch.RejectedRows,Reason=batch.RejectedRows?"source_rows_rejected":collection.Coverage.Reason},null,row.Value);}
        catch(OperationCanceledException)when(!ct.IsCancellationRequested){throw ObservationSourceSettings.Unavailable("logs");}
    }
    public async Task<ExportOutcome> ExportAsync(ActorContext actor,ObservationScopeRequest scope,TimeRange range,LogFilter filter,Stream output,CancellationToken ct)
    {
        filter=filter with{Cursor=null,Limit=100};Validate(scope,range,filter);var trusted=await resolver.ResolveAsync(actor,"log.read",scope,filter.ApiId,filter.ApplicationId,filter.DestinationId,ct);PolicyObservationProjection.RequireFilter(trusted,filter.PolicyId,filter.PolicyDecision);var prepared=source.Prepare(filter);using var bytes=new MemoryStream();bytes.Write(Encoding.UTF8.GetBytes(CsvLogExporter.Header));var rows=0;var truncated=false;var partial=false;var seen=new HashSet<Guid>();
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try{
            while(true){await Reauthorize(actor,scope,filter,trusted,timeout.Token);var page=await ReadPageAsync(actor,trusted,range,filter,prepared,timeout.Token);await Reauthorize(actor,scope,filter,trusted,timeout.Token);
                partial|=!page.Coverage.Complete;foreach(var log in page.Data!.Items){if(!seen.Add(log.Id))continue;var row=CsvLogExporter.Row(log);if(rows>=CsvLogExporter.MaximumRows||bytes.Length+row.Length>CsvLogExporter.MaximumBytes){truncated=true;break;}bytes.Write(row);rows++;}
                truncated|=page.Coverage.Truncated;if(truncated||page.Data.NextCursor is null)break;if(rows>=CsvLogExporter.MaximumRows){truncated=true;break;}filter=filter with{Cursor=page.Data.NextCursor};
            }
            await Reauthorize(actor,scope,filter,trusted,timeout.Token);bytes.Position=0;await bytes.CopyToAsync(output,ct);return new(rows,truncated,partial||truncated?SourceState.Partial:rows==0?SourceState.NoData:SourceState.Available);
        }catch(OperationCanceledException)when(!ct.IsCancellationRequested){throw ObservationSourceSettings.Unavailable("logs");}
    }
    private async Task Reauthorize(ActorContext actor,ObservationScopeRequest scope,LogFilter filter,TrustedObservationScope previous,CancellationToken ct)
    {var current=await resolver.ResolveAsync(actor,"log.read",scope,filter.ApiId,filter.ApplicationId,filter.DestinationId,ct);if(!current.EnvironmentIds.SequenceEqual(previous.EnvironmentIds))throw new ApiException(403,"scope_changed","数据范围已变更，请重新查询。");}
    private static void Validate(ObservationScopeRequest scope,TimeRange range,LogFilter filter){try{ArgumentNullException.ThrowIfNull(scope);ArgumentNullException.ThrowIfNull(filter);ObservationQueryValidator.Validate(range,filter.Limit,DateTimeOffset.UtcNow);_ = LokiLogSource.Nano(range.Start);_ = LokiLogSource.Nano(range.End);}catch(Exception e)when(e is ArgumentException or OverflowException){throw new ApiException(422,"invalid_observation_query","查询范围或分页参数不合法。");}}
}
