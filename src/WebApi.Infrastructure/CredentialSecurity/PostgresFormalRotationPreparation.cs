using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Applications;
using WebApi.Contracts.Common;
using WebApi.Contracts.Runtime;
using WebApi.Contracts.Security;
using WebApi.Domain.CredentialSecurity;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Security;

namespace WebApi.Infrastructure.CredentialSecurity;

// This is a read-only preview. Accepting a formal schedule also requires an
// independently ordered deadline write and an issuer that enforces that deadline.
public sealed class PostgresFormalRotationPreparation(string connectionString, GovernanceJournalId journal,
    ICredentialRevocationCoverage ingress, IFormalRotationLeaseHorizonWitness? horizon)
{
    public Task<FormalRotationPreviewDto> PreviewAsync(Guid credentialId, FormalRotationPreviewRequest request,
        string? ifMatch, ActorContext actor, CancellationToken ct = default,
        Func<WebApiDbContext, CancellationToken, Task>? requireSession = null)
    {
        request = request with { NewValidFrom = request.NewValidFrom.ToUniversalTime(), NewExpiresAt = request.NewExpiresAt.ToUniversalTime(),
            OverlapUntil = request.OverlapUntil.ToUniversalTime(), HardDeadline = request.HardDeadline.ToUniversalTime() };
        var verifier = new CredentialRevocationMutation(actor, ingress, requireSession);
        var store = new PostgresGovernanceCommitStore(connectionString, verifier);
        return store.InspectAsync(journal, credentialId, (db, local, _, token) =>
            ReadPreviewAsync(db, local, credentialId, request, ifMatch, actor, requireSession, token), ct);
    }

    private async Task<FormalRotationPreviewDto> ReadPreviewAsync(WebApiDbContext db, GovernanceLocalJournalState local,
        Guid credentialId, FormalRotationPreviewRequest request, string? ifMatch, ActorContext actor,
        Func<WebApiDbContext, CancellationToken, Task>? requireSession, CancellationToken ct)
    {
        if (requireSession is not null) await requireSession(db, ct);
        var credential = await db.Set<ApplicationCredential>().AsNoTracking().SingleOrDefaultAsync(item => item.Id == credentialId, ct)
            ?? throw new ApiException(404, "credential_not_found", "找不到凭证。");
        var application = await db.Set<ApplicationRecord>().AsNoTracking().SingleAsync(item => item.Id == credential.ApplicationId, ct);
        var auth = new AuthorizationService(db);
        var target = new ResourceRef("credential", credential.Id,
            new(credential.OrganizationId, credential.ProjectId ?? application.ProjectId, credential.EnvironmentId));
        await auth.RequireAsync(actor, "credential.manage", target, ct);
        await auth.RequireAsync(actor, "credential.revoke", target, ct);
        if (credential.BindingMode != "EnvironmentBound" || credential.EnvironmentId is not Guid environmentId ||
            credential.ProjectId is not Guid projectId || request.EnvironmentId != environmentId ||
            credential.OrganizationId != application.OrganizationId || application.ProjectId is Guid applicationProject && applicationProject != projectId)
            throw new ApiException(409, "formal_rotation_scope_unverified", "正式轮换要求凭证绑定到确认的环境。");

        RevisionTag.Require(ifMatch, credential.Revision);
        if (local.AwaitingConfirmation is not null || horizon is null) throw EvidenceUnavailable();
        var environment = await db.Set<EnvironmentRecord>().FromSqlInterpolated($"SELECT * FROM environments WHERE id={environmentId} FOR UPDATE")
            .AsNoTracking().SingleAsync(item => item.ProjectId == projectId, ct);
        var now = await SecurityDatabaseClock.ObserveAsync(db, ct);
        ValidateWindow(request, now);

        CredentialRevocationCoverageScope current;
        SecurityLedgerMaterial ledger;
        RuntimeSnapshot snapshot;
        try { (current, ledger, snapshot) = await CredentialRevocationMutation.ReadRunningCoverageAsync(db, credential, environment, now, ct); }
        catch (ApiException error) when (error.Status is 404 or 409 or 422) { throw ScopeUnverified(); }
        if (!ingress.IsVerified(current) || application.Status != "Active" || credential.Status != "Active") throw ScopeUnverified();
        var published = snapshot.Applications.SingleOrDefault(item => item.Id == application.Id && item.EnvironmentId == environmentId && item.Status == "Active");
        var publishedKey = published?.Credentials.SingleOrDefault(item => item.Id == credentialId && item.BindingMode == "EnvironmentBound" &&
            item.EnvironmentId == environmentId && item.Status == "Active");
        if (publishedKey is null || publishedKey.AccessKey != credential.AccessKey || publishedKey.Hash != credential.SecretHash ||
            credential.ValidFrom > now || publishedKey.ValidFrom > now || credential.ExpiresAt < request.HardDeadline ||
            publishedKey.ExpiresAt < request.HardDeadline) throw ScopeUnverified();

        var revoked = ledger.Revocations.Select(item => item.CredentialId).ToArray();
        var working = !await db.Set<ApplicationCredential>().AnyAsync(item => item.ApplicationId == application.Id &&
            item.Id != credentialId && !revoked.Contains(item.Id) && item.Status == "Active" &&
            item.ValidFrom <= request.HardDeadline && item.ExpiresAt > request.HardDeadline &&
            (item.BindingMode == "EnvironmentBound" && item.EnvironmentId == environmentId ||
             !ledger.RequireEnvironmentCredentials && item.BindingMode == "LegacyGlobal"), ct);
        var running = !published!.Credentials.Any(item => item.Id != credentialId && !revoked.Contains(item.Id) && item.Status == "Active" &&
            item.ValidFrom <= request.HardDeadline && item.ExpiresAt > request.HardDeadline &&
            (item.BindingMode == "EnvironmentBound" && item.EnvironmentId == environmentId ||
             !ledger.RequireEnvironmentCredentials && item.BindingMode == "LegacyGlobal"));
        var targets = current.Nodes.Select(node => new FormalRotationPreviewTarget(node.NodeId, node.InstanceId)).ToArray();
        var evidenceAt = await SecurityDatabaseClock.ObserveAsync(db, ct);
        if (evidenceAt < now) throw EvidenceUnavailable();
        var proof = await FormalRotationLeaseHorizon.RequireAsync(db,
            new(journal, local.Position, current.OrganizationId, current.ProjectId, environmentId,
                current.SecurityGeneration, current.Epoch, current.HeadHash), horizon, evidenceAt, request.HardDeadline, ct);
        var checkedAt = await SecurityDatabaseClock.ObserveAsync(db, ct);
        var prepareBudget = BoundedWitnessHttpClient.RequestTimeout;
        if (checkedAt < evidenceAt || checkedAt > proof.Upper || proof.ValidUntil - checkedAt <= prepareBudget ||
            current.Nodes.Any(node => node.FreshUntil - checkedAt <= prepareBudget))
            throw EvidenceUnavailable();

        // Renewing a lease can change its identifier every few seconds. Bind the
        // reviewed facts and current instances, then obtain fresh proof at a write.
        var hash = Convert.ToHexStringLower(SHA256.HashData(SecuritySignedEncoding.Unsigned("webapi.credential-formal-rotation.preview.v1",
            new { request, actor.UserId, credentialId, credential.Revision, ApplicationId = application.Id, ApplicationRevision = application.Revision,
                ApplicationAuthorizationRevision = application.AuthorizationRevision, EnvironmentRevision = environment.Revision,
                EnvironmentAuthorizationRevision = environment.AuthorizationRevision, current.OrganizationId, current.ProjectId,
                current.SecurityGeneration, current.Epoch, current.HeadHash, current.ReleaseId, current.SnapshotHash,
                current.ConfigVersion, current.DeploymentSequence, targets, working, running })));
        var mask = credential.AccessKey.Length > 4 ? "••••" + credential.AccessKey[^4..] : "••••";
        return new(credentialId, application.Id, environmentId, mask, credential.Revision, hash, current.ConfigVersion,
            current.DeploymentSequence, request.NewValidFrom, request.NewExpiresAt, request.OverlapUntil, request.HardDeadline,
            working, running, targets);
    }

    private static void ValidateWindow(FormalRotationPreviewRequest request, DateTimeOffset now)
    {
        if (!ValidInstant(request.NewValidFrom) || !ValidInstant(request.NewExpiresAt) || !ValidInstant(request.OverlapUntil) ||
            !ValidInstant(request.HardDeadline) || request.NewValidFrom > request.OverlapUntil || request.OverlapUntil <= now ||
            request.OverlapUntil > request.HardDeadline || request.HardDeadline >= request.NewExpiresAt)
            throw new ApiException(422, "formal_rotation_window_invalid", "轮换有效期、重叠窗口或硬截止时间不合法。");
    }

    private static bool ValidInstant(DateTimeOffset instant) => instant > DateTimeOffset.MinValue &&
        instant < DateTimeOffset.MaxValue && instant.Offset == TimeSpan.Zero;

    internal static ApiException EvidenceUnavailable() => new(503, "formal_rotation_evidence_unavailable", "无法核验正式轮换所需的独立证据。");

    private static ApiException ScopeUnverified() => new(409, "formal_rotation_scope_unverified", "正式轮换要求全部运行入口和旧凭证已核验。");
}
