using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;

namespace WebApi.Infrastructure.Releases;

public sealed record RunningDeployment(EnvironmentRecord Environment, ReleaseRecord Release, HistoricalArtifact Snapshot);
public sealed class RunningDeploymentReader(WebApiDbContext db, HistoricalSnapshotService history)
{
    public async Task<RunningDeployment> ReadAsync(Guid environmentId, Guid? expectedReleaseId, CancellationToken ct)
    {
        var environment = await db.Set<EnvironmentRecord>().AsNoTracking().SingleOrDefaultAsync(e => e.Id == environmentId, ct) ?? throw ScopeResolver.Missing();
        var release = await db.Set<ReleaseRecord>().AsNoTracking().SingleOrDefaultAsync(r => r.EnvironmentId == environmentId && r.Status == "Succeeded" && r.ToConfigVersion == environment.DesiredConfigVersion && r.DeploymentSequence == environment.DeploymentSequence, ct);
        if (environment.Status != "Active" || environment.DesiredConfigVersion is null or 0 || release is null || expectedReleaseId is Guid expected && release.Id != expected) throw Unconfirmed();
        var artifact = await history.ReadAsync(environmentId, release.ToConfigVersion, ct);
        var nodes = await db.Set<GatewayNode>().AsNoTracking().Where(n => n.EnvironmentId == environmentId && n.Enabled).ToArrayAsync(ct);
        var threshold = DateTimeOffset.UtcNow.AddSeconds(-120);
        if (nodes.Length < 2 || nodes.Any(n => n.CurrentConfigVersion != release.ToConfigVersion || n.CurrentDeploymentSequence != release.DeploymentSequence || n.LastHeartbeatAt is null || n.LastHeartbeatAt < threshold)) throw Unconfirmed();
        var targets = await db.Set<ReleaseTarget>().AsNoTracking().Where(t => t.ReleaseId == release.Id).ToArrayAsync(ct);
        var acknowledgements = await db.Set<GatewayAck>().AsNoTracking().Where(a => a.ReleaseId == release.Id && a.Success && a.ConfigVersion == release.ToConfigVersion && a.DeploymentSequence == release.DeploymentSequence && a.PayloadHash == artifact.Hash).ToArrayAsync(ct);
        if (nodes.Any(n => !(targets.Any(t => t.NodeId == n.Id && t.InstanceId == n.InstanceId) && acknowledgements.Any(a => a.NodeId == n.Id && a.InstanceId == n.InstanceId)) && !EnvironmentApiAddressService.CurrentInstanceConfirmed(n, release, artifact.Hash))) throw Unconfirmed();
        return new(environment, release, artifact);
    }
    private static ApiException Unconfirmed() => new(409, "running_state_unconfirmed", "来源发布需成功且全部启用节点在线、快照和部署序列一致。");
}
