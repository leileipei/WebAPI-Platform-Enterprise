using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Security;
using WebApi.Domain.Security;
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
        var platformAdmins = AuthorizationService.PlatformAdministratorUserIds(db);
        var user = await db.Set<UserRecord>().AsNoTracking().Where(u => u.Id == id && u.Status == "Active")
            .Select(u => new { u.Username, u.DisplayName, u.AuthSource, u.MustChangePassword, PlatformAdmin = platformAdmins.Contains(u.Id) })
            .SingleOrDefaultAsync(ct) ?? throw new ApiException(401,"inactive_session","会话已失效。");
        if(user.MustChangePassword&&user.AuthSource=="local")return new(id,user.Username,user.DisplayName,[],[],user.AuthSource,true,[]);
        var assigned = await (from membership in db.Set<UserRole>()
                              join role in db.Set<Role>() on membership.RoleId equals role.Id
                              join grant in db.Set<RolePermission>() on role.Id equals grant.RoleId
                              join permission in db.Set<Permission>() on grant.PermissionId equals permission.Id
                              where membership.UserId == id
                              select new { role.OrganizationId, permission.Code }).Distinct().ToArrayAsync(ct);
        var rolePermissions = assigned.GroupBy(item => item.OrganizationId).OrderBy(group => group.Key)
            .Select(group => new OrganizationRolePermissionsDto(group.Key, group.Select(item => item.Code).Order(StringComparer.Ordinal).ToArray()))
            .ToArray();
        var permissions = assigned.Select(item => item.Code).Distinct().Order(StringComparer.Ordinal).ToArray();
        var rows=await db.Set<UserProjectScope>().AsNoTracking().Where(x=>x.UserId==id).Select(x=>new
            {x.OrganizationId,x.ProjectId,x.EnvironmentId,Mode=x.AccessMode==ScopeMatcher.OrganizationAdministratorWrite?"read_write":x.AccessMode}).Distinct().ToArrayAsync(ct);
        var scopes=rows.Select(x=>new ScopeGrantDto(new(x.OrganizationId,x.ProjectId,x.EnvironmentId),x.Mode)).ToArray();
        return new(id,user.Username,user.DisplayName,permissions,scopes,user.AuthSource,false,rolePermissions,user.PlatformAdmin);
    }
}
