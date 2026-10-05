using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Sso;
public sealed class LocalAdministratorGuard(WebApiDbContext db)
{
    private IQueryable<Guid> Available()=>from user in db.Set<UserRecord>() join assignment in db.Set<UserRole>() on user.Id equals assignment.UserId
        join role in db.Set<Role>() on assignment.RoleId equals role.Id
        where user.Status=="Active"&&user.AuthSource=="local"&&user.PasswordHash!=null&&user.PasswordHash!=""&&
            role.IsSystem&&role.OrganizationId==null&&role.Code=="PlatformAdmin" select user.Id;
    public async Task RequireAvailableAsync(CancellationToken ct=default)
    {
        RequireTransaction();
        if(!await Available().AnyAsync(ct))throw new ApiException(409,"local_administrator_required","必须保留可用的本地平台管理员。");
    }
    public async Task ProtectRemovalAsync(Guid userId,CancellationToken ct=default)
    {
        RequireTransaction();var available=Available();
        if(await available.AnyAsync(x=>x==userId,ct)&&!await available.AnyAsync(x=>x!=userId,ct))
            throw new ApiException(409,"last_local_administrator","不能停用或撤销最后一位可用本地平台管理员。");
    }
    private void RequireTransaction(){if(db.Database.CurrentTransaction is null)throw new InvalidOperationException("Local administrator checks require the governance transaction lock.");}
}
