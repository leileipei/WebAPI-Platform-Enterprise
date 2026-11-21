using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Domain.CredentialSecurity;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;

namespace WebApi.Infrastructure.CredentialSecurity;

public interface ICredentialRetiredTargetWitness
{
    // Authenticated deployment evidence only; never perform I/O under the writer fence.
    // Complete means every possible Allow issuance/reservation, across generations and
    // including lost replies. IssuanceClosed must hold for the entire validity interval.
    // IngressIsolated also requires independently verified closure of direct/bypass ingress.
    GovernanceRetiredTargetCoverage? GetVerifiedCoverage(GovernanceRetiredTargetScope scope);
}

internal static class CredentialRetiredTargetCoverage
{
    internal static async Task<bool> CoversKnownLeasesAsync(WebApiDbContext db, GovernanceRetiredTargetScope scope,
        GovernanceRetiredTargetCoverage proof, DateTimeOffset? observedTargetHorizon, DateTimeOffset now, CancellationToken ct)
    {
        // Deliberately do not filter generation or environment: a conflicting local
        // identity scope must veto coverage, and old-generation leases cannot disappear.
        var issued = await db.Set<GatewaySecurityLeaseIssuance>().AsNoTracking()
            .Where(row => row.NodeId == scope.NodeId && row.InstanceId == scope.InstanceId).ToArrayAsync(ct);
        var tracked = await db.Set<GatewaySecurityLease>().AsNoTracking()
            .Where(row => row.NodeId == scope.NodeId && row.InstanceId == scope.InstanceId).ToArrayAsync(ct);
        return CoversKnownLeases(scope, proof, issued, tracked, observedTargetHorizon, now);
    }

    internal static bool CoversKnownLeases(GovernanceRetiredTargetScope scope, GovernanceRetiredTargetCoverage proof,
        IReadOnlyList<GatewaySecurityLeaseIssuance> issued, IReadOnlyList<GatewaySecurityLease> tracked,
        DateTimeOffset? observedTargetHorizon, DateTimeOffset now)
    {
        if (!GovernanceRetiredTargetProtocol.Valid(proof) || proof.Scope != scope || !proof.Complete ||
            proof.HasPendingWrite || !proof.IssuanceClosed || proof.ValidUntil <= now ||
            proof.Lower - now > GovernanceRetiredTargetProtocol.MaximumClockSkew ||
            proof.IngressIsolated && now - proof.Upper > GovernanceRetiredTargetProtocol.MaximumObservationAge)
            return false;

        var reservations = proof.Reservations.ToDictionary(row => row.LeaseId);
        foreach (var row in issued)
        {
            var expected = new GovernanceLeaseReservation(row.LeaseId, row.OrganizationId, row.ProjectId, row.EnvironmentId,
                row.NodeId, row.InstanceId, row.SecurityGeneration, row.Epoch, row.SigningKeyId, row.CheckpointHash,
                row.PayloadHash, row.IssuedAt, row.FreshUntil);
            if (!reservations.TryGetValue(row.LeaseId, out var independent) || independent != expected) return false;
        }

        foreach (var row in tracked)
        {
            if (row.LastIssuedLeaseUntil < row.FreshUntil || !reservations.TryGetValue(row.LeaseId, out var independent) ||
                independent.NodeId != row.NodeId || independent.InstanceId != row.InstanceId ||
                independent.OrganizationId != row.OrganizationId || independent.ProjectId != row.ProjectId ||
                independent.EnvironmentId != row.EnvironmentId || independent.SecurityGeneration != row.SecurityGeneration ||
                independent.Epoch != row.Epoch || independent.SigningKeyId != row.SigningKeyId ||
                independent.CheckpointHash != row.CheckpointHash || independent.IssuedAt != row.IssuedAt ||
                independent.FreshUntil != row.FreshUntil) return false;
        }

        var completeUpper = proof.Reservations.Select(row => (DateTimeOffset?)row.FreshUntil).Max();
        var knownUpper = tracked.Select(row => (DateTimeOffset?)row.LastIssuedLeaseUntil)
            .Append(scope.LastLeaseUntil).Append(observedTargetHorizon).Max();
        if (knownUpper is DateTimeOffset known && (completeUpper is not DateTimeOffset complete || complete < known)) return false;

        // Expiry uses the independent lower time bound, never the DB wall clock.
        // The two-second safety allowance covers the signed gateway clock skew.
        return proof.IngressIsolated || completeUpper is null ||
            proof.Lower - completeUpper.Value >= GovernanceRetiredTargetProtocol.MaximumClockSkew;
    }

    internal static string InventoryHash(GovernanceRetiredTargetCoverage proof) =>
        GovernanceDeploymentEvidenceProtocol.ScopeHash("retired-target-lease-inventory", proof.Reservations);
}
