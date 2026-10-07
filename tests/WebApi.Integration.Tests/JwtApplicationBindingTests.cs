using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Catalog;
using WebApi.Contracts.Policies;
using WebApi.Contracts.Releases;
using WebApi.Contracts.Runtime;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;

namespace WebApi.Integration.Tests;

public sealed class JwtApplicationBindingTests
{
    internal static string Jwt(Guid app, string issuer = "https://issuer.example/realm")
    {
        var modulus = new byte[256]; modulus[0] = 128; modulus[^1] = 1;
        var n = Convert.ToBase64String(modulus).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return JsonSerializer.Serialize(new { mode="JWT", issuer, audiences=new[]{"orders-api"}, allowedAlgorithms=new[]{"RS256"}, allowedTokenTypes=new[]{"JWT","at+jwt"}, clockSkewSeconds=30, maxTokenLifetimeSeconds=3600, applicationClaim="azp", applicationMappings=new[]{new {claimValue="client",applicationId=app}}, jwks=new {keys=new[]{new {kid="rsa-1",kty="RSA",alg="RS256",use="sig",n,e="AQAB"}}}, forwardBearer=false });
    }
    private static async Task<ApiFixture> Prepare(bool fixture = false)
    {
        var f = new ApiFixture(); await f.InitializeAsync(b =>
        {
            b.Configuration["GatewayPolicies:Cache:MaxEntryBytes"] = "262144";
            b.Configuration["GatewayPolicies:Cache:AllowedVaryHeaders:0"] = "X-Region";
            if (fixture) { b.Configuration["GatewayPolicies:FixtureEnabled"] = "true"; b.Configuration["GatewayPolicies:Jwt:HttpFixtureOrigins:0"] = "http://127.0.0.1:4900"; }
        });
        await f.SeedReleaseAsync(); await using var db=f.Context(); var role=await db.Set<UserRole>().Where(r=>r.UserId==f.User.Id).Select(r=>r.RoleId).SingleAsync();
        foreach(var code in new[]{"policy.read","policy.write"}) { var p=new Permission {Code=code,Name=code,Module="policy"}; db.Add(p); db.Add(new RolePermission {RoleId=role,PermissionId=p.Id}); }
        await db.SaveChangesAsync(); using var login=await f.LoginAsync(); login.EnsureSuccessStatusCode(); return f;
    }
    private static async Task<PolicyDto> Create(ApiFixture f, Guid? application = null)
    {
        using var r=await f.WriteAsync(HttpMethod.Post,$"/api/v1/projects/{f.Project.Id}/policies",new SavePolicyRequest("JWT业务认证","authentication",Jwt(application??f.Application.Id)));
        r.EnsureSuccessStatusCode(); return (await r.Content.ReadFromJsonAsync<PolicyDto>())!;
    }
    private static async Task<RouteDto> Bind(ApiFixture f, PolicyDto policy)
    {
        using var r=await f.WriteAsync(HttpMethod.Post,$"/api/v1/environments/{f.Environment.Id}/routes",f.RouteBody("/jwt")); r.EnsureSuccessStatusCode(); var route=(await r.Content.ReadFromJsonAsync<RouteDto>())!;
        using var b=await f.WriteAsync(HttpMethod.Put,$"/api/v1/routes/{route.Id}/policies",new SaveRoutePoliciesRequest([new(policy.Id,0)]),$"\"{route.Revision}\""); b.EnsureSuccessStatusCode();
        return (await f.Client.GetFromJsonAsync<RouteDto>($"/api/v1/routes/{route.Id}"))!;
    }
    private static async Task RemovePermission(ApiFixture f,string code)
    {
        await using var db=f.Context(); var id=await db.Set<Permission>().Where(p=>p.Code==code).Select(p=>p.Id).SingleAsync(); await db.Set<RolePermission>().Where(p=>p.PermissionId==id).ExecuteDeleteAsync();
    }
    private static async Task<JsonElement> Error(HttpResponseMessage response,HttpStatusCode expected)
    { Assert.Equal(expected,response.StatusCode); return await response.Content.ReadFromJsonAsync<JsonElement>(); }

