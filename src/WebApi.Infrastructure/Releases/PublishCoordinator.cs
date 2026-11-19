using WebApi.Infrastructure.Comparisons;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Releases;
using WebApi.Contracts.Runtime;
using WebApi.Contracts.Security;
using WebApi.Domain.Releases;
using WebApi.Domain.Runtime;
using WebApi.Infrastructure.Commands;
using WebApi.Infrastructure.CredentialSecurity;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Messaging;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Security;
using WebApi.Infrastructure.Gateway;
using WebApi.Infrastructure.Policies;
namespace WebApi.Infrastructure.Releases;
public sealed record PublishSettings(int MinimumNodes=2,int HeartbeatGraceSeconds=120,int AckTimeoutSeconds=120,
    IReadOnlySet<Guid>? GovernanceSnapshot24Environments=null)
{
    public int EffectiveMinimumNodes => Math.Max(2,MinimumNodes);
    public bool EnablesGovernanceSnapshot24(Guid environmentId) => GovernanceSnapshot24Environments?.Contains(environmentId) == true;
}
public sealed class PublishCoordinator(WebApiDbContext db,AuthorizationService auth,ScopeResolver scopes,AuditedCommandExecutor commands,IdempotentCommandExecutor idempotency,CommandRequestContext requestContext,ReleaseService releases,SnapshotCompiler compiler,PublishSettings settings,HistoricalSnapshotService history,VersionRiskReviewService reviews,ReleaseAccessContextService accessContexts,WebApi.Infrastructure.Delivery.PromotionExecutionService deliveryExecution,ReleaseCandidateBuilder candidates,GatewayPolicyDeploymentRules? deployment=null)
{
    private readonly GatewayPolicyDeploymentRules bodyLimits=deployment??new();
    private readonly GovernedRollbackCandidate governedRollback = new(history, candidates, compiler);
    private void ValidateBodyPolicies(RuntimeSnapshot snapshot)
    {foreach(var policy in snapshot.Policies.Where(p=>p.Type=="transform")) bodyLimits.Validate(policy.Type,policy.Config);}
    public async Task<ReleaseDto> StartAsync(Guid id,ActorContext actor,CancellationToken ct=default)
    {
        var initial=await db.Set<ReleaseRecord>().AsNoTracking().SingleOrDefaultAsync(r=>r.Id==id,ct)??throw ScopeResolver.Missing();var scope=await scopes.EnvironmentAsync(initial.EnvironmentId,ct);
        return await commands.ExecuteAsync(actor,scope,"release.publish",async(_,token)=>{
            await RequirePublishAsync(initial,actor,scope,token);await deliveryExecution.RequireRecoveryVisibilityAsync(initial,actor,token);
            return await idempotency.ExecuteAsync(new(actor.UserId,scope,"release.publish",requestContext.IdempotencyKey),CanonicalJson.Serialize(new {releaseId=id}),async inner=>{
                var r=await db.Set<ReleaseRecord>().SingleAsync(r=>r.Id==id,inner);await deliveryExecution.RequireExecutionAsync(r,actor,inner);ReleaseStateMachine.Require(r.Status,"Ready");
                await VerifyBaselineAsync(r,inner);if(await db.Set<ReleaseRecord>().AnyAsync(x=>x.EnvironmentId==r.EnvironmentId&&x.Id!=id&&(x.Status=="Building"||x.Status=="Publishing"),inner)) throw new ApiException(409,"environment_busy","该环境已有正在下发的发布。");
                var nodes = await TargetsAsync(db, settings, r.EnvironmentId, inner);
                await VerifyVersionsAsync(r, inner);
                var entry = await db.Set<EnvironmentRecord>().SingleAsync(environment => environment.Id == r.EnvironmentId, inner);
                SnapshotSchemaCapabilities.RequireSupported(nodes, await TargetSchemaAsync(r, entry, inner));
                await ValidateReviewsAsync(r, actor, scope, inner);
                await accessContexts.CaptureAsync(r, entry, inner);
                r.Status = "Building";
                r.PublishRequestedBy = actor.UserId;
                r.PublishTraceId = actor.TraceId;
                await deliveryExecution.RecordDeploymentStateAsync(r, inner);
                return await releases.DtoAsync(r,inner);
            },token);
        },ct);
    }
    private async Task ValidateReviewsAsync(ReleaseRecord r,ActorContext actor,ScopeRef scope,CancellationToken ct)
    {if(r.ReleaseType is "rollback" or "retry")return;var candidate=Candidate(r);
        await reviews.ValidateFrozenReferencesAsync(scope,candidate.VersionIds,candidate.RiskReviewReferences,actor,ct);}
    public async Task LockEnvironmentAsync(Guid envId,CancellationToken ct)=>await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM environments WHERE id={envId} FOR UPDATE",ct);
    private async Task VerifyBaselineAsync(ReleaseRecord r,CancellationToken ct)
    {var current=await db.Set<EnvironmentRecord>().AsNoTracking().Where(e=>e.Id==r.EnvironmentId).Select(e=>e.DesiredConfigVersion).SingleAsync(ct);if((current??0)!=r.BaselineConfigVersion) throw new ApiException(409,"stale_baseline","运行基线已变化，请重新建立候选并审批。");}
    private static FrozenReleaseCandidate Candidate(ReleaseRecord r)=>JsonSerializer.Deserialize<FrozenReleaseCandidate>(r.CandidateBytes!,CanonicalJson.Options)??throw new ApiException(422,"invalid_candidate","冻结候选不完整。");
    private async Task RequirePublishAsync(ReleaseRecord r,ActorContext actor,ScopeRef scope,CancellationToken ct)
    {
        await auth.RequireAsync(actor,"release.publish",new("environment",r.EnvironmentId,scope),ct);
        if(r.ReleaseType=="publish"&&r.PromotionId is Guid promotionId&&await db.Set<WebApi.Infrastructure.Persistence.Entities.ReleasePromotion>().AsNoTracking().AnyAsync(p=>p.Id==promotionId&&p.GateOrigin=="PipelineRunStage",ct))await auth.RequireAsync(actor,"pipeline.run",new("environment",r.EnvironmentId,scope),ct);
        if(r.ApprovalPolicy is null) return;
        var candidate = Candidate(r);
        if (candidate.GovernedRollback is not null)
        {
            await auth.RequireAsync(actor, "release.rollback", new("release", r.Id, scope), ct);
            await auth.RequireAsync(actor, "gateway.config.read", new("environment", r.EnvironmentId, scope), ct);
        }
        foreach(var version in candidate.Versions)
        {var actual=await scopes.ApiAsync(version.Api.Id,ct);if(actual.OrganizationId!=scope.OrganizationId||actual.ProjectId!=scope.ProjectId) throw new ApiException(409,"candidate_scope_changed","候选资源范围已变化。");await auth.RequireAsync(actor,"release.publish",new("api",version.Api.Id,actual with {EnvironmentId=r.EnvironmentId}),ct);}
    }
    private async Task VerifyVersionsAsync(ReleaseRecord r,CancellationToken ct)
    {foreach(var frozen in Candidate(r).Versions) {var v=await db.Set<ApiVersion>().AsNoTracking().SingleOrDefaultAsync(v=>v.Id==frozen.Version.Id,ct);if(v is null||v.ApiId!=frozen.Api.Id||v.Revision!=frozen.Version.Revision) throw new ApiException(409,"version_changed_since_approval","版本内容在审批后发生变化，请重新提交审批。");}}
    internal static async Task<GatewayNode[]> TargetsAsync(WebApiDbContext db,PublishSettings settings,Guid envId,CancellationToken ct)
    {
        var nodes=await db.Set<GatewayNode>().Where(n=>n.EnvironmentId==envId&&n.Enabled).OrderBy(n=>n.Id).ToArrayAsync(ct);var threshold=DateTimeOffset.UtcNow.AddSeconds(-settings.HeartbeatGraceSeconds);
        if(nodes.Length<settings.EffectiveMinimumNodes||nodes.Any(n=>n.IdentityHash.Length!=64||!Guid.TryParse(n.InstanceId,out _)||n.LastHeartbeatAt is null||n.LastHeartbeatAt<threshold)) throw new ApiException(409,"gateway_cohort_unavailable","至少需要两个有效注册节点，且全部已启用节点必须在线；不会缩减确认目标。");return nodes;
    }
    public async Task ValidateCohortAsync(Guid envId,CancellationToken ct=default)=>await TargetsAsync(db,settings,envId,ct);
    public async Task ValidateTargetSchemaAsync(Guid envId,long version,CancellationToken ct=default)
    {
        var nodes=await TargetsAsync(db,settings,envId,ct);
        var env=await db.Set<EnvironmentRecord>().AsNoTracking().SingleAsync(e=>e.Id==envId,ct);
        var artifact=await history.ReadAsync(envId,version,ct);
        await RequireHistoricalDeliveryAsync(env,artifact.Snapshot,ct);
        SnapshotSchemaCapabilities.RequireSupported(nodes,artifact.Snapshot.SchemaVersion);
    }
    private async Task<string> TargetSchemaAsync(ReleaseRecord release,EnvironmentRecord environment,CancellationToken ct)
    {
        var frozen=Candidate(release);
        if(release.ReleaseType is ("rollback" or "retry") && frozen.GovernedRollback is null)
        {
            var artifact=await history.ReadAsync(release.EnvironmentId,release.ToConfigVersion,ct);
            await RequireHistoricalDeliveryAsync(environment,artifact.Snapshot,ct);
            await ReleaseCandidateBuilder.RequireLegacyApplicationsAsync(db,frozen.Applications,ct);
            return artifact.Snapshot.SchemaVersion;
        }
        await RequireFrozenGovernanceAsync(release,frozen,environment,ct);
        if (frozen.Governance is null)
        {
            await ReleaseCandidateBuilder.RequireLegacyEnvironmentAsync(db,environment,ct);
            await ReleaseCandidateBuilder.RequireLegacyApplicationsAsync(db,frozen.Applications,ct);
        }
        var baseline=release.BaselineConfigVersion>0?(await history.ReadAsync(release.EnvironmentId,release.BaselineConfigVersion,ct)).Snapshot:new RuntimeSnapshot("2.0",release.EnvironmentId,0,DateTimeOffset.UtcNow,[],[],[],[]);
        var compiled=compiler.Compile(frozen,baseline,baseline.ConfigVersion+1,DateTimeOffset.UtcNow);using var document=JsonDocument.Parse(compiled.Payload);return document.RootElement.GetProperty("schemaVersion").GetString()!;
    }

