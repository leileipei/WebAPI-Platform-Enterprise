using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Security;

public sealed class PasswordLifecycleGuard(WebApiDbContext db)
{
    public async Task RequireBusinessAccessAsync(ActorContext actor,CancellationToken ct=default)
    {
        // Keep the normal authorization path to one scalar EXISTS; denial reads distinguish state.
        if(await db.Set<UserRecord>().AsNoTracking().AnyAsync(x=>x.Id==actor.UserId&&x.Status=="Active"&&!x.MustChangePassword,ct))return;
        if(!await db.Set<UserRecord>().AsNoTracking().AnyAsync(x=>x.Id==actor.UserId&&x.Status=="Active",ct))
            throw new ApiException(401,"inactive_session","会话已失效。");
        throw new ApiException(403,"password_change_required","请先修改初始密码。");
    }
}
