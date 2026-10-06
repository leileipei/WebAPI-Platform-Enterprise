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
using WebApi.Contracts.OpenApi;
using WebApi.Infrastructure.Contracts;
public sealed class ImportBatchWriter(WebApiDbContext db,AuthorizationService auth,ScopeResolver scopes,SystemSettingsReader settings)
{
    internal async Task<ScopeRef> ScopeAsync(ImportPreviewRequest request,ActorContext actor,CancellationToken ct)
    {
        var scope=await scopes.EnvironmentAsync(request.EnvironmentId,ct);if(scope.ProjectId!=request.ProjectId)throw new ApiException(422,"foreign_environment","导入环境不属于项目。");
        await auth.RequireAsync(actor,"api.create",new("project",request.ProjectId,scope with{EnvironmentId=null}),ct);
        await auth.RequireAsync(actor,"api.create",new("environment",request.EnvironmentId,scope),ct);
        if(!await db.Set<UpstreamCluster>().AnyAsync(c=>c.Id==request.ClusterId&&c.EnvironmentId==request.EnvironmentId&&c.ProjectId==request.ProjectId&&c.Status=="Active",ct))throw new ApiException(422,"foreign_cluster","导入集群不属于目标环境。");return scope;
    }
    internal async Task RequireTargetsAsync(IReadOnlyList<ImportTarget> targets,ImportPreviewRequest input,ActorContext actor,CancellationToken ct)
    {
        var scope=await ScopeAsync(input,actor,ct);await auth.RequireAsync(actor,"route.write",new("environment",input.EnvironmentId,scope),ct);
        foreach(var target in targets){
            ScopeRef? targetScope=null;Guid? id=null;string type="api";
            if(target.ExistingVersionId is Guid versionId){targetScope=await scopes.VersionAsync(versionId,ct);id=versionId;type="version";}
            else if(target.ApiId is Guid apiId){targetScope=await scopes.ApiAsync(apiId,ct);id=apiId;}
            if(targetScope is not null){if(targetScope.OrganizationId!=scope.OrganizationId||targetScope.ProjectId!=input.ProjectId)throw new ApiException(422,"foreign_api","导入目标不属于当前项目。");await auth.RequireAsync(actor,"api.version.write",new(type,id!.Value,targetScope),ct);}
            if(target.ExistingRouteId is Guid routeId&&!await db.Set<ApiRoute>().AnyAsync(r=>r.Id==routeId&&r.ApiVersionId==target.ExistingVersionId&&r.EnvironmentId==input.EnvironmentId,ct))throw new ApiException(422,"foreign_route","目标路由不属于导入版本和环境。");
        }
    }
    public async Task<ImportCommitResponse> WriteAsync(ContractBundle bundle,ImportPreviewRequest targetScope,IReadOnlyList<ImportTarget> targets,ActorContext actor,CancellationToken token)
    {
        if(db.Database.CurrentTransaction is null)throw new InvalidOperationException("Import must participate in its caller's transaction.");
        ContractBundleCodec.Verify(bundle);var root=bundle.Documents.Single(x=>x.Source.LogicalUri==bundle.RootUri);var scope=await ScopeAsync(targetScope,actor,token);await RequireTargetsAsync(targets,targetScope,actor,token);
            if(targets.Count is <1 or >1000||targets.Select(t=>t.OperationId).Distinct().Count()!=targets.Count||targets.Where(t=>t.ExistingVersionId is not null).GroupBy(t=>t.ExistingVersionId).Any(g=>g.Count()>1)) throw new ApiException(422,"invalid_import_targets","每个Operation与已有目标版本只能出现一次。");
            var parser=new OpenApiOperationParser(bundle);var operations=new List<ImportedOperationDto>();var warnings=new List<string>();var routeKeys=new HashSet<string>(StringComparer.Ordinal);
            foreach(var target in targets)
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
                    else {if(target.NewApiCode is null||target.NewApiName is null) throw new ApiException(422,"invalid_api_target","新建API必须提供编码和名称。");GovernanceService.Validate(target.NewApiCode,target.NewApiName);api=new Api {OrganizationId=scope.OrganizationId,ProjectId=targetScope.ProjectId,Code=target.NewApiCode,Name=target.NewApiName,Description=parsed.Summary,OwnerUserId=actor.UserId,LifecycleStatus="Draft"};db.Add(api);}
                    if(target.ExistingRouteId is not null) throw new ApiException(422,"invalid_route_target","新建版本不能覆盖已有路由。");
                    version=new ApiVersion {ApiId=api.Id,Version=target.Version,Status="Draft",ChangeType="unknown",CreatedBy=actor.UserId};db.Add(version);
                }
                version.OpenapiDocument=parsed.Document;version.OpenapiSource=root.Source.RawText;version.SourceFormat=root.Source.Format;version.SchemaHash=CatalogService.Hash(parsed.Document);
                var sourceRow=await db.Set<ApiVersionContractSources>().SingleOrDefaultAsync(x=>x.ApiVersionId==version.Id,token);
                if(sourceRow is null){sourceRow=new(){ApiVersionId=version.Id};db.Add(sourceRow);}
                sourceRow.BundleJson=ContractBundleCodec.Encode(bundle);sourceRow.BundleHash=bundle.Hash;sourceRow.Dialect=root.Dialect.ToString();sourceRow.SourcePolicyRevision=await db.Set<ProjectImportSourcePolicy>().Where(x=>x.ProjectId==scope.ProjectId).Select(x=>x.Revision).SingleOrDefaultAsync(token);
                var sourceDefinitions=new List<ContractDefinitionSource>();
                for(var i=0;i<parsed.Parameters.Count;i++){var p=parsed.Parameters[i];var entity=new ApiParameter{ApiVersionId=version.Id,Location=p.Location,Name=p.Name,DataType=p.DataType,Required=p.Required,Schema=p.Schema,Description=p.Description,ExampleJson=p.ExampleJson};db.Add(entity);var origin=parsed.Origins.Single(x=>x.Kind=="parameter"&&x.Index==i);sourceDefinitions.Add(new(entity.Id,"parameter",origin.ResourceUri,origin.Pointer,VersionContractSourceService.DocumentHash(p.Schema??throw new InvalidOperationException("Parsed parameter must include a schema."))));}
                for(var i=0;i<parsed.Schemas.Count;i++){var s=parsed.Schemas[i];var entity=new ApiSchema{ApiVersionId=version.Id,SchemaType=s.SchemaType,Name=s.Name,StatusCode=s.StatusCode,ContentType=s.ContentType,SchemaJson=s.SchemaJson,SchemaHash=CatalogService.Hash(s.SchemaJson),ExampleJson=s.ExampleJson};db.Add(entity);var origin=parsed.Origins.Single(x=>x.Kind=="schema"&&x.Index==i);sourceDefinitions.Add(new(entity.Id,"schema",origin.ResourceUri,origin.Pointer,VersionContractSourceService.DocumentHash(s.SchemaJson)));}
                sourceRow.SourcesJson=JsonSerializer.Serialize(new ContractSourceMetadata(bundle.Documents.Select(d=>new ContractResourceSource(d.Source.LogicalUri,d.Source.Format)).ToArray(),sourceDefinitions,VersionContractSourceService.DocumentHash(version.OpenapiDocument)),CanonicalJson.Options);
                var normalized=RouteNormalizer.Normalize(parsed.Path);var route=target.ExistingRouteId is Guid rid?await db.Set<ApiRoute>().SingleOrDefaultAsync(r=>r.Id==rid&&r.ApiVersionId==version.Id&&r.EnvironmentId==targetScope.EnvironmentId,token)??throw new ApiException(422,"foreign_route","路由不属于导入版本和环境。"):new ApiRoute {ApiVersionId=version.Id,EnvironmentId=targetScope.EnvironmentId};
                if(target.ExistingRouteId is not null) {RevisionTag.Require(target.ExpectedRouteRevision is long rev?RevisionTag.Format(rev):null,route.Revision);route.Revision++;}else db.Add(route);
                if(!routeKeys.Add(parsed.Method+":"+normalized)||await db.Set<RouteMethod>().AnyAsync(m=>m.EnvironmentId==targetScope.EnvironmentId&&m.Method==parsed.Method&&m.NormalizedPath==normalized&&m.RouteId!=route.Id,token)) throw new ApiException(409,"route_conflict","导入路由与启用工作区路由冲突。");
                route.RouteName=parsed.SuggestedCode;route.Path=parsed.Path;route.NormalizedPath=normalized;route.Methods=[parsed.Method];route.ClusterId=targetScope.ClusterId;route.Priority=100;route.Enabled=true;route.TimeoutMs=target.ExistingRouteId is not null?30000:(await settings.GatewayAsync(token)).DefaultRouteTimeoutMs;route.UpdatedAt=DateTimeOffset.UtcNow;
                var oldMethods=await db.Set<RouteMethod>().Where(m=>m.RouteId==route.Id).ToArrayAsync(token);db.RemoveRange(oldMethods.Where(m=>m.Method!=parsed.Method));var oldMethod=oldMethods.SingleOrDefault(m=>m.Method==parsed.Method);if(oldMethod is not null) oldMethod.NormalizedPath=normalized;else db.Add(new RouteMethod {RouteId=route.Id,Method=parsed.Method,EnvironmentId=targetScope.EnvironmentId,NormalizedPath=normalized});
                if(target.ExistingRouteId is null) {var policy=new Policy {OrganizationId=scope.OrganizationId,ProjectId=scope.ProjectId,Name="RouteAuth-"+route.Id,Type="authentication",Config=JsonSerializer.Serialize(new {mode="ApiKey"})};db.Add(policy);db.Add(new RoutePolicyBinding {RouteId=route.Id,PolicyId=policy.Id});}
                operations.Add(new(parsed.Id,api.Id,version.Id,route.Id));
            }
            return new ImportCommitResponse(Guid.NewGuid(),operations,warnings.Distinct().ToArray());
    }
}
