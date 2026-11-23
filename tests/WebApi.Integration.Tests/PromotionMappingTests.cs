using System.Net;
using WebApi.Contracts.Catalog;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Releases;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;
namespace WebApi.Integration.Tests;
public sealed class PromotionMappingTests
{
    [Fact]
    public async Task LargeDirectoryIsSearchableAndPagedAndSavedMappingsRemainAvailable()
    {
        await using var scenario=new PromotionScenario();
        await scenario.InitializeAsync();
        var path=$"/api/v1/release-promotions/{scenario.PromotionId}/mapping-options";
        await using(var db=scenario.Api.Context()){
            db.AddRange(Enumerable.Range(1,1001).Select(n=>new UpstreamCluster{ProjectId=scenario.Api.Project.Id,EnvironmentId=scenario.TargetEnvironmentId,
                Name=$"directory-{n:D4}",LoadBalancingPolicy="RoundRobin",HealthCheckPath="/health",HealthCheckIntervalSec=30}));
            await db.SaveChangesAsync();
        }
        var first=await scenario.Api.Client.GetFromJsonAsync<PromotionMappingOptionsDto>(path+"?search=directory");
        Assert.True(first!.Truncated);
        Assert.Equal(1000,first.Clusters!.Count);
        var second=await scenario.Api.Client.GetFromJsonAsync<PromotionMappingOptionsDto>(path+"?search=directory&page=2");
        var last=Assert.Single(second!.Clusters!);
        Assert.Equal("directory-1001",last.Name);
        Assert.False(second.Truncated);
        var mapping=scenario.Mapping();
        using var saved=await scenario.SaveAsync(mapping with{Routes=[mapping.Routes[0] with{ClusterId=last.Id}]});
        saved.EnsureSuccessStatusCode();
        var small=await scenario.Api.Client.GetFromJsonAsync<PromotionMappingOptionsDto>(path+"?search=directory-0001&pageSize=1");
        Assert.Contains(small!.Clusters!,c=>c.Id==last.Id);
        Assert.Contains(small.Clusters!,c=>c.Name=="directory-0001");
        Assert.DoesNotContain(small.Clusters!,c=>c.Id==scenario.Api.Cluster.Id);
    }
    [Fact]
    public async Task ASecondCredentialPageIncludesItsParentAndAnExactCredentialSearch()
    {
        await using var s=new PromotionScenario();
        await s.InitializeAsync();
        using var created=await s.Api.WriteAsync(HttpMethod.Post,$"/api/v1/applications/{s.TargetApplicationId}/credentials",
            new{validFrom=DateTimeOffset.UtcNow.AddMinutes(-1),expiresAt=DateTimeOffset.UtcNow.AddDays(1)});
        created.EnsureSuccessStatusCode();
        var path=$"/api/v1/release-promotions/{s.PromotionId}/mapping-options?pageSize=1";
        var first=await s.Api.Client.GetFromJsonAsync<PromotionMappingOptionsDto>(path+$"&search={s.TargetApplicationId}");
        var second=await s.Api.Client.GetFromJsonAsync<PromotionMappingOptionsDto>(path+$"&search={s.TargetApplicationId}&page=2");
        Assert.True(first!.Truncated);Assert.False(second!.Truncated);
        var app=Assert.Single(second.Applications!);
        Assert.Equal(s.TargetApplicationId,app.Id);
        var latest=Assert.Single(app.Credentials);
        Assert.NotEqual(Assert.Single(Assert.Single(first.Applications!).Credentials).Id,latest.Id);
        var exact=await s.Api.Client.GetFromJsonAsync<PromotionMappingOptionsDto>(path+$"&search={latest.Id}");
        Assert.Equal(latest.Id,Assert.Single(Assert.Single(exact!.Applications!).Credentials).Id);
    }
 [Fact] public async Task PreparationCreatesOnlyTargetDraftResourcesAndLeavesRuntimeUntouched()
 {await using var s=new PromotionScenario();await s.InitializeAsync();int outbox;await using(var db=s.Api.Context())outbox=await db.Set<OutboxMessage>().CountAsync();using var saved=await s.SaveAsync(s.Mapping());saved.EnsureSuccessStatusCode();var candidate=await s.PrepareAsync();Assert.Equal(s.TargetEnvironmentId,candidate.EnvironmentId);var r=Assert.Single(candidate.Routes);var source=Assert.Single(s.Source.Artifact.Content.Routes);Assert.Equal(source.Path,r.Path);Assert.Equal(source.Methods.Order(),r.Methods.Order());Assert.Equal(source.ApiId,Assert.Single(candidate.Versions).Api.Id);Assert.Equal(s.TargetClusterId,r.ClusterId);await using var context=s.Api.Context();Assert.Equal(outbox,await context.Set<OutboxMessage>().CountAsync());Assert.Null((await context.Set<EnvironmentRecord>().SingleAsync(e=>e.Id==s.TargetEnvironmentId)).DesiredConfigVersion);Assert.Null((await context.Set<ReleasePromotion>().SingleAsync(p=>p.Id==s.PromotionId)).TargetReleaseId);}
 [Fact] public async Task SavedMappingRequiresCurrentRevisionAndDraftState()
 {await using var s=new PromotionScenario();await s.InitializeAsync();using var stale=await s.SaveAsync(s.Mapping(),"\"0\"");Assert.Equal(HttpStatusCode.PreconditionFailed,stale.StatusCode);using var first=await s.SaveAsync(s.Mapping());first.EnsureSuccessStatusCode();await using(var db=s.Api.Context())await db.Set<ReleasePromotion>().Where(p=>p.Id==s.PromotionId).ExecuteUpdateAsync(x=>x.SetProperty(p=>p.Status,"WaitingApproval"));using var changed=await s.SaveAsync(s.Mapping(),"\"2\"");Assert.Equal(HttpStatusCode.Conflict,changed.StatusCode);}
 [Theory][InlineData("source-cluster")][InlineData("unknown-key")][InlineData("timeout")]
 public async Task ForeignOrForgedRouteMappingIsRejected(string change)
 {await using var s=new PromotionScenario();await s.InitializeAsync();var request=s.Mapping();var route=request.Routes[0];route=change switch{"source-cluster"=>route with{ClusterId=s.Api.Cluster.Id},"unknown-key"=>route with{ArtifactRouteKey=new string('a',64)},_=>route with{TimeoutMs=0}};using var response=await s.SaveAsync(request with{Routes=[route]});Assert.Equal(HttpStatusCode.UnprocessableEntity,response.StatusCode);}
 [Fact] public async Task ReadOnlyTargetScopeCannotEditMapping()
 {await using var s=new PromotionScenario();await s.InitializeAsync();await using(var db=s.Api.Context())await db.Set<UserProjectScope>().Where(x=>x.UserId==s.Api.User.Id).ExecuteUpdateAsync(x=>x.SetProperty(p=>p.AccessMode,"read_only"));using var response=await s.SaveAsync(s.Mapping());Assert.Equal(HttpStatusCode.Forbidden,response.StatusCode);}
 [Fact] public async Task ExistingMatchingTargetRouteIsUpdatedAndOtherRouteIsNotHijacked()
 {await using var s=new PromotionScenario();await s.InitializeAsync();var original=s.Source.Artifact.Content.Routes[0];Guid id;await using(var db=s.Api.Context()){var route=new ApiRoute{EnvironmentId=s.TargetEnvironmentId,ApiVersionId=original.VersionId,Path=original.Path,NormalizedPath=original.Path,Methods=original.Methods.ToArray(),ClusterId=s.TargetClusterId,TimeoutMs=9000};id=route.Id;db.Add(route);await db.SaveChangesAsync();}using var saved=await s.SaveAsync(s.Mapping(id));saved.EnsureSuccessStatusCode();var candidate=await s.PrepareAsync();Assert.Equal(id,Assert.Single(candidate.Routes).Id);Assert.Equal(30000,Assert.Single(candidate.Routes).TimeoutMs);}
 [Fact] public async Task TargetSharedPolicyIsCopiedWithoutChangingOriginal()
 {await using var s=new PromotionScenario();await s.InitializeAsync(timeout:true);using var saved=await s.SaveAsync(s.Mapping());saved.EnsureSuccessStatusCode();var candidate=await s.PrepareAsync();var policy=Assert.Single(candidate.Policies);Assert.NotEqual(s.TargetPolicyId,policy.Id);Assert.Contains("25000",policy.Config);await using var db=s.Api.Context();var shared=await db.Set<Policy>().SingleAsync(p=>p.Id==s.TargetPolicyId);Assert.Equal(1,shared.VersionNo);Assert.Equal(25000,System.Text.Json.JsonDocument.Parse(shared.Config).RootElement.GetProperty("timeoutMs").GetInt32());var again=await s.PrepareAsync();Assert.Equal(policy.Id,Assert.Single(again.Policies).Id);}
 [Fact] public async Task MappingCannotHijackARouteWithAnotherPath()
 {await using var s=new PromotionScenario();await s.InitializeAsync();Guid id;await using(var db=s.Api.Context()){var route=new ApiRoute{EnvironmentId=s.TargetEnvironmentId,ApiVersionId=s.Api.Version.Id,RouteName="Separate",Path="/unrelated",NormalizedPath="/unrelated",Methods=["GET"],ClusterId=s.TargetClusterId,TimeoutMs=9000};id=route.Id;db.Add(route);await db.SaveChangesAsync();}using var response=await s.SaveAsync(s.Mapping(id));Assert.Equal(HttpStatusCode.UnprocessableEntity,response.StatusCode);await using var context=s.Api.Context();Assert.Equal("/unrelated",(await context.Set<ApiRoute>().SingleAsync(r=>r.Id==id)).Path);}
 [Fact] public async Task MappingResponseAndAuditContainIdsButNoCredentialMaterial()
 {await using var s=new PromotionScenario();await s.InitializeAsync();using var response=await s.SaveAsync(s.Mapping());response.EnsureSuccessStatusCode();var json=await response.Content.ReadAsStringAsync();Assert.DoesNotContain("secretHash",json,StringComparison.OrdinalIgnoreCase);Assert.DoesNotContain("accessKey",json,StringComparison.OrdinalIgnoreCase);await using var db=s.Api.Context();var credential=await db.Set<ApplicationCredential>().SingleAsync(c=>c.Id==s.TargetCredentialId);var audit=await db.Set<AuditLog>().SingleAsync(a=>a.Action=="promotion.mapping.save");Assert.DoesNotContain(credential.SecretHash,audit.AfterJson);Assert.DoesNotContain(credential.AccessKey,audit.AfterJson);}
 [Fact] public async Task PreparedCandidateHashAndPrivateResourcesAreStableForSameMapping()
 {await using var s=new PromotionScenario();await s.InitializeAsync(timeout:true);using var saved=await s.SaveAsync(s.Mapping());saved.EnsureSuccessStatusCode();await s.PrepareAsync();string? hash;int routes,policies;await using(var db=s.Api.Context()){hash=(await db.Set<ReleasePromotion>().SingleAsync(p=>p.Id==s.PromotionId)).CandidateHash;routes=await db.Set<ApiRoute>().CountAsync();policies=await db.Set<Policy>().CountAsync();}await s.PrepareAsync();await using var context=s.Api.Context();Assert.Equal(hash,(await context.Set<ReleasePromotion>().SingleAsync(p=>p.Id==s.PromotionId)).CandidateHash);Assert.Equal(routes,await context.Set<ApiRoute>().CountAsync());Assert.Equal(policies,await context.Set<Policy>().CountAsync());}
 [Fact] public async Task MappingCannotOverrideFrozenAuthenticationWithExtraClientFields()
 {await using var s=new PromotionScenario();await s.InitializeAsync();var m=s.Mapping();var route=m.Routes[0];using var saved=await s.Api.WriteAsync(HttpMethod.Put,$"/api/v1/release-promotions/{s.PromotionId}/mapping",new{routes=new[]{new{route.ArtifactRouteKey,route.TargetRouteId,route.ClusterId,route.TimeoutMs,route.Policies,path="/evil",methods=new[]{"DELETE"},authenticationMode="Anonymous"}},applications=m.Applications},"\"1\"");saved.EnsureSuccessStatusCode();var candidate=await s.PrepareAsync();Assert.True(Assert.Single(candidate.Routes).RequireApiKey);Assert.Equal(s.Source.Artifact.Content.Routes[0].Path,Assert.Single(candidate.Routes).Path);Assert.Equal(s.Source.Artifact.Content.Routes[0].Methods.Order(),Assert.Single(candidate.Routes).Methods.Order());}
 [Fact] public async Task AnonymousAuthenticationRemainsFrozenAndUsesIndependentPolicyCopy()
 {await using var s=new PromotionScenario();await s.InitializeAsync(anonymous:true);using var saved=await s.SaveAsync(s.Mapping() with{Applications=[]});saved.EnsureSuccessStatusCode();var candidate=await s.PrepareAsync();Assert.False(Assert.Single(candidate.Routes).RequireApiKey);Assert.Empty(candidate.Applications);Assert.Contains("Anonymous",Assert.Single(candidate.Policies).Config);}
 [Theory][InlineData(true)][InlineData(false)]
 public async Task PreparedPolicyBindingCannotDisappearOrChangeFrozenOrder(bool removed)
 {await using var s=new PromotionScenario();await s.InitializeAsync(timeout:true);using var saved=await s.SaveAsync(s.Mapping());saved.EnsureSuccessStatusCode();var candidate=await s.PrepareAsync();var route=Assert.Single(candidate.Routes);await using(var db=s.Api.Context()){if(removed)await db.Set<RoutePolicyBinding>().Where(b=>b.RouteId==route.Id).ExecuteDeleteAsync();else await db.Set<RoutePolicyBinding>().Where(b=>b.RouteId==route.Id).ExecuteUpdateAsync(x=>x.SetProperty(b=>b.Priority,99));}Assert.Equal(409,(await Assert.ThrowsAsync<WebApi.Contracts.Common.ApiException>(()=>s.PrepareAsync())).Status);}
 [Fact] public async Task RetiredApiCannotSilentlyDisappearFromPromotedArtifact()
 {await using var s=new PromotionScenario();await s.InitializeAsync();using var saved=await s.SaveAsync(s.Mapping());saved.EnsureSuccessStatusCode();await using(var db=s.Api.Context())await db.Set<Api>().Where(a=>a.Id==s.Api.Api.Id).ExecuteUpdateAsync(x=>x.SetProperty(a=>a.LifecycleStatus,"Retired").SetProperty(a=>a.VersionNo,2));Assert.Equal(422,(await Assert.ThrowsAsync<WebApi.Contracts.Common.ApiException>(()=>s.PrepareAsync())).Status);}
    [Fact]
    public async Task PreparingAnExistingRoutePreservesItsNameAndInvalidatesAnOlderEditWhenBindingsChange()
    {
        await using var s = new PromotionScenario();
        await s.InitializeAsync(timeout: true);
        await s.SeedProductionBaselineAsync();
        await PromotionSubmissionTests.Create(s);
        Guid routeId;
        await using(var db = s.Api.Context())
            routeId = await db.Set<ApiRoute>().Where(r => r.EnvironmentId == s.TargetEnvironmentId).Select(r => r.Id).SingleAsync();
        var url = $"/api/v1/routes/{routeId}";
        using var opened = await s.Api.Client.GetAsync(url);
        opened.EnsureSuccessStatusCode();
        var before = (await opened.Content.ReadFromJsonAsync<RouteDto>())!;
        var tag = opened.Headers.ETag!.Tag;
        using var saved = await s.SaveAsync(s.Mapping(routeId));
        saved.EnsureSuccessStatusCode();
        using var precheck = await PromotionSubmissionTests.Send(s, "precheck", new PromotionPrecheckRequest(ConfirmUpstreamHealth: true, UpstreamHealthComment:"人工确认目标后端健康，等待正式独立审批"));
        precheck.EnsureSuccessStatusCode();
        Assert.True((await precheck.Content.ReadFromJsonAsync<PromotionPrecheckDto>())!.CanSubmit);
        using var current = await s.Api.Client.GetAsync(url);
        current.EnsureSuccessStatusCode();
        var after = (await current.Content.ReadFromJsonAsync<RouteDto>())!;
        Assert.Equal(before.RouteName, after.RouteName);
        Assert.NotEqual(tag, current.Headers.ETag!.Tag);
        using var edited = await s.Api.WriteAsync(HttpMethod.Put, url, new SaveRouteRequest(before.Id, before.ApiVersionId, before.RouteName, before.Path, before.Methods, before.ClusterId, before.Priority, before.Enabled, before.TimeoutMs+1000, before.RequireApiKey),tag);
        Assert.Equal(HttpStatusCode.PreconditionFailed, edited.StatusCode);
    }
}
