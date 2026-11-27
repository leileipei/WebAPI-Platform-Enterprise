using WebApi.Contracts.Catalog;
using WebApi.Contracts.Common;
using WebApi.Contracts.Policies;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Commands;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Policies;
using WebApi.Infrastructure.Security;
namespace WebApi.Infrastructure.Routing;

public sealed record CopyRouteRequest(Guid EnvironmentId,Guid ClusterId);

public sealed class RouteCopyService(ScopeResolver scopes,AuthorizationService auth,AuditedCommandExecutor commands,
    IdempotentCommandExecutor receipts,CommandRequestContext context,RouteService routes,RoutePolicyService policies)
{
    public async Task<CommandResult<RouteDto>> CopyAsync(Guid id,CopyRouteRequest input,string? tag,ActorContext actor,CancellationToken ct)
    {
        var target=await scopes.EnvironmentAsync(input.EnvironmentId,ct);
        return await commands.ExecuteAsync(actor,target,"route.copy",async(_,token)=>{
            var source=await routes.GetAsync(id,actor,token);
            var origin=await scopes.EnvironmentAsync(source.EnvironmentId,token);
            if(origin.OrganizationId!=target.OrganizationId||origin.ProjectId!=target.ProjectId)throw ScopeResolver.Missing();
            if(source.EnvironmentId==input.EnvironmentId)throw new ApiException(422,"same_environment","请选择同项目的其他目标环境。");
            await auth.RequireAsync(actor,"route.write",new("environment",input.EnvironmentId,target),token);
            var bindings=await policies.GetAsync(id,actor,token);
            if(bindings.Bindings.Count>0){
                await auth.RequireAsync(actor,"policy.write",new("environment",input.EnvironmentId,target),token);
                await auth.RequireAsync(actor,"route.read",new("environment",input.EnvironmentId,target),token);
            }
            return await receipts.ExecuteAsync(new(actor.UserId,target,"route.copy",context.IdempotencyKey),CanonicalJson.Serialize(new{id,input,tag}),async inner=>{
                RevisionTag.Require(tag,source.Revision);
                // Build with the standard route defaults first. All source bindings are then installed in this transaction.
                var created=await routes.SaveAsync(input.EnvironmentId,new(null,source.ApiVersionId,source.RouteName,source.Path,source.Methods,
                    input.ClusterId,source.Priority,source.Enabled,source.TimeoutMs),null,actor,inner);
                if(bindings.Bindings.Count==0)return created;
                var selected=bindings.Bindings.Select(b=>new PolicyBindingInput(b.PolicyId,b.Priority)).ToArray();
                await policies.ReplaceAsync(created.Value.Id,new(selected),created.ETag,actor,inner);
                var copy=await routes.GetAsync(created.Value.Id,actor,inner);
                return new CommandResult<RouteDto>(copy,RevisionTag.Format(copy.Revision));
            },token);
        },ct);
    }
}
