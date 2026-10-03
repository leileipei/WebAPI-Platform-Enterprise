using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Migrator;
public static class BootstrapAccounts
{
    public static async Task RunAsync(WebApiDbContext db,string username,string secretFile,CancellationToken ct=default)
    {
        if(string.IsNullOrWhiteSpace(username) || username.Length>128 || string.IsNullOrWhiteSpace(secretFile) || !File.Exists(secretFile))
            throw new InvalidOperationException("Explicit administrator name and password file are required.");
        if(!OperatingSystem.IsWindows() && (File.GetUnixFileMode(secretFile) & (UnixFileMode.GroupRead|UnixFileMode.GroupWrite|UnixFileMode.GroupExecute|UnixFileMode.OtherRead|UnixFileMode.OtherWrite|UnixFileMode.OtherExecute))!=0)
            throw new InvalidOperationException("Administrator password file must be accessible only to its owner.");
        var password=(await File.ReadAllTextAsync(secretFile,ct)).TrimEnd('\r','\n');
        if(password.Length<16 || password.Length>1024) throw new InvalidOperationException("Administrator password must contain 16 to 1024 characters.");
        await WebApi.Infrastructure.Governance.PermissionCatalog.SeedAsync(db,ct);
        await using var tx=await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(8901201)",ct);
        if(await db.Set<UserRecord>().AnyAsync(x=>x.Username==username,ct)) { await tx.CommitAsync(ct); return; }
        var role=await db.Set<Role>().SingleOrDefaultAsync(x=>x.Code=="PlatformAdmin" && x.OrganizationId==null,ct);
        if(role is null) {role=new Role { Code="PlatformAdmin",Name="平台管理员",IsSystem=true };db.Add(role);}
        string[] codes=["system.manage","user.manage","role.manage","scope.manage","organization.read","organization.write","project.read","project.write","environment.read","environment.write"];
        foreach(var code in codes)
        {
            var permission=await db.Set<Permission>().SingleOrDefaultAsync(x=>x.Code==code,ct);
            if(permission is null) {permission=new Permission {Code=code,Module=code.Split('.')[0],Name=code,Description="首期平台管理权限"};db.Add(permission);}
            if(!await db.Set<RolePermission>().AnyAsync(x=>x.RoleId==role.Id && x.PermissionId==permission.Id,ct)) db.Add(new RolePermission {RoleId=role.Id,PermissionId=permission.Id});
        }
        var user=new UserRecord {Username=username,DisplayName="平台管理员",SecurityStamp=Guid.NewGuid().ToString("N")};
        user.PasswordHash=new PasswordHasher<UserRecord>().HashPassword(user,password);db.Add(user);db.Add(new UserRole {UserId=user.Id,RoleId=role.Id});
        db.Add(new AuditLog {UserId=user.Id,Action="platform.bootstrap",ResourceType="user",ResourceId=user.Id.ToString(),TraceId=Guid.NewGuid().ToString("N")});
        await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);
    }
}
