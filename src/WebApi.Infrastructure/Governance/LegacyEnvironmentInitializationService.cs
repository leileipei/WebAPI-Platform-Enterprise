using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Governance;
using WebApi.Contracts.Security;
using WebApi.Domain.CredentialSecurity;
using WebApi.Infrastructure.CredentialSecurity;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Releases;
using WebApi.Infrastructure.Security;

namespace WebApi.Infrastructure.Governance;

public sealed class LegacyEnvironmentInitializationService(WebApiDbContext db, AuthorizationService authorization,
    ScopeResolver scopes, AuditedCommandExecutor commands, HistoricalSnapshotService history,
    ILegacyEnvironmentHistoryReader? evidenceReader = null)
{
    internal const string AuditAction = "environment.legacy-initialization.confirm";

    public Task<CommandResult<LegacyEnvironmentInitializationPreview>> PreviewAsync(Guid environmentId, ActorContext actor,
        Func<WebApiDbContext, CancellationToken, Task> requireCurrentSession, CancellationToken ct = default) =>
        ExecuteAsync(environmentId, null, null, actor, requireCurrentSession, ct);

    public Task<CommandResult<LegacyEnvironmentInitializationPreview>> ConfirmAsync(Guid environmentId,
        ConfirmLegacyEnvironmentInitialization request, string? ifMatch, ActorContext actor,
        Func<WebApiDbContext, CancellationToken, Task> requireCurrentSession, CancellationToken ct = default)
    {
        if (!SecuritySignedEncoding.HexHash(request.PreviewHash) || request.Reason?.Trim() is not { Length: >= 1 and <= 1000 } reason ||
            reason.Contains('\0'))
            throw new ApiException(422, "legacy_initialization_invalid", "须提交有效的预览指纹和确认理由。");
        return ExecuteAsync(environmentId, request with { Reason = reason }, ifMatch, actor, requireCurrentSession, ct);
    }

    private async Task<CommandResult<LegacyEnvironmentInitializationPreview>> ExecuteAsync(Guid environmentId,
        ConfirmLegacyEnvironmentInitialization? request, string? ifMatch, ActorContext actor,
        Func<WebApiDbContext, CancellationToken, Task> requireCurrentSession, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        ILegacyEnvironmentHistoryFence? fence = null;
        var committing = false;
        var releaseFence = false;
        try
        {
            var result = await ReadAndApplyAsync();
            ct.ThrowIfCancellationRequested();
            committing = true;
            await transaction.CommitAsync(ct);
            releaseFence = true;
            return result;
        }
        catch (Exception error) when (fence is not null && Recoverable(error))
        {
            // A failed COMMIT may have applied. Retain the independent stop-write
            // hold until an operator can reconcile the database and witness facts.
            if (committing) throw FenceRetained(fence);
            try
            {
                using var rollback = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await transaction.RollbackAsync(rollback.Token);
                releaseFence = true;
            }
            catch (Exception rollbackError) when (Recoverable(rollbackError)) { throw FenceRetained(fence); }
            throw;
        }
        finally
        {
            if (releaseFence && fence is not null) await fence.DisposeAsync();
        }

        async Task<CommandResult<LegacyEnvironmentInitializationPreview>> ReadAndApplyAsync()
        {
            await db.Database.ExecuteSqlRawAsync("SET LOCAL search_path = public, pg_temp", ct);
            await db.Database.ExecuteSqlRawAsync("SELECT pg_catalog.pg_advisory_xact_lock_shared(1464156498,1196381253)", ct);
            await db.Database.ExecuteSqlRawAsync("SELECT pg_catalog.pg_advisory_xact_lock(8901202)", ct);
            var environment = await db.Set<EnvironmentRecord>().FromSqlInterpolated($"SELECT * FROM public.environments WHERE id={environmentId} FOR UPDATE")
                .SingleOrDefaultAsync(ct) ?? throw ScopeResolver.Missing();
            var scope = await scopes.EnvironmentAsync(environmentId, ct);
            await requireCurrentSession(db, ct);
            var resource = new ResourceRef("environment", environmentId, scope);
            await authorization.RequireAsync(actor, "environment.write", resource, ct);
            await authorization.RequireAsync(actor, "gateway.config.read", resource, ct);
            if (request is not null) RevisionTag.Require(ifMatch, environment.Revision);

            var origin = await db.Set<EnvironmentSecurityInitialization>().SingleOrDefaultAsync(row => row.EnvironmentId == environmentId, ct);
            if (origin?.State is { } state && state != "Unknown")
                return Unavailable(state, "legacy_initialization_already_resolved");
            if (origin is null || origin.ProjectId != environment.ProjectId || origin.OrganizationId != scope.OrganizationId)
                return Unavailable("Unknown", "legacy_initialization_source_unverified");

            var source = await LegacyEnvironmentInitializationEvidence.ReadSourceAsync(db, origin, ct);
            if (source is null) return Unavailable("Unknown", "legacy_initialization_source_unverified");
            fence = evidenceReader is null ? null : await evidenceReader.OpenAsync(environmentId, ct);
            var heldFence = fence;
            var evidence = heldFence?.Evidence;
            if (heldFence is null || evidence is null || !await heldFence.IsHeldAsync(ct))
                return Unavailable("Unknown", "legacy_initialization_history_unverified");
            if (await LegacyEnvironmentInitializationEvidence.HasGovernanceAsync(db, environment, scope, ct))
                return Unavailable("Unknown", "legacy_initialization_governance_detected");

            var nodes = await db.Set<GatewayNode>().AsNoTracking().Where(row => row.EnvironmentId == environmentId).ToArrayAsync(ct);
            var release = await LegacyEnvironmentInitializationEvidence.ReadPublicationAsync(db, history, environment, nodes, ct);
            if (release is null) return Unavailable("Unknown", "legacy_initialization_release_unverified");

            var known = await LegacyEnvironmentInitializationEvidence.ReadKnownInstancesAsync(db, environmentId, ct);
            if (evidence.Nodes is null) return Unavailable("Unknown", "legacy_initialization_history_unverified");
            var reviewed = evidence with { Nodes = evidence.Nodes.ToArray() };
            if (!LegacyEnvironmentInitializationEvidence.Matches(reviewed, source, release.Current, nodes, known))
                return Unavailable("Unknown", "legacy_initialization_history_unverified");

            reviewed = reviewed with { Nodes = reviewed.Nodes.OrderBy(row => row.NodeId).ThenBy(row => row.InstanceId, StringComparer.Ordinal)
                .ThenBy(row => row.StorageIdentityHash, StringComparer.Ordinal).ToArray() };
            var evidenceHash = Hash(new
            {
                reviewed,
                currentMigration = db.Database.GetMigrations().Last(),
                published = release.History,
                currentNodes = nodes.OrderBy(row => row.Id).Select(row => new { row.Id, row.InstanceId, row.AppVersion, row.Enabled,
                    row.CurrentConfigVersion, row.CurrentDeploymentSequence }).ToArray(),
                knownInstances = known.OrderBy(row => row.NodeId).ThenBy(row => row.InstanceId, StringComparer.Ordinal).ToArray()
            });
            var previewHash = Hash(new { purpose = "webapi.legacy-environment-initialization.v1", actor.UserId,
                environment.Revision, initializationRevision = origin.Revision, evidenceHash });
            var preview = new LegacyEnvironmentInitializationPreview(environmentId, environment.Revision, "Unknown",
                "legacy_initialization_confirmation_required", previewHash, evidenceHash, reviewed.DeploymentManifestHash,
                source.Migration, reviewed.Nodes.Count, release.Current.ReleaseId, release.Current.ConfigVersion, release.Current.SnapshotHash);

            if (request is not null)
            {
                if (request.PreviewHash != previewHash)
                    throw new ApiException(409, "legacy_initialization_preview_changed", "证据或范围已变化，请重新预览。");
                var confirmedAt = await SecurityDatabaseClock.ObserveAsync(db, ct);
                if (confirmedAt < source.CapturedAt)
                    throw new ApiException(409, "legacy_initialization_source_unverified", "历史迁移来源时间无法复核。");

                await commands.ExecuteAsync(actor, scope, AuditAction, (_, _) =>
                {
                    origin.State = "VerifiedLegacyNetwork";
                    origin.EvidenceHash = evidenceHash;
                    origin.ConfirmedBy = actor.UserId;
                    origin.ConfirmedAt = confirmedAt;
                    origin.ConfirmationReason = request.Reason;
                    origin.UpdatedAt = confirmedAt;
                    origin.Revision = checked(origin.Revision + 1);
                    environment.Revision = checked(environment.Revision + 1);
                    return Task.FromResult(environment.Revision);
                }, async (_, token) =>
                {
                    // The genesis trigger observes the verified origin saved by the audited command.
                    db.Add(new EnvironmentSecurityState { EnvironmentId = environmentId, ProjectId = environment.ProjectId,
                        OrganizationId = scope.OrganizationId, UpdatedAt = confirmedAt });
                    await db.SaveChangesAsync(token);
                    if (!await heldFence.IsHeldAsync(token))
                        throw new ApiException(409, "legacy_initialization_history_unverified", "停写证明已失效，请重新核验。");
                }, ct);
                preview = preview with { EnvironmentRevision = environment.Revision, State = "VerifiedLegacyNetwork",
                    StatusCode = "legacy_initialization_confirmed", PreviewHash = null };
            }

            return new(preview, RevisionTag.Format(environment.Revision));

            CommandResult<LegacyEnvironmentInitializationPreview> Unavailable(string state, string code)
            {
                if (request is not null) throw new ApiException(409, code, "环境缺少可复核的旧网络初始化证据或已经完成初始化。");
                return new(new(environmentId, environment.Revision, state, code), RevisionTag.Format(environment.Revision));
            }
        }
    }

    private static bool Recoverable(Exception error) => error is not (OutOfMemoryException or StackOverflowException or AccessViolationException);

    private static ApiException FenceRetained(ILegacyEnvironmentHistoryFence fence)
    {
        fence.RetainForRecovery();
        return new(503, "legacy_initialization_fence_retained",
            "本地事务结果无法确认，独立停写栅栏已保留。请核对数据库事务与见证的持有记录后按运维流程恢复。");
    }

    private static string Hash<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(CanonicalJson.Serialize(value)));
}