    private async Task RequireFrozenGovernanceAsync(ReleaseRecord release,FrozenReleaseCandidate frozen,EnvironmentRecord env,CancellationToken ct)
    {
        if (frozen.Governance is null)
        {
            if (settings.EnablesGovernanceSnapshot24(env.Id) || frozen.GovernedRollback is not null)
                throw ReleaseCandidateBuilder.GovernanceRequired();
            return;
        }
        if (!settings.EnablesGovernanceSnapshot24(env.Id) || release.ApprovalPolicy is null)
            throw ReleaseCandidateBuilder.GovernanceRequired();

        if (frozen.GovernedRollback is not null)
        {
            await governedRollback.RequireCurrentAsync(release, env, frozen, ct);
            return;
        }
        // The execution guard already rebuilt the exact promotion mapping and checked
        // its reviewed candidate hash in this transaction. A general publish preview
        // would include working grants outside the explicitly approved mapping.
        if (release.ReleaseType == "publish" && release.PromotionId is not null) return;

        var current = await candidates.PreviewAsync(env.Id,new(release.BaselineConfigVersion,frozen.VersionIds),ct);
        var rebuilt = CanonicalJson.Serialize(current with { RiskReviewReferences = frozen.RiskReviewReferences });
        if (!rebuilt.AsSpan().SequenceEqual(release.CandidateBytes))
            throw new ApiException(409,"governance_snapshot_preconditions_changed","冻结的账本、网关目标或业务资源已变化，请重新预览和审批。");
    }

