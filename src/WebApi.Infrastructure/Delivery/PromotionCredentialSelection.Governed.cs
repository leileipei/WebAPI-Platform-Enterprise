using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Releases;
using WebApi.Contracts.Runtime;
using WebApi.Contracts.Security;
using WebApi.Domain.Policies;
using WebApi.Infrastructure.Applications;
using WebApi.Infrastructure.Persistence.Entities;

namespace WebApi.Infrastructure.Delivery;

public sealed partial class PromotionCredentialSelection
{
    private async Task<PromotionCredentialResult> SelectGovernedAsync(ReleasePromotion promotion, ArtifactContent content,
        IReadOnlyList<PromotionApplicationMapping> selections, ActorContext actor, CancellationToken ct)
    {
        var scope = await scopes.EnvironmentAsync(promotion.TargetEnvironmentId, ct);
        var applications = await ReadGovernedRetainedAsync(promotion, content, scope, actor, ct);
        var impact = new List<SharedCredentialImpact>();
        foreach (var selection in selections.OrderBy(x => x.ApplicationId))
        {
            var app = await db.Set<ApplicationRecord>().AsNoTracking().SingleAsync(a => a.Id == selection.ApplicationId, ct);
            var credentials = await db.Set<ApplicationCredential>().AsNoTracking()
                .Where(x => selection.CredentialIds.Contains(x.Id)).OrderBy(x => x.Id).ToArrayAsync(ct);
            var grants = await db.Set<ApplicationApiPermission>().AsNoTracking()
                .Where(x => selection.AuthorizationIds.Contains(x.Id)).OrderBy(x => x.Id).ToArrayAsync(ct);
            var frozen = new FrozenApplication(ApplicationService.Dto(app), credentials.Select(FreezeGoverned).ToArray(),
                grants.Select(ApplicationService.Dto).ToArray());

            applications.TryGetValue(app.Id, out var retained);
            var shared = await db.Set<ApplicationApiPermission>().AnyAsync(x => x.ApplicationId == app.Id &&
                x.EnvironmentId != promotion.TargetEnvironmentId, ct);
            if (retained is not null && !retained.Credentials.Select(x => x.Id).SequenceEqual(frozen.Credentials.Select(x => x.Id)))
                throw BaselineChanged();
            if (shared || retained is not null)
            {
                if (!selection.ConfirmSharedCredentialImpact)
                    throw new ApiException(409, "shared_credential_confirmation_required", "共享应用的目标凭证和授权影响须明确确认。");
                impact.Add(new(app.Id, retained?.Permissions.Select(x => x.ApiId).Distinct().Order().ToArray() ?? [],
                    retained?.Credentials.Select(x => x.Id).ToArray() ?? [], true, shared));
            }
            if (retained is not null)
                frozen = frozen with { Permissions = frozen.Permissions.Concat(retained.Permissions).OrderBy(x => x.Id).ToArray() };
            applications[app.Id] = frozen;
        }

        var selected = applications.Values.OrderBy(x => x.Application.Id).ToArray();
        var revisions = selected.SelectMany(app => app.Credentials.Select(x => new ResourceRevision("credential", x.Id, x.Revision))
                .Concat(app.Permissions.Select(x => new ResourceRevision("authorization", x.Id, x.Revision)))
                .Append(new ResourceRevision("application", app.Application.Id, app.Application.Revision)))
            .OrderBy(x => x.Type, StringComparer.Ordinal).ThenBy(x => x.Id).ToArray();
        return new(selected, revisions, impact);
    }

    private async Task<Dictionary<Guid, FrozenApplication>> ReadGovernedRetainedAsync(ReleasePromotion promotion,
        ArtifactContent content, ScopeRef scope, ActorContext actor, CancellationToken ct)
    {
        var retained = new Dictionary<Guid, FrozenApplication>();
        if (promotion.BaselineConfigVersion == 0) return retained;
        var baseline = (await history.ReadAsync(promotion.TargetEnvironmentId, promotion.BaselineConfigVersion, ct)).Snapshot;
        var selectedIds = content.Apis.Select(x => x.ApiId).ToHashSet();
        var retainedRoutes = baseline.Routes.Where(x => !selectedIds.Contains(x.ApiId)).ToArray();
        if (retainedRoutes.Length == 0) return retained;
        if (baseline.SchemaVersion != "2.4")
            throw new ApiException(409, "governance_snapshot_full_freeze_required", "首次发布 2.4 必须冻结全部仍在运行的 API。");
        var apiIds = retainedRoutes.Select(x => x.ApiId).ToHashSet();
        var policyIds = retainedRoutes.SelectMany(x => x.PolicyBindings ?? []).Select(x => x.PolicyId).ToHashSet();
        var mappedIds = baseline.Policies.Where(x => x.Type == "authentication" && policyIds.Contains(x.Id))
            .SelectMany(x => PolicyConfigurationValidator.ParseAuthentication(x.Config).Jwt?.ApplicationMappings ?? [])
            .Select(x => x.ApplicationId).ToHashSet();
        foreach (var runtime in baseline.Applications)
        {
            var expected = runtime.Permissions.Where(x => apiIds.Contains(x.ApiId)).ToArray();
            if (expected.Length == 0 && !mappedIds.Contains(runtime.Id)) continue;
            await auth.RequireAsync(actor, "app.read", new("application", runtime.Id, scope), ct);
            var app = await db.Set<ApplicationRecord>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == runtime.Id, ct);
            if (app is null || app.OrganizationId != promotion.OrganizationId ||
                app.ProjectId is Guid projectId && projectId != promotion.ProjectId || app.Status != runtime.Status)
                throw BaselineChanged();
            var ids = runtime.Credentials.Select(x => x.Id).ToArray();
            var current = await db.Set<ApplicationCredential>().AsNoTracking()
                .Where(x => x.ApplicationId == app.Id && ids.Contains(x.Id)).OrderBy(x => x.Id).ToArrayAsync(ct);
            var keys = current.Select(FreezeGoverned).ToArray();
            if (keys.Length != ids.Length || keys.Any(key => !runtime.Credentials.Any(x =>
                Same(key, x) && key.EnvironmentId == x.EnvironmentId && key.BindingMode == x.BindingMode)))
                throw BaselineChanged();
            var expectedApis = expected.Select(x => x.ApiId).ToArray();
            var grants = await db.Set<ApplicationApiPermission>().AsNoTracking().Where(x => x.ApplicationId == app.Id &&
                x.EnvironmentId == promotion.TargetEnvironmentId && expectedApis.Contains(x.ApiId)).OrderBy(x => x.Id).ToArrayAsync(ct);
            if (grants.Length != expected.Length || grants.Any(x => !expected.Any(g => g.ApiId == x.ApiId && g.Mode == x.Mode &&
                g.ValidFrom == x.ValidFrom && g.ExpiresAt == x.ExpiresAt))) throw BaselineChanged();
            retained.Add(app.Id, new(ApplicationService.Dto(app), keys, grants.Select(ApplicationService.Dto).ToArray()));
        }
        return retained;
    }

    private static FrozenCredential FreezeGoverned(ApplicationCredential credential) => new(credential.Id, credential.AccessKey,
        credential.SecretHash, credential.SecretLast4, credential.Status, credential.ValidFrom, credential.ExpiresAt,
        credential.Revision, credential.EnvironmentId, credential.BindingMode);
}
