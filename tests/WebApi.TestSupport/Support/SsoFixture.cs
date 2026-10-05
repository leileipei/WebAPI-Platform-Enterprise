using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using WebApi.Contracts.Common;
using WebApi.Contracts.Sso;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Sso;
namespace WebApi.Integration.Tests.Support;

public sealed class SsoFixture:IAsyncDisposable
{
    public ApiFixture Api {get;}=new();
    public sealed class Clock:TimeProvider {public DateTimeOffset Now=DateTimeOffset.UtcNow;public override DateTimeOffset GetUtcNow()=>Now;}
    public Clock Time {get;}=new();
    public sealed class Network:IOidcMetadataClient,ISsoSecretResolver
    {
        public int Calls {get;private set;}
        public TaskCompletionSource Started {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Delay {get;set;}
        public Task<string> ResolveAsync(string reference,CancellationToken ct=default)=>Task.FromResult("synthetic-test-secret");
        public async Task<OidcMetadataSnapshot> GetAsync(string issuer,bool forceRefresh=false,CancellationToken ct=default)
        {
            Calls++;Started.TrySetResult();if(Delay)await Release.Task.WaitAsync(ct);
            return new(new OpenIdConnectConfiguration{Issuer=issuer,AuthorizationEndpoint=issuer+"/authorize",TokenEndpoint=issuer+"/token",JwksUri=issuer+"/keys"},DateTimeOffset.UtcNow,["RS256"],true,true,true);
        }
    }
    public Network IdentityService {get;}=new();
    public async Task InitializeAsync(bool login=true,bool platform=true)
    {
        await Api.InitializeAsync(builder=>{
            builder.Services.AddSingleton<TimeProvider>(Time);
            builder.Services.AddSingleton<IOidcMetadataClient>(IdentityService);
            builder.Services.AddSingleton<ISsoSecretResolver>(IdentityService);
            builder.Services.Configure<SsoOptions>(options=>{options.PublicBaseUrl="http://127.0.0.1:4290";options.FixtureEnabled=true;});
        });
        await Api.SeedScopeAsync(platformAdmin:platform);await PromoteAsync(Api);
        if(!platform){await using var db=Api.Context();var role=await db.Set<Role>().SingleAsync();role.IsSystem=false;role.OrganizationId=Api.Organization.Id;await db.SaveChangesAsync();}
        if(login)(await Api.LoginAsync()).EnsureSuccessStatusCode();
    }
    public static async Task PromoteAsync(ApiFixture fixture)
    {
        await using var db=fixture.Context();
        var role=await (from ur in db.Set<UserRole>() join r in db.Set<Role>() on ur.RoleId equals r.Id where ur.UserId==fixture.User.Id select r).SingleAsync();
        role.Code="PlatformAdmin";role.IsSystem=true;role.OrganizationId=null;
        var permission=await db.Set<Permission>().SingleOrDefaultAsync(x=>x.Code=="system.sso.manage");
        if(permission is null){permission=new Permission{Code="system.sso.manage",Module="system",Name="SSO 管理"};db.Add(permission);}
        if(!await db.Set<RolePermission>().AnyAsync(x=>x.RoleId==role.Id&&x.PermissionId==permission.Id))db.Add(new RolePermission{RoleId=role.Id,PermissionId=permission.Id});
        await db.SaveChangesAsync();
    }
    public static SaveSsoProviderRequest Body(string name="企业身份源",Guid? organizationId=null,string issuer="https://id.example.test/realm")=>
        new(organizationId,name,issuer,"webapi-console","file://sso/enterprise",["openid","profile","email"],new("name","email"));
    public async Task<HttpResponseMessage> SendAsync(HttpMethod method,string suffix,object body,string? tag=null,string? key=null)
    {
        using var request=new HttpRequestMessage(method,"/api/v1/settings/sso/providers"+suffix){Content=JsonContent.Create(body)};
        request.Headers.Add("X-CSRF-Token",await Api.CsrfAsync());request.Headers.Add("Idempotency-Key",key??Guid.NewGuid().ToString("N"));
        if(tag is not null)request.Headers.Add("If-Match",tag);return await Api.Client.SendAsync(request);
    }
    public async Task<SsoProviderDto> CreateAsync(SaveSsoProviderRequest? body=null)
    {
        using var response=await SendAsync(HttpMethod.Post,"",body??Body());response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<SsoProviderDto>())!;
    }
    public async Task<SsoProviderDto> EnableAsync(SsoProviderDto provider)
    {
        using var tested=await SendAsync(HttpMethod.Post,$"/{provider.Id}/test",new{},RevisionTag.Format(provider.Revision));tested.EnsureSuccessStatusCode();
        using var enabled=await SendAsync(HttpMethod.Post,$"/{provider.Id}/enable",new{},RevisionTag.Format(provider.Revision));enabled.EnsureSuccessStatusCode();
        return (await enabled.Content.ReadFromJsonAsync<SsoProviderDto>())!;
    }
    public ValueTask DisposeAsync()=>Api.DisposeAsync();
}
