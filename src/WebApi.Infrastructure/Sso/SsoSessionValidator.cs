using Microsoft.EntityFrameworkCore;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Sso;
public sealed class SsoSessionValidator(WebApiDbContext db)
{
    public async Task<bool> ValidateAsync(Guid userId,string stamp,Guid providerId,long authRevision,Guid bindingId,CancellationToken ct=default)
    {
        if(string.IsNullOrEmpty(stamp)||authRevision<1)return false;
        var source=await (from user in db.Set<UserRecord>().AsNoTracking()
            join identity in db.Set<UserExternalIdentity>().AsNoTracking() on user.Id equals identity.UserId
            join provider in db.Set<SsoProvider>().AsNoTracking() on identity.ProviderId equals provider.Id
            where user.Id==userId&&user.Status=="Active"&&user.AuthSource=="sso"&&user.PasswordHash==null&&user.SecurityStamp==stamp&&
                identity.Id==bindingId&&identity.Enabled&&identity.ProviderId==providerId&&identity.Issuer==provider.Issuer&&
                provider.Enabled&&provider.Id==providerId&&provider.AuthRevision==authRevision
            select new Admission(provider.OrganizationId)).SingleOrDefaultAsync(ct);
        return source is not null&&await SsoAdmission.HasOrganizationAccessAsync(db,source.OrganizationId,userId,ct);
    }
    private sealed record Admission(Guid? OrganizationId);
}
