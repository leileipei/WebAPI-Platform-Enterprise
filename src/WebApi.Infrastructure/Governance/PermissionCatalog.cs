using Microsoft.EntityFrameworkCore;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Governance;
public static class PermissionCatalog
{
    public static readonly string[] Codes=[
        "organization.read","organization.write","project.read","project.write","environment.read","environment.write",
        "api.read","api.create","api.edit","api.version.read","api.version.write","api.schema.read","api.schema.write",
        "route.read","route.write","cluster.read","cluster.write","policy.read","policy.write",
        "app.read","app.write","credential.manage","app.permission.manage",
        "pipeline.read","pipeline.manage","pipeline.run",
        "release.test.record","release.test.accept","release.verify","release.read","release.create","release.publish","release.rollback","approval.act","api.approve",
        "gateway.read","gateway.operate","gateway.config.read","audit.read","user.manage","role.manage","scope.manage","system.manage",
        "metrics.read","log.read","trace.read","alert.read","alert.operate","alert.rule.manage","system.sso.manage"
    ];
    public static async Task SeedAsync(WebApiDbContext db,CancellationToken ct=default)
    {
        await using var tx=await db.Database.BeginTransactionAsync(ct);await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(8901202)",ct);
        var permissions=await db.Set<Permission>().ToDictionaryAsync(x=>x.Code,ct);
        foreach(var code in Codes) if(!permissions.ContainsKey(code)) {var p=new Permission {Code=code,Module=code.Split('.')[0],Name=code,Description="源文档平台功能权限"};permissions.Add(code,p);db.Add(p);}
        var read=Codes.Where(c=>c.EndsWith(".read",StringComparison.Ordinal)&&!c.StartsWith("pipeline.",StringComparison.Ordinal)).ToArray();
        Dictionary<string,(string Name,string[] Permissions)> roles=new() {
            ["PlatformAdmin"]=("平台管理员",Codes),
            ["OrganizationAdmin"]=("组织管理员",Codes.Except(["pipeline.read","pipeline.manage","pipeline.run","release.test.record","release.test.accept","release.verify","system.manage","system.sso.manage","user.manage","scope.manage"]).ToArray()),
            ["ProjectAdmin"]=("项目管理员",Codes.Except(["pipeline.read","pipeline.manage","pipeline.run","release.test.record","release.test.accept","release.verify","organization.write","system.manage","system.sso.manage","user.manage","role.manage","scope.manage"]).ToArray()),
            ["ApiDeveloper"]=("API开发人员",read.Concat(["api.create","api.edit","api.version.write","api.schema.write","route.write","cluster.write","policy.write","release.create","release.publish"]).Distinct().ToArray()),
            ["ApiApprover"]=("API审批人员",read.Concat(["approval.act","api.approve"]).ToArray()),
            ["SecurityReviewer"]=("安全审核人员",read.Concat(["approval.act"]).ToArray()),
            ["Operator"]=("运维人员",read.Concat(["cluster.write","gateway.operate","release.publish","release.rollback","alert.operate"]).ToArray()),
            ["Auditor"]=("审计人员",read),["Viewer"]=("只读用户",read.Except(["audit.read","log.read","trace.read"]).ToArray())
        };
        foreach(var (code,definition) in roles)
        {
            var role=await db.Set<Role>().SingleOrDefaultAsync(r=>r.Code==code&&r.OrganizationId==null,ct);
            if(role is null) {role=new Role {Code=code,Name=definition.Name,IsSystem=true};db.Add(role);}
            if(!role.IsSystem) throw new InvalidOperationException("Built-in role code is occupied by a custom role.");
            var assigned=await db.Set<RolePermission>().Where(r=>r.RoleId==role.Id).Select(r=>r.PermissionId).ToArrayAsync(ct);
            foreach(var permission in definition.Permissions) if(!assigned.Contains(permissions[permission].Id)) db.Add(new RolePermission {RoleId=role.Id,PermissionId=permissions[permission].Id});
        }
        await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);
    }
}
