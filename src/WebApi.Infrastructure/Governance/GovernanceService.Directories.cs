using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Governance;
using WebApi.Contracts.Security;
using WebApi.Domain.Security;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;

namespace WebApi.Infrastructure.Governance;

public sealed partial class GovernanceService
{
    private IQueryable<UserRecord> ProjectOwnerCandidates(ScopeRef scope) =>
        db.Set<UserRecord>().AsNoTracking().Where(user=>user.Status=="Active" &&
            db.Set<UserProjectScope>().Any(grant=>grant.UserId==user.Id && grant.OrganizationId==scope.OrganizationId
                && (grant.ProjectId==null||grant.ProjectId==scope.ProjectId)));

    public async Task<PageResult<GovernanceMemberDto>> ProjectOwnerCandidatesAsync(Guid projectId,string? search,int page,ActorContext actor,CancellationToken ct)
    {
        var scope=await scopes.ProjectAsync(projectId,ct);
        await Require(actor,"project.write",scope,projectId,ct);
        var candidates=ProjectOwnerCandidates(scope).Select(u=>new GovernanceMemberDto{Id=u.Id,DisplayName=u.DisplayName,Username=u.Username,Revision=u.Revision});
        return await MemberPageAsync(candidates,search,page,ct);
    }

    public async Task<CommandResult<ProjectDto>> SetProjectOwnerAsync(Guid projectId,Guid userId,string? tag,ActorContext actor,CancellationToken ct)
    {
        var scope=await scopes.ProjectAsync(projectId,ct);
        return await commands.ExecuteAsync(actor,scope,"project.owner",async(_,token)=>{
            await Require(actor,"project.write",scope,projectId,token);
            var project=await db.Set<Project>().SingleAsync(p=>p.Id==projectId,token);
            RevisionTag.Require(tag,project.Revision);
            if(!await ProjectOwnerCandidates(scope).AnyAsync(u=>u.Id==userId,token))
                throw new ApiException(422,"invalid_project_owner","请选择在该项目具有数据范围的有效用户。");
            project.OwnerUserId=userId;project.Revision++;project.UpdatedAt=DateTimeOffset.UtcNow;
            return new CommandResult<ProjectDto>(Dto(project),RevisionTag.Format(project.Revision));
        },ct);
    }

    public async Task<CommandResult<RoleDto>> CopyRoleAsync(Guid id,SaveRoleRequest request,string? tag,ActorContext actor,CancellationToken ct)
    {
        var scope=request.OrganizationId is Guid org?new ScopeRef(org):platform;
        return await commands.ExecuteAsync(actor,scope,"role.copy",async(_,token)=>{
            await Require(actor,"role.manage",scope,id,token);
            var source=await db.Set<Role>().AsNoTracking().SingleOrDefaultAsync(r=>r.Id==id && r.OrganizationId==request.OrganizationId,token)??throw ScopeResolver.Missing();
            RevisionTag.Require(tag,source.Revision);Validate(request.Code,request.Name);
            var permissions=await (from link in db.Set<RolePermission>() join permission in db.Set<Permission>() on link.PermissionId equals permission.Id
                where link.RoleId==id orderby permission.Code select permission).ToArrayAsync(token);
            foreach(var permission in permissions)await Require(actor,permission.Code,scope,id,token);
            var copy=new Role{Code=request.Code,Name=request.Name,OrganizationId=source.OrganizationId};
            db.Add(copy);
            db.AddRange(permissions.Select(p=>new RolePermission{RoleId=copy.Id,PermissionId=p.Id}));
            return new CommandResult<RoleDto>(new(copy.Id,copy.Code,copy.Name,copy.OrganizationId,false,copy.Revision,permissions.Select(p=>p.Code).ToArray()),RevisionTag.Format(copy.Revision));
        },ct);
    }

    private async Task RequireAdminSettingsAsync(Guid org,ActorContext actor,CancellationToken ct)
    {
        await Require(actor,"user.manage",platform,org,ct);
        await Require(actor,"scope.manage",platform,org,ct);
        await Require(actor,"organization.write",new(org),org,ct);
    }

    private IQueryable<Role> AdministratorRoles()=>db.Set<Role>().Where(role=>role.IsSystem
        && (role.Code=="OrganizationAdmin" || role.OrganizationId!=null && role.Code==PermissionCatalog.ScopedOrganizationAdministrator));

