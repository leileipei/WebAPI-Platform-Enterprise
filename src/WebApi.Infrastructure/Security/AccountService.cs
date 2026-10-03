using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Security;
public sealed class AccountService(WebApiDbContext db,IPasswordHasher<UserRecord> hasher)
{
    private readonly UserRecord dummy = new();
    public async Task<UserRecord> AuthenticateAsync(LoginRequest request,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(request.Username) || request.Username.Length>128 || string.IsNullOrEmpty(request.Password) || request.Password.Length>1024)
            throw new ApiException(401,"invalid_credentials","用户名或密码错误。");
        var user=await db.Set<UserRecord>().SingleOrDefaultAsync(x=>x.Username==request.Username,ct);
        var fallback=hasher.HashPassword(dummy,"dummy-verification-input");
        var result=hasher.VerifyHashedPassword(user??dummy,user?.PasswordHash??fallback,request.Password);
        if(user is null || user.Status!="Active" || user.AuthSource!="local" || user.PasswordHash is null || result==PasswordVerificationResult.Failed)
            throw new ApiException(401,"invalid_credentials","用户名或密码错误。");
        if(result==PasswordVerificationResult.SuccessRehashNeeded) user.PasswordHash=hasher.HashPassword(user,request.Password);
        return user;
    }
    public async Task<IdentityDto> IdentityAsync(Guid id,CancellationToken ct)
    {
        var user=await db.Set<UserRecord>().AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id && x.Status=="Active",ct) ?? throw new ApiException(401,"inactive_session","会话已失效。");
        var permissions=await (from ur in db.Set<UserRole>() join rp in db.Set<RolePermission>() on ur.RoleId equals rp.RoleId join p in db.Set<Permission>() on rp.PermissionId equals p.Id where ur.UserId==id select p.Code).Distinct().ToArrayAsync(ct);
        var scopes=await db.Set<UserProjectScope>().AsNoTracking().Where(x=>x.UserId==id).Select(x=>new ScopeGrantDto(new(x.OrganizationId,x.ProjectId,x.EnvironmentId),x.AccessMode)).ToArrayAsync(ct);
        return new(id,user.Username,user.DisplayName,permissions,scopes);
    }
}
