using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Applications;
using WebApi.Contracts.Common;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;

namespace WebApi.Infrastructure.Applications;

public sealed partial class PermissionMatrixService
{
    private sealed record PreviewProof(Guid ActorId, Guid ApplicationId, Guid ProjectId, string Hash, DateTimeOffset ExpiresAt);
    private sealed record StoredReceipt(MatrixReceipt Receipt, Guid[] RoutePermissions);
    private sealed record EnvironmentAccess(EnvironmentRecord Environment, ResourceRef Resource, bool ReadRoutes, bool ReadRunning);
    private sealed record PreparedChange(MatrixChange Change, PermissionSaveRequest? Save, MatrixImpact Impact);

    public async Task<MatrixPreview> PreviewAsync(Guid id, MatrixPreviewRequest request, ActorContext actor,
        Func<WebApiDbContext, CancellationToken, Task> requireSession, CancellationToken ct)
    {
        var changes = Normalize(request.Changes);
        var scope = await scopes.ProjectAsync(request.ProjectId, ct);
        return await commands.ExecuteAsync(actor, scope, "app.permission.matrix.preview", async (_, token) =>
        {
            var access = await AuthorizeAsync(id, request.ProjectId, changes, actor, requireSession, token);
            var prepared = await PrepareAsync(id, changes, access, actor, token);
            var expires = clock.GetUtcNow().AddMinutes(5);
            var proof = new PreviewProof(actor.UserId, id, request.ProjectId, Fingerprint(changes, prepared), expires);
            var protectedProof = previewProtector.Protect(JsonSerializer.Serialize(proof));
            return new MatrixPreview(protectedProof, expires, prepared.Any(p => p.Impact.IsProduction), prepared.Select(p => p.Impact).ToArray());
        }, ct);
    }

    public async Task<MatrixReceipt> CommitAsync(Guid id, MatrixCommitRequest request, CommandRequestContext command,
        ActorContext actor, Func<WebApiDbContext, CancellationToken, Task> requireSession, CancellationToken ct)
    {
        var changes = Normalize(request.Changes);
        var scope = await scopes.ProjectAsync(request.ProjectId, ct);
        var identity = new CommandIdentity(actor.UserId, scope, "app.permission.matrix.commit", command.IdempotencyKey);
        var business = CanonicalJson.Serialize(new { applicationId = id, request.ProjectId, changes, request.ConfirmProduction });
        return await commands.ExecuteAsync(actor, scope, identity.Operation, async (_, token) =>
        {
            var access = await AuthorizeAsync(id, request.ProjectId, changes, actor, requireSession, token);
            var stored = await idempotency.ExecuteAsync(identity, business, async commandToken =>
            {
                var prepared = await PrepareAsync(id, changes, access, actor, commandToken);
                var proof = ReadProof(request.PreviewToken);
                if (proof.ActorId != actor.UserId || proof.ApplicationId != id || proof.ProjectId != request.ProjectId ||
                    proof.ExpiresAt <= clock.GetUtcNow() || proof.Hash != Fingerprint(changes, prepared)) throw StalePreview();
                if (prepared.Any(p => p.Impact.IsProduction) && !request.ConfirmProduction)
                    throw new ApiException(409, "production_confirmation_required", "请确认生产环境变更。");

                if (changes is [{ Action: "MoveLegacy" } move])
                {
                    // Both cells were checked under the same locks. Preserve the original grant ID and the move-specific audit.
                    var destination = prepared.Single(p => p.Save is not null).Save!;
                    await applications.SavePermissionAsync(id, move.PermissionId, destination, RevisionTag.Format(move.Revision!.Value),
                        actor, commandToken, requireSession);
                }
                else
                {
                    foreach (var item in prepared)
                    {
                        var change = item.Change;
                        var tag = change.Revision is long revision ? RevisionTag.Format(revision) : null;
                        if (item.Save is null)
                            await applications.DeletePermissionAsync(change.PermissionId!.Value, tag, actor, commandToken, requireSession);
                        else
                            await applications.SavePermissionAsync(id, change.PermissionId, item.Save, tag, actor, commandToken, requireSession);
                    }
                }
                return new StoredReceipt(new MatrixReceipt(changes.Length, "WorkingConfiguration"),
                    prepared.Where(p => p.Save?.Mode == "RouteMethod").Select(p => p.Change.EnvironmentId).Distinct().ToArray());
            }, token);
            foreach (var envId in stored.RoutePermissions)
                await auth.RequireAsync(actor, "route.read", access[envId].Resource, token);
            return stored.Receipt;
        }, ct);
    }

