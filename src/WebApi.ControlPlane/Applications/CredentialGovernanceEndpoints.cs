using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Applications;
using WebApi.Contracts.Common;
using WebApi.Contracts.Security;
using WebApi.ControlPlane.Security;
using WebApi.Infrastructure.CredentialSecurity;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Security;
using WebApi.Infrastructure.Sso;

namespace WebApi.ControlPlane.Applications;

internal static class CredentialGovernanceEndpoints
{
    public static void MapCredentialGovernance(this WebApplication app)
    {
        var endpoints = app.MapGroup("/api/v1").RequireAuthorization().AddEndpointFilter<RequestValidationFilter>();
        endpoints.MapPost("/credentials/{id:guid}/revoke", RevokeAsync);
        endpoints.MapGet("/credential-revocations/{id:guid}", ReadRevocationAsync);
        endpoints.MapPost("/credential-governance/journal/enroll", EnrollAsync);
        endpoints.MapPost("/credential-governance/journal/recover", RecoverAsync);
    }

    private static async Task<IResult> RevokeAsync(Guid id, RevokeCredentialRequest request, HttpContext context,
        WebApiDbContext db, AuthorizationService authorization, CommandRequestContext command, CancellationToken ct)
    {
        context.Response.Headers.CacheControl = "no-store";
        var actor = context.Actor();
        var scope = await (from credential in db.Set<ApplicationCredential>().AsNoTracking()
                           join application in db.Set<ApplicationRecord>().AsNoTracking() on credential.ApplicationId equals application.Id
                           where credential.Id == id && credential.OrganizationId == application.OrganizationId
                           select new ScopeRef(credential.OrganizationId, credential.ProjectId ?? application.ProjectId, credential.EnvironmentId))
            .SingleOrDefaultAsync(ct) ?? throw new ApiException(404, "credential_not_found", "找不到凭证。");
        await authorization.RequireAsync(actor, "credential.revoke", new("credential", id, scope), ct);
        // A user-supplied note could contain a sensitive value. The independently
        // persisted command receives only one of these public audit reasons.
        if (request.Reason is not ("compromised" or "owner_request" or "credential_retired" or "policy_change"))
            throw new ApiException(422, "revocation_reason_invalid", "请选择预定义的撤销理由。");

        var service = context.RequestServices.GetService<PostgresCredentialRevocationService>() ?? throw Unavailable();
        try
        {
            var receipt = await service.RevokeAsync(id, request, context.Request.Headers.IfMatch, command, actor, ct,
                RequireCurrentSession(context.User, actor));
            return Results.Accepted(receipt.PollUrl, receipt);
        }
        catch (InvalidDataException)
        {
            throw Unavailable();
        }
    }

    private static async Task<IResult> ReadRevocationAsync(Guid id, HttpContext context, WebApiDbContext db, CancellationToken ct)
    {
        context.Response.Headers.CacheControl = "no-store";
        var actor = context.Actor();
        var result = await new SecurityOperationStatusReader(db).ReadRevocationAsync(id, actor,
            RequireCurrentSession(context.User, actor), ct);
        return Results.Ok(result);
    }

    private static async Task<IResult> EnrollAsync(HttpContext context, AuthorizationService authorization, CancellationToken ct)
    {
        context.Response.Headers.CacheControl = "no-store";
        var actor = context.Actor();
        await RequireRecoveryAsync(authorization, actor, ct);
        var service = context.RequestServices.GetService<PostgresGovernanceJournalEnrollment>() ?? throw Unavailable();
        var requireSession = RequireCurrentSession(context.User, actor);
        var created = await service.EnrollAsync(async (fenced, token) =>
        {
            await requireSession(fenced, token);
            await RequireRecoveryAsync(new(fenced), actor, token);
        }, ct);
        return Results.Ok(new { created });
    }

    private static async Task<IResult> RecoverAsync(HttpContext context, AuthorizationService authorization, CancellationToken ct)
    {
        context.Response.Headers.CacheControl = "no-store";
        var actor = context.Actor();
        await RequireRecoveryAsync(authorization, actor, ct);
        var service = context.RequestServices.GetService<PostgresCredentialRevocationService>() ?? throw Unavailable();
        try
        {
            await service.RecoverAsync(actor, ct, RequireCurrentSession(context.User, actor));
            return Results.NoContent();
        }
        catch (InvalidDataException)
        {
            throw Unavailable();
        }
    }

    private static Task RequireRecoveryAsync(AuthorizationService authorization, ActorContext actor, CancellationToken ct) =>
        authorization.RequireAsync(actor, "credential.governance.recover", new("credential_governance", Guid.Empty, new(Guid.Empty)), ct);

    private static Func<WebApiDbContext, CancellationToken, Task> RequireCurrentSession(ClaimsPrincipal principal, ActorContext actor)
    {
        var stamp = principal.FindFirstValue("security_stamp");
        var provider = principal.FindFirstValue("sso_provider");
        var binding = principal.FindFirstValue("sso_binding");
        var revision = principal.FindFirstValue("sso_auth_revision");
        return async (db, ct) =>
        {
            if (string.IsNullOrEmpty(stamp)) throw SessionExpired();
            bool current;
            if (provider is not null || binding is not null || revision is not null)
            {
                current = Guid.TryParse(provider, out var providerId) && Guid.TryParse(binding, out var bindingId) &&
                    long.TryParse(revision, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var authRevision) &&
                    await new SsoSessionValidator(db).ValidateAsync(actor.UserId, stamp, providerId, authRevision, bindingId, ct);
            }
            else
            {
                current = await db.Set<UserRecord>().AsNoTracking().AnyAsync(user => user.Id == actor.UserId &&
                    user.Status == "Active" && user.AuthSource == "local" && user.SecurityStamp == stamp, ct);
            }
            if (!current) throw SessionExpired();
        };
    }

    private static ApiException SessionExpired() => new(401, "authentication_required", "请重新登录。");

    internal static ApiException Unavailable() =>
        new(503, "credential_governance_unavailable", "独立见证或受控入口证据不可用。");
}
