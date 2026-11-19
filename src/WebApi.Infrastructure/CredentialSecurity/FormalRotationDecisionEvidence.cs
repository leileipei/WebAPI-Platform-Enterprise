using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Runtime;
using WebApi.Domain.Applications;
using WebApi.Domain.CredentialSecurity;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;

namespace WebApi.Infrastructure.CredentialSecurity;

public sealed record FormalRotationRequestProofScope(GovernanceJournalId Journal, GovernanceJournalPosition Confirmed,
    Guid RotationId, Guid ReplacementCredentialId, DateTimeOffset RecordedAt, DateTimeOffset HardDeadline,
    CredentialRevocationCoverageScope Deployment);

/// <summary>Supplies independently authenticated successful requests for the exact current cohort.</summary>
public interface IFormalRotationRequestProofWitness
{
    // Authenticate the gateway producer and node instance. Return at most one
    // completed proof per requested instance from bounded durable storage.
    // Return promptly and use a bounded asynchronous transport that honors
    // cancellation. The caller holds the shared write fence while observing.
    Task<IReadOnlyList<FormalRotationRequestProof>> ObserveAsync(FormalRotationRequestProofScope scope, CancellationToken ct);
}

internal static class FormalRotationDecisionEvidence
{
    internal static async Task<FormalRotationRequestProof[]> ObserveAsync(IFormalRotationRequestProofWitness witness,
        FormalRotationRequestProofScope scope, CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(BoundedWitnessHttpClient.RequestTimeout);
        IReadOnlyList<FormalRotationRequestProof> supplied;
        try { supplied = await witness.ObserveAsync(scope, budget.Token).WaitAsync(budget.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw PostgresFormalRotationPreparation.EvidenceUnavailable(); }
        if (supplied is null || supplied.Count > scope.Deployment.Nodes.Count || supplied.Any(x => x is null) ||
            supplied.Select(x => x.NodeId).Distinct().Count() != supplied.Count)
            throw PostgresFormalRotationPreparation.EvidenceUnavailable();
        return supplied.OrderBy(x => x.NodeId).ToArray();
    }

    internal static async Task RequireSuccessfulRequestsAsync(WebApiDbContext db, FormalRotationScheduleIntent schedule,
        CredentialRevocationCoverageScope coverage, RuntimeSnapshot snapshot, ApplicationCredential replacement,
        DateTimeOffset now, FormalRotationRequestProof[] proofs, CancellationToken ct)
    {
        var verified = await ReadVerifiedTargetsAsync(db, schedule, coverage, snapshot, replacement, now, proofs, ct);
        if (verified.Count != coverage.Nodes.Count) throw Incomplete();
    }

    internal static async Task<HashSet<(Guid NodeId, Guid InstanceId)>> ReadVerifiedTargetsAsync(WebApiDbContext db,
        FormalRotationScheduleIntent schedule, CredentialRevocationCoverageScope coverage, RuntimeSnapshot snapshot,
        ApplicationCredential replacement, DateTimeOffset now, FormalRotationRequestProof[] proofs, CancellationToken ct)
    {
        var application = snapshot.Applications.SingleOrDefault(x => x.Id == schedule.ApplicationId &&
            x.EnvironmentId == coverage.EnvironmentId && x.Status == "Active");
        var key = application?.Credentials.SingleOrDefault(x => x.Id == replacement.Id && x.Status == "Active" &&
            x.BindingMode == "EnvironmentBound" && x.EnvironmentId == coverage.EnvironmentId);
        if (application is null || key is null || replacement.Status != "Active" || key.AccessKey != replacement.AccessKey ||
            key.Hash != replacement.SecretHash || key.ValidFrom != schedule.Window.NewValidFrom ||
            key.ExpiresAt != schedule.Window.NewExpiresAt || replacement.ValidFrom != key.ValidFrom || replacement.ExpiresAt != key.ExpiresAt ||
            now < key.ValidFrom || now >= key.ExpiresAt || proofs.Length == 0) return [];

        var targets = coverage.Nodes.Select(x => (x.NodeId, x.InstanceId)).ToHashSet();
        var leaseIds = proofs.Select(proof => proof.LeaseId).Distinct().ToArray();
        var issued = await db.Set<GatewaySecurityLeaseIssuance>().AsNoTracking()
            .Where(x => leaseIds.Contains(x.LeaseId)).ToDictionaryAsync(x => x.LeaseId, ct);
        var verified = new HashSet<(Guid, Guid)>();
        foreach (var proof in proofs)
        {
            var route = snapshot.Routes.SingleOrDefault(x => x.Id == proof.RouteId);
            var target = (proof.NodeId, proof.InstanceId);
            if (!targets.Contains(target) ||
                proof.EnvironmentId != coverage.EnvironmentId || proof.CredentialId != replacement.Id ||
                proof.ApplicationId != application.Id || proof.ReleaseId != coverage.ReleaseId ||
                proof.ConfigVersion != coverage.ConfigVersion || proof.DeploymentSequence != coverage.DeploymentSequence ||
                proof.SnapshotHash != coverage.SnapshotHash || proof.SecurityGeneration != coverage.SecurityGeneration ||
                proof.SecurityEpoch != coverage.Epoch || proof.SecurityHeadHash != coverage.HeadHash ||
                proof.StatusCode is < 200 or >= 400 || proof.AdmittedAt.Offset != TimeSpan.Zero ||
                proof.CompletedAt.Offset != TimeSpan.Zero || proof.AdmittedAt < schedule.RecordedAt ||
                proof.AdmittedAt < key.ValidFrom || proof.CompletedAt < proof.AdmittedAt || proof.CompletedAt > now ||
                !issued.TryGetValue(proof.LeaseId, out var lease) || lease.NodeId != proof.NodeId ||
                lease.InstanceId != proof.InstanceId || lease.EnvironmentId != coverage.EnvironmentId ||
                lease.OrganizationId != coverage.OrganizationId || lease.ProjectId != coverage.ProjectId ||
                lease.SecurityGeneration != proof.SecurityGeneration || lease.Epoch != proof.SecurityEpoch ||
                lease.IssuedAt > proof.AdmittedAt || lease.FreshUntil <= proof.CompletedAt ||
                route is null || !route.RequireApiKey || !PermissionEvaluator.IsAllowed(application, route, proof.Method, proof.AdmittedAt))
                continue;
            verified.Add(target);
        }
        return verified;
    }

    internal static async Task<FormalRotationAbortAck[]?> ReadAbortAcksAsync(WebApiDbContext db,
        CredentialRevocationCoverageScope coverage, CredentialRevocation tombstone, DateTimeOffset now, CancellationToken ct)
    {
        var targets = await db.Set<SecurityOperationTarget>().AsNoTracking().Where(x =>
            x.EnvironmentId == coverage.EnvironmentId && x.OperationId == tombstone.OperationId)
            .OrderBy(x => x.NodeId).ThenBy(x => x.InstanceGuid).ToArrayAsync(ct);
        var cohort = coverage.Nodes.Select(node => (node.NodeId, node.InstanceId));
        // A changed cohort calls for independent evidence that the previous instances are isolated.
        if (targets.Any(x => !x.ProtocolSupportedAtFreeze || x.SupersededByInstanceGuid != null) ||
            !targets.Select(x => (x.NodeId, x.InstanceGuid)).SequenceEqual(cohort)) return null;

        var candidates = await (from ack in db.Set<SecurityAckVerification>().AsNoTracking()
                                join lease in db.Set<GatewaySecurityLeaseIssuance>().AsNoTracking() on ack.LeaseId equals lease.LeaseId
                                where ack.EnvironmentId == tombstone.EnvironmentId && ack.OperationId == tombstone.OperationId &&
                                    ack.SecurityGeneration == coverage.SecurityGeneration && ack.Epoch >= tombstone.Epoch && ack.Epoch <= coverage.Epoch &&
                                    lease.EnvironmentId == ack.EnvironmentId && lease.NodeId == ack.NodeId && lease.InstanceId == ack.InstanceGuid &&
                                    lease.OrganizationId == coverage.OrganizationId && lease.ProjectId == coverage.ProjectId &&
                                    lease.SecurityGeneration == ack.SecurityGeneration && lease.Epoch == ack.Epoch &&
                                    lease.CheckpointHash == ack.CheckpointHash && lease.IssuedAt <= ack.ReceivedAt &&
                                    ack.ReceivedAt >= tombstone.RevokedAt && ack.ReceivedAt < lease.FreshUntil && ack.ReceivedAt <= now
                                select new FormalRotationAbortAck(ack.NodeId, ack.InstanceGuid, ack.ChallengeNonce,
                                    ack.LeaseId, ack.Epoch, ack.CheckpointHash, ack.ReceivedAt))
            .ToArrayAsync(ct);
        var acks = candidates.ToDictionary(x => (x.NodeId, x.InstanceId));
        if (targets.Any(x => !acks.TryGetValue((x.NodeId, x.InstanceGuid), out var ack) || x.ChallengeNonce != ack.ChallengeNonce))
            return null;
        return targets.Select(x => acks[(x.NodeId, x.InstanceGuid)]).ToArray();
    }

    internal static ApiException Incomplete() => new(409, "formal_rotation_replacement_unverified",
        "当前发布的新钥尚缺少全部网关实例的成功请求证明。");
}
