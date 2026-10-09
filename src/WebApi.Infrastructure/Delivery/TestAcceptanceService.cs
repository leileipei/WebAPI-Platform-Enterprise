using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Releases;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Commands;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Security;
namespace WebApi.Infrastructure.Delivery;

public sealed class TestAcceptanceService(WebApiDbContext db,ReleaseArtifactService artifacts,ScopeResolver scopes,AuthorizationService auth,
    AuditedCommandExecutor commands,IdempotentCommandExecutor idempotency,CommandRequestContext requestContext)
{
    private static readonly string[] defaultTypes=["InterfaceFunction","Integration","ContractCompatibility"];
    private static ApiException Stale()=>new(409,"test_evidence_not_current","测试证据、来源运行状态、入口或连接规则已变化，请重新测试并申请验收。");
    public async Task<ArtifactEligibilityDto> EligibilityAsync(Guid artifactId,ActorContext actor,CancellationToken ct)
    {var artifact=await artifacts.GetAsync(artifactId,actor,ct);return (await EligibilityFactsAsync(artifact,actor,ct)).Dto;}
    private async Task<(ArtifactEligibilityDto Dto,ReleaseVerification[] Current)> EligibilityFactsAsync(ReleaseArtifactDto artifact,ActorContext actor,CancellationToken ct)
    {
        var scope=await scopes.EnvironmentAsync(artifact.SourceEnvironmentId,ct);
        var policy=await db.Set<ProjectDeliveryPolicy>().AsNoTracking().SingleOrDefaultAsync(p=>p.ProjectId==artifact.ProjectId,ct);
        var required=policy?.RequiredTestTypes??defaultTypes;var revision=policy?.Revision??0;var reasons=new List<string>();
        var writable=await auth.CanAsync(actor,"release.test.record",new("environment",artifact.SourceEnvironmentId,scope),ct);
        if(!writable)reasons.Add("release_test_record_or_write_scope_required");
        Guid? target=null;var restricted=false;
        if(policy is not null){if(await auth.CanAsync(actor,"environment.read",new("environment",policy.TargetEnvironmentId,scope with{EnvironmentId=policy.TargetEnvironmentId}),ct))target=policy.TargetEnvironmentId;else restricted=true;if(policy.SourceEnvironmentId!=artifact.SourceEnvironmentId)reasons.Add("delivery_source_mismatch");}
        var current=new List<ReleaseVerification>();var sourceCurrent=false;
        try{
            var source=await artifacts.RequireSourceAsync(artifact.SourceReleaseId,actor,ct);
            sourceCurrent=source.Deployment.Snapshot.Hash==artifact.SourceSnapshotHash&&WebApi.Domain.Delivery.ReleaseArtifactCanonicalizer.Hash(source.Content)==artifact.ArtifactHash&&(policy is null||policy.SourceEnvironmentId==artifact.SourceEnvironmentId);
            if(sourceCurrent)foreach(var type in defaultTypes){var row=await db.Set<ReleaseVerification>().AsNoTracking().Where(v=>v.ArtifactId==artifact.Id&&v.Phase=="SourceTest"&&v.Type==type).OrderByDescending(v=>v.CreatedAt).ThenByDescending(v=>v.Id).FirstOrDefaultAsync(ct);if(row is not null&&row.Result=="Passed"&&row.IsManual&&row.ExpiresAt>DateTimeOffset.UtcNow&&row.PolicyRevision==revision&&row.ReleaseId==artifact.SourceReleaseId&&row.EnvironmentId==artifact.SourceEnvironmentId&&row.ConfigVersion==source.Deployment.Release.ToConfigVersion&&row.DeploymentSequence==source.Deployment.Release.DeploymentSequence&&row.SnapshotHash==artifact.SourceSnapshotHash&&row.AccessAddressRevision==source.Deployment.Environment.AccessAddressRevision)current.Add(row);}
        }catch(ApiException e)when(e.Status is 409 or 422){reasons.Add(e.Code);}
        if(!sourceCurrent)reasons.Add("artifact_source_not_current");var complete=required.All(type=>current.Any(v=>v.Type==type));if(!complete)reasons.Add("current_required_passed_evidence_incomplete");
        return(new(artifact.Id,artifact.SourceEnvironmentId,target,restricted,policy?.Mode??"Legacy",required,policy?.VerificationValidityMinutes??1440,revision,writable&&sourceCurrent,writable,writable&&sourceCurrent&&complete,current.Where(v=>required.Contains(v.Type)).Select(v=>v.Id).Order().ToArray(),reasons.Distinct().ToArray()),current.ToArray());
    }
    private TestAcceptanceDto AuthorityView(ReleaseTestAcceptance row,ReleaseArtifactDto artifact,IReadOnlyList<ReleaseVerification> evidence,(ArtifactEligibilityDto Dto,ReleaseVerification[] Current) eligibility,bool canAct,ActorContext actor)
    {
        var independent=row.RequestedBy!=actor.UserId;var eligible=independent&&canAct;
        var current=row.PolicyRevision==eligibility.Dto.PolicyRevision&&evidence.Count==row.VerificationIds.Length&&eligibility.Dto.RequiredTestTypes.All(t=>evidence.Any(v=>v.Type==t))&&evidence.All(v=>eligibility.Current.Any(c=>c.Id==v.Id))&&row.EvidenceHash==EvidenceHash(artifact,evidence);
        var reasons=new List<string>();if(!independent)reasons.Add("independent_test_acceptor_required");if(!canAct)reasons.Add("test_accept_permission_or_write_scope_required");if(!current)reasons.Add("test_evidence_not_current");
        return View(row,artifact,evidence) with{CanAccept=eligible&&row.Status=="Requested"&&current,CanReject=eligible&&row.Status=="Requested",CanRevoke=eligible&&row.Status=="Accepted",ReasonCodes=reasons};
    }
    public async Task<TestAcceptanceDto> RequestAsync(Guid artifactId,IReadOnlyList<Guid> verificationIds,ActorContext actor,CancellationToken ct)
    {
        var artifact=await artifacts.GetAsync(artifactId,actor,ct);var scope=await scopes.EnvironmentAsync(artifact.SourceEnvironmentId,ct);
        if(verificationIds is null||verificationIds.Count is <1 or >3||verificationIds.Distinct().Count()!=verificationIds.Count)throw new ApiException(422,"invalid_test_evidence","请提供不重复的测试证据 ID。");
        var ids=verificationIds.Order().ToArray();
        return await commands.ExecuteAsync(actor,scope,"test_acceptance.request",async(_,token)=>{
            await auth.RequireAsync(actor,"release.test.record",new("environment",artifact.SourceEnvironmentId,scope),token);
            return await idempotency.ExecuteAsync(new(actor.UserId,scope,"test_acceptance.request",requestContext.IdempotencyKey),CanonicalJson.Serialize(new{artifactId,verificationIds=ids}),async inner=>{
                await LockAsync(artifact.SourceEnvironmentId,artifact.ProjectId,null,inner);
                var actual=await artifacts.GetAsync(artifactId,actor,inner);var evidence=await RequireEvidenceAsync(actual,ids,actor,inner);
                var row=new ReleaseTestAcceptance{OrganizationId=actual.OrganizationId,ProjectId=actual.ProjectId,ArtifactId=actual.Id,SourceEnvironmentId=actual.SourceEnvironmentId,VerificationIds=ids,EvidenceHash=EvidenceHash(actual,evidence.Rows),PolicyRevision=evidence.PolicyRevision,RequestedBy=actor.UserId};db.Add(row);Append(row,null,"Requested","test_acceptance_requested",actor.UserId);
                return View(row,actual,evidence.Rows);
            },token);
        },ct);
    }
    public async Task<TestAcceptanceDto> GetAsync(Guid id,ActorContext actor,CancellationToken ct)
    {
        var row=await db.Set<ReleaseTestAcceptance>().AsNoTracking().SingleOrDefaultAsync(a=>a.Id==id,ct)??throw ScopeResolver.Missing();var artifact=await artifacts.GetAsync(row.ArtifactId,actor,ct);
        if(artifact.ProjectId!=row.ProjectId||artifact.OrganizationId!=row.OrganizationId||artifact.SourceEnvironmentId!=row.SourceEnvironmentId)throw ScopeResolver.Missing();
        var evidence=await db.Set<ReleaseVerification>().AsNoTracking().Where(v=>row.VerificationIds.Contains(v.Id)&&v.ArtifactId==row.ArtifactId&&v.Phase=="SourceTest").ToArrayAsync(ct);
        var eligible=await EligibilityFactsAsync(artifact,actor,ct);var scope=await scopes.EnvironmentAsync(artifact.SourceEnvironmentId,ct);var canAct=await auth.CanAsync(actor,"release.test.accept",new("environment",artifact.SourceEnvironmentId,scope),ct);
        return AuthorityView(row,artifact,evidence,eligible,canAct,actor);
    }
    public async Task<IReadOnlyList<TestAcceptanceDto>> ListAsync(Guid artifactId,ActorContext actor,CancellationToken ct)
    {
        var artifact=await artifacts.GetAsync(artifactId,actor,ct);
        var rows=await db.Set<ReleaseTestAcceptance>().AsNoTracking().Where(a=>a.ArtifactId==artifactId).OrderByDescending(a=>a.CreatedAt).ThenBy(a=>a.Id).Take(100).ToArrayAsync(ct);
        var ids=rows.SelectMany(a=>a.VerificationIds).Distinct().ToArray();
        var evidence=await db.Set<ReleaseVerification>().AsNoTracking().Where(v=>ids.Contains(v.Id)&&v.ArtifactId==artifactId&&v.Phase=="SourceTest").ToArrayAsync(ct);
        var eligible=await EligibilityFactsAsync(artifact,actor,ct);var scope=await scopes.EnvironmentAsync(artifact.SourceEnvironmentId,ct);var canAct=await auth.CanAsync(actor,"release.test.accept",new("environment",artifact.SourceEnvironmentId,scope),ct);
        return rows.Select(row=>AuthorityView(row,artifact,evidence.Where(v=>row.VerificationIds.Contains(v.Id)).ToArray(),eligible,canAct,actor)).ToArray();
    }
    public async Task<TestAcceptanceDto> ActAsync(Guid acceptanceId,string action,string comment,string? etag,ActorContext actor,CancellationToken ct)
    {
        var initial=await GetAsync(acceptanceId,actor,ct);var scope=await scopes.EnvironmentAsync(initial.SourceEnvironmentId,ct);
        if(action is not("accept" or "reject" or "revoke")||(comment?.Length??0)>4000)throw new ApiException(422,"invalid_test_action","验收操作或说明不合法。");
        return await commands.ExecuteAsync(actor,scope,"test_acceptance."+action,async(_,token)=>{
            await auth.RequireAsync(actor,"release.test.accept",new("environment",initial.SourceEnvironmentId,scope),token);
            if(initial.RequestedBy==actor.UserId)throw new ApiException(403,"independent_test_acceptor_required","测试验收人与申请人必须不同，平台管理员同样适用。");
            return await idempotency.ExecuteAsync(new(actor.UserId,scope,"test_acceptance."+action,requestContext.IdempotencyKey),CanonicalJson.Serialize(new{acceptanceId,action,comment=comment??"",etag}),async inner=>{
                await LockAsync(initial.SourceEnvironmentId,scope.ProjectId!.Value,acceptanceId,inner);
                var row=await db.Set<ReleaseTestAcceptance>().SingleAsync(a=>a.Id==acceptanceId,inner);RevisionTag.Require(etag,row.Revision);
                if(action=="revoke"?row.Status!="Accepted":row.Status!="Requested")throw new ApiException(409,"test_acceptance_state","当前验收状态不允许此操作。");
                var artifact=await artifacts.GetAsync(row.ArtifactId,actor,inner);
                ReleaseVerification[] evidence;
                if(action=="accept"){
                    var actual=await RequireEvidenceAsync(artifact,row.VerificationIds,actor,inner);evidence=actual.Rows;
                    if(row.PolicyRevision!=actual.PolicyRevision||row.EvidenceHash!=EvidenceHash(artifact,evidence))throw Stale();
                }else evidence=await db.Set<ReleaseVerification>().AsNoTracking().Where(v=>row.VerificationIds.Contains(v.Id)&&v.ArtifactId==row.ArtifactId&&v.Phase=="SourceTest").ToArrayAsync(inner);
                var before=row.Status;row.Status=action switch{"accept"=>"Accepted","reject"=>"Rejected",_=>"Revoked"};row.Revision++;row.ActedBy=actor.UserId;row.ActedAt=DateTimeOffset.UtcNow;row.Comment=comment??"";
                Append(row,before,row.Status,"test_acceptance_"+row.Status.ToLowerInvariant(),actor.UserId);
                if(action=="revoke")await InvalidatePendingAsync(row,actor,inner);
                return View(row,artifact,evidence);
            },token);
        },ct);
    }
    public async Task<ReleaseTestAcceptance> RequireAcceptedCurrentAsync(Guid acceptanceId,Guid artifactId,ActorContext actor,CancellationToken ct)
    {
        var row=await db.Set<ReleaseTestAcceptance>().AsNoTracking().SingleOrDefaultAsync(a=>a.Id==acceptanceId&&a.ArtifactId==artifactId,ct)??throw ScopeResolver.Missing();
        if(row.Status!="Accepted"||row.ActedBy is not Guid acceptedBy||acceptedBy==row.RequestedBy)throw Stale();
        var artifact=await artifacts.GetAsync(artifactId,actor,ct);var evidence=await RequireEvidenceAsync(artifact,row.VerificationIds,actor,ct);
        if(row.PolicyRevision!=evidence.PolicyRevision||row.EvidenceHash!=EvidenceHash(artifact,evidence.Rows))throw Stale();
        var acceptor=new ActorContext(acceptedBy,actor.TraceId);await artifacts.GetAsync(artifactId,acceptor,ct);var scope=await scopes.EnvironmentAsync(row.SourceEnvironmentId,ct);
        await auth.RequireAsync(acceptor,"release.test.accept",new("environment",row.SourceEnvironmentId,scope),ct);return row;
    }
    private async Task<(ReleaseVerification[] Rows,long PolicyRevision)> RequireEvidenceAsync(ReleaseArtifactDto artifact,IReadOnlyList<Guid> ids,ActorContext actor,CancellationToken ct)
    {
        var facts=await db.Set<ReleaseVerification>().AsNoTracking().Where(v=>ids.Contains(v.Id)&&v.ArtifactId==artifact.Id&&v.Phase=="SourceTest"&&v.EnvironmentId==artifact.SourceEnvironmentId).ToArrayAsync(ct);
        if(facts.Length!=ids.Count)throw ScopeResolver.Missing();
        var policy=await db.Set<ProjectDeliveryPolicy>().AsNoTracking().SingleOrDefaultAsync(p=>p.ProjectId==artifact.ProjectId,ct);var required=policy?.RequiredTestTypes??defaultTypes;var revision=policy?.Revision??0;
        if(policy is not null&&policy.SourceEnvironmentId!=artifact.SourceEnvironmentId)throw Stale();
        if(facts.Select(v=>v.Type).Distinct().Count()!=facts.Length||required.Except(facts.Select(v=>v.Type)).Any())throw new ApiException(422,"test_evidence_incomplete","必需测试类型尚未齐全。");
        var source=await artifacts.RequireSourceAsync(artifact.SourceReleaseId,actor,ct);var now=DateTimeOffset.UtcNow;
        foreach(var fact in facts)
        {
            var latest=await db.Set<ReleaseVerification>().AsNoTracking().Where(v=>v.ArtifactId==artifact.Id&&v.Phase=="SourceTest"&&v.Type==fact.Type).OrderByDescending(v=>v.CreatedAt).ThenByDescending(v=>v.Id).Select(v=>v.Id).FirstAsync(ct);
            if(fact.Result!="Passed"||!fact.IsManual||fact.ExpiresAt<=now||fact.PolicyRevision!=revision||fact.ReleaseId!=artifact.SourceReleaseId||fact.ConfigVersion!=source.Deployment.Release.ToConfigVersion||fact.DeploymentSequence!=source.Deployment.Release.DeploymentSequence||fact.SnapshotHash!=artifact.SourceSnapshotHash||fact.AccessAddressRevision!=source.Deployment.Environment.AccessAddressRevision||latest!=fact.Id)throw Stale();
        }
        return(facts,revision);
    }
    internal async Task LockAsync(Guid sourceEnvironmentId,Guid projectId,Guid? acceptanceId,CancellationToken ct)
    {
        // Caller owns the common governance transaction lock. Include historic target scopes
        // so a changed connection cannot leave an older queued promotion outside revocation.
        var targets=await db.Set<ProjectDeliveryPolicy>().Where(p=>p.ProjectId==projectId).Select(p=>p.TargetEnvironmentId).ToListAsync(ct);
        if(acceptanceId is Guid id)targets.AddRange(await db.Set<ReleasePromotion>().Where(p=>p.AcceptanceId==id).Select(p=>p.TargetEnvironmentId).ToArrayAsync(ct));
        foreach(var environment in targets.Append(sourceEnvironmentId).Distinct().Order())await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM environments WHERE id={environment} FOR UPDATE",ct);
    }
    private async Task InvalidatePendingAsync(ReleaseTestAcceptance acceptance,ActorContext actor,CancellationToken ct)
    {
        var pending=await db.Set<ReleasePromotion>().Where(p=>p.AcceptanceId==acceptance.Id&&(p.Status=="Draft"||p.Status=="WaitingApproval"||p.Status=="Ready"||p.Status=="Deploying")).ToArrayAsync(ct);
        foreach(var promotion in pending){ReleaseRecord? release=null;if(promotion.TargetReleaseId is Guid id)release=await db.Set<ReleaseRecord>().SingleAsync(r=>r.Id==id,ct);
            if(release is not null&&release.Status is not("Draft" or "WaitingApproval" or "Ready" or "Building"))continue;
            var before=promotion.Status;promotion.Status="Invalidated";promotion.Revision++;promotion.CompletedAt=DateTimeOffset.UtcNow;
            if(release is not null){release.Status="Cancelled";release.CompletedAt=DateTimeOffset.UtcNow;release.FailureCode="test_acceptance_revoked";}
            db.Add(new ReleasePromotionEvent{OrganizationId=promotion.OrganizationId,ProjectId=promotion.ProjectId,PromotionId=promotion.Id,EnvironmentId=promotion.TargetEnvironmentId,Phase="PreExecution",FromStatus=before,ToStatus="Invalidated",ReasonCode="test_acceptance_revoked",ActorId=actor.UserId,ReleaseId=release?.Id});
        }
    }
    private void Append(ReleaseTestAcceptance row,string? before,string status,string reason,Guid actor)=>db.Add(new ReleasePromotionEvent{OrganizationId=row.OrganizationId,ProjectId=row.ProjectId,AcceptanceId=row.Id,EnvironmentId=row.SourceEnvironmentId,Phase="TestAcceptance",FromStatus=before,ToStatus=status,ReasonCode=reason,ActorId=actor});
    private static string EvidenceHash(ReleaseArtifactDto artifact,IReadOnlyList<ReleaseVerification> facts)=>Convert.ToHexStringLower(SHA256.HashData(CanonicalJson.Serialize(new{artifactHash=artifact.ArtifactHash,evidence=facts.OrderBy(v=>v.Id).Select(v=>new{v.Id,v.ArtifactId,v.ReleaseId,v.EnvironmentId,v.ConfigVersion,v.DeploymentSequence,v.SnapshotHash,v.AccessAddressRevision,v.AccessContextJson,v.PolicyRevision,v.Type,v.Result,v.IsManual,v.ReportId,v.ReportHash,v.StartedAt,v.FinishedAt,v.ExpiresAt,v.CreatedBy})})));
    private static TestAcceptanceDto View(ReleaseTestAcceptance row,ReleaseArtifactDto artifact,IReadOnlyList<ReleaseVerification> evidence)=>new(row.Id,row.ArtifactId,row.SourceEnvironmentId,artifact.ArtifactHash,row.VerificationIds,row.EvidenceHash,row.PolicyRevision,row.Status,row.Revision,row.RequestedBy,row.ActedBy,row.Comment,row.CreatedAt,row.ActedAt,evidence.Count==0?null:evidence.Min(v=>v.ExpiresAt));
}
