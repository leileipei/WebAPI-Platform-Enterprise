using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Security;
using WebApi.Domain.CredentialSecurity;
using WebApi.Infrastructure.Gateway;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;

using WebApi.Infrastructure.CredentialSecurity;

namespace WebApi.Integration.Tests.Support;

// Historical isolated test fixture only; this code is not part of the production assembly.
// Its output does not establish independent time or security history.
internal sealed class UntrustedGenesisLeaseMaterialBuilder(WebApiDbContext db, NodeIdentityService identities, SecurityLedgerReader reader, SecuritySigningSettings signing)
{
    private static ApiException Unavailable() => new(503, "security_ledger_unavailable", "安全账本或环境来源暂不可用。");

    public async Task<SecurityNodeMaterial> IssueAsync(NodeIdentity identity, Guid instanceId, CancellationToken ct = default)
    {
        var signer = signing.LoadSigner();
        try
        {
            var trusted = signing.LoadTrustedKeys();
            if (signing.KeyId is null || !trusted.SubjectPublicKeys.TryGetValue(signing.KeyId, out var publicKey) || publicKey != signer.ExportPublicKey())
                throw new ApiException(503, "security_signing_key_conflict", "签名公钥配置不一致。");
        }
        catch (Exception error) when (error is CryptographicException or ArgumentException or InvalidDataException)
        { throw new ApiException(503, "security_signing_unavailable", "安全签发暂不可用。"); }
        if (db.Database.CurrentTransaction is not null || db.ChangeTracker.HasChanges())
            throw new InvalidOperationException("Security lease issuance requires its own clean transaction.");
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_catalog.pg_advisory_xact_lock(8901202)", ct);
        var environment = await db.Set<EnvironmentRecord>().FromSqlInterpolated($"SELECT * FROM environments WHERE id={identity.EnvironmentId} FOR NO KEY UPDATE").SingleAsync(ct);
        var node = await identities.RevalidateAsync(identity, instanceId, ct);
        if (instanceId == Guid.Empty) throw NodeIdentityService.Denied();
        var project = await db.Set<Project>().AsNoTracking().SingleAsync(x => x.Id == environment.ProjectId, ct);
        var organization = await db.Set<Organization>().AsNoTracking().SingleAsync(x => x.Id == project.OrganizationId, ct);
        var origin = await db.Set<EnvironmentSecurityInitialization>().AsNoTracking().SingleOrDefaultAsync(x => x.EnvironmentId == environment.Id, ct);
        if (origin is null || origin.State != "FreshNonStrict" || origin.FreshProofId is null || origin.FreshProofId == Guid.Empty ||
            origin.ProjectId != project.Id || origin.OrganizationId != organization.Id || environment.RequireEnvironmentCredential ||
            environment.Status != "Active" || project.Status != "Active" || organization.Status != "Active") throw Unavailable();
        if (!SnapshotSchemaCapabilities.SupportsGovernanceAtFloor(node, "2.0"))
            throw new ApiException(409, "gateway_security_capability_required", "节点不具备所需的快照及安全能力。");

        var now = await SecurityDatabaseClock.ObserveAsync(db,ct);
        if (await SecurityLeaseExpiryRecorder.HasLaterObservationAsync(db, now, ct)) throw Unavailable();
        var state = await db.Set<EnvironmentSecurityState>().SingleOrDefaultAsync(x => x.EnvironmentId == environment.Id, ct);
        if (state is null)
        {
            state = new EnvironmentSecurityState { EnvironmentId = environment.Id, ProjectId = project.Id, OrganizationId = organization.Id,
                InitialRequireEnvironmentCredentials = false, RequireEnvironmentCredentials = false, UpdatedAt = now };
            db.Add(state);
            await db.SaveChangesAsync(ct);
        }
        SecurityLedgerMaterial material;
        try { material = await reader.ReadCompleteLockedAsync(environment.Id, ct); }
        catch (InvalidOperationException) { throw Unavailable(); }
        if (material.Epoch != 0 || material.Events.Count != 0 || material.Revocations.Count != 0 ||
            material.RequireEnvironmentCredentials || material.MinimumSnapshotSchema != "2.0" ||
            await db.Set<GatewaySecurityLease>().AnyAsync(x => x.EnvironmentId == environment.Id &&
                (x.SecurityGeneration != material.SecurityGeneration || x.Epoch > material.Epoch), ct)) throw Unavailable();

        var until = now.AddSeconds(state.LeaseSeconds);
        var checkpointPayload = new SecurityCheckpointPayload(SecuritySignedEncoding.Protocol, environment.Id, material.SecurityGeneration,
            0, material.HeadHash, [], false, "2.0", now, until, signing.KeyId!);
        var checkpointHash = SecuritySignedEncoding.CheckpointHash(checkpointPayload);
        var leaseId = Guid.NewGuid();
        var leasePayload = new SecurityNodeLeasePayload(leaseId, SecuritySignedEncoding.Protocol, environment.Id, material.SecurityGeneration,
            0, checkpointHash, node.Id, instanceId, SecuritySignedEncoding.Audience, now, until, signing.KeyId!,
            state.LeaseSeconds, state.MaximumClockSkewSeconds, true, []);
        byte[] checkpoint; byte[] signedLease;
        try { checkpoint = signer.SignCheckpoint(checkpointPayload); signedLease = signer.SignNodeLease(leasePayload); }
        catch (Exception error) when (error is CryptographicException or ArgumentException or InvalidDataException)
        { throw new ApiException(503, "security_signing_unavailable", "安全签发暂不可用。"); }
        var beforeWrite = await SecurityDatabaseClock.ObserveAsync(db,ct);
        if (beforeWrite < now || beforeWrite >= until || await SecurityLeaseExpiryRecorder.HasLaterObservationAsync(db, beforeWrite, ct)) throw Unavailable();

        var lease = await db.Set<GatewaySecurityLease>().SingleOrDefaultAsync(x => x.NodeId == node.Id && x.InstanceId == instanceId, ct);
        if (lease is null)
        {
            lease = new GatewaySecurityLease { NodeId = node.Id, InstanceId = instanceId, EnvironmentId = environment.Id,
                ProjectId = project.Id, OrganizationId = organization.Id };
            db.Add(lease);
        }
        else
        {
            // Different signed leases for one node instance must have a strict
            // order. A tied or regressed database clock cannot supply one.
            if (lease.EnvironmentId != environment.Id || lease.SecurityGeneration != material.SecurityGeneration || lease.IssuedAt >= now) throw Unavailable();
            lease.Revision = checked(lease.Revision + 1);
        }
        lease.SecurityGeneration = material.SecurityGeneration; lease.Epoch = 0; lease.CheckpointHash = checkpointHash;
        lease.LeaseId = leaseId; lease.IssuedAt = now; lease.FreshUntil = until;
        lease.LastIssuedLeaseUntil = lease.LastIssuedLeaseUntil > until ? lease.LastIssuedLeaseUntil : until;
        lease.SigningKeyId = signing.KeyId!; lease.LeaseSeconds = state.LeaseSeconds;
        db.Add(SecurityLeaseIssuanceEvidence.Create(leasePayload, organization.Id, project.Id));
        await db.SaveChangesAsync(ct);
        var beforeCommit = await SecurityDatabaseClock.ObserveAsync(db,ct);
        if (beforeCommit < beforeWrite || beforeCommit >= until || await SecurityLeaseExpiryRecorder.HasLaterObservationAsync(db, beforeCommit, ct)) throw Unavailable();
        await transaction.CommitAsync(ct);
        return new(checkpoint, signedLease);
    }
}
