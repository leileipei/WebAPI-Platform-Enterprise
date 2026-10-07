using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Policies;
using WebApi.Contracts.Security;
using WebApi.Domain.Policies;
using WebApi.Infrastructure.Commands;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Security;
namespace WebApi.Infrastructure.Policies;
public sealed class PolicyService(WebApiDbContext db,AuthorizationService auth,PolicyAccess access,AuditedCommandExecutor commands,IdempotentCommandExecutor idempotency,CommandRequestContext requestContext,PolicyReferenceService references,JwtApplicationBindingService mappings,GatewayPolicyDeploymentRules deployment)
{
    public static PolicyDto Dto(Policy p)=>new(p.Id,p.OrganizationId,p.ProjectId,p.Name,p.Type,p.Config,p.Enabled,p.VersionNo);
    public async Task<PageResult<PolicyDto>> ListAsync(PolicyScopeRequest requested,ActorContext actor,int page,int pageSize,string? type,string? search,bool? enabled,CancellationToken ct=default)
    {
        var scope=await access.ResolveAsync(requested,ct);if(!await access.CanReadAsync(scope,actor,ct)) throw ScopeResolver.Missing();
        if(type is not null&&type is not ("authentication" or "timeout" or "rate_limit" or "circuit_breaker" or "retry" or "cache")||search?.Length>128) throw new ApiException(422,"invalid_policy_filter","策略筛选条件不合法。");
        var query=db.Set<Policy>().AsNoTracking().Where(p=>p.OrganizationId==scope.OrganizationId&&(scope.ProjectId==null||p.ProjectId==null||p.ProjectId==scope.ProjectId));
        if(type is not null) query=query.Where(p=>p.Type==type);if(enabled is bool on) query=query.Where(p=>p.Enabled==on);if(!string.IsNullOrWhiteSpace(search)) query=query.Where(p=>p.Name.Contains(search));
        var visible=new List<PolicyDto>();foreach(var p in await query.OrderBy(p=>p.Name).ThenBy(p=>p.Id).ToArrayAsync(ct)) if(await access.CanReadAsync(new(p.OrganizationId,p.ProjectId),actor,ct)) visible.Add(Dto(p));
        return Pagination.Slice(visible,page,pageSize);
    }
    public async Task<PolicyDto> GetAsync(Guid id,ActorContext actor,CancellationToken ct=default)
    {var p=await db.Set<Policy>().AsNoTracking().SingleOrDefaultAsync(p=>p.Id==id,ct)??throw ScopeResolver.Missing();if(!await access.CanReadAsync(new(p.OrganizationId,p.ProjectId),actor,ct)) throw ScopeResolver.Missing();return Dto(p);}
    public async Task<CommandResult<PolicyDto>> SaveAsync(PolicyScopeRequest requested,Guid? id,SavePolicyRequest request,string? ifMatch,ActorContext actor,CancellationToken ct=default)
    {
        var scope=await access.ResolveAsync(requested,ct);
        return await commands.ExecuteAsync(actor,scope,"policy.save",async(_,token)=>{
            if(id is Guid existing) {var current=await GetAsync(existing,actor,token);if(current.OrganizationId!=scope.OrganizationId||current.ProjectId!=scope.ProjectId) throw ScopeResolver.Missing();}
            await auth.RequireAsync(actor,"policy.write",new("policy",id??Guid.Empty,scope),token);
            var name=request.Name.Trim();if(name.Length is <1 or >128) throw new ApiException(422,"invalid_policy_name","策略名称长度须为1至128。");
            var config=PolicyConfigurationValidator.Normalize(request.Type,request.Config);await ValidateConfigurationAsync(scope,request.Type,config,actor,token);var normalized=request with {Name=name,Config=config};
            return await idempotency.ExecuteAsync(new(actor.UserId,scope,"policy.save",requestContext.IdempotencyKey),CanonicalJson.Serialize(new {id,request=normalized,ifMatch}),async inner=>{
                var p=id is Guid actual?await db.Set<Policy>().SingleOrDefaultAsync(p=>p.Id==actual,inner)??throw ScopeResolver.Missing():new Policy {OrganizationId=scope.OrganizationId,ProjectId=scope.ProjectId};
                if(id is not null) {PolicyPreconditions.Require(ifMatch,p.VersionNo);if(p.Type!=request.Type) throw new ApiException(422,"immutable_policy_type","策略类型不可修改。");}
                if(name.StartsWith("RouteAuth-",StringComparison.OrdinalIgnoreCase)&&(id is null||name!=p.Name)) throw new ApiException(422,"reserved_policy_name","该名称前缀由路由认证配置保留。");
                if(id is null) db.Add(p);else p.VersionNo++;
                p.Name=name;p.Type=request.Type;p.Config=config;p.Enabled=request.Enabled;p.UpdatedAt=DateTimeOffset.UtcNow;
                if(id is not null) foreach(var routeId in await db.Set<RoutePolicyBinding>().Where(b=>b.PolicyId==p.Id).Select(b=>b.RouteId).ToArrayAsync(inner)) {
                    var bindings=await RoutePolicyService.LoadAsync(db,routeId,inner);
                    var timeout=await db.Set<ApiRoute>().Where(r=>r.Id==routeId).Select(r=>r.TimeoutMs).SingleAsync(inner);
                    PolicyBindingRules.Validate(bindings.Select(b=>b.PolicyId==p.Id?b with {Config=config,Enabled=request.Enabled}:b).ToArray(),true,timeout);
                }
                return new CommandResult<PolicyDto>(Dto(p),RevisionTag.Format(p.VersionNo));
            },token);
        },ct);
    }
    public async Task<CommandResult<PolicyDto>> CopyAsync(Guid id,CopyPolicyRequest request,ActorContext actor,CancellationToken ct=default)
    {var original=await GetAsync(id,actor,ct);return await SaveAsync(new(original.OrganizationId,request.TargetProjectId),null,new(request.Name,original.Type,original.Config,original.Enabled),null,actor,ct);}
    public async Task DeleteAsync(Guid id,string? ifMatch,ActorContext actor,CancellationToken ct=default)
    {
        var original=await GetAsync(id,actor,ct);var scope=new ScopeRef(original.OrganizationId,original.ProjectId);
        await commands.ExecuteAsync(actor,scope,"policy.delete",async(_,token)=>{
            await auth.RequireAsync(actor,"policy.write",new("policy",id,scope),token);
            return await idempotency.ExecuteAsync(new(actor.UserId,scope,"policy.delete",requestContext.IdempotencyKey),CanonicalJson.Serialize(new {id,ifMatch}),async inner=>{
                var p=await db.Set<Policy>().SingleAsync(p=>p.Id==id,inner);PolicyPreconditions.Require(ifMatch,p.VersionNo);
                if((await references.FindProtectedAsync(id,inner)).Count>0) throw new ApiException(409,"policy_in_use","策略仍被工作配置或受保护的发布快照引用。");db.Remove(p);return true;
            },token);
        },ct);
    }
    public async Task<PolicyValidationDto> ValidateAsync(ValidatePolicyRequest request,ActorContext actor,CancellationToken ct=default)
    {var scope=await access.ResolveAsync(request.Scope,ct);await auth.RequireAsync(actor,"policy.write",new("policy",Guid.Empty,scope),ct);var config=PolicyConfigurationValidator.Normalize(request.Type,request.Config);await ValidateConfigurationAsync(scope,request.Type,config,actor,ct);return new(true,config);}
    private async Task ValidateConfigurationAsync(ScopeRef scope,string type,string config,ActorContext actor,CancellationToken ct)
    {
        deployment.Validate(type,config);
        if(type=="authentication"&&PolicyConfigurationValidator.ParseAuthentication(config).Jwt is {} jwt) await mappings.ValidateAsync(scope,jwt,actor,ct);
    }
    public async Task<PolicyLimitsDto> LimitsAsync(PolicyScopeRequest requested,ActorContext actor,CancellationToken ct)
    {
        var scope=await access.ResolveAsync(requested,ct);
        if(!await access.CanReadAsync(scope,actor,ct)) throw ScopeResolver.Missing();
        return deployment.Limits;
    }
}