    private async Task RequireHistoricalDeliveryAsync(EnvironmentRecord environment,RuntimeSnapshot historical,CancellationToken ct,
        RuntimeSnapshot? baseline=null)
    {
        var governed = settings.EnablesGovernanceSnapshot24(environment.Id) || historical.RequiredSecurityGeneration is not null;
        if (!governed && baseline is null && environment.DesiredConfigVersion is long current && current != historical.ConfigVersion)
            baseline = (await history.ReadAsync(environment.Id,current,ct)).Snapshot;
        // Historical business state is still available for inspection. Governed delivery needs a new candidate using current authority.
        if (governed || baseline?.RequiredSecurityGeneration is not null)
            throw new ApiException(409,"governance_history_rebuild_required","治理环境恢复须使用当前授权重新冻结并审批 2.4 候选。");
        await ReleaseCandidateBuilder.RequireLegacyEnvironmentAsync(db,environment,ct);
        ValidateBodyPolicies(historical);
    }

    private async Task RequireLegacyPayloadAsync(EnvironmentRecord environment, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        await ReleaseCandidateBuilder.RequireLegacyEnvironmentAsync(db,environment,ct);
        var snapshot = SnapshotValidator.ParsePayload(payload.Span, environment.Id);
        if (snapshot.RequiredSecurityGeneration is not null) throw ReleaseCandidateBuilder.GovernanceRequired();
        var credentials = snapshot.Applications.SelectMany(app => app.Credentials).Select(key => key.Id).ToArray();
        if (await db.Set<ApplicationCredential>().AnyAsync(key => credentials.Contains(key.Id) &&
            (key.BindingMode != "LegacyGlobal" || key.EnvironmentId != null), ct))
            throw ReleaseCandidateBuilder.GovernanceRequired();
    }
    public async Task<bool> BuildNextAsync(CancellationToken ct=default)
    {
        await using var tx=await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_catalog.pg_advisory_xact_lock(8901202)",ct);
        var selected=await db.Set<ReleaseRecord>().AsNoTracking().Where(r=>r.Status=="Building").OrderBy(r=>r.CreatedAt).ThenBy(r=>r.Id).FirstOrDefaultAsync(ct);
        if(selected is null){await tx.CommitAsync(ct);return false;}
        await deliveryExecution.LockReleaseAsync(selected,ct);
        var r=await db.Set<ReleaseRecord>().FromSqlInterpolated($"SELECT * FROM release_records WHERE id={selected.Id} AND status='Building' FOR UPDATE SKIP LOCKED").SingleOrDefaultAsync(ct);
        if(r is null){await tx.CommitAsync(ct);return false;}
        var scope=await scopes.EnvironmentAsync(r.EnvironmentId,ct);
        try
        {
            if(r.PublishRequestedBy is not Guid publisher) throw new ApiException(409,"publisher_missing","发布身份缺失。");
            await deliveryExecution.RequireExecutionAsync(r,new(publisher,r.PublishTraceId??r.Id.ToString()),ct);await RequirePublishAsync(r,new(publisher,r.PublishTraceId??r.Id.ToString()),scope,ct);await VerifyBaselineAsync(r,ct);await VerifyVersionsAsync(r,ct);var nodes=await TargetsAsync(db,settings,r.EnvironmentId,ct);
            await ValidateReviewsAsync(r,new(publisher,r.PublishTraceId??r.Id.ToString()),scope,ct);
            var env=await db.Set<EnvironmentRecord>().SingleAsync(e=>e.Id==r.EnvironmentId,ct);var baseline=new RuntimeSnapshot("2.0",env.Id,0,DateTimeOffset.UtcNow,[],[],[],[]);
            if(r.BaselineConfigVersion>0) baseline=(await history.ReadAsync(env.Id,r.BaselineConfigVersion,ct)).Snapshot;
            var next=(await db.Set<GatewayConfigVersion>().Where(v=>v.EnvironmentId==env.Id).Select(v=>(long?)v.VersionNo).MaxAsync(ct)??0)+1;var frozen=Candidate(r);CompiledSnapshot compiled;
            var historicalDelivery = r.ReleaseType is ("rollback" or "retry") && frozen.GovernedRollback is null;
            RuntimeSnapshot? historical = null;
            if(historicalDelivery) {next=r.ToConfigVersion;var artifact=await history.ReadAsync(env.Id,next,ct);historical=artifact.Snapshot;compiled=new(artifact.Payload,artifact.Hash,artifact.Size);}
            else
            {
                await RequireFrozenGovernanceAsync(r,frozen,env,ct);
                compiled=compiler.Compile(frozen,baseline,next,DateTimeOffset.UtcNow);
            }
            var schema=await GlobalCredentialPublicationGuard.ValidateAsync(db,env,scope.OrganizationId,compiled.Payload,ct);
            if (historical is not null) await RequireHistoricalDeliveryAsync(env,historical,ct,baseline);
            if (schema != "2.4")
            {
                await RequireLegacyPayloadAsync(env,compiled.Payload,ct);
                await ReleaseCandidateBuilder.RequireLegacyApplicationsAsync(db,frozen.Applications,ct);
            }
            SnapshotSchemaCapabilities.RequireSupported(nodes,schema);
            if(!historicalDelivery) {var config=new GatewayConfigVersion {EnvironmentId=env.Id,VersionNo=next,Status="Publishing",CreatedBy=publisher,SnapshotHash=compiled.Hash};config.SnapshotKey=$"environment/{env.Id}/snapshot/{next}";db.Add(config);db.Add(new GatewayConfigSnapshot {ConfigVersionId=config.Id,PayloadBytes=compiled.Payload.ToArray(),Payload=Encoding.UTF8.GetString(compiled.Payload.Span),SizeBytes=compiled.Size});}
            env.DeploymentSequence=checked(env.DeploymentSequence+1);env.DesiredConfigVersion=next;r.ToConfigVersion=next;r.DeploymentSequence=env.DeploymentSequence;r.Status="Publishing";r.DeadlineAt=DateTimeOffset.UtcNow.AddSeconds(settings.AckTimeoutSeconds);
            if(!historicalDelivery)FrozenContractSource.Capture(db,r,frozen);
            foreach(var node in nodes) {db.Add(new ReleaseTarget {ReleaseId=r.Id,NodeId=node.Id,InstanceId=node.InstanceId});node.TargetConfigVersion=next;}
            foreach(var v in frozen.Versions) {var version=await db.Set<ApiVersion>().SingleAsync(x=>x.Id==v.Version.Id,ct);version.SealedAt??=DateTimeOffset.UtcNow;version.Status="Publishing";}
            var message=new OutboxMessage {EnvironmentId=env.Id,ReleaseId=r.Id,DeploymentSequence=env.DeploymentSequence,EventType="desired_config"};message.Payload=JsonSerializer.Serialize(new DeploymentEvent(message.Id,env.Id,env.DeploymentSequence,r.Id,next),CanonicalJson.Options);db.Add(message);
            Audit(r,scope,"release.snapshot.persisted",new {configVersion=next,deploymentSequence=env.DeploymentSequence,payloadHash=compiled.Hash,targetCount=nodes.Length});
        }
        catch(ApiException error) {r.Status="Failed";r.FailureCode=error.Code;r.CompletedAt=DateTimeOffset.UtcNow;Audit(r,scope,"release.build.failed",new {error=error.Code});}
        await deliveryExecution.RecordDeploymentStateAsync(r,ct);await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return true;
    }
    private void Audit(ReleaseRecord r,ScopeRef scope,string action,object after)=>db.Add(new AuditLog {OrganizationId=scope.OrganizationId,ProjectId=scope.ProjectId,EnvironmentId=r.EnvironmentId,UserId=r.PublishRequestedBy,Action=action,ResourceType="ReleaseRecord",ResourceId=r.Id.ToString(),TraceId=r.PublishTraceId,AfterJson=JsonSerializer.Serialize(after,CanonicalJson.Options)});
}
