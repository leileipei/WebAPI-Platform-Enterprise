using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Settings;
using WebApi.Infrastructure.Commands;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Sso;
namespace WebApi.Infrastructure.Security;

// Session material is an in-process grant, never a response or a persisted replay.
public sealed record PasswordChangeGrant([property:JsonIgnore] Guid UserId,[property:JsonIgnore] string SecurityStamp,[property:JsonIgnore] IdentityDto Identity)
{
    public override string ToString()=>nameof(PasswordChangeGrant);
}
public sealed class PasswordLifecycleService(WebApiDbContext db,IPasswordHasher<UserRecord> passwords,SystemSettingsReader settings,AuditedCommandExecutor commands,AccountService accounts,TimeProvider clock,AuthorizationService authorization,IdempotentCommandExecutor idempotency,LoginKeyHasher keys,LocalAdministratorGuard localAdministrators)
{
    public async Task<PasswordChangeGrant> ChangeAsync(ActorContext actor,string expectedStamp,ChangePasswordRequest request,CancellationToken ct=default)
    {
        if(request.CurrentPassword is null||request.CurrentPassword.Length is 0 or >1024||request.NewPassword is null||request.ConfirmPassword is null||request.NewPassword!=request.ConfirmPassword)
            throw new ApiException(422,"invalid_password_request","请填写当前密码及一致的新密码。");
        var stamp=await commands.ExecuteAsync(actor,new(Guid.Empty),"auth.password.change",async(_,token)=>{
            var user=await db.Set<UserRecord>().FromSqlInterpolated($"SELECT * FROM users WHERE id={actor.UserId} FOR UPDATE").SingleOrDefaultAsync(token);
            if(user is null||user.Status!="Active"||user.SecurityStamp!=expectedStamp)throw new ApiException(401,"inactive_session","会话已失效。");
            if(user.AuthSource!="local"||user.PasswordHash is null)throw new ApiException(403,"local_password_required","此账号的密码由企业身份提供方管理。");
            if(passwords.VerifyHashedPassword(user,user.PasswordHash,request.CurrentPassword)==PasswordVerificationResult.Failed)
                throw new ApiException(403,"current_password_invalid","当前密码不正确。");
            if(request.NewPassword==request.CurrentPassword)throw new ApiException(422,"password_unchanged","新密码必须与当前密码不同。");
            var security=await settings.SecurityAsync(token);PasswordPolicyValidator.Validate(request.NewPassword,security.PasswordMinLength,security.PasswordComplexity);
            user.PasswordHash=passwords.HashPassword(user,request.NewPassword);user.MustChangePassword=false;
            user.PasswordChangedAt=clock.GetUtcNow();user.UpdatedAt=user.PasswordChangedAt.Value;user.Revision=checked(user.Revision+1);user.SecurityStamp=Guid.NewGuid().ToString("N");
            return user.SecurityStamp;
        },ct);
        return new(actor.UserId,stamp,await accounts.IdentityAsync(actor.UserId,ct));
    }
    public Task<CommandResult<PasswordResetDto>> ResetAsync(Guid targetId,ResetPasswordRequest request,string? ifMatch,ActorContext actor,CommandRequestContext command,CancellationToken ct=default)
        =>commands.ExecuteAsync(actor,new(Guid.Empty),"user.password.reset",async(_,token)=>{
            await authorization.RequireAsync(actor,"user.manage",new("user",targetId,new(Guid.Empty)),token);
            var user=await db.Set<UserRecord>().FromSqlInterpolated($"SELECT * FROM users WHERE id={targetId} FOR UPDATE").SingleOrDefaultAsync(token)??throw ScopeResolver.Missing();
            RevisionTag.Require(ifMatch,user.Revision);
            if(targetId==actor.UserId)throw new ApiException(409,"self_password_reset_denied","请通过个人改密修改自己的密码。");
            if(user.AuthSource!="local"||user.Status!="Active"||user.PasswordHash is null)
                throw new ApiException(409,"local_password_reset_unavailable","仅可重置启用的本地账号；企业账号由身份提供方管理。");
            var security=await settings.SecurityAsync(token);PasswordPolicyValidator.Validate(request.TemporaryPassword,security.PasswordMinLength,security.PasswordComplexity);
            var expected=user.Revision;
            var normalized=CanonicalJson.Serialize(new {TargetId=targetId,ExpectedRevision=expected,PasswordFingerprint=keys.PasswordResetFingerprint(actor.UserId,targetId,expected,request.TemporaryPassword)});
            return await idempotency.ExecuteAsync(new(actor.UserId,new(Guid.Empty),"user.password.reset",command.IdempotencyKey),normalized,async inner=>{
                user.PasswordHash=passwords.HashPassword(user,request.TemporaryPassword);user.SecurityStamp=Guid.NewGuid().ToString("N");user.MustChangePassword=true;
                user.PasswordChangedAt=clock.GetUtcNow();user.UpdatedAt=user.PasswordChangedAt.Value;user.Revision=checked(user.Revision+1);
                await localAdministrators.RequireAvailableAsync(inner);
                return new CommandResult<PasswordResetDto>(new(user.Id,user.Revision,user.MustChangePassword),RevisionTag.Format(user.Revision));
            },token);
        },ct);

}
