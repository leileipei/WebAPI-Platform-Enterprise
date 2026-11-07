using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Catalog;
using WebApi.Contracts.Policies;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;
namespace WebApi.Integration.Tests;
public sealed class RoutePolicyBindingTests
{
    private static async Task<ApiFixture> Prepare()
    {var f=new ApiFixture();await f.InitializeAsync();await f.SeedCatalogAsync();await using var db=f.Context();var role=await db.Set<UserRole>().Where(r=>r.UserId==f.User.Id).Select(r=>r.RoleId).SingleAsync();foreach(var code in new[]{"policy.read","policy.write"}) {var p=new Permission {Code=code,Name=code,Module="policy"};db.Add(p);db.Add(new RolePermission {RoleId=role,PermissionId=p.Id});}await db.SaveChangesAsync();using var login=await f.LoginAsync();login.EnsureSuccessStatusCode();return f;}
    private static SaveRouteRequest Body(ApiFixture f,string path,bool key=true)=>new(null,f.Version.Id,"路由",path,["GET"],f.Cluster.Id,RequireApiKey:key);
    private static async Task<RouteDto> Route(ApiFixture f,string path,bool key=true)
    {using var response=await f.WriteAsync(HttpMethod.Post,$"/api/v1/environments/{f.Environment.Id}/routes",Body(f,path,key));response.EnsureSuccessStatusCode();return (await response.Content.ReadFromJsonAsync<RouteDto>())!;}
    private static async Task<PolicyDto> Policy(ApiFixture f,string type,string config)
    {using var response=await f.WriteAsync(HttpMethod.Post,$"/api/v1/projects/{f.Project.Id}/policies",new SavePolicyRequest("共享"+Guid.NewGuid(),type,config));response.EnsureSuccessStatusCode();return (await response.Content.ReadFromJsonAsync<PolicyDto>())!;}
    private static async Task<RoutePoliciesDto> Bind(ApiFixture f,RouteDto r,params PolicyDto[] p)
    {using var response=await f.WriteAsync(HttpMethod.Put,$"/api/v1/routes/{r.Id}/policies",new SaveRoutePoliciesRequest(p.Select((x,i)=>new PolicyBindingInput(x.Id,i)).ToArray()),$"\"{r.Revision}\"");response.EnsureSuccessStatusCode();return (await response.Content.ReadFromJsonAsync<RoutePoliciesDto>())!;}
    [Fact] public async Task AnonymousRouteCreationReturnsItsCommittedRevision()
    {
        await using var f=await Prepare();var created=await Route(f,"/anonymous",false);
        var read=(await f.Client.GetFromJsonAsync<RouteDto>($"/api/v1/routes/{created.Id}"))!;
        await using var db=f.Context();var stored=await db.Set<ApiRoute>().AsNoTracking().SingleAsync(x=>x.Id==created.Id);
        Assert.Equal("Anonymous",stored.AuthenticationMode);Assert.False(read.RequireApiKey);
        Assert.Equal(stored.Revision,read.Revision);Assert.Equal(stored.Revision,created.Revision);
    }
    [Fact] public async Task AuthenticationBindingReturnsCommittedRevisionAndIdempotentReplay()
    {
        await using var f=await Prepare();var route=await Route(f,"/a");var policy=await Policy(f,"authentication","{\"mode\":\"Anonymous\"}");
        long beforeAuth;await using(var db=f.Context())beforeAuth=(await db.Set<ApiRoute>().AsNoTracking().SingleAsync(x=>x.Id==route.Id)).AuthorizationRevision;
        var key=Guid.NewGuid().ToString("N");var body=new SaveRoutePoliciesRequest([new PolicyBindingInput(policy.Id,0)]);
        async Task<HttpResponseMessage> Send()
        {
            using var request=new HttpRequestMessage(HttpMethod.Put,$"/api/v1/routes/{route.Id}/policies"){Content=JsonContent.Create(body)};
            request.Headers.Add("X-CSRF-Token",await f.CsrfAsync());request.Headers.Add("Idempotency-Key",key);request.Headers.Add("If-Match",$"\"{route.Revision}\"");
            return await f.Client.SendAsync(request);
        }
        using var first=await Send();first.EnsureSuccessStatusCode();var result=(await first.Content.ReadFromJsonAsync<RoutePoliciesDto>())!;
        using var replay=await Send();replay.EnsureSuccessStatusCode();var replayed=(await replay.Content.ReadFromJsonAsync<RoutePoliciesDto>())!;
        var read=(await f.Client.GetFromJsonAsync<RouteDto>($"/api/v1/routes/{route.Id}"))!;
        await using var check=f.Context();var stored=await check.Set<ApiRoute>().AsNoTracking().SingleAsync(x=>x.Id==route.Id);
        Assert.Equal("Anonymous",stored.AuthenticationMode);Assert.Equal(beforeAuth+1,stored.AuthorizationRevision);
        Assert.Equal(route.Revision+1,stored.Revision);Assert.Equal(stored.Revision,read.Revision);Assert.Equal(stored.Revision,result.Revision);
        Assert.Equal(result.Revision,replayed.Revision);Assert.Equal(first.Headers.ETag?.Tag,replay.Headers.ETag?.Tag);Assert.Equal($"\"{stored.Revision}\"",first.Headers.ETag?.Tag);
    }
    [Fact] public async Task AuthenticationBindingKeepsPriorMethodGrantAsStaleEvidence()
    {
        await using var f=await Prepare();var route=await Route(f,"/a");var policy=await Policy(f,"authentication","{\"mode\":\"Anonymous\"}");
        var app=new ApplicationRecord{OrganizationId=f.Organization.Id,ProjectId=f.Project.Id,Code="GRANT-APP",Name="Grant app",Owner="Fixture"};
        var permission=new ApplicationApiPermission{ApplicationId=app.Id,ApiId=f.Api.Id,EnvironmentId=f.Environment.Id,Mode="RouteMethod",CreatedBy=f.User.Id};
        long prior;
        await using(var db=f.Context())
        {
            prior=(await db.Set<ApiRoute>().AsNoTracking().SingleAsync(x=>x.Id==route.Id)).AuthorizationRevision;
            db.Add(app);db.Add(permission);db.Add(new ApplicationRouteMethodGrant{PermissionId=permission.Id,EnvironmentId=f.Environment.Id,ApiId=f.Api.Id,RouteId=route.Id,Method="GET",AuthorizationRevision=prior});
            await db.SaveChangesAsync();
        }
        var bound=await Bind(f,route,policy);
        await using var read=f.Context();var current=await read.Set<ApiRoute>().AsNoTracking().SingleAsync(x=>x.Id==route.Id);
        var historical=await read.Set<ApplicationRouteMethodGrant>().AsNoTracking().SingleAsync(x=>x.PermissionId==permission.Id);
        Assert.Equal(bound.Revision,current.Revision);Assert.Equal(prior+1,current.AuthorizationRevision);
        Assert.Equal(prior,historical.AuthorizationRevision);Assert.NotEqual(current.AuthorizationRevision,historical.AuthorizationRevision);
    }
    [Fact] public async Task BindingRequiresBothPermissionsAndBumpsRouteRevision()
    {await using var f=await Prepare();var r=await Route(f,"/a");var p=await Policy(f,"timeout","{\"timeoutMs\":1000}");var changed=await Bind(f,r,p);Assert.Equal(r.Revision+1,changed.Revision);await using(var db=f.Context()) {var permission=await db.Set<Permission>().SingleAsync(x=>x.Code=="policy.write");await db.Set<RolePermission>().Where(x=>x.PermissionId==permission.Id).ExecuteDeleteAsync();}using var denied=await f.WriteAsync(HttpMethod.Put,$"/api/v1/routes/{r.Id}/policies",new SaveRoutePoliciesRequest([]),$"\"{changed.Revision}\"");Assert.Equal(HttpStatusCode.Forbidden,denied.StatusCode);}
    [Fact] public async Task ReplaceDuplicateAndForeignPolicyRollsBack()
    {await using var f=await Prepare();var r=await Route(f,"/a");var p=await Policy(f,"timeout","{\"timeoutMs\":1000}");var before=await f.Client.GetFromJsonAsync<RoutePoliciesDto>($"/api/v1/routes/{r.Id}/policies");foreach(var ids in new[]{new[]{p.Id,p.Id},new[]{Guid.NewGuid()}}) {using var denied=await f.WriteAsync(HttpMethod.Put,$"/api/v1/routes/{r.Id}/policies",new SaveRoutePoliciesRequest(ids.Select(x=>new PolicyBindingInput(x,0)).ToArray()),$"\"{r.Revision}\"");Assert.Equal(HttpStatusCode.UnprocessableEntity,denied.StatusCode);}var after=await f.Client.GetFromJsonAsync<RoutePoliciesDto>($"/api/v1/routes/{r.Id}/policies");Assert.Equal(before!.Revision,after!.Revision);Assert.Equal(before.Bindings.Select(b=>b.PolicyId),after.Bindings.Select(b=>b.PolicyId));}
    [Fact] public async Task PathEditKeepsSharedAuthentication()
    {await using var f=await Prepare();var r=await Route(f,"/a");var p=await Policy(f,"authentication","{\"mode\":\"Anonymous\"}");var b=await Bind(f,r,p);using var edit=await f.WriteAsync(HttpMethod.Put,$"/api/v1/routes/{r.Id}",Body(f,"/changed",false) with {Id=r.Id,TimeoutMs=r.TimeoutMs},$"\"{b.Revision}\"");edit.EnsureSuccessStatusCode();var after=await f.Client.GetFromJsonAsync<RoutePoliciesDto>($"/api/v1/routes/{r.Id}/policies");Assert.Equal(p.Id,Assert.Single(after!.Bindings).PolicyId);Assert.False((await edit.Content.ReadFromJsonAsync<RouteDto>())!.RequireApiKey);}
    [Fact] public async Task AuthToggleDetachesOnlyCurrentRoute()
    {await using var f=await Prepare();var a=await Route(f,"/a");var b=await Route(f,"/b");var p=await Policy(f,"authentication","{\"mode\":\"Anonymous\"}");var first=await Bind(f,a,p);await Bind(f,b,p);using var edit=await f.WriteAsync(HttpMethod.Put,$"/api/v1/routes/{a.Id}",Body(f,"/a") with {Id=a.Id,TimeoutMs=a.TimeoutMs},$"\"{first.Revision}\"");edit.EnsureSuccessStatusCode();var other=await f.Client.GetFromJsonAsync<RoutePoliciesDto>($"/api/v1/routes/{b.Id}/policies");Assert.Equal(p.Id,Assert.Single(other!.Bindings).PolicyId);Assert.Equal(p.VersionNo,other.Bindings[0].Policy.VersionNo);Assert.False((await f.Client.GetFromJsonAsync<RouteDto>($"/api/v1/routes/{b.Id}"))!.RequireApiKey);var current=await f.Client.GetFromJsonAsync<RoutePoliciesDto>($"/api/v1/routes/{a.Id}/policies");Assert.NotEqual(p.Id,Assert.Single(current!.Bindings).PolicyId);}
    [Fact] public async Task DisableAnonymousDefaultsToApiKey()
    {
        await using var f=await Prepare();var r=await Route(f,"/a");var p=await Policy(f,"authentication","{\"mode\":\"Anonymous\"}");await Bind(f,r,p);
        long beforeAuth,beforeRevision;await using(var before=f.Context()){var stored=await before.Set<ApiRoute>().AsNoTracking().SingleAsync(x=>x.Id==r.Id);beforeAuth=stored.AuthorizationRevision;beforeRevision=stored.Revision;}
        using var disabled=await f.WriteAsync(HttpMethod.Put,$"/api/v1/policies/{p.Id}",new SavePolicyRequest(p.Name,p.Type,p.Config,false),"\"1\"");disabled.EnsureSuccessStatusCode();
        var read=(await f.Client.GetFromJsonAsync<RouteDto>($"/api/v1/routes/{r.Id}"))!;Assert.True(read.RequireApiKey);
        await using var after=f.Context();var current=await after.Set<ApiRoute>().AsNoTracking().SingleAsync(x=>x.Id==r.Id);
        Assert.Equal("ApiKey",current.AuthenticationMode);Assert.Equal(beforeAuth+1,current.AuthorizationRevision);Assert.Equal(beforeRevision+1,current.Revision);Assert.Equal(current.Revision,read.Revision);
    }
    [Fact] public async Task TimeoutBindingOverridesBaseValue()
    {await using var f=await Prepare();var r=await Route(f,"/a");var p=await Policy(f,"timeout","{\"timeoutMs\":1000}");await Bind(f,r,p);Assert.Equal(1000,(await f.Client.GetFromJsonAsync<RouteDto>($"/api/v1/routes/{r.Id}"))!.EffectiveTimeoutMs);await using var db=f.Context();Assert.Equal(30000,(await db.Set<ApiRoute>().SingleAsync()).TimeoutMs);}
    [Fact] public async Task PathEditAndTimeoutUnbindPreserveBaseTimeout()
    {
        await using var f=await Prepare();var r=await Route(f,"/a");var p=await Policy(f,"timeout","{\"timeoutMs\":1000}");await Bind(f,r,p);
        var editor=(await f.Client.GetFromJsonAsync<RouteDto>($"/api/v1/routes/{r.Id}"))!;Assert.Equal(30000,editor.TimeoutMs);
        using var edit=await f.WriteAsync(HttpMethod.Put,$"/api/v1/routes/{r.Id}",Body(f,"/changed") with {Id=r.Id,TimeoutMs=editor.TimeoutMs},$"\"{editor.Revision}\"");edit.EnsureSuccessStatusCode();var saved=(await edit.Content.ReadFromJsonAsync<RouteDto>())!;
        using var unbind=await f.WriteAsync(HttpMethod.Put,$"/api/v1/routes/{r.Id}/policies",new SaveRoutePoliciesRequest([]),$"\"{saved.Revision}\"");unbind.EnsureSuccessStatusCode();Assert.Equal(30000,(await f.Client.GetFromJsonAsync<RouteDto>($"/api/v1/routes/{r.Id}"))!.TimeoutMs);await using var db=f.Context();Assert.Equal(30000,(await db.Set<ApiRoute>().SingleAsync()).TimeoutMs);
    }
    [Fact] public async Task SharedPolicyEditValidatesEveryBoundRoute()
    {await using var f=await Prepare();var r=await Route(f,"/a");var auth=await Policy(f,"authentication","{\"mode\":\"ApiKey\"}");var rate=await Policy(f,"rate_limit","{\"algorithm\":\"TokenBucket\",\"keyBy\":\"ApplicationRoute\",\"refillTokens\":1,\"windowMs\":1000,\"burst\":3,\"redisFailureMode\":\"Reject\"}");await Bind(f,r,auth,rate);using var invalid=await f.WriteAsync(HttpMethod.Put,$"/api/v1/policies/{auth.Id}",new SavePolicyRequest(auth.Name,auth.Type,"{\"mode\":\"Anonymous\"}"),"\"1\"");Assert.Equal(HttpStatusCode.UnprocessableEntity,invalid.StatusCode);await using var db=f.Context();Assert.Equal(1,(await db.Set<Policy>().SingleAsync(x=>x.Id==auth.Id)).VersionNo);}
}
