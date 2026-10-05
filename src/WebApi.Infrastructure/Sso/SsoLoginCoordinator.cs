using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Sso;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Settings;
namespace WebApi.Infrastructure.Sso;
public sealed class SsoLoginCoordinator(WebApiDbContext db,SystemSettingsReader settings,TimeProvider clock)
{
    public async Task<PendingSsoLogin> BeginAsync(Guid providerId,string? returnPath,CancellationToken ct=default)
    {
        var path=returnPath??"/organizations";
        if(!SsoProviderValidator.IsSafeReturnPath(path))throw new ApiException(422,"invalid_sso_return_path","登录返回路径不可用。");
        await using var transaction=await db.Database.BeginTransactionAsync(ct);await Lock(ct);
        var provider=await db.Set<SsoProvider>().AsNoTracking().SingleOrDefaultAsync(x=>x.Id==providerId&&x.Enabled,ct)??throw EntryUnavailable();
        if(provider.OrganizationId.HasValue&&!await db.Set<Organization>().AnyAsync(x=>x.Id==provider.OrganizationId&&x.Status=="Active",ct))throw EntryUnavailable();
        var now=clock.GetUtcNow();var attempt=new SsoLoginAttempt{ProviderId=provider.Id,ProviderRevision=provider.Revision,AuthRevision=provider.AuthRevision,ReturnPath=path,CreatedAt=now,ExpiresAt=now.AddMinutes(5)};
        db.Add(attempt);await db.SaveChangesAsync(ct);await transaction.CommitAsync(ct);
        var snapshot=new SsoProviderSnapshot(provider.Id,provider.OrganizationId,provider.Issuer,provider.ClientId,provider.SecretRef,
            JsonSerializer.Deserialize<string[]>(provider.ScopesJson,CanonicalJson.Options)!,JsonSerializer.Deserialize<SsoClaimMapping>(provider.ClaimMappingJson,CanonicalJson.Options)!,provider.Revision,provider.AuthRevision);
        return new(attempt.Id,snapshot,path,attempt.ExpiresAt);
    }
    public async Task ClaimAsync(Guid attemptId,Guid providerId,long expectedRevision,CancellationToken ct=default)
    {
        await using var transaction=await db.Database.BeginTransactionAsync(ct);await Lock(ct);
        var provider=await db.Set<SsoProvider>().AsNoTracking().SingleOrDefaultAsync(x=>x.Id==providerId&&x.Enabled&&x.Revision==expectedRevision,ct)??throw Failed();
        var now=clock.GetUtcNow();
        var claimed=await db.Set<SsoLoginAttempt>().Where(x=>x.Id==attemptId&&x.ProviderId==providerId&&x.ProviderRevision==expectedRevision&&
            x.AuthRevision==provider.AuthRevision&&x.State=="Pending"&&x.ExpiresAt>now).ExecuteUpdateAsync(update=>update.SetProperty(x=>x.State,"Processing"),ct);
        if(claimed!=1)throw Failed();await transaction.CommitAsync(ct);
    }
    public async Task<SsoSessionGrant> CompleteAsync(Guid attemptId,ValidatedOidcIdentity identity,string traceId,IPAddress? ip,CancellationToken ct=default)
    {
        await using var transaction=await db.Database.BeginTransactionAsync(ct);await Lock(ct);var now=clock.GetUtcNow();
        var attempt=await db.Set<SsoLoginAttempt>().AsNoTracking().SingleOrDefaultAsync(x=>x.Id==attemptId&&x.State=="Processing"&&x.ExpiresAt>now,ct)??throw Failed();
        var provider=await db.Set<SsoProvider>().AsNoTracking().SingleOrDefaultAsync(x=>x.Id==attempt.ProviderId&&x.Enabled&&x.Revision==attempt.ProviderRevision&&x.AuthRevision==attempt.AuthRevision,ct)??throw Failed();
        if(identity.Issuer!=provider.Issuer||string.IsNullOrWhiteSpace(identity.Subject)||identity.Subject.Length>255||identity.Subject.Any(char.IsControl))throw Failed();
        var binding=await db.Set<UserExternalIdentity>().AsNoTracking().SingleOrDefaultAsync(x=>x.ProviderId==provider.Id&&x.Issuer==identity.Issuer&&x.Subject==identity.Subject&&x.Enabled,ct)??throw Failed();
        var user=await db.Set<UserRecord>().AsNoTracking().SingleOrDefaultAsync(x=>x.Id==binding.UserId&&x.Status=="Active"&&x.AuthSource=="sso"&&x.PasswordHash==null,ct)??throw Failed();
        if(!await SsoAdmission.HasOrganizationAccessAsync(db,provider.OrganizationId,user.Id,ct))throw Failed();
        var mapping=JsonSerializer.Deserialize<SsoClaimMapping>(provider.ClaimMappingJson,CanonicalJson.Options)!;
        var displayName=Profile(mapping.DisplayName,identity.Claims,user.DisplayName,128,true)!;
        var email=Profile(mapping.Email,identity.Claims,user.Email,256,false);
        var changed=new List<string>();
        if(displayName!=user.DisplayName)changed.Add("displayName");if(email!=user.Email)changed.Add("email");
        if(changed.Count>0)
        {
            var updated=await db.Set<UserRecord>().Where(x=>x.Id==user.Id&&x.Status=="Active"&&x.SecurityStamp==user.SecurityStamp&&x.AuthSource=="sso"&&x.PasswordHash==null)
                .ExecuteUpdateAsync(update=>update.SetProperty(x=>x.DisplayName,displayName).SetProperty(x=>x.Email,email).SetProperty(x=>x.Revision,x=>x.Revision+1).SetProperty(x=>x.UpdatedAt,now),ct);
            if(updated!=1)throw Failed();
        }
        var completed=await db.Set<SsoLoginAttempt>().Where(x=>x.Id==attemptId&&x.State=="Processing"&&x.ExpiresAt>now)
            .ExecuteUpdateAsync(update=>update.SetProperty(x=>x.State,"Succeeded").SetProperty(x=>x.FailureCode,(string?)null),ct);
        if(completed!=1)throw Failed();
        var ttl=(await settings.SecurityAsync(ct)).SessionTtlMinutes;
        db.Add(new AuditLog{UserId=user.Id,OrganizationId=provider.OrganizationId,Action="auth.sso.login",ResourceType="user",ResourceId=user.Id.ToString(),
            AfterJson=JsonSerializer.Serialize(new{providerId=provider.Id,bindingId=binding.Id,attemptId,authRevision=provider.AuthRevision,changedFields=changed},CanonicalJson.Options),
            TraceId=traceId,Ip=ip,CreatedAt=now});
        await db.SaveChangesAsync(ct);await transaction.CommitAsync(ct);
        return new(user.Id,user.SecurityStamp,provider.Id,provider.AuthRevision,binding.Id,ttl,attempt.ReturnPath);
    }
    public Task FailAsync(Guid attemptId,string safeCode,CancellationToken ct=default)=>FailCoreAsync(attemptId,safeCode,true,ct);
    public Task FailPendingAsync(Guid attemptId,string safeCode,CancellationToken ct=default)=>FailCoreAsync(attemptId,safeCode,false,ct);
    private async Task FailCoreAsync(Guid attemptId,string safeCode,bool ownsProcessing,CancellationToken ct)
    {
        string[] allowed=["cancelled","protocol_error","provider_changed","attempt_expired","unknown_identity","admission_denied","configuration_unavailable"];
        var code=allowed.Contains(safeCode,StringComparer.Ordinal)?safeCode:"protocol_error";
        await using var transaction=await db.Database.BeginTransactionAsync(ct);await Lock(ct);
        var changed=await db.Set<SsoLoginAttempt>().Where(x=>x.Id==attemptId&&(x.State=="Pending"||ownsProcessing&&x.State=="Processing"))
            .ExecuteUpdateAsync(update=>update.SetProperty(x=>x.State,"Failed").SetProperty(x=>x.FailureCode,code),ct);
        if(changed==1||!ownsProcessing)db.Add(new AuditLog{Action="auth.sso.failed",ResourceType="sso_login_attempt",ResourceId=attemptId.ToString(),AfterJson=JsonSerializer.Serialize(new{code},CanonicalJson.Options),CreatedAt=clock.GetUtcNow()});
        await db.SaveChangesAsync(ct);await transaction.CommitAsync(ct);
    }
    private Task<int> Lock(CancellationToken ct)=>db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(8901202)",ct);
    private static string? Profile(string? claim,IReadOnlyDictionary<string,string> claims,string? current,int maximum,bool required)
    {
        if(claim is null||!claims.TryGetValue(claim,out var value))return current;
        if(value.Length>maximum||value.Any(char.IsControl)||required&&string.IsNullOrWhiteSpace(value))throw Failed();
        return value;
    }
    private static ApiException Failed()=>new(401,"sso_login_failed","企业登录未完成，请重试或联系管理员。");
    private static ApiException EntryUnavailable()=>new(404,"sso_entry_unavailable","企业登录入口不可用。");
}
