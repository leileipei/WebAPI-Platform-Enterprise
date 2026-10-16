using System.Data;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Applications;
using WebApi.Contracts.Common;
using WebApi.Contracts.Runtime;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Releases;
using WebApi.Infrastructure.Security;
namespace WebApi.Infrastructure.Applications;

public sealed class CredentialRolloutQuery(WebApiDbContext db,AuthorizationService auth,ScopeResolver scopes,RunningDeploymentReader running,HistoricalSnapshotService history,TimeProvider clock)
{
    public async Task<CredentialRolloutDto> ReadAsync(Guid applicationId,Guid environmentId,Guid releaseId,Guid newCredentialId,Guid oldCredentialId,string stage,ActorContext actor,CancellationToken ct=default)
    {
        await using var transaction=await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead,ct);
        var appScope=await scopes.ApplicationAsync(applicationId,ct);await auth.RequireAsync(actor,"app.read",new("application",applicationId,appScope),ct);
        var envScope=await scopes.EnvironmentAsync(environmentId,ct);await auth.RequireAsync(actor,"release.read",new("environment",environmentId,envScope),ct);
        if(appScope.OrganizationId!=envScope.OrganizationId||appScope.ProjectId is Guid project&&project!=envScope.ProjectId)throw new ApiException(403,"credential_rollout_scope_denied","应用与发布环境范围不一致。");
        if(releaseId==Guid.Empty||newCredentialId==Guid.Empty||oldCredentialId==Guid.Empty||newCredentialId==oldCredentialId||stage is not ("Overlap" or "Revoked"))throw new ApiException(422,"invalid_credential_rollout","轮换凭证或阶段不合法。");
        var current=!(stage=="Overlap"&&await db.Set<ApplicationCredential>().AnyAsync(x=>x.Id==oldCredentialId&&x.ApplicationId==applicationId&&x.Status=="Revoked",ct));
        RunningDeployment deployment;Guid[] nodes;var now=clock.GetUtcNow();var evaluatedAt=now;
        if(current)
        {
            deployment=await running.ReadAsync(environmentId,releaseId,ct);
            nodes=await db.Set<GatewayNode>().AsNoTracking().Where(x=>x.EnvironmentId==environmentId&&x.Enabled).OrderBy(x=>x.Id).Select(x=>x.Id).ToArrayAsync(ct);
        }
        else
        {
            // Navigation may return after the second publication. Only a revoked working old key can
            // restore the immutable FIRST-stage proof; it is explicitly historical, never current.
            var release=await db.Set<ReleaseRecord>().AsNoTracking().SingleOrDefaultAsync(x=>x.Id==releaseId&&x.EnvironmentId==environmentId&&x.Status=="Succeeded"&&x.ToConfigVersion>0&&x.DeploymentSequence>0,ct)??throw Unconfirmed();
            var artifact=await history.ReadAsync(environmentId,release.ToConfigVersion,ct);
            var targets=await db.Set<ReleaseTarget>().AsNoTracking().Where(x=>x.ReleaseId==release.Id).ToArrayAsync(ct);
            var acknowledgements=await db.Set<GatewayAck>().AsNoTracking().Where(x=>x.ReleaseId==release.Id&&x.Success&&x.ConfigVersion==release.ToConfigVersion&&x.DeploymentSequence==release.DeploymentSequence&&x.PayloadHash==artifact.Hash).ToArrayAsync(ct);
            if(targets.Length<2||targets.Any(x=>!acknowledgements.Any(a=>a.NodeId==x.NodeId&&a.InstanceId==x.InstanceId)))throw Unconfirmed();
            nodes=targets.Select(x=>x.NodeId).Distinct().Order().ToArray();if(nodes.Length<2)throw Unconfirmed();evaluatedAt=acknowledgements.Max(x=>x.AppliedAt);
            deployment=new(await db.Set<EnvironmentRecord>().AsNoTracking().SingleAsync(x=>x.Id==environmentId,ct),release,artifact);
        }
        var app=deployment.Snapshot.Snapshot.Applications.SingleOrDefault(x=>x.Id==applicationId);
        static bool Eligible(RuntimeCredential? key,DateTimeOffset time)=>key is not null&&key.Status=="Active"&&key.ValidFrom<=time&&key.ExpiresAt>time;
        var old=app?.Credentials.SingleOrDefault(x=>x.Id==oldCredentialId);var next=app?.Credentials.SingleOrDefault(x=>x.Id==newCredentialId);
        if(app is null||app.Status!="Active"||!Eligible(next,evaluatedAt)||stage=="Overlap"&&!Eligible(old,evaluatedAt)||stage=="Revoked"&&Eligible(old,evaluatedAt))throw new ApiException(409,"credential_rollout_unconfirmed","当前运行快照尚未证明此轮换阶段的凭证状态。");
        var result=new CredentialRolloutDto(deployment.Release.ToConfigVersion.ToString(CultureInfo.InvariantCulture),deployment.Release.DeploymentSequence!.Value.ToString(CultureInfo.InvariantCulture),deployment.Snapshot.Hash,nodes,now,current);
        await transaction.CommitAsync(ct);return result;
    }
    private static ApiException Unconfirmed()=>new(409,"credential_rollout_unconfirmed","历史发布尚未具备全部目标实例的匹配确认。");
}