    private IQueryable<GovernanceMemberDto> OrganizationMembers(Guid org)
    {
        var members=from link in db.Set<UserRole>() join role in AdministratorRoles() on link.RoleId equals role.Id
                    where (role.OrganizationId==null||role.OrganizationId==org)
                    select new{link.UserId,role.OrganizationId};
        return from user in db.Set<UserRecord>().AsNoTracking()
               let writable=db.Set<UserProjectScope>().Any(g=>g.UserId==user.Id && g.OrganizationId==org
                   && g.ProjectId==null && g.EnvironmentId==null && (g.AccessMode=="read_write" || g.AccessMode==ScopeMatcher.OrganizationAdministratorWrite))
               where user.Status=="Active"
               select new GovernanceMemberDto{Id=user.Id,DisplayName=user.DisplayName,Username=user.Username,Revision=user.Revision,
                   Administrator=writable&&members.Any(m=>m.UserId==user.Id),LegacyAdministrator=writable&&members.Any(m=>m.UserId==user.Id && m.OrganizationId==null)};
    }

    public async Task<PageResult<GovernanceMemberDto>> OrganizationAdministratorsAsync(Guid org,string? search,int page,bool candidates,ActorContext actor,CancellationToken ct)
    {
        await RequireAdminSettingsAsync(org,actor,ct);
        return await MemberPageAsync(OrganizationMembers(org).Where(u=>u.Administrator!=candidates),search,page,ct);
    }

    private static async Task<PageResult<GovernanceMemberDto>> MemberPageAsync(IQueryable<GovernanceMemberDto> query,string? search,int page,CancellationToken ct)
    {
        if(page is <1 or >1000000 || search?.Length>128)throw new ApiException(422,"invalid_member_filter","人员检索条件无效。");
        if(!string.IsNullOrWhiteSpace(search))query=query.Where(u=>u.Username.Contains(search)||u.DisplayName.Contains(search));
        var total=await query.CountAsync(ct);
        return new(await query.OrderBy(u=>u.Username).ThenBy(u=>u.Id).Skip((page-1)*25).Take(25).ToArrayAsync(ct),total,page,25);
    }

    public Task<CommandResult<OrganizationAdministratorResult>> SetOrganizationAdministratorAsync(Guid org,Guid userId,bool enabled,string? tag,ActorContext actor,CancellationToken ct) =>
        commands.ExecuteAsync(actor,new(org),"organization.administrator",async(_,token)=>{
            await RequireAdminSettingsAsync(org,actor,token);
            var user=await db.Set<UserRecord>().SingleOrDefaultAsync(u=>u.Id==userId && u.Status=="Active",token)??throw ScopeResolver.Missing();
            RevisionTag.Require(tag,user.Revision);
            var roles=await (from link in db.Set<UserRole>() join role in AdministratorRoles() on link.RoleId equals role.Id
                where link.UserId==userId && (role.OrganizationId==null || role.OrganizationId==org)
                select new{Link=link,role.OrganizationId}).ToArrayAsync(token);
            var grants=await OrganizationGrants(userId,org).ToArrayAsync(token);
            var writable=grants.Any(g=>ScopeMatcher.CanWrite(g.AccessMode));
            if(enabled && (!writable||roles.Length==0))
            {
                if(!roles.Any(r=>r.OrganizationId==org))
                {
                    var role=await OrganizationAdminRoleAsync(org,token);
                    db.Add(new UserRole{UserId=userId,RoleId=role.Id});
                }
                if(!grants.Any(g=>g.AccessMode==ScopeMatcher.OrganizationAdministratorWrite))
                    db.Add(new UserProjectScope{UserId=userId,OrganizationId=org,AccessMode=ScopeMatcher.OrganizationAdministratorWrite});
            }
            else if(!enabled && roles.Length!=0)
            {
                // Protect only the selected organization. Legacy global role holders retain their role in all others.
                await ProtectOrganizationAdministratorsAsync(userId,token,onlyOrganization:org);
                db.RemoveRange(roles.Where(r=>r.OrganizationId==org).Select(r=>r.Link));
                await RemoveAppointmentGrantsAsync(grants,token);
                // A global legacy role remains effective wherever an independent organization write grant exists.
                if(roles.Any(r=>r.OrganizationId==null))
                {
                    if(roles.Any(r=>r.OrganizationId==org) && grants.Any(g=>db.Entry(g).State!=EntityState.Deleted && g.AccessMode=="read_write"))
                        throw new ApiException(409,"independent_organization_admin","此人还有全局组织管理员角色和独立授予的本组织读写范围。请先调整全局角色或手工配置的范围，再撤任。");
                    DowngradeLegacyOrganizationGrants(grants);
                }
            }
            user.Revision++;user.UpdatedAt=DateTimeOffset.UtcNow;
            return new CommandResult<OrganizationAdministratorResult>(new(user.Id,enabled,user.Revision),RevisionTag.Format(user.Revision));
        },ct);

    private IQueryable<UserProjectScope> OrganizationGrants(Guid user,Guid org) => db.Set<UserProjectScope>()
        .Where(g=>g.UserId==user && g.OrganizationId==org && g.ProjectId==null && g.EnvironmentId==null);

