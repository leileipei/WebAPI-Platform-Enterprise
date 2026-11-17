using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Applications;
using WebApi.Contracts.Common;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Commands;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Security;

namespace WebApi.Infrastructure.Applications;

public sealed class CredentialCreateReceiptService(WebApiDbContext db, AuthorizationService auth, ScopeResolver scopes,
    AuditedCommandExecutor commands, IdempotentCommandExecutor idempotency)
{
    // The idempotency executor serializes its result. Never pass the response
    // containing Secret/ApiKey to it, including on the first successful request.
    private sealed record CredentialCreateMetadata(CredentialDto Credential);

    public async Task<CredentialCreateResponse> CreateAsync(Guid applicationId, CredentialCreateRequest request,
        CommandRequestContext command, ActorContext actor, CancellationToken ct = default)
    {
        var scope = await scopes.ApplicationAsync(applicationId, ct);
        return await commands.ExecuteAsync(actor, scope, "credential.create", async (_, token) =>
        {
            // The command lock is acquired before authorization and receipt reads,
            // so a replay cannot bypass a concurrent role or scope revocation.
            await auth.RequireAsync(actor, "credential.manage", new("application", applicationId, scope), token);
            if (request.ValidFrom == DateTimeOffset.MinValue || request.ExpiresAt <= request.ValidFrom)
                throw new ApiException(422, "invalid_validity_window", "有效期结束必须晚于开始时间。");
            var app = await db.Set<ApplicationRecord>().SingleAsync(x => x.Id == applicationId, token);
            if (app.Status != "Active") throw new ApiException(409, "application_disabled", "停用应用不能创建凭证。");

            var identity = new CommandIdentity(actor.UserId, scope, "credential.create", command.IdempotencyKey);
            var normalized = CanonicalJson.Serialize(new
            {
                applicationId,
                request = request with { ValidFrom = request.ValidFrom.ToUniversalTime(), ExpiresAt = request.ExpiresAt.ToUniversalTime() }
            });
            var replay = await idempotency.TryReplayAsync<CredentialCreateMetadata>(identity, normalized, token);
            if (replay.Found) return new CredentialCreateResponse(replay.Value!.Credential, null, null, true);

            var material = ApiKeySecret.Create();
            var credential = new ApplicationCredential
            {
                ApplicationId = applicationId, OrganizationId = app.OrganizationId,
                AccessKey = material.AccessKey, SecretHash = material.Hash, SecretLast4 = material.Last4,
                Status = "Active", ValidFrom = request.ValidFrom.ToUniversalTime(), ExpiresAt = request.ExpiresAt.ToUniversalTime()
            };
            db.Add(credential);
            app.Revision++;
            app.UpdatedAt = DateTimeOffset.UtcNow;
            var metadata = new CredentialCreateMetadata(ApplicationService.Dto(credential));
            await idempotency.ExecuteAsync(identity, normalized, _ => Task.FromResult(metadata), token);
            return new CredentialCreateResponse(metadata.Credential, material.Secret, material.AccessKey + "." + material.Secret);
        }, ct);
    }
}
