using System.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Applications;
using WebApi.Contracts.Common;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Commands;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Releases;
using WebApi.Infrastructure.Security;

namespace WebApi.Infrastructure.Applications;

public sealed partial class PermissionMatrixService(WebApiDbContext db, AuthorizationService auth, ScopeResolver scopes,
    CollectionAuthorizationQuery visibility, ApplicationService applications, AuditedCommandExecutor commands,
    IdempotentCommandExecutor idempotency, RunningDeploymentReader deployments, IDataProtectionProvider protection, TimeProvider clock)
{
    private readonly IDataProtector previewProtector = protection.CreateProtector("WebApi.ApplicationPermissionMatrix.Preview.v1");

    public async Task<PermissionMatrixPage> ReadAsync(Guid applicationId, Guid projectId, int page, int pageSize,
        ActorContext actor, CancellationToken ct)
    {
        if (page is < 1 or > 1000000 || pageSize is < 1 or > 100)
            throw new ApiException(422, "invalid_paging", "分页范围不合法。");

        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var application = await applications.RequireReadablePermissionProjectAsync(applicationId, projectId, actor, ct);
        var canManageApp = await auth.CanAsync(actor, "app.permission.manage", application, ct);
        var manage = visibility.Scopes(actor, "app.permission.manage");
        var routeRead = visibility.Scopes(actor, "route.read");
        var columns = await applications.ReadablePermissionEnvironments(actor).Where(e => e.ProjectId == projectId)
            .OrderBy(e => e.SortOrder).ThenBy(e => e.Code).ThenBy(e => e.Id)
            .Select(e => new
            {
                e.Id,
                e.Code,
                e.Name,
                e.IsProduction,
                Manage = canManageApp && manage.Any(s => s.EnvironmentId == e.Id),
                ReadRoutes = routeRead.Any(s => s.EnvironmentId == e.Id),
                Strict = db.Set<EnvironmentSecurityState>().Any(s => s.EnvironmentId == e.Id &&
                    (s.MinimumSnapshotSchema == "2.3" || s.MinimumSnapshotSchema == "2.4"))
            }).ToArrayAsync(ct);
        var apiQuery = db.Set<Api>().AsNoTracking().Where(a => a.ProjectId == projectId && a.OrganizationId == application.Scope.OrganizationId);
        var total = await apiQuery.CountAsync(ct);
        var rows = await apiQuery.OrderBy(a => a.Code).ThenBy(a => a.Id).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(a => new { a.Id, a.Code, a.Name }).ToArrayAsync(ct);
        var apiIds = rows.Select(x => x.Id).ToArray();
        var envIds = columns.Select(x => x.Id).ToArray();
        var grants = await db.Set<ApplicationApiPermission>().AsNoTracking().Where(g => g.ApplicationId == applicationId &&
            envIds.Contains(g.EnvironmentId) && apiIds.Contains(g.ApiId)).OrderBy(g => g.EnvironmentId).ToListAsync(ct);
        var routeVisible = columns.Where(e => e.ReadRoutes).Select(e => e.Id).ToHashSet();
        var details = (await applications.ReadPermissionDetailsAsync(grants.Where(g => routeVisible.Contains(g.EnvironmentId)).ToList(), ct))
            .ToDictionary(g => g.Id);
        var now = clock.GetUtcNow();
        var byApi = grants.ToLookup(g => g.ApiId, g => new MatrixGrant(g.Id, g.EnvironmentId, g.Mode, g.ValidFrom, g.ExpiresAt,
            g.Revision, Phase(g.ValidFrom, g.ExpiresAt, now), details.GetValueOrDefault(g.Id)?.RouteMethods));
        await tx.CommitAsync(ct);
        return new(applicationId, projectId, now, columns.Select(e => new MatrixEnvironment(e.Id, e.Code, e.Name, e.IsProduction,
            e.Manage, e.Manage && !e.Strict, e.Manage && e.ReadRoutes)).ToArray(),
            rows.Select(a => new MatrixApi(a.Id, a.Code, a.Name, byApi[a.Id].ToArray())).ToArray(), total, page, pageSize);
    }

    private static string Phase(DateTimeOffset from, DateTimeOffset? expires, DateTimeOffset now) =>
        now < from ? "Pending" : expires <= now ? "Expired" : "Active";
}