    private async Task RemoveRevokedAdministratorGrantsAsync(Guid user,Guid[] retainedRoleIds,CancellationToken ct)
    {
        var removed=await (from link in db.Set<UserRole>() join role in AdministratorRoles() on link.RoleId equals role.Id
            where link.UserId==user && !retainedRoleIds.Contains(link.RoleId) select role.OrganizationId).ToArrayAsync(ct);
        if(removed.Length==0)return;
        var retained=await AdministratorRoles().Where(r=>retainedRoleIds.Contains(r.Id)).Select(r=>r.OrganizationId).ToArrayAsync(ct);
        var organizationGrants=await db.Set<UserProjectScope>().Where(g=>g.UserId==user && g.ProjectId==null && g.EnvironmentId==null).ToArrayAsync(ct);
        foreach(var group in organizationGrants.GroupBy(g=>g.OrganizationId))
        {
            var lostLocalRole=removed.Contains(group.Key);
            var lostGlobalRole=removed.Contains(null) && !retained.Contains(group.Key);
            if(!lostLocalRole && !lostGlobalRole)continue;
            var grants=group.ToArray();
            await RemoveAppointmentGrantsAsync(grants,ct);
            if(!grants.Any(g=>db.Entry(g).State!=EntityState.Deleted && ScopeMatcher.CanWrite(g.AccessMode)))
                await ProtectOrganizationAdministratorsAsync(user,ct,onlyOrganization:group.Key);
        }
    }

    private void DowngradeLegacyOrganizationGrants(UserProjectScope[] grants)
    {
        var remaining=grants.Where(g=>db.Entry(g).State!=EntityState.Deleted).ToArray();
        var writes=remaining.Where(g=>g.AccessMode=="read_write").ToArray();
        if(writes.Length==0)return;
        if(!remaining.Any(g=>g.AccessMode=="read")){writes[0].AccessMode="read";writes[0].Revision++;}
        db.RemoveRange(writes.Where(g=>g.AccessMode=="read_write"));
    }

    private async Task RemoveAppointmentGrantsAsync(UserProjectScope[] grants,CancellationToken ct)
    {
        var managed=grants.Where(g=>g.AccessMode==ScopeMatcher.OrganizationAdministratorWrite).ToArray();
        db.RemoveRange(managed);
        if(managed.Length!=0)return;
        // Earlier appointments changed an ordinary row. Its audited id and revision identify
        // the exact change. A later explicit scope edit replaces the row and has a different id.
        foreach(var grant in grants.Where(g=>g.AccessMode=="read_write"))
        {
            var change=await db.Database.SqlQuery<PreviousAdministratorGrant>($"""
                SELECT prior.field->>'AccessMode' AS "AccessMode"
                FROM audit_logs audit
                CROSS JOIN LATERAL jsonb_array_elements(audit.after_json) AS updated(entry)
                LEFT JOIN LATERAL (
                    SELECT entry->'Fields' AS field FROM jsonb_array_elements(audit.before_json) AS previous(entry)
                    WHERE entry->>'Type'='UserProjectScope' AND entry->'Fields'->>'Id'={grant.Id.ToString()}
                ) prior ON true
                WHERE audit.action='organization.administrator' AND audit.organization_id={grant.OrganizationId}
                  AND updated.entry->>'Type'='UserProjectScope' AND updated.entry->'Fields'->>'Id'={grant.Id.ToString()}
                  AND updated.entry->'Fields'->>'UserId'={grant.UserId.ToString()}
                  AND updated.entry->'Fields'->>'AccessMode'='read_write' AND updated.entry->'Fields'->'Revision'=to_jsonb({grant.Revision})
                ORDER BY audit.id DESC LIMIT 1
                """).FirstOrDefaultAsync(ct);
            if(change is null)continue;
            if(change.AccessMode is null)db.Remove(grant);
            else if(!ScopeMatcher.CanWrite(change.AccessMode)){grant.AccessMode=change.AccessMode;grant.Revision++;}
        }
    }

    private sealed class PreviousAdministratorGrant { public string? AccessMode { get; set; } }

    private async Task<Role> OrganizationAdminRoleAsync(Guid org,CancellationToken ct)
    {
        var role=await AdministratorRoles().FirstOrDefaultAsync(r=>r.OrganizationId==org,ct);
        if(role is not null)return role;
        var template=await db.Set<Role>().SingleOrDefaultAsync(r=>r.OrganizationId==null && r.Code=="OrganizationAdmin" && r.IsSystem,ct)
            ??throw new ApiException(409,"organization_admin_role_missing","平台尚未初始化内置组织管理员角色。");
        role=new Role{OrganizationId=org,Code=PermissionCatalog.ScopedOrganizationAdministrator,Name=template.Name,IsSystem=true};
        db.Add(role);
        var permissions=await db.Set<RolePermission>().Where(p=>p.RoleId==template.Id).Select(p=>p.PermissionId).ToArrayAsync(ct);
        db.AddRange(permissions.Select(p=>new RolePermission{RoleId=role.Id,PermissionId=p}));
        return role;
    }
}
