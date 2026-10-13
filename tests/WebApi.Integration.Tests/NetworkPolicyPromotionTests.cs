using System.Net;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Releases;
using WebApi.Domain.Policies;
using WebApi.Infrastructure.Gateway;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;
namespace WebApi.Integration.Tests;
public sealed class NetworkPolicyPromotionTests
{
    private const string Cors="{\"allowedOrigins\":[\"https://test.example.com\"],\"allowCredentials\":false,\"allowedMethods\":[\"GET\"],\"allowedHeaders\":[\"X-API-Key\"],\"exposedHeaders\":[],\"maxAgeSeconds\":600}";
    private const string Ip="{\"mode\":\"AllowList\",\"ranges\":[\"10.0.0.0/8\"]}";
    private const string Transform="{\"requestHeaders\":[],\"responseHeaders\":[],\"path\":null,\"query\":[],\"requestBody\":{\"methods\":[\"POST\"],\"maxBodyBytes\":1024,\"operations\":[{\"op\":\"add\",\"path\":\"/source\",\"value\":\"frozen\"}]},\"responseBody\":{\"statusCodes\":[200],\"maxBodyBytes\":1024,\"operations\":[{\"op\":\"remove\",\"path\":\"/debug\"}]}}";
    private static async Task<Policy> Init(PromotionScenario scenario,string type)
    {
        await scenario.InitializeAsync(configureSourceAuthentication:async api=>
        {
            await using var db=api.Context();var route=await db.Set<ApiRoute>().SingleAsync();var p=new Policy{OrganizationId=api.Organization.Id,ProjectId=api.Project.Id,Name="SourceNetwork",Type=type,Config=type=="cors"?Cors:Ip};db.Add(p);db.Add(new RoutePolicyBinding{RouteId=route.Id,PolicyId=p.Id,Priority=1});var transform=new Policy{OrganizationId=api.Organization.Id,ProjectId=api.Project.Id,Name="SourceTransform",Type="transform",Config=Transform};db.Add(transform);db.Add(new RoutePolicyBinding{RouteId=route.Id,PolicyId=transform.Id,Priority=2});route.Revision++;
            foreach(var node in await db.Set<GatewayNode>().ToArrayAsync())node.Metadata=SnapshotSchemaCapabilities.Merge(node.Metadata,Guid.Parse(node.InstanceId),["2.0","2.1","2.2","2.3"]);await db.SaveChangesAsync();
        });
        await using var target=scenario.Api.Context();foreach(var node in await target.Set<GatewayNode>().Where(n=>n.EnvironmentId==scenario.TargetEnvironmentId).ToArrayAsync())node.Metadata=SnapshotSchemaCapabilities.Merge(node.Metadata,Guid.Parse(node.InstanceId),["2.0","2.1","2.2","2.3"]);
        var policy=new Policy{OrganizationId=scenario.Api.Organization.Id,ProjectId=scenario.Api.Project.Id,Name="TargetSharedNetwork",Type=type,Config=type=="cors"?Cors.Replace("https://test.example.com","https://prod.example.com",StringComparison.Ordinal):Ip.Replace("10.0.0.0/8","192.168.0.0/16",StringComparison.Ordinal)};scenario.TargetPolicyId=policy.Id;target.Add(policy);await target.SaveChangesAsync();return policy;
    }
    [Theory][InlineData("cors")][InlineData("ip_filter")]
    public async Task Promotion_NetworkParametersAreExplicitAndTargetPoliciesIndependent(string type)
    {await using var s=new PromotionScenario();var shared=await Init(s,type);using var saved=await s.SaveAsync(s.Mapping());saved.EnsureSuccessStatusCode();var candidate=await s.PrepareAsync();var network=Assert.Single(candidate.Policies,p=>p.Type==type);Assert.NotEqual(shared.Id,network.Id);Assert.Equal(PolicyConfigurationValidator.Normalize(type,shared.Config),PolicyConfigurationValidator.Normalize(type,network.Config));Assert.Equal(PolicyConfigurationValidator.Normalize("transform",Transform),PolicyConfigurationValidator.Normalize("transform",Assert.Single(candidate.Policies,p=>p.Type=="transform").Config));await using var db=s.Api.Context();var original=await db.Set<Policy>().SingleAsync(p=>p.Id==shared.Id);Assert.Equal(1,original.VersionNo);Assert.Equal(PolicyConfigurationValidator.Normalize(type,shared.Config),PolicyConfigurationValidator.Normalize(type,original.Config));var again=await s.PrepareAsync();Assert.Equal(network.Id,Assert.Single(again.Policies,p=>p.Type==type).Id);}
    [Theory][InlineData("cors")][InlineData("ip_filter")]
    public async Task Promotion_NetworkTargetCannotBeOmitted(string type)
    {await using var s=new PromotionScenario();await Init(s,type);var mapping=s.Mapping();var route=mapping.Routes[0];var policies=route.Policies.Select(p=>p.Type==type?p with{TargetPolicyId=null,TargetPolicyRevision=null}:p).ToArray();using var response=await s.SaveAsync(mapping with{Routes=[route with{Policies=policies}]});Assert.Equal(HttpStatusCode.UnprocessableEntity,response.StatusCode);}
    [Theory][InlineData("cors")][InlineData("ip_filter")]
    public async Task Promotion_StaleTargetNetworkPolicyReferenceIsRejected(string type)
    {await using var s=new PromotionScenario();var policy=await Init(s,type);await using(var db=s.Api.Context()){var target=await db.Set<Policy>().SingleAsync(p=>p.Id==policy.Id);target.VersionNo++;target.Config=type=="cors"?target.Config.Replace("600","601",StringComparison.Ordinal):target.Config.Replace("AllowList","DenyList",StringComparison.Ordinal);await db.SaveChangesAsync();}using var response=await s.SaveAsync(s.Mapping());Assert.Equal(HttpStatusCode.UnprocessableEntity,response.StatusCode);}
    [Theory][InlineData("cors")][InlineData("ip_filter")]
    public async Task Promotion_ForeignProjectNetworkPolicyIsRejected(string type)
    {await using var s=new PromotionScenario();var policy=await Init(s,type);await using(var db=s.Api.Context()){var other=new Project{OrganizationId=s.Api.Organization.Id,Code="FOREIGN_NETWORK",Name="Other",OwnerUserId=s.Api.User.Id};db.Add(other);(await db.Set<Policy>().SingleAsync(p=>p.Id==policy.Id)).ProjectId=other.Id;await db.SaveChangesAsync();}using var response=await s.SaveAsync(s.Mapping());Assert.Equal(HttpStatusCode.UnprocessableEntity,response.StatusCode);}
    [Theory][InlineData(false)][InlineData(true)]
    public async Task Promotion_PreparedFrozenBodyPolicyCannotBeTamperedOrShared(bool share)
    {await using var s=new PromotionScenario();await Init(s,"cors");using var saved=await s.SaveAsync(s.Mapping());saved.EnsureSuccessStatusCode();var candidate=await s.PrepareAsync();var privatePolicy=Assert.Single(candidate.Policies,p=>p.Type=="transform");await using(var db=s.Api.Context()){if(share)db.Add(new RoutePolicyBinding{RouteId=(await db.Set<ApiRoute>().SingleAsync(r=>r.EnvironmentId==s.Api.Environment.Id)).Id,PolicyId=privatePolicy.Id,Priority=9});else(await db.Set<Policy>().SingleAsync(p=>p.Id==privatePolicy.Id)).Config=Transform.Replace("frozen","tampered",StringComparison.Ordinal);await db.SaveChangesAsync();}var error=await Assert.ThrowsAsync<WebApi.Contracts.Common.ApiException>(()=>s.PrepareAsync());Assert.Equal(409,error.Status);Assert.Equal("prepared_policy_changed",error.Code);}

}
