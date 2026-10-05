using WebApi.Infrastructure.Settings;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Catalog;
using WebApi.Contracts.Security;
using WebApi.Domain.Routing;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Security;
using WebApi.Infrastructure.Commands;
namespace WebApi.Infrastructure.Catalog;
public sealed class OpenApiImportService(WebApiDbContext db,AuthorizationService auth,ScopeResolver scopes,AuditedCommandExecutor commands,IdempotentCommandExecutor idempotency,CommandRequestContext requestContext,SystemSettingsReader settings)
{
    private async Task<ScopeRef> ScopeAsync(ImportPreviewRequest request,ActorContext actor,CancellationToken ct)
    {
        var scope=await scopes.EnvironmentAsync(request.EnvironmentId,ct);if(scope.ProjectId!=request.ProjectId) throw new ApiException(422,"foreign_environment","导入环境不属于项目。");
        await auth.RequireAsync(actor,"api.create",new("project",request.ProjectId,scope with {EnvironmentId=null}),ct);
        if(!await db.Set<UpstreamCluster>().AnyAsync(c=>c.Id==request.ClusterId&&c.EnvironmentId==request.EnvironmentId&&c.ProjectId==request.ProjectId&&c.Status=="Active",ct)) throw new ApiException(422,"foreign_cluster","导入集群不属于目标环境。");return scope;
    }
    public async Task<ImportPreviewResponse> PreviewAsync(ImportPreviewRequest request,ActorContext actor,CancellationToken ct=default)
    {
        await ScopeAsync(request,actor,ct);var parser=new OpenApiOperationParser(request.Source);var items=new List<ImportOperationDto>();
        foreach(var (id,method,path) in parser.Operations())
        {
            try {var operation=parser.Parse(id);items.Add(new(id,method,path,operation.Summary,operation.SuggestedCode,true,operation.Warnings,operation.Parameters.Count,operation.Schemas.Count));}
            catch(ApiException e) {items.Add(new(id,method,path,id,"",false,[e.Code+": "+e.Message],0,0));}
        }
        return new(items,["首期按Operation分别映射API Version；完整YAML、URL抓取、循环及外部引用暂不支持。", "导入预览不授权提交，提交会再次校验权限、原文、目标及路由冲突。"]);
    }
    public async Task<ImportCommitResponse> CommitAsync(ImportCommitRequest request,ActorContext actor,CancellationToken ct=default)
    {
        var scope=await scopes.EnvironmentAsync(request.Input.EnvironmentId,ct);return await commands.ExecuteAsync(actor,scope,"openapi.import",async(_,token)=>{
            scope=await ScopeAsync(request.Input,actor,token);await auth.RequireAsync(actor,"route.write",new("environment",request.Input.EnvironmentId,scope),token);
            return await idempotency.ExecuteAsync(new(actor.UserId,scope,"openapi.import",requestContext.IdempotencyKey),CanonicalJson.Serialize(request),async inner=>{
            token=inner;
            if(request.Targets.Count is <1 or >1000||request.Targets.Select(t=>t.OperationId).Distinct().Count()!=request.Targets.Count||request.Targets.Where(t=>t.ExistingVersionId is not null).GroupBy(t=>t.ExistingVersionId).Any(g=>g.Count()>1)) throw new ApiException(422,"invalid_import_targets","每个Operation与已有目标版本只能出现一次。");
            var parser=new OpenApiOperationParser(request.Input.Source);var operations=new List<ImportedOperationDto>();var warnings=new List<string>();var routeKeys=new HashSet<string>(StringComparer.Ordinal);
            foreach(var target in request.Targets)
            {
                var parsed=parser.Parse(target.OperationId);warnings.AddRange(parsed.Warnings);ApiVersion version;Api api;
                if(target.ExistingVersionId is Guid versionId)
                {
                    if(target.ApiId is not null||target.NewApiCode is not null||target.NewApiName is not null) throw new ApiException(422,"ambiguous_target","已有版本不能混用新建API字段。");
                    var versionScope=await scopes.VersionAsync(versionId,token);if(versionScope.ProjectId!=scope.ProjectId||versionScope.OrganizationId!=scope.OrganizationId) throw new ApiException(422,"foreign_version","目标版本不属于当前项目。");await auth.RequireAsync(actor,"api.version.write",new("version",versionId,versionScope),token);
                    version=await db.Set<ApiVersion>().SingleAsync(v=>v.Id==versionId,token);CatalogService.RequireDraft(version);RevisionTag.Require(target.ExpectedRevision is long revision?RevisionTag.Format(revision):null,version.Revision);api=await db.Set<Api>().SingleAsync(a=>a.Id==version.ApiId,token);version.Revision++;
                    db.RemoveRange(await db.Set<ApiParameter>().Where(p=>p.ApiVersionId==versionId).ToArrayAsync(token));db.RemoveRange(await db.Set<ApiSchema>().Where(s=>s.ApiVersionId==versionId).ToArrayAsync(token));
                }
                else
                {
                    CatalogService.ValidateVersion(new(target.Version));
                    if(target.ApiId is Guid apiId)
                    {
                        if(target.NewApiCode is not null||target.NewApiName is not null) throw new ApiException(422,"ambiguous_target","已有API不能混用新建字段。");var apiScope=await scopes.ApiAsync(apiId,token);if(apiScope.ProjectId!=scope.ProjectId||apiScope.OrganizationId!=scope.OrganizationId) throw new ApiException(422,"foreign_api","目标API不属于当前项目。");await auth.RequireAsync(actor,"api.version.write",new("api",apiId,apiScope),token);api=await db.Set<Api>().SingleAsync(a=>a.Id==apiId,token);
                    }
                    else {if(target.NewApiCode is null||target.NewApiName is null) throw new ApiException(422,"invalid_api_target","新建API必须提供编码和名称。");GovernanceService.Validate(target.NewApiCode,target.NewApiName);api=new Api {OrganizationId=scope.OrganizationId,ProjectId=request.Input.ProjectId,Code=target.NewApiCode,Name=target.NewApiName,Description=parsed.Summary,OwnerUserId=actor.UserId,LifecycleStatus="Draft"};db.Add(api);}
                    if(target.ExistingRouteId is not null) throw new ApiException(422,"invalid_route_target","新建版本不能覆盖已有路由。");
                    version=new ApiVersion {ApiId=api.Id,Version=target.Version,Status="Draft",ChangeType="unknown",CreatedBy=actor.UserId};db.Add(version);
                }
                version.OpenapiDocument=parsed.Document;version.OpenapiSource=request.Input.Source;version.SourceFormat="json";version.SchemaHash=CatalogService.Hash(parsed.Document);
                foreach(var p in parsed.Parameters) db.Add(new ApiParameter {ApiVersionId=version.Id,Location=p.Location,Name=p.Name,DataType=p.DataType,Required=p.Required,Schema=p.Schema,Description=p.Description,ExampleJson=p.ExampleJson});
                foreach(var s in parsed.Schemas) db.Add(new ApiSchema {ApiVersionId=version.Id,SchemaType=s.SchemaType,Name=s.Name,StatusCode=s.StatusCode,ContentType=s.ContentType,SchemaJson=s.SchemaJson,SchemaHash=CatalogService.Hash(s.SchemaJson),ExampleJson=s.ExampleJson});
                var normalized=RouteNormalizer.Normalize(parsed.Path);var route=target.ExistingRouteId is Guid rid?await db.Set<ApiRoute>().SingleOrDefaultAsync(r=>r.Id==rid&&r.ApiVersionId==version.Id&&r.EnvironmentId==request.Input.EnvironmentId,token)??throw new ApiException(422,"foreign_route","路由不属于导入版本和环境。"):new ApiRoute {ApiVersionId=version.Id,EnvironmentId=request.Input.EnvironmentId};
                if(target.ExistingRouteId is not null) {RevisionTag.Require(target.ExpectedRouteRevision is long rev?RevisionTag.Format(rev):null,route.Revision);route.Revision++;}else db.Add(route);
                if(!routeKeys.Add(parsed.Method+":"+normalized)||await db.Set<RouteMethod>().AnyAsync(m=>m.EnvironmentId==request.Input.EnvironmentId&&m.Method==parsed.Method&&m.NormalizedPath==normalized&&m.RouteId!=route.Id,token)) throw new ApiException(409,"route_conflict","导入路由与启用工作区路由冲突。");
                route.RouteName=parsed.SuggestedCode;route.Path=parsed.Path;route.NormalizedPath=normalized;route.Methods=[parsed.Method];route.ClusterId=request.Input.ClusterId;route.Priority=100;route.Enabled=true;route.TimeoutMs=target.ExistingRouteId is not null?30000:(await settings.GatewayAsync(token)).DefaultRouteTimeoutMs;route.UpdatedAt=DateTimeOffset.UtcNow;
                var oldMethods=await db.Set<RouteMethod>().Where(m=>m.RouteId==route.Id).ToArrayAsync(token);db.RemoveRange(oldMethods.Where(m=>m.Method!=parsed.Method));var oldMethod=oldMethods.SingleOrDefault(m=>m.Method==parsed.Method);if(oldMethod is not null) oldMethod.NormalizedPath=normalized;else db.Add(new RouteMethod {RouteId=route.Id,Method=parsed.Method,EnvironmentId=request.Input.EnvironmentId,NormalizedPath=normalized});
                if(target.ExistingRouteId is null) {var policy=new Policy {OrganizationId=scope.OrganizationId,ProjectId=scope.ProjectId,Name="RouteAuth-"+route.Id,Type="authentication",Config=JsonSerializer.Serialize(new {mode="ApiKey"})};db.Add(policy);db.Add(new RoutePolicyBinding {RouteId=route.Id,PolicyId=policy.Id});}
                operations.Add(new(parsed.Id,api.Id,version.Id,route.Id));
            }
            return new ImportCommitResponse(Guid.NewGuid(),operations,warnings.Distinct().ToArray());
            },token);
        },ct);
    }
}
