using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Policies;
using WebApi.Contracts.Releases;
using WebApi.Contracts.Runtime;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Security;
namespace WebApi.Infrastructure.Policies;
public sealed class PolicyReferenceService(WebApiDbContext db,PolicyAccess access,AuthorizationService auth,ScopeResolver scopes)
{
    public async Task<PageResult<PolicyReferenceDto>> ListAsync(Guid id,ActorContext actor,int page,int pageSize,CancellationToken ct=default)
    {
        var p=await db.Set<Policy>().AsNoTracking().SingleOrDefaultAsync(p=>p.Id==id,ct)??throw ScopeResolver.Missing();if(!await access.CanReadAsync(new(p.OrganizationId,p.ProjectId),actor,ct)) throw ScopeResolver.Missing();
        var result=new List<PolicyReferenceDto>();foreach(var item in await FindProtectedAsync(id,ct)) {
            var scope=await scopes.EnvironmentAsync(item.EnvironmentId,ct);var permission=item.ReferenceKind=="Working"?"route.read":item.ReferenceKind=="FrozenRelease"?"release.read":"gateway.config.read";
            if(await auth.CanAsync(actor,permission,new("environment",item.EnvironmentId,scope),ct)) result.Add(item);
        }
        return Pagination.Slice(result.OrderBy(r=>r.ReferenceKind).ThenBy(r=>r.EnvironmentId).ThenBy(r=>r.Name).ToArray(),page,pageSize);
    }
    public async Task<IReadOnlyList<PolicyReferenceDto>> FindProtectedAsync(Guid id,CancellationToken ct=default)
    {
        var p=await db.Set<Policy>().AsNoTracking().SingleOrDefaultAsync(p=>p.Id==id,ct)??throw ScopeResolver.Missing();var result=new List<PolicyReferenceDto>();
        result.AddRange(await (from binding in db.Set<RoutePolicyBinding>().AsNoTracking() join route in db.Set<ApiRoute>() on binding.RouteId equals route.Id join version in db.Set<ApiVersion>() on route.ApiVersionId equals version.Id where binding.PolicyId==id select new PolicyReferenceDto("Working",route.EnvironmentId,version.ApiId,route.Id,null,p.VersionNo,route.RouteName)).ToArrayAsync(ct));
        var environmentIds=await (from e in db.Set<EnvironmentRecord>() join project in db.Set<Project>() on e.ProjectId equals project.Id where project.OrganizationId==p.OrganizationId select e.Id).ToArrayAsync(ct);
        var frozen=await db.Set<ReleaseRecord>().AsNoTracking().Where(r=>environmentIds.Contains(r.EnvironmentId)&&r.CandidateBytes!=null&&((r.ApprovalPolicy!=null&&(r.Status=="WaitingApproval"||r.Status=="Ready"||r.Status=="Building"||r.Status=="Publishing"))||(r.ReleaseType=="rollback"&&r.Status=="Draft"))).ToArrayAsync(ct);
        foreach(var release in frozen) {
            var candidate=Read<FrozenReleaseCandidate>(release.CandidateBytes!);foreach(var policy in candidate.Policies.Where(policy=>(policy.SourcePolicyId??policy.Id)==id)) result.Add(new("FrozenRelease",release.EnvironmentId,null,null,release.ToConfigVersion==0?null:release.ToConfigVersion,policy.Revision,release.ReleaseNo));
        }
        var desired=await db.Set<EnvironmentRecord>().AsNoTracking().Where(e=>environmentIds.Contains(e.Id)&&e.DesiredConfigVersion!=null).Select(e=>new {EnvironmentId=e.Id,Version=e.DesiredConfigVersion!.Value}).ToArrayAsync(ct);
        var applied=await db.Set<GatewayNode>().AsNoTracking().Where(n=>environmentIds.Contains(n.EnvironmentId)&&n.CurrentConfigVersion>0).Select(n=>new {n.EnvironmentId,Version=n.CurrentConfigVersion}).ToArrayAsync(ct);
        foreach(var current in desired.Concat(applied).Distinct()) {
            var bytes=await (from v in db.Set<GatewayConfigVersion>().AsNoTracking() join s in db.Set<GatewayConfigSnapshot>() on v.Id equals s.ConfigVersionId where v.EnvironmentId==current.EnvironmentId&&v.VersionNo==current.Version select s.PayloadBytes).SingleOrDefaultAsync(ct);
            if(bytes is null||bytes.Length==0) throw Unavailable();
            // 2.0 identifies policies directly; 2.1 adds sourcePolicyId and sourceRevision.
            try {using var snapshot=JsonDocument.Parse(bytes);foreach(var policy in snapshot.RootElement.GetProperty("policies").EnumerateArray()) {
                var source=policy.TryGetProperty("sourcePolicyId",out var sourceId)&&sourceId.ValueKind==JsonValueKind.String?sourceId.GetGuid():policy.GetProperty("id").GetGuid();
                if(source==id) result.Add(new("Runtime",current.EnvironmentId,null,null,current.Version,policy.TryGetProperty("sourceRevision",out var revision)&&revision.ValueKind==JsonValueKind.Number?revision.GetInt64():null,$"配置 v{current.Version}"));
            }} catch(Exception ex) when(ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException) {throw Unavailable();}
        }
        return result;
    }
    private static T Read<T>(byte[] bytes)
    {try {return JsonSerializer.Deserialize<T>(bytes,CanonicalJson.Options)??throw Unavailable();}catch(JsonException) {throw Unavailable();}}
    private static ApiException Unavailable()=>new(409,"policy_reference_unavailable","受保护快照无法校验，请先修复快照。");
}