    [Fact]
    public async Task ForeignAndUnreadableMappingReturnsUniformError()
    {
        await using var f=await Prepare(); Guid foreign;
        await using(var db=f.Context()) { var org=new Organization {Code="FOREIGN",Name="秘密组织"}; var app=new ApplicationRecord {OrganizationId=org.Id,Code="SECRET",Name="秘密应用",Owner="秘密负责人"}; foreign=app.Id; db.AddRange(org,app); await db.SaveChangesAsync(); }
        var errors=new List<string>();
        foreach(var id in new[]{foreign,Guid.NewGuid()}) { using var r=await f.WriteAsync(HttpMethod.Post,$"/api/v1/projects/{f.Project.Id}/policies",new SavePolicyRequest("JWT","authentication",Jwt(id))); errors.Add((await Error(r,HttpStatusCode.UnprocessableEntity)).GetProperty("code").GetString()!); Assert.DoesNotContain("秘密",await r.Content.ReadAsStringAsync()); }
        await RemovePermission(f,"app.read"); using var unreadable=await f.WriteAsync(HttpMethod.Post,$"/api/v1/projects/{f.Project.Id}/policies",new SavePolicyRequest("JWT","authentication",Jwt(f.Application.Id))); errors.Add((await Error(unreadable,HttpStatusCode.UnprocessableEntity)).GetProperty("code").GetString()!);
        Assert.Single(errors.Distinct()); await using var check=f.Context(); Assert.False(await check.Set<Policy>().AnyAsync());
    }
    [Fact]
    public async Task OrgPolicyCannotBindProjectApplication()
    {
        await using var f=await Prepare(); using var bad=await f.WriteAsync(HttpMethod.Post,$"/api/v1/organizations/{f.Organization.Id}/policies",new SavePolicyRequest("JWT","authentication",Jwt(f.Application.Id))); await Error(bad,HttpStatusCode.UnprocessableEntity);
        await using(var db=f.Context()) { (await db.Set<ApplicationRecord>().SingleAsync()).ProjectId=null; await db.SaveChangesAsync(); }
        using var good=await f.WriteAsync(HttpMethod.Post,$"/api/v1/organizations/{f.Organization.Id}/policies",new SavePolicyRequest("JWT","authentication",Jwt(f.Application.Id))); good.EnsureSuccessStatusCode();
    }
    [Fact]
    public async Task CopyRevalidatesDestinationScope()
    {
        await using var f=await Prepare(); var policy=await Create(f); Guid other;
        await using(var db=f.Context()) { var project=new Project {OrganizationId=f.Organization.Id,Code="OTHER",Name="其他项目"}; other=project.Id; db.Add(project); await db.SaveChangesAsync(); }
        using var bad=await f.WriteAsync(HttpMethod.Post,$"/api/v1/policies/{policy.Id}/copy",new CopyPolicyRequest("JWT副本",other)); await Error(bad,HttpStatusCode.UnprocessableEntity);
        using var org=await f.WriteAsync(HttpMethod.Post,$"/api/v1/policies/{policy.Id}/copy",new CopyPolicyRequest("组织副本",null)); await Error(org,HttpStatusCode.UnprocessableEntity);
        using var good=await f.WriteAsync(HttpMethod.Post,$"/api/v1/policies/{policy.Id}/copy",new CopyPolicyRequest("同范围副本",f.Project.Id)); good.EnsureSuccessStatusCode();
        await using var db2=f.Context(); Assert.Equal(2,await db2.Set<Policy>().CountAsync());
    }
    [Fact]
    public async Task MappedZeroGrantApplicationIsFrozenAndRevisionRequired()
    {
        await using var f=await Prepare(); await Bind(f,await Create(f)); var input=await f.PreviewReleaseRequestAsync();
        Assert.Contains(input.ResourceRevisions,r=>r.Type=="application"&&r.Id==f.Application.Id);
        using var missing=await ApiFixture.CommandAsync(f.Client,$"/api/v1/environments/{f.Environment.Id}/releases",input with {ResourceRevisions=input.ResourceRevisions.Where(r=>r.Type!="application").ToArray()}); await Error(missing,HttpStatusCode.UnprocessableEntity);
        await using(var db=f.Context()) { (await db.Set<ApplicationRecord>().SingleAsync()).Revision++; await db.SaveChangesAsync(); }
        using var stale=await ApiFixture.CommandAsync(f.Client,$"/api/v1/environments/{f.Environment.Id}/releases",input); await Error(stale,HttpStatusCode.PreconditionFailed);
        var id=await f.CreateSubmittedReleaseAsync(); var release=(await f.Client.GetFromJsonAsync<ReleaseDto>($"/api/v1/releases/{id}"))!;
        var app=Assert.Single(release.FrozenCandidate!.Applications); Assert.Equal(f.Application.Id,app.Application.Id); Assert.Empty(app.Permissions); Assert.Empty(app.Credentials);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteMappedApplicationIsProtected(bool frozenOnly)
    {
        await using var f=await Prepare(); var policy=await Create(f);
        if(frozenOnly) { await Bind(f,policy); await f.CreateSubmittedReleaseAsync(); await using var db=f.Context(); await db.Set<RoutePolicyBinding>().ExecuteDeleteAsync(); await db.Set<Policy>().ExecuteDeleteAsync(); }
        using var r=await f.WriteAsync(HttpMethod.Delete,$"/api/v1/applications/{f.Application.Id}",etag:"\"1\""); await Error(r,HttpStatusCode.Conflict);
        Assert.DoesNotContain("JWT业务认证",await r.Content.ReadAsStringAsync()); await using var check=f.Context(); Assert.True(await check.Set<ApplicationRecord>().AnyAsync(a=>a.Id==f.Application.Id));
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RuntimeMappedApplicationIsProtectedAndBaselineRetained(bool desired)
    {
        await using var f=await Prepare(); var config=Jwt(f.Application.Id); var runtimePolicy=Guid.NewGuid(); var oldApi=Guid.NewGuid();
        await using(var db=f.Context())
        {
            var version=new GatewayConfigVersion {EnvironmentId=f.Environment.Id,VersionNo=1,CreatedBy=f.User.Id}; db.Add(version);
            var snapshot=new RuntimeSnapshot("2.2",f.Environment.Id,1,DateTimeOffset.UtcNow,[new(Guid.NewGuid(),oldApi,Guid.NewGuid(),Guid.NewGuid(),"/old-jwt",["GET"],1,30000,false,[new(runtimePolicy,0)])],[],[new(runtimePolicy,"authentication",config)],[]);
            db.Add(new GatewayConfigSnapshot {ConfigVersionId=version.Id,PayloadBytes=CanonicalJson.Serialize(snapshot)});
            if(desired) (await db.Set<EnvironmentRecord>().SingleAsync()).DesiredConfigVersion=1;
            else db.Add(new GatewayNode {EnvironmentId=f.Environment.Id,NodeName="停用旧节点",InstanceId=Guid.NewGuid().ToString(),Enabled=false,CurrentConfigVersion=1});
            await db.SaveChangesAsync();
        }
        using var denied=await f.WriteAsync(HttpMethod.Delete,$"/api/v1/applications/{f.Application.Id}",etag:"\"1\""); await Error(denied,HttpStatusCode.Conflict);
        if(desired) { var preview=await f.PreviewReleaseRequestAsync(1); Assert.Contains(preview.ResourceRevisions,r=>r.Type=="application"&&r.Id==f.Application.Id); }
    }
    [Fact]
    public async Task CorruptProtectedSnapshotBlocksDeletionWithoutDisclosure()
    {
        await using var f=await Prepare();
        await using(var db=f.Context())
        {
            var version=new GatewayConfigVersion {EnvironmentId=f.Environment.Id,VersionNo=1,CreatedBy=f.User.Id}; db.Add(version);
            db.Add(new GatewayConfigSnapshot {ConfigVersionId=version.Id,PayloadBytes=System.Text.Encoding.UTF8.GetBytes("{\"schemaVersion\":\"2.2\",\"policies\":null}")});
            (await db.Set<EnvironmentRecord>().SingleAsync()).DesiredConfigVersion=1; await db.SaveChangesAsync();
        }
        using var denied=await f.WriteAsync(HttpMethod.Delete,$"/api/v1/applications/{f.Application.Id}",etag:"\"1\""); await Error(denied,HttpStatusCode.Conflict);
        var body=await denied.Content.ReadAsStringAsync(); Assert.DoesNotContain(f.Application.Id.ToString(),body); Assert.DoesNotContain("NullReference",body);
        await using var check=f.Context(); Assert.True(await check.Set<ApplicationRecord>().AnyAsync(a=>a.Id==f.Application.Id));
    }
    [Fact]
    public async Task FrozenMappingUnaffectedByLaterEdits()
    {
        await using var f=await Prepare(); var policy=await Create(f); await Bind(f,policy); var id=await f.CreateSubmittedReleaseAsync(); var before=(await f.Client.GetFromJsonAsync<ReleaseDto>($"/api/v1/releases/{id}"))!;
        using var edit=await f.WriteAsync(HttpMethod.Put,$"/api/v1/policies/{policy.Id}",new SavePolicyRequest(policy.Name,"authentication",Jwt(f.Application.Id,"https://new-issuer.example")),"\"1\""); edit.EnsureSuccessStatusCode();
        await using(var db=f.Context()) { var app=await db.Set<ApplicationRecord>().SingleAsync(); app.Status="Disabled"; app.Revision++; await db.SaveChangesAsync(); }
        var after=(await f.Client.GetFromJsonAsync<ReleaseDto>($"/api/v1/releases/{id}"))!; Assert.Equal(before.CandidateHash,after.CandidateHash); Assert.Equal("Active",Assert.Single(after.FrozenCandidate!.Applications).Application.Status); Assert.Contains("https://issuer.example/realm",after.FrozenCandidate.Policies!.Single().Config);
    }
    [Fact]
    public async Task DeploymentLimitsEnforceHttpIssuerExtraVaryAndEntrySize()
    {
        await using var f=await Prepare(true);
        foreach(var issuer in new[]{"http://127.0.0.1:4901/realm","http://localhost:4900/realm","http://public.example/realm"}) { using var r=await f.WriteAsync(HttpMethod.Post,"/api/v1/policies/validate",new ValidatePolicyRequest(new(f.Organization.Id,f.Project.Id),"authentication",Jwt(f.Application.Id,issuer))); await Error(r,HttpStatusCode.UnprocessableEntity); }
        using var allowed=await f.WriteAsync(HttpMethod.Post,"/api/v1/policies/validate",new ValidatePolicyRequest(new(f.Organization.Id,f.Project.Id),"authentication",Jwt(f.Application.Id,"http://127.0.0.1:4900/realm"))); allowed.EnsureSuccessStatusCode();
        string Cache(string header,int max) => JsonSerializer.Serialize(new {ttlSeconds=60,maxEntryBytes=max,varyHeaders=new[]{header},identityPartition="VerifiedIdentity",redisFailureMode="Bypass"});
        foreach(var source in new[]{Cache("X-Unregistered",1024),Cache("Accept",262145)}) { using var r=await f.WriteAsync(HttpMethod.Post,"/api/v1/policies/validate",new ValidatePolicyRequest(new(f.Organization.Id,f.Project.Id),"cache",source)); await Error(r,HttpStatusCode.UnprocessableEntity); }
        using var good=await f.WriteAsync(HttpMethod.Post,"/api/v1/policies/validate",new ValidatePolicyRequest(new(f.Organization.Id,f.Project.Id),"cache",Cache("x-region",262144))); good.EnsureSuccessStatusCode();
        using var limits=await f.Client.GetAsync($"/api/v1/policies/limits?organizationId={f.Organization.Id}&projectId={f.Project.Id}"); limits.EnsureSuccessStatusCode(); var dto=await limits.Content.ReadFromJsonAsync<JsonElement>(); Assert.Equal(262144,dto.GetProperty("maxEntryBytes").GetInt32()); Assert.Contains(dto.GetProperty("allowedVaryHeaders").EnumerateArray(),v=>v.GetString()=="X-Region");
        await RemovePermission(f,"policy.read"); using var denied=await f.Client.GetAsync($"/api/v1/policies/limits?organizationId={f.Organization.Id}&projectId={f.Project.Id}"); Assert.Equal(HttpStatusCode.NotFound,denied.StatusCode);
    }
    [Fact]
    public async Task LegacyRouteEchoPreservesJwtAndExplicitAnonymousChangeRequiresPolicyWrite()
    {
        await using var f=await Prepare(); var policy=await Create(f); var route=await Bind(f,policy);
        var json=await f.Client.GetFromJsonAsync<JsonElement>($"/api/v1/routes/{route.Id}"); Assert.Equal("JWT",json.GetProperty("effectiveAuthenticationMode").GetString());
        await RemovePermission(f,"policy.write");
        object Body(string? change,bool key) => new {id=route.Id,apiVersionId=route.ApiVersionId,routeName="元数据编辑",path="/changed",methods=route.Methods,clusterId=route.ClusterId,priority=route.Priority,enabled=true,timeoutMs=route.TimeoutMs,requireApiKey=key,authenticationChange=change};
        using var echo=await f.WriteAsync(HttpMethod.Put,$"/api/v1/routes/{route.Id}",Body(null,false),$"\"{route.Revision}\""); echo.EnsureSuccessStatusCode(); route=(await echo.Content.ReadFromJsonAsync<RouteDto>())!;
        var bindings=await f.Client.GetFromJsonAsync<RoutePoliciesDto>($"/api/v1/routes/{route.Id}/policies"); Assert.Equal(policy.Id,Assert.Single(bindings!.Bindings).PolicyId);
        using var denied=await f.WriteAsync(HttpMethod.Put,$"/api/v1/routes/{route.Id}",Body("Anonymous",false),$"\"{route.Revision}\""); await Error(denied,HttpStatusCode.Forbidden);
        using var conflict=await f.WriteAsync(HttpMethod.Put,$"/api/v1/routes/{route.Id}",Body("Anonymous",true),$"\"{route.Revision}\""); await Error(conflict,HttpStatusCode.UnprocessableEntity);
    }
}