    private async Task<Dictionary<Guid, EnvironmentAccess>> AuthorizeAsync(Guid id, Guid projectId, MatrixChange[] changes,
        ActorContext actor, Func<WebApiDbContext, CancellationToken, Task> requireSession, CancellationToken ct)
    {
        await requireSession(db, ct);
        var app = await applications.RequireReadablePermissionProjectAsync(id, projectId, actor, ct);
        await auth.RequireAsync(actor, "app.permission.manage", app, ct);
        var cells = AffectedCells(changes);
        var apiIds = cells.Select(c => c.ApiId).Distinct().ToArray();
        if (await db.Set<Api>().CountAsync(a => a.OrganizationId == app.Scope.OrganizationId && a.ProjectId == projectId &&
            apiIds.Contains(a.Id), ct) != apiIds.Length) throw ScopeResolver.Missing();

        var ids = cells.Select(c => c.EnvironmentId).Distinct().ToArray();
        var environments = await db.Set<EnvironmentRecord>().FromSqlInterpolated(
            $"SELECT * FROM environments WHERE project_id={projectId} AND id=ANY({ids}) ORDER BY id FOR UPDATE").AsNoTracking().ToArrayAsync(ct);
        if (environments.Length != ids.Length) throw ScopeResolver.Missing();
        var result = new Dictionary<Guid, EnvironmentAccess>();
        foreach (var environment in environments)
        {
            var env = new ResourceRef("environment", environment.Id, new(app.Scope.OrganizationId, projectId, environment.Id));
            if (environment.Status != "Active" || !await auth.CanAsync(actor, "environment.read", env, ct) ||
                !await auth.CanAsync(actor, "app.read", env, ct)) throw ScopeResolver.Missing();
            await auth.RequireAsync(actor, "app.permission.manage", env, ct);
            result.Add(env.Id, new(environment, env, await auth.CanAsync(actor, "route.read", env, ct), await auth.CanAsync(actor, "gateway.config.read", env, ct)));
        }
        return result;
    }

    private static MatrixChange[] Normalize(IReadOnlyList<MatrixChange> changes)
    {
        if (changes is not { Count: >= 1 and <= 20 } || changes.Any(c => c is null) ||
            changes.DistinctBy(c => (c.EnvironmentId, c.ApiId)).Count() != changes.Count) throw InvalidBatch();
        return changes.Select(c =>
        {
            if (c.ApiId == Guid.Empty || c.EnvironmentId == Guid.Empty || c.PermissionId == Guid.Empty || c.Revision is <= 0 ||
                c.PermissionId.HasValue != c.Revision.HasValue) throw InvalidBatch();
            if (c.Action != "MoveLegacy" && (c.DestinationEnvironmentId.HasValue || c.DestinationApiId.HasValue)) throw InvalidBatch();
            if (c.Action == "Revoke")
            {
                if (c.PermissionId is null || c.ValidFrom is not null || c.ExpiresAt is not null || c.Mode is not null || c.RouteMethods is not null)
                    throw InvalidBatch();
                return c;
            }
            if (c.Action == "MoveLegacy")
            {
                if (changes.Count != 1 || c.PermissionId is null || c.Mode is not null || c.RouteMethods is not null ||
                    c.DestinationApiId is not Guid targetApi || targetApi == Guid.Empty ||
                    c.DestinationEnvironmentId is not Guid targetEnv || targetEnv == Guid.Empty ||
                    (targetApi == c.ApiId && targetEnv == c.EnvironmentId)) throw InvalidBatch();
            }
            else if (c.Action == "SetWindow")
            {
                if (c.PermissionId is null || c.Mode is not null || c.RouteMethods is not null) throw InvalidBatch();
            }
            else if (c.Action != "Save" || c.Mode is not ("LegacyApi" or "RouteMethod") ||
                (c.Mode != "RouteMethod" && c.RouteMethods is not null)) throw InvalidBatch();
            if (c.ValidFrom is not DateTimeOffset from) throw InvalidBatch();
            ApplicationService.ValidateWindow(from, c.ExpiresAt);
            return c with
            {
                ValidFrom = from.ToUniversalTime(),
                ExpiresAt = c.ExpiresAt?.ToUniversalTime(),
                RouteMethods = c.Mode == "RouteMethod" ? ApplicationService.ValidateMethods(c.RouteMethods).Values.OrderBy(r => r.RouteId).ToArray() : null
            };
        }).OrderBy(c => c.EnvironmentId).ThenBy(c => c.ApiId).ToArray();
    }

    private static MatrixChange[] AffectedCells(MatrixChange[] changes) =>
        changes is [{ Action: "MoveLegacy" } move]
            ? [new(move.ApiId, move.EnvironmentId, "Revoke", move.PermissionId, move.Revision),
                new(move.DestinationApiId!.Value, move.DestinationEnvironmentId!.Value, "Save", ValidFrom: move.ValidFrom,
                    ExpiresAt: move.ExpiresAt, Mode: "LegacyApi")]
            : changes;

    private PreviewProof ReadProof(string token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 4096) throw StalePreview();
        try { return JsonSerializer.Deserialize<PreviewProof>(previewProtector.Unprotect(token)) ?? throw StalePreview(); }
        catch (Exception e) when (e is CryptographicException or JsonException or FormatException) { throw StalePreview(); }
    }

    private static string Fingerprint(MatrixChange[] changes, PreparedChange[] prepared) =>
        Convert.ToHexString(SHA256.HashData(CanonicalJson.Serialize(new { changes, prepared })));
    private static ApiException InvalidBatch() => new(422, "invalid_permission_matrix", "请提供 1 至 20 个不重复的授权单元、有效窗口及对应的修订。");
    private static ApiException StalePreview() => new(409, "permission_preview_changed", "预览已过期或授权影响已变化，请重新预览。");
}
