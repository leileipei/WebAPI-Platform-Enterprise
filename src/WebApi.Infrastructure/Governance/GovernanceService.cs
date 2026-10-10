using WebApi.Domain.Governance;
using WebApi.Infrastructure.Sso;
using WebApi.Infrastructure.Settings;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Governance;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Security;
namespace WebApi.Infrastructure.Governance;
public sealed class GovernanceService(WebApiDbContext db,AuthorizationService auth,ScopeResolver scopes,AuditedCommandExecutor commands,IPasswordHasher<UserRecord> passwords,SystemSettingsReader settings,AuditAccessQuery auditAccess,LocalAdministratorGuard localAdministrators)
{
    private static readonly ScopeRef platform=new(Guid.Empty);
    private static OrganizationDto Dto(Organization x)=>new(x.Id,x.Code,x.Name,x.Status,x.Revision);
    private static ProjectDto Dto(Project x)=>new(x.Id,x.OrganizationId,x.Code,x.Name,x.Status,x.Revision,x.OwnerUserId);
    private static EnvironmentDto Dto(EnvironmentRecord x)=>new(x.Id,x.ProjectId,x.Code,x.Name,x.Status,x.IsProduction,x.SortOrder,x.ReleasePolicyId,x.DesiredConfigVersion,x.DeploymentSequence,x.Revision,x.GatewayPublicUrl,x.BasePath,x.AccessAddressRevision);
    private Task Require(ActorContext actor,string permission,ScopeRef scope,Guid id,CancellationToken ct)=>auth.RequireAsync(actor,permission,new(permission.Split('.')[0],id,scope),ct);
    public static void Validate(string code,string name,string status="Active")
    {
        if(string.IsNullOrWhiteSpace(code)||code.Length>64||!Regex.IsMatch(code,"^[A-Za-z0-9][A-Za-z0-9_.-]*$")||string.IsNullOrWhiteSpace(name)||name.Length>128||status is not ("Active" or "Disabled")) throw new ApiException(422,"invalid_fields","编码、名称或状态不合法。");
    }
    public async Task<ScopeTreeDto> GetScopeTreeAsync(ActorContext actor,CancellationToken ct=default)
    {
        var visibleOrgs=new List<OrganizationDto>();var visibleProjects=new List<ProjectDto>();var visibleEnvs=new List<EnvironmentDto>();
        var projects=await db.Set<Project>().AsNoTracking().OrderBy(x=>x.Code).ToArrayAsync(ct);
        var envs=await db.Set<EnvironmentRecord>().AsNoTracking().OrderBy(x=>x.SortOrder).ToArrayAsync(ct);
        foreach(var p in projects)
        {
            foreach(var e in envs.Where(e=>e.ProjectId==p.Id))
                if(await auth.CanAsync(actor,"environment.read",new("environment",e.Id,new(p.OrganizationId,p.Id,e.Id)),ct)) visibleEnvs.Add(Dto(e));
            if(visibleEnvs.Any(e=>e.ProjectId==p.Id)||await auth.CanAsync(actor,"project.read",new("project",p.Id,new(p.OrganizationId,p.Id)),ct)) visibleProjects.Add(Dto(p));
        }
        foreach(var org in await db.Set<Organization>().AsNoTracking().OrderBy(x=>x.Code).ToArrayAsync(ct))
            if(visibleProjects.Any(p=>p.OrganizationId==org.Id)||await auth.CanAsync(actor,"organization.read",new("organization",org.Id,new(org.Id)),ct)) visibleOrgs.Add(Dto(org));
        return new(visibleOrgs,visibleProjects,visibleEnvs);
    }
    public async Task<EnvironmentDetailDto> EnvironmentAsync(Guid id,ActorContext actor,CancellationToken ct=default)
    {
        var scope=await scopes.EnvironmentAsync(id,ct);
        if(!await auth.CanAsync(actor,"environment.read",new("environment",id,scope),ct)) throw ScopeResolver.Missing();
        var e=await db.Set<EnvironmentRecord>().AsNoTracking().SingleAsync(x=>x.Id==id,ct);
        var internalAllowed=await auth.CanAsync(actor,"environment.write",new("environment",id,scope),ct);
        return new(e.Id,e.ProjectId,e.Code,e.Name,e.Status,e.IsProduction,e.SortOrder,e.ReleasePolicyId,e.DesiredConfigVersion,e.DeploymentSequence,e.Revision,e.GatewayPublicUrl,e.BasePath,e.AccessAddressRevision,internalAllowed?e.GatewayInternalUrl:null);
    }
    private static void ApplyAccess(EnvironmentRecord e,OptionalJsonProperty<string> publicUrl,OptionalJsonProperty<string> internalUrl,OptionalJsonProperty<string> basePath,bool production,bool creating=false)
    {
        var settings=EnvironmentAccessAddressValidator.Normalize(new(publicUrl.IsSpecified?publicUrl.Value:e.GatewayPublicUrl,internalUrl.IsSpecified?internalUrl.Value:e.GatewayInternalUrl,basePath.IsSpecified?basePath.Value??"/":e.BasePath),production);
        var changed=e.GatewayPublicUrl!=settings.PublicOrigin||e.GatewayInternalUrl!=settings.InternalOrigin||e.BasePath!=settings.BasePath;
        e.GatewayPublicUrl=settings.PublicOrigin;e.GatewayInternalUrl=settings.InternalOrigin;e.BasePath=settings.BasePath;
        if(changed&&!creating)e.AccessAddressRevision=checked(e.AccessAddressRevision+1);
    }
    public async Task<OrganizationDto> OrganizationAsync(Guid id,ActorContext actor,CancellationToken ct=default)
    {
        var org=await db.Set<Organization>().AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,ct)??throw ScopeResolver.Missing();
        if(!await auth.CanAsync(actor,"organization.read",new("organization",id,new(id)),ct)) throw ScopeResolver.Missing();return Dto(org);
    }
    public Task<CommandResult<OrganizationDto>> CreateOrganizationAsync(SaveResourceRequest request,ActorContext actor,CancellationToken ct=default)=>commands.ExecuteAsync(actor,platform,"organization.create",async(_,token)=>{
        await Require(actor,"organization.write",platform,Guid.Empty,token);Validate(request.Code,request.Name,request.Status);
        var org=new Organization {Code=request.Code,Name=request.Name,Status=request.Status};db.Add(org);
        db.Add(new UserProjectScope {UserId=actor.UserId,OrganizationId=org.Id,AccessMode="read_write"});return new CommandResult<OrganizationDto>(Dto(org),RevisionTag.Format(org.Revision));
    },ct);
    public Task<CommandResult<OrganizationDto>> SaveOrganizationAsync(Guid id,SaveResourceRequest request,string? tag,ActorContext actor,CancellationToken ct=default)=>commands.ExecuteAsync(actor,new(id),"organization.update",async(_,token)=>{
        var org=await db.Set<Organization>().SingleOrDefaultAsync(x=>x.Id==id,token)??throw ScopeResolver.Missing();await Require(actor,"organization.write",new(id),id,token);RevisionTag.Require(tag,org.Revision);Validate(request.Code,request.Name,request.Status);
        if(request.Status=="Disabled" && await ActiveReleaseAsync(db.Set<EnvironmentRecord>().Where(e=>db.Set<Project>().Any(p=>p.Id==e.ProjectId&&p.OrganizationId==id)).Select(e=>e.Id),token)) throw new ApiException(409,"release_in_progress","组织中存在进行中的发布。");
        org.Code=request.Code;org.Name=request.Name;org.Status=request.Status;org.Revision++;org.UpdatedAt=DateTimeOffset.UtcNow;return new CommandResult<OrganizationDto>(Dto(org),RevisionTag.Format(org.Revision));
    },ct);
    public async Task<CommandResult<ProjectDto>> CreateProjectAsync(Guid orgId,SaveResourceRequest request,ActorContext actor,CancellationToken ct=default)=>await commands.ExecuteAsync(actor,new(orgId),"project.create",async(_,token)=>{
        await Require(actor,"project.write",new(orgId),orgId,token);Validate(request.Code,request.Name,request.Status);var p=new Project {OrganizationId=orgId,Code=request.Code,Name=request.Name,Status=request.Status,OwnerUserId=actor.UserId};db.Add(p);return new CommandResult<ProjectDto>(Dto(p),RevisionTag.Format(p.Revision));
    },ct);
    public async Task<CommandResult<ProjectDto>> SaveProjectAsync(Guid id,SaveResourceRequest request,string? tag,ActorContext actor,CancellationToken ct=default)
    {
        var scope=await scopes.ProjectAsync(id,ct);return await commands.ExecuteAsync(actor,scope,"project.update",async(_,token)=>{
            var p=await db.Set<Project>().SingleAsync(x=>x.Id==id,token);await Require(actor,"project.write",scope,id,token);RevisionTag.Require(tag,p.Revision);Validate(request.Code,request.Name,request.Status);
            if(request.Status=="Disabled" && await ActiveReleaseAsync(db.Set<EnvironmentRecord>().Where(e=>e.ProjectId==id).Select(e=>e.Id),token)) throw new ApiException(409,"release_in_progress","项目中存在进行中的发布。");
            p.Code=request.Code;p.Name=request.Name;p.Status=request.Status;p.Revision++;p.UpdatedAt=DateTimeOffset.UtcNow;return new CommandResult<ProjectDto>(Dto(p),RevisionTag.Format(p.Revision));
        },ct);
    }
    public async Task<CommandResult<EnvironmentDto>> CreateEnvironmentAsync(Guid projectId,CreateEnvironmentRequest request,ActorContext actor,CancellationToken ct=default)
    {
        var scope=await scopes.ProjectAsync(projectId,ct);return await commands.ExecuteAsync(actor,scope,"environment.create",async(_,token)=>{
            await Require(actor,"environment.write",scope,projectId,token);Validate(request.Code,request.Name);await ValidateFlowAsync(request.ReleasePolicyId,scope.OrganizationId,token);
            if(request.Code.Length>32||request.Name.Length>64) throw new ApiException(422,"invalid_fields","环境编码或名称过长。");
            var e=new EnvironmentRecord {ProjectId=projectId,Code=request.Code,Name=request.Name,IsProduction=request.IsProduction,SortOrder=request.SortOrder,ReleasePolicyId=request.ReleasePolicyId};ApplyAccess(e,request.GatewayPublicUrl,request.GatewayInternalUrl,request.BasePath,request.IsProduction,true);db.Add(e);return new CommandResult<EnvironmentDto>(Dto(e),RevisionTag.Format(e.Revision));
        },ct);
    }
    public async Task<CommandResult<EnvironmentDto>> SaveEnvironmentAsync(Guid id,UpdateEnvironmentRequest request,string? tag,ActorContext actor,CancellationToken ct=default)
    {
        var scope=await scopes.EnvironmentAsync(id,ct);return await commands.ExecuteAsync(actor,scope,"environment.update",async(_,token)=>{
            await Require(actor,"environment.write",scope,id,token);var e=await db.Set<EnvironmentRecord>().SingleAsync(x=>x.Id==id,token);RevisionTag.Require(tag,e.Revision);Validate(request.Code,request.Name,request.Status);await ValidateFlowAsync(request.ReleasePolicyId,scope.OrganizationId,token);
            if(request.Code.Length>32||request.Name.Length>64) throw new ApiException(422,"invalid_fields","环境编码或名称过长。");
            if((request.Status=="Disabled"||request.IsProduction!=e.IsProduction||request.ReleasePolicyId!=e.ReleasePolicyId) && await ActiveReleaseAsync(db.Set<EnvironmentRecord>().Where(x=>x.Id==id).Select(x=>x.Id),token)) throw new ApiException(409,"release_in_progress","发布进行中，不能停用环境或更改审批规则。");
            ApplyAccess(e,request.GatewayPublicUrl,request.GatewayInternalUrl,request.BasePath,request.IsProduction);
            e.Code=request.Code;e.Name=request.Name;e.Status=request.Status;e.IsProduction=request.IsProduction;e.SortOrder=request.SortOrder;e.ReleasePolicyId=request.ReleasePolicyId;e.Revision++;return new CommandResult<EnvironmentDto>(Dto(e),RevisionTag.Format(e.Revision));
        },ct);
    }
    private Task<bool> ActiveReleaseAsync(IQueryable<Guid> environments,CancellationToken ct)=>db.Set<ReleaseRecord>().AnyAsync(r=>environments.Contains(r.EnvironmentId)&&(r.Status=="WaitingApproval"||r.Status=="Ready"||r.Status=="Building"||r.Status=="Publishing"),ct);
    private async Task ValidateFlowAsync(Guid? id,Guid org,CancellationToken ct) {if(id is not null && !await db.Set<ApprovalFlow>().AnyAsync(f=>f.Id==id&&f.OrganizationId==org&&f.Enabled,ct)) throw new ApiException(422,"invalid_approval_flow","审批流程不存在或属于其他组织。");}
    public async Task<PageResult<UserDto>> UsersAsync(ActorContext actor,int page=1,int pageSize=50,CancellationToken ct=default)
    {
        await Require(actor,"user.manage",platform,Guid.Empty,ct);page=Math.Clamp(page,1,1000000);pageSize=Math.Clamp(pageSize,1,100);var query=db.Set<UserRecord>().AsNoTracking();var total=await query.CountAsync(ct);var list=await query.OrderBy(x=>x.Username).Skip((page-1)*pageSize).Take(pageSize).ToArrayAsync(ct);
        var result=new List<UserDto>();foreach(var u in list) result.Add(await UserDtoAsync(u,ct));return new(result,total,page,pageSize);
    }
    private async Task<UserDto> UserDtoAsync(UserRecord u,CancellationToken ct)=>new(u.Id,u.Username,u.DisplayName,u.Email,u.Status,u.AuthSource,u.Revision,await db.Set<UserRole>().Where(r=>r.UserId==u.Id).Select(r=>r.RoleId).ToArrayAsync(ct));
    public Task<CommandResult<UserDto>> CreateUserAsync(CreateUserRequest request,ActorContext actor,CancellationToken ct=default)=>commands.ExecuteAsync(actor,platform,"user.create",async(_,token)=>{
        await Require(actor,"user.manage",platform,Guid.Empty,token);if(string.IsNullOrWhiteSpace(request.Username)||request.Username.Length>128||!Regex.IsMatch(request.Username,"^[A-Za-z0-9][A-Za-z0-9_.@+-]*$")||string.IsNullOrWhiteSpace(request.DisplayName)||request.DisplayName.Length>128) throw new ApiException(422,"invalid_fields","用户名或显示名称不合法。");
        var policy=await settings.SecurityAsync(token);
        var complex=policy.PasswordComplexity switch {"LettersAndDigits"=>request.Password.Any(char.IsLetter)&&request.Password.Any(char.IsDigit),"UpperLowerDigitSpecial"=>request.Password.Any(char.IsUpper)&&request.Password.Any(char.IsLower)&&request.Password.Any(char.IsDigit)&&request.Password.Any(c=>!char.IsLetterOrDigit(c)&&!char.IsWhiteSpace(c)),_=>true};
        if(request.Password.Length<policy.PasswordMinLength||request.Password.Length>1024||!complex||request.Email?.Length>256) throw new ApiException(422,"invalid_fields","密码不符合当前长度或复杂度策略，邮箱不超过256字符。");
        var user=new UserRecord {Username=request.Username,DisplayName=request.DisplayName,Email=request.Email,SecurityStamp=Guid.NewGuid().ToString("N")};user.PasswordHash=passwords.HashPassword(user,request.Password);db.Add(user);return new CommandResult<UserDto>(new(user.Id,user.Username,user.DisplayName,user.Email,user.Status,user.AuthSource,user.Revision,[]),RevisionTag.Format(user.Revision));
    },ct);
    public Task<CommandResult<UserDto>> SaveUserAsync(Guid id,UpdateUserRequest request,string? tag,ActorContext actor,CancellationToken ct=default)=>commands.ExecuteAsync(actor,platform,"user.update",async(_,token)=>{
        await Require(actor,"user.manage",platform,id,token);var user=await db.Set<UserRecord>().SingleOrDefaultAsync(x=>x.Id==id,token)??throw ScopeResolver.Missing();RevisionTag.Require(tag,user.Revision);
        if(string.IsNullOrWhiteSpace(request.DisplayName)||request.DisplayName.Length>128||request.Status is not ("Active" or "Disabled")||request.Email?.Length>256) throw new ApiException(422,"invalid_fields","用户字段不合法。");
        if(request.Status=="Disabled") await ProtectAdminAsync(id,token);
        user.DisplayName=request.DisplayName;user.Email=request.Email;user.Status=request.Status;user.Revision++;user.UpdatedAt=DateTimeOffset.UtcNow;if(user.Status=="Disabled") user.SecurityStamp=Guid.NewGuid().ToString("N");return new CommandResult<UserDto>(await UserDtoAsync(user,token),RevisionTag.Format(user.Revision));
    },ct);
    private async Task ProtectAdminAsync(Guid id,CancellationToken ct)
    {
        var admins=from u in db.Set<UserRecord>() join ur in db.Set<UserRole>() on u.Id equals ur.UserId join r in db.Set<Role>() on ur.RoleId equals r.Id where u.Status=="Active"&&r.IsSystem&&r.OrganizationId==null&&r.Code=="PlatformAdmin" select u.Id;
        if(await admins.AnyAsync(x=>x==id,ct)&&!await admins.AnyAsync(x=>x!=id,ct)) throw new ApiException(409,"last_platform_admin","不能停用或撤销最后一位平台管理员。");
        await localAdministrators.ProtectRemovalAsync(id,ct);
    }
    public Task<CommandResult<UserDto>> AssignRolesAsync(Guid id,AssignRolesRequest request,string? tag,ActorContext actor,CancellationToken ct=default)=>commands.ExecuteAsync(actor,platform,"user.roles",async(_,token)=>{
        await Require(actor,"user.manage",platform,id,token);var user=await db.Set<UserRecord>().SingleOrDefaultAsync(x=>x.Id==id,token)??throw ScopeResolver.Missing();RevisionTag.Require(tag,user.Revision);
        var ids=request.RoleIds.Distinct().ToArray();var roles=await db.Set<Role>().Where(r=>ids.Contains(r.Id)).ToArrayAsync(token);if(roles.Length!=ids.Length) throw new ApiException(422,"invalid_role","角色不存在。");
        if(!roles.Any(r=>r.Code=="PlatformAdmin"&&r.IsSystem&&r.OrganizationId==null)) await ProtectAdminAsync(id,token);
        db.RemoveRange(await db.Set<UserRole>().Where(x=>x.UserId==id&&!ids.Contains(x.RoleId)).ToArrayAsync(token));var old=await db.Set<UserRole>().Where(x=>x.UserId==id).Select(x=>x.RoleId).ToArrayAsync(token);
        foreach(var role in ids.Except(old)) db.Add(new UserRole {UserId=id,RoleId=role});user.Revision++;return new CommandResult<UserDto>(new(user.Id,user.Username,user.DisplayName,user.Email,user.Status,user.AuthSource,user.Revision,ids),RevisionTag.Format(user.Revision));
    },ct);
    public async Task<IReadOnlyList<ScopeGrantDto>> UserScopesAsync(Guid id,ActorContext actor,CancellationToken ct=default)
    {await Require(actor,"scope.manage",platform,id,ct);return await db.Set<UserProjectScope>().Where(x=>x.UserId==id).Select(x=>new ScopeGrantDto(new(x.OrganizationId,x.ProjectId,x.EnvironmentId),x.AccessMode)).ToArrayAsync(ct);}
    public Task<CommandResult<IReadOnlyList<ScopeGrantDto>>> SaveScopesAsync(Guid id,SaveScopesRequest request,string? tag,ActorContext actor,CancellationToken ct=default)=>commands.ExecuteAsync(actor,platform,"user.scopes",async(_,token)=>{
        await Require(actor,"scope.manage",platform,id,token);var user=await db.Set<UserRecord>().SingleOrDefaultAsync(x=>x.Id==id,token)??throw ScopeResolver.Missing();RevisionTag.Require(tag,user.Revision);
        foreach(var grant in request.Scopes)
        {
            var s=grant.Scope;if(grant.AccessMode is not ("read" or "read_write")||s.OrganizationId==Guid.Empty||s.EnvironmentId is not null&&s.ProjectId is null||!await db.Set<Organization>().AnyAsync(o=>o.Id==s.OrganizationId,token)) throw new ApiException(422,"invalid_scope","数据范围不合法。");
            if(s.ProjectId is Guid p && (await scopes.ProjectAsync(p,token)).OrganizationId!=s.OrganizationId) throw new ApiException(422,"invalid_scope","项目不属于组织。");
            if(s.EnvironmentId is Guid e && (await scopes.EnvironmentAsync(e,token))!=s) throw new ApiException(422,"invalid_scope","环境不属于项目。");
        }
        db.RemoveRange(await db.Set<UserProjectScope>().Where(x=>x.UserId==id).ToArrayAsync(token));foreach(var g in request.Scopes.Distinct()) db.Add(new UserProjectScope {UserId=id,OrganizationId=g.Scope.OrganizationId,ProjectId=g.Scope.ProjectId,EnvironmentId=g.Scope.EnvironmentId,AccessMode=g.AccessMode});user.Revision++;
        return new CommandResult<IReadOnlyList<ScopeGrantDto>>(request.Scopes,RevisionTag.Format(user.Revision));
    },ct);
    public async Task<IReadOnlyList<RoleDto>> RolesAsync(ActorContext actor,CancellationToken ct=default)
    {await Require(actor,"role.manage",platform,Guid.Empty,ct);var list=await db.Set<Role>().AsNoTracking().OrderBy(x=>x.Name).ToArrayAsync(ct);var result=new List<RoleDto>();foreach(var r in list) result.Add(await RoleDtoAsync(r,ct));return result;}
    private async Task<RoleDto> RoleDtoAsync(Role r,CancellationToken ct)=>new(r.Id,r.Code,r.Name,r.OrganizationId,r.IsSystem,r.Revision,await(from rp in db.Set<RolePermission>() join p in db.Set<Permission>() on rp.PermissionId equals p.Id where rp.RoleId==r.Id select p.Code).ToArrayAsync(ct));
    public Task<CommandResult<RoleDto>> CreateRoleAsync(SaveRoleRequest request,ActorContext actor,CancellationToken ct=default)=>commands.ExecuteAsync(actor,request.OrganizationId is Guid o?new(o):platform,"role.create",async(_,token)=>{
        var scope=request.OrganizationId is Guid org?new ScopeRef(org):platform;await Require(actor,"role.manage",scope,Guid.Empty,token);Validate(request.Code,request.Name);
        var role=new Role {Code=request.Code,Name=request.Name,OrganizationId=request.OrganizationId,IsSystem=false};db.Add(role);return new CommandResult<RoleDto>(new(role.Id,role.Code,role.Name,role.OrganizationId,false,role.Revision,[]),RevisionTag.Format(role.Revision));
    },ct);
    public async Task<CommandResult<RoleDto>> SaveRoleAsync(Guid id,SaveRoleRequest request,string? tag,ActorContext actor,CancellationToken ct=default)
    {
        var existing=await db.Set<Role>().AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,ct)??throw ScopeResolver.Missing();var scope=existing.OrganizationId is Guid org?new ScopeRef(org):platform;
        return await commands.ExecuteAsync(actor,scope,"role.update",async(_,token)=>{await Require(actor,"role.manage",scope,id,token);var r=await db.Set<Role>().SingleAsync(x=>x.Id==id,token);RevisionTag.Require(tag,r.Revision);if(r.IsSystem) throw new ApiException(409,"system_role_immutable","系统角色请复制后修改。");if(request.OrganizationId!=r.OrganizationId) throw new ApiException(422,"role_scope_immutable","不能改变角色所属组织。");Validate(request.Code,request.Name);r.Code=request.Code;r.Name=request.Name;r.Revision++;return new CommandResult<RoleDto>(await RoleDtoAsync(r,token),RevisionTag.Format(r.Revision));},ct);
    }
    public async Task<CommandResult<RoleDto>> PermissionsAsync(Guid id,AssignPermissionsRequest request,string? tag,ActorContext actor,CancellationToken ct=default)
    {
        var existing=await db.Set<Role>().AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,ct)??throw ScopeResolver.Missing();var scope=existing.OrganizationId is Guid org?new ScopeRef(org):platform;
        return await commands.ExecuteAsync(actor,scope,"role.permissions",async(_,token)=>{
            await Require(actor,"role.manage",scope,id,token);var r=await db.Set<Role>().SingleAsync(x=>x.Id==id,token);RevisionTag.Require(tag,r.Revision);if(r.IsSystem) throw new ApiException(409,"system_role_immutable","系统角色请复制后配置权限。");
            var codes=request.Permissions.Distinct().ToArray();var permissions=await db.Set<Permission>().Where(p=>codes.Contains(p.Code)).ToArrayAsync(token);if(permissions.Length!=codes.Length) throw new ApiException(422,"invalid_permission","存在未知权限。");
            // An organization administrator cannot grant a functional permission they do not hold in this scope.
            foreach(var code in codes) await Require(actor,code,scope,id,token);
            var assigned=await db.Set<RolePermission>().Where(x=>x.RoleId==id).ToArrayAsync(token);var ids=permissions.Select(p=>p.Id).ToArray();db.RemoveRange(assigned.Where(x=>!ids.Contains(x.PermissionId)));foreach(var p in permissions.Where(p=>!assigned.Any(a=>a.PermissionId==p.Id))) db.Add(new RolePermission {RoleId=id,PermissionId=p.Id});r.Revision++;
            return new CommandResult<RoleDto>(new(r.Id,r.Code,r.Name,r.OrganizationId,r.IsSystem,r.Revision,codes),RevisionTag.Format(r.Revision));
        },ct);
    }
    public async Task DeleteRoleAsync(Guid id,string? tag,ActorContext actor,CancellationToken ct=default)
    {
        var r=await db.Set<Role>().AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,ct)??throw ScopeResolver.Missing();var scope=r.OrganizationId is Guid org?new ScopeRef(org):platform;
        await commands.ExecuteAsync(actor,scope,"role.delete",async(_,token)=>{await Require(actor,"role.manage",scope,id,token);var role=await db.Set<Role>().SingleAsync(x=>x.Id==id,token);RevisionTag.Require(tag,role.Revision);if(role.IsSystem||await db.Set<UserRole>().AnyAsync(x=>x.RoleId==id,token)) throw new ApiException(409,"role_in_use","系统角色或已分配角色不能删除。");db.RemoveRange(await db.Set<RolePermission>().Where(x=>x.RoleId==id).ToArrayAsync(token));db.Remove(role);return true;},ct);
    }
    public async Task<object> PermissionDictionaryAsync(ActorContext actor,CancellationToken ct=default) {await Require(actor,"role.manage",platform,Guid.Empty,ct);return await db.Set<Permission>().AsNoTracking().OrderBy(x=>x.Module).ThenBy(x=>x.Code).Select(x=>new {x.Id,x.Code,x.Module,x.Name,x.Description}).ToArrayAsync(ct);}
    public async Task<AuditPageDto> AuditAsync(ActorContext actor,int page=1,int pageSize=50,CancellationToken ct=default,string? traceId=null,string? resourceId=null)
    {
        page=Math.Clamp(page,1,1000000);pageSize=Math.Clamp(pageSize,1,100);var query=await auditAccess.QueryAsync(actor,traceId,resourceId,ct);
        return new(await query.OrderByDescending(x=>x.Id).Skip((page-1)*pageSize).Take(pageSize).Select(x=>new AuditDto(x.Id,x.OrganizationId,x.ProjectId,x.EnvironmentId,x.UserId,x.Action,x.ResourceType,x.ResourceId,x.BeforeJson,x.AfterJson,x.Ip==null?null:x.Ip.ToString(),x.TraceId,x.CreatedAt)).ToArrayAsync(ct),await query.CountAsync(ct),page,pageSize,(await settings.AuditAsync(ct)).AuditExportEnabled);
    }
}
