using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WebApi.ControlPlane;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Integration.Tests.Support;
public sealed class ApiFixture : IAsyncDisposable
{
    public PostgresDatabase Database { get; } = new();
    public UserRecord User { get; } = new() { Username = "actor_"+Guid.NewGuid().ToString("N"), DisplayName="测试用户", SecurityStamp=Guid.NewGuid().ToString("N") };
    public string Password { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    private WebApplication? app;
    public HttpClient Client { get; private set; } = null!;
    public WebApiDbContext Context() => Database.Context();
    public IServiceScope Services() => app!.Services.CreateScope();
    public async Task InitializeAsync(Action<WebApplicationBuilder>? configure=null)
    {
        await Database.InitializeAsync();
        await using(var db=Context()) { await db.Database.MigrateAsync(); User.PasswordHash=new PasswordHasher<UserRecord>().HashPassword(User,Password); db.Add(User); await db.SaveChangesAsync(); }
        app=ControlPlaneApp.Build(["--environment","Development"],builder=>{
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Configuration["ConnectionStrings:WebApi"]=Database.ConnectionString;
            builder.Logging.ClearProviders();
            configure?.Invoke(builder);
        });
        await app.StartAsync();
        var url=app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        Client=new HttpClient(new HttpClientHandler { CookieContainer=new CookieContainer(), AllowAutoRedirect=false }) { BaseAddress=new Uri(url) };
    }
    public async Task<string> CsrfAsync()
    {
        using var response=await Client.GetAsync("/api/v1/auth/csrf");
        if(!response.IsSuccessStatusCode) return "missing-token";
        var body=await response.Content.ReadFromJsonAsync<Dictionary<string,string>>(); return body!["token"];
    }
    public async Task<HttpResponseMessage> LoginAsync(string? password=null)
    {
        var token=await CsrfAsync(); using var request=new HttpRequestMessage(HttpMethod.Post,"/api/v1/auth/login") {Content=JsonContent.Create(new {username=User.Username,password=password??Password})};
        request.Headers.Add("X-CSRF-Token",token); return await Client.SendAsync(request);
    }
    public Organization Organization { get; } = new() {Code="ORG",Name="组织"};
    public Project Project { get; } = new() {Code="PROJECT",Name="项目"};
    public EnvironmentRecord Environment { get; } = new() {Code="TEST",Name="测试",SortOrder=0};
    public async Task SeedScopeAsync(string mode="read_write",bool platformAdmin=false)
    {
        await using var db=Context();Project.OrganizationId=Organization.Id;Environment.ProjectId=Project.Id;
        db.AddRange(Organization,Project,Environment);
        var role=new Role {Code=platformAdmin?"PlatformAdmin":"ProjectAdmin",Name="测试角色",IsSystem=platformAdmin,OrganizationId=platformAdmin?null:Organization.Id};db.Add(role);
        db.Add(new UserRole {UserId=User.Id,RoleId=role.Id});
        string[] codes=["organization.read","organization.write","project.read","project.write","environment.read","environment.write","user.manage","role.manage","scope.manage","audit.read"];
        foreach(var code in codes) {var permission=new Permission {Code=code,Module=code.Split('.')[0],Name=code};db.Add(permission);db.Add(new RolePermission {RoleId=role.Id,PermissionId=permission.Id});}
        db.Add(new UserProjectScope {UserId=User.Id,OrganizationId=Organization.Id,AccessMode=mode});await db.SaveChangesAsync();
    }
    public async Task<HttpResponseMessage> WriteAsync(HttpMethod method,string path,object? body=null,string? etag=null)
    {
        var token=await CsrfAsync();using var request=new HttpRequestMessage(method,path) {Content=body is null?null:JsonContent.Create(body)};
        request.Headers.Add("X-CSRF-Token",token);request.Headers.Add("Idempotency-Key",Guid.NewGuid().ToString("N"));if(etag is not null) request.Headers.Add("If-Match",etag);return await Client.SendAsync(request);
    }
    public Api Api { get; } = new() {Code="ORDERS",Name="订单",LifecycleStatus="Draft"};
    public ApiVersion Version { get; } = new() {Version="1.0.0",Status="Draft",ChangeType="compatible"};
    public UpstreamCluster Cluster { get; } = new() {Name="OrdersBackend",LoadBalancingPolicy="RoundRobin",HealthCheckPath="/health",HealthCheckIntervalSec=30};
    public UpstreamDestination Destination { get; } = new() {Name="backend",Address="http://test-backend:8080/",Weight=1};
    public async Task SeedCatalogAsync()
    {
        await SeedScopeAsync();await using var db=Context();Api.OrganizationId=Organization.Id;Api.ProjectId=Project.Id;Api.OwnerUserId=User.Id;Version.ApiId=Api.Id;Version.CreatedBy=User.Id;Cluster.ProjectId=Project.Id;Cluster.EnvironmentId=Environment.Id;Destination.ClusterId=Cluster.Id;db.AddRange(Api,Version,Cluster,Destination);
        var roleId=await db.Set<UserRole>().Where(x=>x.UserId==User.Id).Select(x=>x.RoleId).SingleAsync();
        string[] codes=["api.read","api.create","api.edit","api.version.read","api.version.write","api.schema.read","api.schema.write","route.read","route.write","cluster.read","cluster.write"];
        foreach(var code in codes) {var p=new Permission {Code=code,Module=code.Split('.')[0],Name=code};db.Add(p);db.Add(new RolePermission {RoleId=roleId,PermissionId=p.Id});}await db.SaveChangesAsync();
    }
    public object RouteBody(string path,string method="GET",Guid? cluster=null,Guid? id=null)=>new {id,apiVersionId=Version.Id,routeName="Orders",path,methods=new[]{method},clusterId=cluster??Cluster.Id,priority=100,enabled=true,timeoutMs=30000};
    public ApplicationRecord Application { get; } = new() {Code="CONSUMER",Name="消费方",Owner="测试所有者"};
    public async Task SeedConsumerAsync()
    {
        await SeedCatalogAsync();await using var db=Context();Application.OrganizationId=Organization.Id;Application.ProjectId=Project.Id;db.Add(Application);
        var roleId=await db.Set<UserRole>().Where(x=>x.UserId==User.Id).Select(x=>x.RoleId).SingleAsync();string[] codes=["app.read","app.write","credential.manage","app.permission.manage"];
        foreach(var code in codes) {var p=new Permission {Code=code,Module=code.Split('.')[0],Name=code};db.Add(p);db.Add(new RolePermission {RoleId=roleId,PermissionId=p.Id});}await db.SaveChangesAsync();
    }
    public ApprovalFlow ApprovalFlow {get;}=new() {Name="生产两级审批",ScopeType="environment"};
    private readonly List<HttpClient> extraClients=[];
    public async Task SeedReleaseAsync()
    {
        await SeedConsumerAsync();await using var db=Context();ApprovalFlow.OrganizationId=Organization.Id;db.Add(ApprovalFlow);
        db.Add(new ApprovalStep {FlowId=ApprovalFlow.Id,StepOrder=1,RoleCode="ApiApprover",RequiredCount=1});db.Add(new ApprovalStep {FlowId=ApprovalFlow.Id,StepOrder=2,RoleCode="SecurityReviewer",RequiredCount=1});
        var env=await db.Set<EnvironmentRecord>().SingleAsync();env.IsProduction=true;env.ReleasePolicyId=ApprovalFlow.Id;
        var roleId=await db.Set<UserRole>().Where(x=>x.UserId==User.Id).Select(x=>x.RoleId).SingleAsync();string[] codes=["release.read","release.create","release.publish","release.rollback","approval.act","api.approve"];
        foreach(var code in codes) {var p=new Permission {Code=code,Module=code.Split('.')[0],Name=code};db.Add(p);db.Add(new RolePermission {RoleId=roleId,PermissionId=p.Id});}await db.SaveChangesAsync();
    }
    public async Task<(UserRecord User,HttpClient Client)> NewReviewerAsync(params string[] roleCodes)
    {
        var user=new UserRecord {Username="reviewer_"+Guid.NewGuid().ToString("N"),DisplayName="独立审核",SecurityStamp=Guid.NewGuid().ToString("N")};var password=Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));user.PasswordHash=new PasswordHasher<UserRecord>().HashPassword(user,password);
        await using(var db=Context()) {db.Add(user);db.Add(new UserProjectScope {UserId=user.Id,OrganizationId=Organization.Id,AccessMode="read_write"});foreach(var code in roleCodes) {var role=await db.Set<Role>().SingleOrDefaultAsync(r=>r.Code==code&&r.OrganizationId==Organization.Id);if(role is null) {role=new Role {Code=code,Name=code,OrganizationId=Organization.Id};db.Add(role);foreach(var permission in await db.Set<Permission>().Where(p=>p.Code=="approval.act"||p.Code=="api.approve"||p.Code=="release.read").ToArrayAsync()) db.Add(new RolePermission {RoleId=role.Id,PermissionId=permission.Id});}db.Add(new UserRole {UserId=user.Id,RoleId=role.Id});}await db.SaveChangesAsync();}
        var client=new HttpClient(new HttpClientHandler {CookieContainer=new CookieContainer(),AllowAutoRedirect=false}) {BaseAddress=Client.BaseAddress};extraClients.Add(client);
        var csrf=await client.GetFromJsonAsync<Dictionary<string,string>>("/api/v1/auth/csrf");using var req=new HttpRequestMessage(HttpMethod.Post,"/api/v1/auth/login") {Content=JsonContent.Create(new {username=user.Username,password})};req.Headers.Add("X-CSRF-Token",csrf!["token"]);using var login=await client.SendAsync(req);login.EnsureSuccessStatusCode();return (user,client);
    }
    public static async Task<HttpResponseMessage> CommandAsync(HttpClient client,string path,object? body=null,string? key=null)
    {
        var csrf=await client.GetFromJsonAsync<Dictionary<string,string>>("/api/v1/auth/csrf");using var req=new HttpRequestMessage(HttpMethod.Post,path) {Content=body is null?JsonContent.Create(new {}):JsonContent.Create(body)};req.Headers.Add("X-CSRF-Token",csrf!["token"]);req.Headers.Add("Idempotency-Key",key??Guid.NewGuid().ToString("N"));return await client.SendAsync(req);
    }
    public object ReleaseRequest()=>new {baseConfigVersion=0L,versionIds=new[]{Version.Id},resourceRevisions=new[]{new {type="version",id=Version.Id,revision=1L}}};
    public async Task<Guid> CreateSubmittedReleaseAsync()
    {
        using var created=await CommandAsync(Client,$"/api/v1/environments/{Environment.Id}/releases",ReleaseRequest());created.EnsureSuccessStatusCode();var value=await created.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();var id=value.GetProperty("id").GetGuid();using var submitted=await CommandAsync(Client,$"/api/v1/releases/{id}/submit");submitted.EnsureSuccessStatusCode();return id;
    }
    public async ValueTask DisposeAsync()
    {
        foreach(var client in extraClients) client.Dispose();Client?.Dispose(); if(app is not null) {await app.StopAsync(); await app.DisposeAsync();} await Database.DisposeAsync();
    }
    public Task StopServerAsync() => app!.StopAsync();
}
