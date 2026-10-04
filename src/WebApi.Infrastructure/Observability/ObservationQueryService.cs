using WebApi.Contracts.Common;
using WebApi.Contracts.Observability;
using WebApi.Contracts.Security;
using WebApi.Domain.Observability;
namespace WebApi.Infrastructure.Observability;
public sealed class ObservationQueryService(ObservationScopeResolver resolver,PrometheusMetricSource source)
{
    public async Task<ObservationEnvelope<MetricsDto>> MetricsAsync(ActorContext actor,ObservationScopeRequest scope,TimeRange range,MetricFilter filter,CancellationToken ct)
    {
        try{ObservationQueryValidator.Validate(range,filter.PageSize,DateTimeOffset.UtcNow);}
        catch(ArgumentException){throw new ApiException(422,"invalid_observation_query","时间范围或分页参数不合法。");}
        if(filter.Page is <1 or >1000000||filter.GroupBy is not("None" or "Api" or "Application" or "Destination")||!PrometheusMetricSource.KpiKeys.Contains(filter.SortBy))throw new ApiException(422,"invalid_observation_query","指标分组或排序参数不合法。");
        var trusted=await resolver.ResolveAsync(actor,"metrics.read",scope,filter.ApiId,filter.ApplicationId,filter.DestinationId,ct);
        var result=await source.QueryAsync(trusted,range,filter,ct);
        var current=await resolver.ResolveAsync(actor,"metrics.read",scope,filter.ApiId,filter.ApplicationId,filter.DestinationId,ct);
        if(!trusted.EnvironmentIds.SequenceEqual(current.EnvironmentIds))throw new ApiException(403,"scope_changed","数据范围已变更，请刷新。");
        return result;
    }
}
