using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Releases;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Security;

namespace WebApi.Infrastructure.Releases;

public sealed class ReleaseListReader(WebApiDbContext db,CollectionAuthorizationQuery visibility,ApprovalEligibilityService eligibility,AuthorizationService auth,WebApi.Infrastructure.Delivery.DeliveryReleaseGuard delivery)
{
    public async Task<IReadOnlyList<ReleaseDto>> ReadAsync(IReadOnlyList<ReleaseRecord> releases,ActorContext actor,CancellationToken ct)
    {
        if(releases.Count==0)return [];
        var environment=releases[0].EnvironmentId;
        if(releases.Any(r=>r.EnvironmentId!=environment))throw new ArgumentException("A release page must use one environment.",nameof(releases));
        var entryInfo=await (from e in db.Set<EnvironmentRecord>().AsNoTracking() join p in db.Set<Project>().AsNoTracking() on e.ProjectId equals p.Id
            where e.Id==environment select new {p.OrganizationId,ProjectId=p.Id,e.AccessAddressRevision,e.GatewayPublicUrl,e.BasePath}).SingleAsync(ct);
        var scope=new ScopeRef(entryInfo.OrganizationId,entryInfo.ProjectId,environment);
        var ids=releases.Select(r=>r.Id).ToArray();
        var tasks=(await db.Set<ApprovalTask>().AsNoTracking().Where(t=>ids.Contains(t.ReleaseId)).OrderBy(t=>t.StepOrder).ThenBy(t=>t.Id).ToArrayAsync(ct)).ToLookup(t=>t.ReleaseId);
        var targets=(await db.Set<ReleaseTarget>().AsNoTracking().Where(t=>ids.Contains(t.ReleaseId)).ToArrayAsync(ct)).ToLookup(t=>t.ReleaseId);
        var acks=await db.Set<GatewayAck>().AsNoTracking().Where(a=>ids.Contains(a.ReleaseId)).ToDictionaryAsync(a=>(a.ReleaseId,a.NodeId),ct);
        var eligible=await eligibility.EvaluateBatchAsync(releases,scope,actor,ct);
        var contexts=await db.Set<ReleaseAccessContext>().AsNoTracking().Where(c=>ids.Contains(c.ReleaseId)&&c.EnvironmentId==environment).ToDictionaryAsync(c=>c.ReleaseId,ct);
        var showInternal=contexts.Count>0&&await auth.CanAsync(actor,"environment.write",new("environment",environment,scope),ct);
        var traces=await delivery.TraceBatchAsync(releases,actor,ct);
        var candidates=releases.Where(r=>r.CandidateBytes is not null&&r.ApprovalPolicy is not null)
            .ToDictionary(r=>r.Id,r=>JsonSerializer.Deserialize<FrozenReleaseCandidate>(r.CandidateBytes!,CanonicalJson.Options)!);
        var apiIds=candidates.Values.SelectMany(c=>c.RiskReviewReferences??[]).Select(r=>r.ApiId).Distinct().ToArray();
        var readable=new Dictionary<Guid,ScopeRef>();
        if(apiIds.Length>0)
        {
            var apiRead=visibility.Scopes(actor,"api.read");var versionRead=visibility.Scopes(actor,"api.version.read");var schemaRead=visibility.Scopes(actor,"api.schema.read");
            var resources=await db.Set<Api>().AsNoTracking().Where(a=>apiIds.Contains(a.Id)
                &&apiRead.Any(s=>s.OrganizationId==a.OrganizationId&&s.ProjectId==a.ProjectId&&s.EnvironmentId==null)
                &&versionRead.Any(s=>s.OrganizationId==a.OrganizationId&&s.ProjectId==a.ProjectId&&s.EnvironmentId==null)
                &&schemaRead.Any(s=>s.OrganizationId==a.OrganizationId&&s.ProjectId==a.ProjectId&&s.EnvironmentId==null))
                .Select(a=>new {a.Id,a.OrganizationId,a.ProjectId}).ToArrayAsync(ct);
            readable=resources.ToDictionary(a=>a.Id,a=>new ScopeRef(a.OrganizationId,a.ProjectId));
        }
        var result=new List<ReleaseDto>(releases.Count);
        foreach(var release in releases)
        {
            FrozenCandidateView? view=null;string? hash=null;
            if(candidates.TryGetValue(release.Id,out var candidate))
            {
                view=ReleaseService.SafeView(candidate);
                if(candidate.RiskReviewReferences is {Count:>0})view=view with {RiskReviews=candidate.RiskReviewReferences.Select(r=>r.Summary with {
                    Comment=readable.TryGetValue(r.ApiId,out var actual)&&actual.OrganizationId==candidate.OrganizationId&&actual.ProjectId==candidate.ProjectId?r.Summary.Comment:null}).ToArray()};
                hash=Convert.ToHexStringLower(SHA256.HashData(release.CandidateBytes!));
            }
            var rules=release.ApprovalPolicy is null?Array.Empty<ApprovalRule>():JsonSerializer.Deserialize<ApprovalRule[]>(release.ApprovalPolicy,CanonicalJson.Options)!;
            var steps=tasks[release.Id].Select(t=>new ApprovalTaskDto(t.Id,t.StepOrder,rules.Single(rule=>rule.StepOrder==t.StepOrder).RoleCode,t.Status,t.AssigneeUserId,t.Comment,t.ActedAt)).ToArray();
            var deployed=targets[release.Id].Select(t=>{acks.TryGetValue((release.Id,t.NodeId),out var ack);return new ReleaseTargetDto(t.NodeId,t.InstanceId,ack?.Success==true,ack?.ConfigVersion,ack?.DeploymentSequence,ack?.ErrorCode);}).ToArray();
            contexts.TryGetValue(release.Id,out var context);
            ReleaseAccessContextDto? entry=context is null?null:new(context.ReleaseId,context.EnvironmentId,context.AccessAddressRevision,context.PublicOrigin,context.BasePath,context.CapturedAt,showInternal?context.InternalOrigin:null,release.RecoveryOf);
            result.Add(new(release.Id,release.EnvironmentId,release.ReleaseNo,release.Status,release.ReleaseType,release.RequestedBy,release.CreatedAt,release.BaselineConfigVersion,release.ToConfigVersion,release.DeploymentSequence,release.RollbackOf,view,hash,steps,deployed,release.FailureCode is null?[]:[release.FailureCode],release.RecoveryOf,eligible[release.Id],entry,entryInfo.GatewayPublicUrl,entry is not null&&entry.AccessAddressRevision!=entryInfo.AccessAddressRevision,entryInfo.BasePath,traces[release.Id]));
        }
        return result;
    }
}
