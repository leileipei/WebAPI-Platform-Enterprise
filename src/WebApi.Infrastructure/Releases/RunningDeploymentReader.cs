using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Runtime;
using WebApi.Infrastructure.Gateway;
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
        var release = await CurrentRelease(environment).SingleOrDefaultAsync(ct) ?? throw Unconfirmed();
        if (expectedReleaseId.HasValue && expectedReleaseId != release.Id) throw Unconfirmed();
        return new(environment, release, await ConfirmSnapshotAsync(environment, release.Id, ct));
    }

    internal async Task<HistoricalArtifact> ReadSnapshotAsync(EnvironmentRecord environment, CancellationToken ct)
    {
        // Address and contract reads do not need the release's full frozen candidate.
        var releaseId = await CurrentRelease(environment).Select(r => (Guid?)r.Id).SingleOrDefaultAsync(ct) ?? throw Unconfirmed();
        return await ConfirmSnapshotAsync(environment, releaseId, ct);
    }

    private IQueryable<ReleaseRecord> CurrentRelease(EnvironmentRecord environment)
    {
        if (environment.Status != "Active" || environment.DesiredConfigVersion is null or 0) throw Unconfirmed();
        return db.Set<ReleaseRecord>().AsNoTracking().Where(r => r.EnvironmentId == environment.Id && r.Status == "Succeeded"
            && r.ToConfigVersion == environment.DesiredConfigVersion && r.DeploymentSequence == environment.DeploymentSequence);
    }

    private async Task<HistoricalArtifact> ConfirmSnapshotAsync(EnvironmentRecord environment, Guid releaseId, CancellationToken ct)
    {
        var version = environment.DesiredConfigVersion!.Value;
        var sequence = environment.DeploymentSequence;
        var nodes = await db.Set<GatewayNode>().AsNoTracking().Where(n => n.EnvironmentId == environment.Id && n.Enabled).ToArrayAsync(ct);
        var threshold = DateTimeOffset.UtcNow.AddSeconds(-120);
        if (nodes.Length < 2 || nodes.Any(n => n.Status != "Ready" || n.LastHeartbeatAt is null || n.LastHeartbeatAt < threshold
            || n.CurrentConfigVersion != version || n.CurrentDeploymentSequence != sequence)) throw Unconfirmed();

        var artifact = await history.ReadAsync(environment.Id, version, ct);
        var envelope = new SnapshotEnvelope(releaseId, sequence, version, artifact.Hash, artifact.Size);
        var confirmed = await (from target in db.Set<ReleaseTarget>()
            join ack in db.Set<GatewayAck>() on new { target.ReleaseId, target.NodeId, target.InstanceId } equals new { ack.ReleaseId, ack.NodeId, ack.InstanceId }
            where target.ReleaseId == releaseId && ack.Success && ack.ConfigVersion == version && ack.DeploymentSequence == sequence && ack.PayloadHash == artifact.Hash
            select new { target.NodeId, target.InstanceId }).ToDictionaryAsync(t => t.NodeId, t => t.InstanceId, ct);
        if (nodes.Any(n => !(confirmed.TryGetValue(n.Id, out var instance) && instance == n.InstanceId)
            && !RuntimeApplicationReceipt.Matches(n, envelope))) throw Unconfirmed();
        return artifact;
    }

    private static ApiException Unconfirmed() => new(409, "running_state_unconfirmed", "运行状态尚未确认：需要成功发布，且全部启用节点在线就绪并确认当前快照和部署序列。");
}
