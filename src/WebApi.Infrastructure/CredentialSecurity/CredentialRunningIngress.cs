using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Runtime;
using WebApi.Domain.CredentialSecurity;
using WebApi.Infrastructure.Gateway;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Releases;

namespace WebApi.Infrastructure.CredentialSecurity;

public sealed record CredentialIngressInstance(Guid NodeId, Guid InstanceId, string NodeIdentityHash);

public sealed record CredentialRunningIngressScope(Guid OrganizationId, Guid ProjectId, Guid EnvironmentId, long AccessAddressRevision,
    string IngressAddressHash, Guid SecurityGeneration, long Epoch, string HeadHash, Guid ReleaseId, string SnapshotHash,
    long DeploymentSequence, long ConfigVersion, IReadOnlyList<CredentialIngressInstance> Instances);

/// <summary>Independently verifies the current cohort and isolation of all bypass and older process entrances.</summary>
public interface ICredentialRunningIngressWitness
{
    // The proof is obtained outside the writer fence. The caller rechecks the exact scope and expiry under the fence.
    DateTimeOffset? VerifiedUntil(CredentialRunningIngressScope scope, DateTimeOffset now);
}

internal static class CredentialRunningIngress
{
    internal static async Task<(CredentialRunningIngressScope Scope, RuntimeSnapshot Snapshot)> ReadAsync(WebApiDbContext db,
        Guid environmentId, SecurityLedgerMaterial ledger, DateTimeOffset now, CancellationToken ct)
    {
        var deployment = await new RunningDeploymentReader(db, new HistoricalSnapshotService(db, new ScopeResolver(db)))
            .ReadAsync(environmentId, null, ct);
        var env = deployment.Environment;
        var published = deployment.Snapshot.Snapshot;
        var nodes = await db.Set<GatewayNode>().AsNoTracking().Where(x => x.EnvironmentId == env.Id && x.Enabled)
            .OrderBy(x => x.Id).ToArrayAsync(ct);
        var grace = TimeSpan.FromSeconds(new PublishSettings().HeartbeatGraceSeconds);
        if (env.Id != ledger.EnvironmentId || env.ProjectId != ledger.ProjectId || published.SchemaVersion != "2.4" ||
            published.RequiredSecurityGeneration != ledger.SecurityGeneration ||
            published.RequiredSecurityEpoch is not long publishedEpoch || publishedEpoch > ledger.Epoch ||
            published.MinimumSnapshotSchema != ledger.MinimumSnapshotSchema || published.RequireEnvironmentCredentials != ledger.RequireEnvironmentCredentials ||
            env.DesiredConfigVersion is not > 0 || env.DeploymentSequence <= 0 || env.AccessAddressRevision <= 0 || nodes.Length < 2 ||
            nodes.Any(n => n.Status != "Ready" || n.CurrentConfigVersion != env.DesiredConfigVersion ||
                n.CurrentDeploymentSequence != env.DeploymentSequence || !Guid.TryParse(n.InstanceId, out var instance) || instance == Guid.Empty ||
                !SecuritySignedEncoding.HexHash(n.IdentityHash) || n.LastHeartbeatAt is not DateTimeOffset heartbeat || heartbeat > now ||
                now - heartbeat > grace))
            throw new ApiException(409, "credential_revocation_coverage_unknown", "全部入口的治理安全状态尚未核验。");

        SnapshotSchemaCapabilities.RequireSupported(nodes, "2.4");

        var addressHash = Convert.ToHexStringLower(SHA256.HashData(SecuritySignedEncoding.Unsigned(
            "webapi.credential-governance.running-ingress-addresses.v1", new { env.GatewayPublicUrl, env.GatewayInternalUrl, env.BasePath })));
        var scope = new CredentialRunningIngressScope(ledger.OrganizationId, ledger.ProjectId, env.Id, env.AccessAddressRevision,
            addressHash, ledger.SecurityGeneration, ledger.Epoch, ledger.HeadHash, deployment.Release.Id, deployment.Snapshot.Hash,
            env.DeploymentSequence, env.DesiredConfigVersion.Value,
            nodes.Select(node => new CredentialIngressInstance(node.Id, Guid.Parse(node.InstanceId), node.IdentityHash)).ToArray());
        return (scope, published);
    }

    // Embedded hosts may supply their original verified full-coverage provider. Configured hosts use the
    // current ingress scope and separately prove the exact same instances' operation and lease facts locally.
    internal static DateTimeOffset? VerifiedUntil(ICredentialRevocationCoverage coverage, CredentialRevocationCoverageScope leaseScope,
        CredentialRunningIngressScope ingressScope, DateTimeOffset now) =>
        coverage is ICredentialRunningIngressWitness witness
            ? witness.VerifiedUntil(ingressScope, now)
            : coverage.IsVerified(leaseScope) ? DateTimeOffset.MaxValue : null;
}
