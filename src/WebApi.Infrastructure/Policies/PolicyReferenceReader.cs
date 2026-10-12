using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Policies;
using WebApi.Contracts.Releases;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Security;
namespace WebApi.Infrastructure.Policies;
internal sealed record ReferencePayloadDescriptor(Guid Id,string Kind,Guid EnvironmentId,long SizeBytes);
public sealed class PolicyReferenceReader(WebApiDbContext db,CollectionAuthorizationQuery visibility)
{
    private const long BatchBytes=8*1024*1024;
    private sealed record Summary(PolicyReferenceDto Reference,Guid SourceId,int Position=0);
    private sealed record FrozenSource(Guid Id,Guid EnvironmentId,long Version,string Name,long SizeBytes);
    private sealed record RuntimeSource(Guid? Id,Guid EnvironmentId,long Version,long? SizeBytes);
    internal static IReadOnlyList<IReadOnlyList<ReferencePayloadDescriptor>> PlanBatches(IReadOnlyList<ReferencePayloadDescriptor> sources)
    {
        var result=new List<IReadOnlyList<ReferencePayloadDescriptor>>();var batch=new List<ReferencePayloadDescriptor>();long bytes=0;
        foreach(var source in sources)
        {
            if(source.SizeBytes<0)throw new ArgumentOutOfRangeException(nameof(sources));
            if(batch.Count>0&&(batch.Count==200||batch[0].Kind!=source.Kind||source.SizeBytes>BatchBytes-bytes))
            {result.Add(batch.ToArray());batch.Clear();bytes=0;}
            batch.Add(source);bytes+=source.SizeBytes;
            if(source.SizeBytes>BatchBytes){result.Add(batch.ToArray());batch.Clear();bytes=0;}
        }
        if(batch.Count>0)result.Add(batch.ToArray());return result;
    }
    public async Task<IReadOnlyList<PolicyReferenceDto>> ReadVisibleAsync(Guid policyId,ActorContext actor,CancellationToken ct)
    {
        await visibility.RequireActiveActorAsync(actor,ct);
        var policy=await db.Set<Policy>().AsNoTracking().Where(p=>p.Id==policyId).Select(p=>new{p.OrganizationId,p.VersionNo}).SingleOrDefaultAsync(ct)??throw ScopeResolver.Missing();
        var routes=visibility.Scopes(actor,"route.read");
        var result=(await (from binding in db.Set<RoutePolicyBinding>().AsNoTracking()
            join route in db.Set<ApiRoute>().AsNoTracking() on binding.RouteId equals route.Id
            join version in db.Set<ApiVersion>().AsNoTracking() on route.ApiVersionId equals version.Id
            where binding.PolicyId==policyId&&routes.Any(s=>s.EnvironmentId==route.EnvironmentId)
            select new{route.Id,route.EnvironmentId,version.ApiId,route.RouteName}).ToArrayAsync(ct))
            .Select(r=>new Summary(new("Working",r.EnvironmentId,r.ApiId,r.Id,null,policy.VersionNo,r.RouteName),r.Id)).ToList();
        var releaseScopes=visibility.Scopes(actor,"release.read");
        // Length is translated to octet_length(bytea); descriptors never load payload arrays.
        var frozen=await (from release in db.Set<ReleaseRecord>().AsNoTracking()
            join environment in db.Set<EnvironmentRecord>().AsNoTracking() on release.EnvironmentId equals environment.Id
            join project in db.Set<Project>().AsNoTracking() on environment.ProjectId equals project.Id
            where project.OrganizationId==policy.OrganizationId&&releaseScopes.Any(s=>s.EnvironmentId==environment.Id)
                &&release.CandidateBytes!=null&&((release.ApprovalPolicy!=null&&(release.Status=="WaitingApproval"||release.Status=="Ready"||release.Status=="Building"||release.Status=="Publishing"))||(release.ReleaseType=="rollback"&&release.Status=="Draft"))
            orderby release.Id
            select new FrozenSource(release.Id,release.EnvironmentId,release.ToConfigVersion,release.ReleaseNo,(long)release.CandidateBytes!.Length)).ToArrayAsync(ct);
        var runtimeScopes=visibility.Scopes(actor,"gateway.config.read");
        var desired=from environment in db.Set<EnvironmentRecord>().AsNoTracking()
            join project in db.Set<Project>().AsNoTracking() on environment.ProjectId equals project.Id
            where project.OrganizationId==policy.OrganizationId&&environment.DesiredConfigVersion!=null&&runtimeScopes.Any(s=>s.EnvironmentId==environment.Id)
            select new{EnvironmentId=environment.Id,Version=environment.DesiredConfigVersion!.Value};
        // Disabled nodes still protect and expose their actually applied version when readable.
        var applied=from node in db.Set<GatewayNode>().AsNoTracking()
            join environment in db.Set<EnvironmentRecord>().AsNoTracking() on node.EnvironmentId equals environment.Id
            join project in db.Set<Project>().AsNoTracking() on environment.ProjectId equals project.Id
            where project.OrganizationId==policy.OrganizationId&&node.CurrentConfigVersion>0&&runtimeScopes.Any(s=>s.EnvironmentId==environment.Id)
            select new{node.EnvironmentId,Version=node.CurrentConfigVersion};
        var runtime=await (from current in desired.Union(applied)
            join version in db.Set<GatewayConfigVersion>().AsNoTracking() on new{current.EnvironmentId,current.Version} equals new{version.EnvironmentId,Version=version.VersionNo} into versions
            from version in versions.DefaultIfEmpty()
            join snapshot in db.Set<GatewayConfigSnapshot>().AsNoTracking() on version.Id equals snapshot.ConfigVersionId into snapshots
            from snapshot in snapshots.DefaultIfEmpty()
            select new RuntimeSource((Guid?)snapshot.ConfigVersionId,current.EnvironmentId,current.Version,(long?)snapshot.PayloadBytes.Length)).ToArrayAsync(ct);
        if(runtime.Any(s=>s.Id is null||s.SizeBytes is null or 0))throw Unavailable();
        var frozenById=frozen.ToDictionary(s=>s.Id);var runtimeById=runtime.ToDictionary(s=>s.Id!.Value);
        var descriptors=frozen.Select(s=>new ReferencePayloadDescriptor(s.Id,"FrozenRelease",s.EnvironmentId,s.SizeBytes))
            .Concat(runtime.OrderBy(s=>s.Id).Select(s=>new ReferencePayloadDescriptor(s.Id!.Value,"Runtime",s.EnvironmentId,s.SizeBytes!.Value))).ToArray();
        foreach(var batch in PlanBatches(descriptors).SelectMany(b=>b.Select(d=>(IReadOnlyList<ReferencePayloadDescriptor>)new[]{d})))
        {
            if(batch[0].Kind=="FrozenRelease")result.AddRange(await ReadFrozenBatchAsync(batch,frozenById,policyId,ct));
            else result.AddRange(await ReadRuntimeBatchAsync(batch,runtimeById,policyId,ct));
        }
        return result.OrderBy(r=>r.Reference.ReferenceKind).ThenBy(r=>r.Reference.EnvironmentId).ThenBy(r=>r.Reference.Name)
            .ThenBy(r=>r.SourceId).ThenBy(r=>r.Position).Select(r=>r.Reference).ToArray();
    }
    private async Task<IReadOnlyList<Summary>> ReadFrozenBatchAsync(IReadOnlyList<ReferencePayloadDescriptor> batch,IReadOnlyDictionary<Guid,FrozenSource> metadata,Guid policyId,CancellationToken ct)
    {
        var ids=batch.Select(s=>s.Id).ToArray();var payloads=await db.Set<ReleaseRecord>().AsNoTracking().Where(r=>ids.Contains(r.Id)).Select(r=>new{r.Id,r.CandidateBytes}).ToArrayAsync(ct);
        if(payloads.Length!=ids.Length)throw Unavailable();var result=new List<Summary>();
        foreach(var payload in payloads)
        {
            var source=metadata[payload.Id];if(payload.CandidateBytes is null||payload.CandidateBytes.Length==0)throw Unavailable();
            try
            {
                var candidate=JsonSerializer.Deserialize<FrozenReleaseCandidate>(payload.CandidateBytes,CanonicalJson.Options)??throw Unavailable();
                var policies=candidate.Policies??throw Unavailable();var position=0;
                foreach(var policy in policies)
                {
                    if(policy is null)throw Unavailable();
                    if((policy.SourcePolicyId??policy.Id)==policyId)result.Add(new(new("FrozenRelease",source.EnvironmentId,null,null,source.Version==0?null:source.Version,policy.Revision,source.Name),source.Id,position));position++;
                }
            }
            catch(JsonException){throw Unavailable();}
        }
        return result;
    }
    private async Task<IReadOnlyList<Summary>> ReadRuntimeBatchAsync(IReadOnlyList<ReferencePayloadDescriptor> batch,IReadOnlyDictionary<Guid,RuntimeSource> metadata,Guid policyId,CancellationToken ct)
    {
        var ids=batch.Select(s=>s.Id).ToArray();var payloads=await db.Set<GatewayConfigSnapshot>().AsNoTracking().Where(s=>ids.Contains(s.ConfigVersionId)).Select(s=>new{s.ConfigVersionId,s.PayloadBytes}).ToArrayAsync(ct);
        if(payloads.Length!=ids.Length)throw Unavailable();var result=new List<Summary>();
        foreach(var payload in payloads)
        {
            var source=metadata[payload.ConfigVersionId];if(payload.PayloadBytes.Length==0)throw Unavailable();
            try
            {
                using var snapshot=JsonDocument.Parse(payload.PayloadBytes);var position=0;
                foreach(var policy in snapshot.RootElement.GetProperty("policies").EnumerateArray())
                {
                    var id=policy.TryGetProperty("sourcePolicyId",out var native)&&native.ValueKind==JsonValueKind.String?native.GetGuid():policy.GetProperty("id").GetGuid();
                    if(id==policyId)result.Add(new(new("Runtime",source.EnvironmentId,null,null,source.Version,policy.TryGetProperty("sourceRevision",out var revision)&&revision.ValueKind==JsonValueKind.Number?revision.GetInt64():null,$"配置 v{source.Version}"),payload.ConfigVersionId,position));position++;
                }
            }
            catch(Exception error)when(error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException){throw Unavailable();}
        }
        return result;
    }
    private static ApiException Unavailable()=>new(409,"policy_reference_unavailable","受保护快照无法校验，请先修复快照。");
}
