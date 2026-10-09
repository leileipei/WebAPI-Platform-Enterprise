using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Contracts.Common;
using WebApi.Contracts.Releases;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Gateway;
using WebApi.Infrastructure.Releases;
using WebApi.Contracts.Runtime;
using System.Text;
using System.Text.Json;
namespace WebApi.Integration.Tests.Support;
public sealed class PromotionScenario:IAsyncDisposable
{
    public DeliveryScenario Source {get;}=new();public ApiFixture Api=>Source.Api;
    public Guid TargetEnvironmentId,TargetClusterId,TargetApplicationId,TargetCredentialId,TargetAuthorizationId,PromotionId,TargetPolicyId;
    public async Task InitializeAsync(bool timeout=false,bool anonymous=false)
    {
        await Source.InitializeAsync(beforePublish:async api=>{
            if(!timeout&&!anonymous)return;await Grant(api,api.User.Id,"policy.read","policy.write");await using var db=api.Context();var route=await db.Set<ApiRoute>().SingleAsync();if(timeout){var policy=new Policy{OrganizationId=api.Organization.Id,ProjectId=api.Project.Id,Name="SourceTimeout",Type="timeout",Config="{\"timeoutMs\":10000}"};db.Add(policy);db.Add(new RoutePolicyBinding{RouteId=route.Id,PolicyId=policy.Id,Priority=7});}if(anonymous){var policy=new Policy{OrganizationId=api.Organization.Id,ProjectId=api.Project.Id,Name="SourceAnonymous",Type="authentication",Config="{\"mode\":\"Anonymous\"}"};db.Add(policy);db.Add(new RoutePolicyBinding{RouteId=route.Id,PolicyId=policy.Id,Priority=0});}await db.SaveChangesAsync();
        });
        await Grant(Api,Api.User.Id,"policy.read","policy.write");
        await using(var db=Api.Context()){
            var target=new EnvironmentRecord{ProjectId=Api.Project.Id,Code="PROD",Name="生产目标",IsProduction=true,GatewayPublicUrl="https://prod-gateway.example",ReleasePolicyId=Api.ApprovalFlow.Id};TargetEnvironmentId=target.Id;db.Add(target);
            var cluster=new UpstreamCluster{ProjectId=Api.Project.Id,EnvironmentId=target.Id,Name="ProdBackend",LoadBalancingPolicy="RoundRobin",HealthCheckPath="/health",HealthCheckIntervalSec=30};TargetClusterId=cluster.Id;db.Add(cluster);db.Add(new UpstreamDestination{ClusterId=cluster.Id,Name="prod-node",Weight=1,Address="http://test-backend:8080/prod/"});
            for(var i=0;i<2;i++){var instance=Guid.NewGuid();db.Add(new GatewayNode{EnvironmentId=target.Id,NodeName="target-"+i,InstanceId=instance.ToString(),IdentityHash=new string('f',64),LastHeartbeatAt=DateTimeOffset.UtcNow,Metadata=SnapshotSchemaCapabilities.Merge(null,instance,["2.0","2.1","2.2"])});}
            if(timeout){var policy=new Policy{OrganizationId=Api.Organization.Id,ProjectId=Api.Project.Id,Name="TargetSharedTimeout",Type="timeout",Config="{\"timeoutMs\":25000}"};TargetPolicyId=policy.Id;db.Add(policy);}
            await db.SaveChangesAsync();
        }
        using(var application=await Api.WriteAsync(HttpMethod.Post,"/api/v1/applications",new{organizationId=Api.Organization.Id,projectId=Api.Project.Id,code="PROD_CONSUMER",name="生产消费方",owner="目标负责人"})){application.EnsureSuccessStatusCode();TargetApplicationId=(await application.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("id").GetGuid();}
        using(var credential=await Api.WriteAsync(HttpMethod.Post,$"/api/v1/applications/{TargetApplicationId}/credentials",new{validFrom=DateTimeOffset.UtcNow.AddMinutes(-1),expiresAt=DateTimeOffset.UtcNow.AddDays(1)})){credential.EnsureSuccessStatusCode();TargetCredentialId=(await credential.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("credential").GetProperty("id").GetGuid();}
        using(var permission=await Api.WriteAsync(HttpMethod.Post,$"/api/v1/applications/{TargetApplicationId}/permissions",new{apiId=Api.Api.Id,environmentId=TargetEnvironmentId,validFrom=DateTimeOffset.UtcNow.AddMinutes(-1)})){permission.EnsureSuccessStatusCode();TargetAuthorizationId=(await permission.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("id").GetGuid();}
        var policyBody=new SaveDeliveryPolicyRequest(Api.Environment.Id,TargetEnvironmentId,"PromotionRequired",["InterfaceFunction","Integration","ContractCompatibility"],1440);using(var policy=await Api.WriteAsync(HttpMethod.Put,$"/api/v1/projects/{Api.Project.Id}/delivery-policy",policyBody,"\"0\""))policy.EnsureSuccessStatusCode();
        var ids=new List<Guid>();foreach(var type in new[]{"InterfaceFunction","Integration","ContractCompatibility"}){using var evidence=await Source.RecordAsync(type:type);evidence.EnsureSuccessStatusCode();ids.Add((await evidence.Content.ReadFromJsonAsync<ReleaseVerificationDto>())!.Id);}
        using var requested=await ApiFixture.CommandAsync(Api.Client,$"/api/v1/release-artifacts/{Source.Artifact.Id}/test-acceptances",new RequestTestAcceptanceRequest(ids));requested.EnsureSuccessStatusCode();var acceptance=(await requested.Content.ReadFromJsonAsync<TestAcceptanceDto>())!;
        var reviewer=await Api.NewReviewerAsync("TestAcceptor");await Grant(Api,reviewer.User.Id,"release.test.accept","environment.read","route.read","api.read","api.version.read","api.schema.read","policy.read");
        var csrf=await reviewer.Client.GetFromJsonAsync<Dictionary<string,string>>("/api/v1/auth/csrf");using var action=new HttpRequestMessage(HttpMethod.Post,$"/api/v1/test-acceptances/{acceptance.Id}/accept"){Content=JsonContent.Create(new TestAcceptanceActionRequest("独立验收"))};action.Headers.Add("X-CSRF-Token",csrf!["token"]);action.Headers.Add("Idempotency-Key",Guid.NewGuid().ToString("N"));action.Headers.Add("If-Match","\"1\"");using var accepted=await reviewer.Client.SendAsync(action);accepted.EnsureSuccessStatusCode();
        await using var context=Api.Context();var promotion=new ReleasePromotion{OrganizationId=Api.Organization.Id,ProjectId=Api.Project.Id,ArtifactId=Source.Artifact.Id,SourceEnvironmentId=Api.Environment.Id,TargetEnvironmentId=TargetEnvironmentId,SourceReleaseId=Source.ReleaseId,AcceptanceId=acceptance.Id,RequestedBy=Api.User.Id};PromotionId=promotion.Id;context.Add(promotion);await context.SaveChangesAsync();
    }
    private static async Task Grant(ApiFixture api,Guid user,params string[] codes)
    {await using var db=api.Context();var roleId=await db.Set<UserRole>().Where(r=>r.UserId==user).Select(r=>r.RoleId).FirstAsync();foreach(var code in codes){var permission=await db.Set<Permission>().SingleOrDefaultAsync(p=>p.Code==code);if(permission is null){permission=new(){Code=code,Name=code,Module=code.Split('.')[0]};db.Add(permission);}if(!await db.Set<RolePermission>().AnyAsync(r=>r.RoleId==roleId&&r.PermissionId==permission.Id))db.Add(new RolePermission{RoleId=roleId,PermissionId=permission.Id});}await db.SaveChangesAsync();}
    public PromotionMappingRequest Mapping(Guid? routeId=null,bool policies=true)=>new([new(Source.Artifact.Content.Routes[0].Key,routeId,TargetClusterId,30000,policies?Source.Artifact.Content.Routes[0].Policies.Select(p=>new PromotionPolicyMapping(p.Type,p.Priority,p.EnvironmentFields.Count>0?TargetPolicyId:null,p.EnvironmentFields.Count>0?1:null)).ToArray():[])],[new(TargetApplicationId,[TargetCredentialId],[TargetAuthorizationId])]);
    public Task<HttpResponseMessage> SaveAsync(PromotionMappingRequest request,string etag="\"1\"")=>Api.WriteAsync(HttpMethod.Put,$"/api/v1/release-promotions/{PromotionId}/mapping",request,etag);
    public async Task<FrozenReleaseCandidate> PrepareAsync()
    {
        using var services=Api.Services();var db=services.ServiceProvider.GetRequiredService<WebApiDbContext>();await using var transaction=await db.Database.BeginTransactionAsync();await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(8901202)");
        foreach(var environment in new[]{Api.Environment.Id,TargetEnvironmentId}.Order())await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM environments WHERE id={environment} FOR UPDATE");
        var promotion=await db.Set<ReleasePromotion>().SingleAsync(p=>p.Id==PromotionId);
        var prepared=await services.ServiceProvider.GetRequiredService<WebApi.Infrastructure.Delivery.PromotionCandidateBuilder>().PrepareAsync(promotion,new ActorContext(Api.User.Id,"mapping-real-service-test"),CancellationToken.None);
        var candidate=prepared.Candidate;await db.SaveChangesAsync();await transaction.CommitAsync();return candidate;
    }
    public async Task<(Guid ApiId,Guid CredentialId)> SeedRetainedBusinessAsync()
    {
        Guid otherId,versionId;await using(var db=Api.Context()){
            var api=new Api{OrganizationId=Api.Organization.Id,ProjectId=Api.Project.Id,Code="RETAINED",Name="保留业务",LifecycleStatus="Published",OwnerUserId=Api.User.Id};otherId=api.Id;
            var version=new ApiVersion{ApiId=api.Id,Version="1.0",CreatedBy=Api.User.Id,SealedAt=DateTimeOffset.UtcNow};versionId=version.Id;
            db.AddRange(api,version,new ApiRoute{EnvironmentId=TargetEnvironmentId,ApiVersionId=version.Id,RouteName="Retained",Path="/retained",NormalizedPath="/retained",Methods=["GET"],ClusterId=TargetClusterId,TimeoutMs=9000});await db.SaveChangesAsync();
        }
        using var permission=await Api.WriteAsync(HttpMethod.Post,$"/api/v1/applications/{TargetApplicationId}/permissions",new{apiId=otherId,environmentId=TargetEnvironmentId,validFrom=DateTimeOffset.UtcNow.AddMinutes(-1)});permission.EnsureSuccessStatusCode();
        using var credential=await Api.WriteAsync(HttpMethod.Post,$"/api/v1/applications/{TargetApplicationId}/credentials",new{validFrom=DateTimeOffset.UtcNow.AddMinutes(-1),expiresAt=DateTimeOffset.UtcNow.AddDays(1)});credential.EnsureSuccessStatusCode();var credentialId=(await credential.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("credential").GetProperty("id").GetGuid();
        using var services=Api.Services();var db2=services.ServiceProvider.GetRequiredService<WebApiDbContext>();var candidate=await services.ServiceProvider.GetRequiredService<ReleaseCandidateBuilder>().PreviewAsync(TargetEnvironmentId,new(0,[versionId]),CancellationToken.None);var compiled=services.ServiceProvider.GetRequiredService<SnapshotCompiler>().Compile(candidate,new RuntimeSnapshot("2.0",TargetEnvironmentId,0,DateTimeOffset.UtcNow,[],[],[],[]),1,DateTimeOffset.UtcNow);
        var config=new GatewayConfigVersion{EnvironmentId=TargetEnvironmentId,VersionNo=1,SnapshotHash=compiled.Hash,CreatedBy=Api.User.Id};db2.Add(config);db2.Add(new GatewayConfigSnapshot{ConfigVersionId=config.Id,Payload=Encoding.UTF8.GetString(compiled.Payload.Span),PayloadBytes=compiled.Payload.ToArray(),SizeBytes=compiled.Size});
        db2.Add(new ReleaseRecord{EnvironmentId=TargetEnvironmentId,ReleaseNo="retained-baseline",ReleaseType="publish",Status="Succeeded",RequestedBy=Api.User.Id,ToConfigVersion=1,CandidateBytes=CanonicalJson.Serialize(candidate)});
        var env=await db2.Set<EnvironmentRecord>().SingleAsync(e=>e.Id==TargetEnvironmentId);env.DesiredConfigVersion=1;
        var promotion=await db2.Set<ReleasePromotion>().SingleAsync(p=>p.Id==PromotionId);promotion.BaselineConfigVersion=1;await db2.SaveChangesAsync();return(otherId,credentialId);
    }
    public ValueTask DisposeAsync()=>Source.DisposeAsync();
}
