using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Governance;
using WebApi.Contracts.Releases;
using WebApi.Contracts.Security;
using WebApi.Domain.Governance;
using WebApi.Infrastructure.Catalog;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Releases;
using WebApi.Infrastructure.Security;
namespace WebApi.Infrastructure.Governance;
internal sealed record AccessSelection(EnvironmentRecord Environment,IReadOnlyList<(Guid Id,Guid VersionId,string Path,string Method)> Routes,IReadOnlyList<FrozenApiVersion> Versions,long? RunningConfigVersion,bool InternalAllowed);
public sealed class EnvironmentApiAddressService(WebApiDbContext db,AuthorizationService auth,ScopeResolver scopes,HistoricalSnapshotService history)
{
    public async Task<EnvironmentApiAccessDto> GetAsync(Guid envId,Guid apiId,Guid? versionId,AccessView view,ActorContext actor,CancellationToken ct=default)
    {
        var selection=await SelectAsync(envId,apiId,versionId,view,actor,ct);var e=selection.Environment;
        return new(e.Id,e.AccessAddressRevision,e.GatewayPublicUrl is not null,view,selection.Routes.Select(r=>new RouteAccessDto(r.Id,r.Method,r.Path,e.GatewayPublicUrl is null?null:ClientApiAddressBuilder.BuildTemplate(e.GatewayPublicUrl,e.BasePath,r.Path),selection.InternalAllowed&&e.GatewayInternalUrl is not null?ClientApiAddressBuilder.BuildTemplate(e.GatewayInternalUrl,e.BasePath,r.Path):null,selection.RunningConfigVersion)).ToArray());
    }
    internal async Task<AccessSelection> SelectAsync(Guid envId,Guid apiId,Guid? versionId,AccessView view,ActorContext actor,CancellationToken ct)
    {
        var scope=await scopes.EnvironmentAsync(envId,ct);var apiScope=await scopes.ApiAsync(apiId,ct);
        if(scope.OrganizationId!=apiScope.OrganizationId||scope.ProjectId!=apiScope.ProjectId)throw ScopeResolver.Missing();
        if(!await auth.CanAsync(actor,"environment.read",new("environment",envId,scope),ct))throw ScopeResolver.Missing();
        foreach(var permission in new[]{"api.read","api.version.read"})if(!await auth.CanAsync(actor,permission,new("api",apiId,scope),ct))throw ScopeResolver.Missing();
        var e=await db.Set<EnvironmentRecord>().AsNoTracking().SingleAsync(x=>x.Id==envId,ct);var internalAllowed=await auth.CanAsync(actor,"environment.write",new("environment",envId,scope),ct);
        if(versionId is Guid requested&&!await db.Set<ApiVersion>().AnyAsync(x=>x.Id==requested&&x.ApiId==apiId,ct))throw ScopeResolver.Missing();
        var rows=new List<(Guid Id,Guid VersionId,string Path,string Method)>();var versions=new List<FrozenApiVersion>();long? running=null;
        if(view==AccessView.Working)
        {
            var routes=await db.Set<ApiRoute>().AsNoTracking().Where(r=>r.EnvironmentId==envId&&r.Enabled&&db.Set<ApiVersion>().Any(v=>v.Id==r.ApiVersionId&&v.ApiId==apiId)&&(versionId==null||r.ApiVersionId==versionId)).OrderBy(r=>r.Path).ThenBy(r=>r.Id).ToArrayAsync(ct);
            rows.AddRange(routes.SelectMany(r=>r.Methods.Select(m=>(r.Id,r.ApiVersionId,r.Path,m))));
            foreach(var id in rows.Select(r=>r.VersionId).Distinct())
            {
                var v=await db.Set<ApiVersion>().AsNoTracking().SingleAsync(x=>x.Id==id,ct);var a=await db.Set<Api>().AsNoTracking().SingleAsync(x=>x.Id==apiId,ct);
                var parameters=await db.Set<ApiParameter>().AsNoTracking().Where(x=>x.ApiVersionId==id).Select(x=>new WebApi.Contracts.Catalog.ParameterDto(x.Id,id,x.Location,x.Name,x.DataType,x.Required,x.Schema,x.Description,x.ExampleJson)).ToArrayAsync(ct);
                var schemas=await db.Set<ApiSchema>().AsNoTracking().Where(x=>x.ApiVersionId==id).Select(x=>new WebApi.Contracts.Catalog.SchemaDto(x.Id,id,x.SchemaType,x.Name,x.StatusCode,x.ContentType,x.SchemaJson,x.SchemaHash,x.ExampleJson)).ToArrayAsync(ct);
                versions.Add(new(CatalogService.Dto(a),CatalogService.Dto(v),parameters,schemas));
            }
        }
        else if(view==AccessView.Running)
        {
            running=e.DesiredConfigVersion;
            if(running is null||running==0)throw Unconfirmed();
            var release=await db.Set<ReleaseRecord>().AsNoTracking().SingleOrDefaultAsync(x=>x.EnvironmentId==envId&&x.ToConfigVersion==running&&x.DeploymentSequence==e.DeploymentSequence&&x.Status=="Succeeded",ct);
            var nodes=await db.Set<GatewayNode>().AsNoTracking().Where(x=>x.EnvironmentId==envId&&x.Enabled).ToArrayAsync(ct);var threshold=DateTimeOffset.UtcNow.AddSeconds(-120);
            if(release is null||nodes.Length<2||nodes.Any(n=>n.CurrentConfigVersion!=running||n.CurrentDeploymentSequence!=e.DeploymentSequence||n.LastHeartbeatAt is null||n.LastHeartbeatAt<threshold))throw Unconfirmed();
            var targets=await db.Set<ReleaseTarget>().AsNoTracking().Where(t=>t.ReleaseId==release.Id).ToArrayAsync(ct);
            var artifact=await history.ReadAsync(envId,running.Value,ct);
            var acknowledgements=await db.Set<GatewayAck>().AsNoTracking().Where(a=>a.ReleaseId==release.Id&&a.Success&&a.ConfigVersion==running&&a.DeploymentSequence==e.DeploymentSequence&&a.PayloadHash==artifact.Hash).ToArrayAsync(ct);
            if(nodes.Any(n=>!(targets.Any(t=>t.NodeId==n.Id&&t.InstanceId==n.InstanceId)&&acknowledgements.Any(a=>a.NodeId==n.Id&&a.InstanceId==n.InstanceId))&&!CurrentInstanceConfirmed(n,release,artifact.Hash)))throw Unconfirmed();
            var snapshot=artifact.Snapshot;
            var routes=snapshot.Routes.Where(r=>r.ApiId==apiId&&(versionId==null||r.ApiVersionId==versionId)).ToArray();
            if(versionId is not null&&routes.Length==0)throw new ApiException(409,"version_not_running","所选版本未在该环境运行。");
            rows.AddRange(routes.SelectMany(r=>r.Methods.Select(m=>(r.Id,r.ApiVersionId,r.Path,m))));
            var origins=await db.Set<ReleaseRecord>().AsNoTracking().Where(x=>x.EnvironmentId==envId&&x.ReleaseType=="publish"&&x.ToConfigVersion<=running&&x.ToConfigVersion>0&&x.CandidateBytes!=null&&x.ApprovalPolicy!=null).OrderByDescending(x=>x.ToConfigVersion).ToArrayAsync(ct);
            foreach(var id in routes.Select(r=>r.ApiVersionId).Distinct())
            {
                var frozen=origins.Select(x=>JsonSerializer.Deserialize<FrozenReleaseCandidate>(x.CandidateBytes!,CanonicalJson.Options)!).SelectMany(x=>x.Versions).FirstOrDefault(x=>x.Version.Id==id&&x.Api.Id==apiId)??throw new ApiException(422,"historical_contract_missing","运行版本的冻结契约缺失。");versions.Add(frozen);
            }
        }
        else throw new ApiException(422,"invalid_access_view","请选择 working 或 running 视图。");
        return new(e,rows.OrderBy(r=>r.Path,StringComparer.Ordinal).ThenBy(r=>r.Method,StringComparer.Ordinal).ThenBy(r=>r.Id).ToArray(),versions,running,internalAllowed);
    }
    private static ApiException Unconfirmed()=>new(409,"running_state_unconfirmed","运行状态尚未确认：需要成功发布且全部启用节点在线、版本和部署序列一致。");
    internal static bool CurrentInstanceConfirmed(GatewayNode node,ReleaseRecord release,string hash)
    {
        try
        {
            using var metadata=JsonDocument.Parse(node.Metadata??"{}");
            if(!metadata.RootElement.TryGetProperty("runtimeApplication",out var receipt))return false;
            return receipt.GetProperty("schemaVersion").GetInt32()==1&&receipt.GetProperty("instanceId").GetString()==node.InstanceId&&receipt.GetProperty("releaseId").GetString()==release.Id.ToString()&&receipt.GetProperty("configVersion").GetInt64()==release.ToConfigVersion&&receipt.GetProperty("deploymentSequence").GetInt64()==release.DeploymentSequence&&receipt.GetProperty("payloadHash").GetString()==hash;
        }
        catch(Exception e) when(e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException){return false;}
    }
}
