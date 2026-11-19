using System.Globalization;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Governance;
using WebApi.Contracts.Security;
using WebApi.ControlPlane.Security;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Sso;

namespace WebApi.ControlPlane.Governance;

internal static class LegacyEnvironmentInitializationEndpoints
{
    internal static void MapLegacyEnvironmentInitialization(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/environments/{environmentId:guid}/legacy-initialization").RequireAuthorization();
        group.MapGet("/preview", async (Guid environmentId, HttpContext context, LegacyEnvironmentInitializationService service, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var actor = context.Actor();
            return GovernanceEndpoints.Command(context, await service.PreviewAsync(environmentId, actor, RequireSession(context.User, actor), ct));
        });
        group.MapPost("/confirm", async (Guid environmentId, ConfirmLegacyEnvironmentInitialization request,
            HttpContext context, LegacyEnvironmentInitializationService service, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var actor = context.Actor();
            return GovernanceEndpoints.Command(context,
                await service.ConfirmAsync(environmentId, request, context.Request.Headers.IfMatch, actor, RequireSession(context.User, actor), ct));
        });
    }

    private static Func<WebApiDbContext, CancellationToken, Task> RequireSession(ClaimsPrincipal principal, ActorContext actor)
    {
        var stamp = principal.FindFirstValue("security_stamp");
        var provider = principal.FindFirstValue("sso_provider");
        var binding = principal.FindFirstValue("sso_binding");
        var revision = principal.FindFirstValue("sso_auth_revision");
        return async (db, ct) =>
        {
            if (string.IsNullOrEmpty(stamp)) throw SessionExpired();
            var current = provider is not null || binding is not null || revision is not null
                ? Guid.TryParse(provider, out var providerId) && Guid.TryParse(binding, out var bindingId) &&
                  long.TryParse(revision, NumberStyles.None, CultureInfo.InvariantCulture, out var authRevision) &&
                  await new SsoSessionValidator(db).ValidateAsync(actor.UserId, stamp, providerId, authRevision, bindingId, ct)
                : await db.Set<UserRecord>().AsNoTracking().AnyAsync(row => row.Id == actor.UserId && row.Status == "Active" &&
                    row.AuthSource == "local" && row.SecurityStamp == stamp, ct);
            if (!current) throw SessionExpired();
        };
    }

    private static ApiException SessionExpired() => new(401, "authentication_required", "请重新登录。");
}
