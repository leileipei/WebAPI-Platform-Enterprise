using WebApi.Contracts.Common;using WebApi.Contracts.Observability;using WebApi.Contracts.Security;using WebApi.Domain.Observability;
namespace WebApi.Infrastructure.Observability;
public sealed class TraceQueryService(ObservationScopeResolver resolver,TempoTraceSource source,ObservationCursorCodec cursors,ObservationSignalCoverage coverage,ObservationSourceSettings settings)
{
    public async Task<ObservationEnvelope<CursorPage<TraceSummaryDto>>> SearchAsync(ActorContext actor,ObservationScopeRequest scope,TimeRange range,TraceFilter filter,CancellationToken ct)
    {
        Validate(scope,range,filter);var trusted=await resolver.ResolveAsync(actor,"trace.read",scope,filter.ApiId,null,null,ct);var hash=ObservationCursorCodec.Hash(filter with{Cursor=null,Limit=0});var cursor=filter.Cursor is null?null:cursors.Decode(filter.Cursor,"traces",actor.UserId,trusted,range,hash);
        if(trusted.EnvironmentIds.Count==0)return new(SourceState.NoData,range,null,new(false,[],"no_accessible_environments",false),Sampling(),new([],null,false));
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try{
            var candidateTask=source.SearchAsync(trusted,range,filter,timeout.Token);var coverageTask=coverage.QueryAsync(trusted,range,"traces",timeout.Token);var candidates=await candidateTask;var summaries=new List<TraceSummaryDto>();var partial=candidates.Truncated;using var concurrency=new SemaphoreSlim(4);
            var results=await Task.WhenAll(candidates.TraceIds.Select(async id=>{await concurrency.WaitAsync(timeout.Token);try{var batch=await source.DetailAsync(id,range,timeout.Token);var projected=ProjectRange(id,batch,trusted,range);var matching=projected.Spans.Where(x=>filter.ApiId is null||x.Tags.GetValueOrDefault("webapi.api.id")==filter.ApiId.Value.ToString()).ToArray();
                    if(matching.Length==0)return (Summary:(TraceSummaryDto?)null,Partial:projected.PartialTrace);
                    var start=matching.Min(x=>x.Start);var end=matching.Max(x=>x.Start.AddMilliseconds(x.DurationMs));var duration=(end-start).TotalMilliseconds;var outcome=matching.Any(x=>x.Status=="Error"||x.Tags.GetValueOrDefault("webapi.outcome") is "Timeout" or "ProxyError" or "ClientAborted")?"Error":"Success";
                    if(filter.MinDurationMs is double min&&duration<min||filter.Outcome is string expected&&expected!=outcome)return(Summary:(TraceSummaryDto?)null,Partial:projected.PartialTrace);
                    return(Summary:(TraceSummaryDto?)new(id,start,duration,outcome),Partial:projected.PartialTrace);
                }catch(ApiException e)when(e.Code=="observation_trace_not_found"){return(Summary:(TraceSummaryDto?)null,Partial:true);}finally{concurrency.Release();}}));
            foreach(var item in results){partial|=item.Partial;if(item.Summary is not null)summaries.Add(item.Summary);}var ordered=summaries.OrderByDescending(x=>x.Start).ThenByDescending(x=>Guid.ParseExact(x.TraceId,"N"));
            var eligible=ordered.Where(x=>cursor is null||LokiLogSource.Nano(x.Start)<cursor.Nanoseconds||LokiLogSource.Nano(x.Start)==cursor.Nanoseconds&&!cursor.BoundaryIds.Contains(Guid.ParseExact(x.TraceId,"N"))).ToArray();var take=eligible.Take(filter.Limit).ToArray();var more=eligible.Length>take.Length;var boundary=take.LastOrDefault()?.Start;var ids=boundary is null?[]:take.Where(x=>x.Start==boundary).Select(x=>Guid.ParseExact(x.TraceId,"N")).Concat(cursor?.Nanoseconds==LokiLogSource.Nano(boundary.Value)?cursor.BoundaryIds:[]).Distinct().ToArray();
            var truncated=candidates.Truncated||(more&&ids.Length>ObservationCursorCodec.MaximumBoundaryIds);string? next=null;if(more&&!truncated&&boundary is not null)next=cursors.Encode("traces",actor.UserId,trusted,range,hash,LokiLogSource.Nano(boundary.Value),ids);
            var collection=await coverageTask;await Reauthorize(actor,scope,filter.ApiId,trusted,ct);var reason=candidates.Truncated?"provider_candidate_cap":truncated?"cursor_boundary_limit":partial?"partial_trace":collection.Coverage.Reason;
            return new(partial||truncated||collection.State==SourceState.Partial?SourceState.Partial:take.Length==0?SourceState.NoData:SourceState.Available,range,collection.ObservedAt,collection.Coverage with{Complete=collection.Coverage.Complete&&!partial&&!truncated,Reason=reason,Truncated=truncated},Sampling(),new(take,next,truncated));
        }catch(OperationCanceledException)when(!ct.IsCancellationRequested){throw ObservationSourceSettings.Unavailable("traces");}
    }
    public async Task<ObservationEnvelope<TraceDetailDto>> DetailAsync(ActorContext actor,ObservationScopeRequest scope,string traceId,TimeRange range,CancellationToken ct)
    {
        Validate(scope,range,new TraceFilter(traceId));traceId=TempoTraceSource.TraceId(traceId);var trusted=await resolver.ResolveAsync(actor,"trace.read",scope,null,null,null,ct);
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try{var batch=await source.DetailAsync(traceId,range,timeout.Token);var projected=ProjectRange(traceId,batch,trusted,range);if(projected.Spans.Count==0)throw new ApiException(404,"observation_trace_not_found","未找到当前可访问范围的链路；可能未采样或已超过保留期。");var collection=await coverage.QueryAsync(trusted,range,"traces",timeout.Token);await Reauthorize(actor,scope,null,trusted,ct);
            return new(projected.PartialTrace||collection.State==SourceState.Partial?SourceState.Partial:SourceState.Available,range,collection.ObservedAt,collection.Coverage with{Complete=collection.Coverage.Complete&&!projected.PartialTrace,Reason=projected.PartialTrace?"partial_trace":collection.Coverage.Reason},Sampling(),projected);
        }catch(OperationCanceledException)when(!ct.IsCancellationRequested){throw ObservationSourceSettings.Unavailable("traces");}
    }
    private static TraceDetailDto ProjectRange(string id,TraceSpanBatch batch,TrustedObservationScope scope,TimeRange range)
    {var projected=TraceProjection.Project(id,batch.Spans,scope);var visible=projected.Spans.Where(x=>x.Start>=range.Start&&x.Start<range.End).ToArray();var ids=visible.Select(x=>x.SpanId).ToHashSet();var removedParent=visible.Any(x=>x.ParentSpanId is string parent&&!ids.Contains(parent));return projected with{PartialTrace=projected.PartialTrace||batch.Rejected||visible.Length!=projected.Spans.Count||removedParent,Spans=visible.Select(x=>x.ParentSpanId is string parent&&!ids.Contains(parent)?x with{ParentSpanId=null}:x).ToArray()};}
    private SamplingDto Sampling()=>new(settings.TraceSampleRatio,"ParentBasedTraceIdRatioBased");
    private async Task Reauthorize(ActorContext actor,ObservationScopeRequest scope,Guid? api,TrustedObservationScope previous,CancellationToken ct){var current=await resolver.ResolveAsync(actor,"trace.read",scope,api,null,null,ct);if(!current.EnvironmentIds.SequenceEqual(previous.EnvironmentIds))throw new ApiException(403,"scope_changed","数据范围已变更，请重新查询。");}
    private static void Validate(ObservationScopeRequest scope,TimeRange range,TraceFilter filter)
    {try{ArgumentNullException.ThrowIfNull(scope);ArgumentNullException.ThrowIfNull(filter);ObservationQueryValidator.Validate(range,filter.Limit,DateTimeOffset.UtcNow);_=LokiLogSource.Nano(range.Start);_=LokiLogSource.Nano(range.End);if(filter.MinDurationMs is double value&&(!double.IsFinite(value)||value<0)||filter.Outcome is not(null or "Success" or "Error"))throw new ArgumentException();if(filter.TraceId is not null)_=TempoTraceSource.TraceId(filter.TraceId);}catch(Exception e)when(e is ArgumentException or OverflowException){throw new ApiException(422,"invalid_observation_query","链路查询条件不合法。");}}
}
