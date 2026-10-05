using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting;
using WebApi.Contracts.Common;
using WebApi.Contracts.Security;
using WebApi.Contracts.Sso;
using WebApi.Infrastructure.Commands;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Security;
namespace WebApi.Infrastructure.Sso;
public sealed class SsoProviderService(WebApiDbContext db,AuthorizationService auth,AuditedCommandExecutor commands,
    IdempotentCommandExecutor idempotency,IOidcMetadataClient metadata,ISsoSecretResolver secrets,
    IOptions<SsoOptions> options,IHostEnvironment environment,TimeProvider clock,LocalAdministratorGuard localAdministrators,SsoSecretVersion secretVersion)
{
    private static readonly ScopeRef Platform=new(Guid.Empty);
    private Task Require(ActorContext actor,CancellationToken ct)=>auth.RequireAsync(actor,"system.sso.manage",new("sso_provider",Guid.Empty,Platform),ct);
    private async Task<SsoProvider> Find(Guid id,CancellationToken ct)=>await db.Set<SsoProvider>().SingleOrDefaultAsync(x=>x.Id==id,ct)??throw Missing();
    public async Task<PageResult<SsoProviderDto>> ListAsync(ActorContext actor,Guid? organizationId,string? search,int page,int pageSize,CancellationToken ct=default)
    {
        await Require(actor,ct);var query=db.Set<SsoProvider>().AsNoTracking();
        if(organizationId.HasValue)query=query.Where(x=>x.OrganizationId==organizationId);
        if(!string.IsNullOrWhiteSpace(search)){var term=search.Trim();query=query.Where(x=>x.Name.Contains(term)||x.Issuer.Contains(term));}
        var total=await query.CountAsync(ct);var p=Math.Clamp(page,1,1000000);var size=Math.Clamp(pageSize,1,100);
        var rows=await query.OrderByDescending(x=>x.IsDefault).ThenBy(x=>x.Name).ThenBy(x=>x.Id).Skip((p-1)*size).Take(size).ToArrayAsync(ct);
        var views=new List<SsoProviderDto>();foreach(var row in rows)views.Add(await View(row,ct));return new(views,total,p,size);
    }
    public async Task<SsoProviderDto> GetAsync(Guid id,ActorContext actor,CancellationToken ct=default){await Require(actor,ct);return await View(await Find(id,ct),ct);}
    public Task<CommandResult<SsoProviderDto>> CreateAsync(SaveSsoProviderRequest request,CommandRequestContext command,ActorContext actor,CancellationToken ct=default)=>
        commands.ExecuteAsync(actor,Platform,"settings.sso.provider.create",async(_,token)=>{
            await Require(actor,token);
            return await idempotency.ExecuteAsync(Identity(actor,command,"create"),CanonicalJson.Serialize(request),async commandToken=>{
                var validated=Validate(request);await Organization(validated.OrganizationId,commandToken);
                var row=new SsoProvider{CreatedAt=clock.GetUtcNow(),UpdatedAt=clock.GetUtcNow()};Apply(row,validated);db.Add(row);
                return new CommandResult<SsoProviderDto>(await View(row,commandToken),RevisionTag.Format(row.Revision));
            },token);
        },ct);
    public Task<CommandResult<SsoProviderDto>> SaveAsync(Guid id,SaveSsoProviderRequest request,string? tag,CommandRequestContext command,ActorContext actor,CancellationToken ct=default)=>
        commands.ExecuteAsync(actor,Platform,"settings.sso.provider.update",async(_,token)=>{
            await Require(actor,token);
            return await idempotency.ExecuteAsync(Identity(actor,command,"save:"+id),CanonicalJson.Serialize(new{id,request,tag}),async commandToken=>{
                var row=await Find(id,commandToken);RevisionTag.Require(tag,row.Revision);var validated=Validate(request);
                await Organization(validated.OrganizationId,commandToken);
                if((row.Issuer!=validated.Issuer||row.ClientId!=validated.ClientId||row.OrganizationId!=validated.OrganizationId)&&
                    await db.Set<UserExternalIdentity>().AnyAsync(x=>x.ProviderId==id,commandToken))throw new ApiException(409,"sso_bound_configuration","已有绑定的身份源不能修改 Issuer、ClientId 或组织，请新建身份源。");
                var authenticationChanged=row.Issuer!=validated.Issuer||row.ClientId!=validated.ClientId||row.SecretRef!=validated.SecretRef||
                    row.OrganizationId!=validated.OrganizationId||!JsonEquals(row.ScopesJson,validated.Scopes)||
                    !JsonEquals(row.ClaimMappingJson,validated.ClaimMapping);
                // Changing a default's scope cannot strand another default in that scope.
                if(row.IsDefault&&row.OrganizationId!=validated.OrganizationId)await ClearDefault(validated.OrganizationId,row.Id,commandToken);
                Apply(row,validated);row.Revision++;if(authenticationChanged){row.AuthRevision++;await PinSecret(row,commandToken);}row.UpdatedAt=clock.GetUtcNow();
                return new CommandResult<SsoProviderDto>(await View(row,commandToken),RevisionTag.Format(row.Revision));
            },token);
        },ct);
    public Task<CommandResult<SsoProviderDto>> OperateAsync(Guid id,SsoProviderOperation operation,string? tag,CommandRequestContext command,ActorContext actor,CancellationToken ct=default)=>
        commands.ExecuteAsync(actor,Platform,"settings.sso.provider."+operation.ToString().ToLowerInvariant(),async(_,token)=>{
            await Require(actor,token);
            return await idempotency.ExecuteAsync(Identity(actor,command,operation+":"+id),CanonicalJson.Serialize(new{id,operation,tag}),async commandToken=>{
                var row=await Find(id,commandToken);RevisionTag.Require(tag,row.Revision);
                switch(operation)
                {
                    case SsoProviderOperation.Enable:
                        await Organization(row.OrganizationId,commandToken);
                        await localAdministrators.RequireAvailableAsync(commandToken);
                        var test=await db.Set<SsoProviderTest>().Where(x=>x.ProviderId==id&&x.ProviderRevision==row.Revision).OrderByDescending(x=>x.TestedAt).FirstOrDefaultAsync(commandToken);
                        if(test is null||test.Status!="Passed"||clock.GetUtcNow()-test.TestedAt>TimeSpan.FromMinutes(15)||test.TestedAt>clock.GetUtcNow())
                            throw new ApiException(422,"sso_test_required","请先通过当前修订的连接测试；结果有效期为15分钟。");
                        if(!row.Enabled){row.Enabled=true;row.AuthRevision++;await PinSecret(row,commandToken);}break;
                    case SsoProviderOperation.Disable:
                        if(row.Enabled){row.Enabled=false;row.AuthRevision++;row.ProtectedSecretFingerprint=null;}row.IsDefault=false;break;
                    case SsoProviderOperation.Default:
                        if(!row.Enabled)throw new ApiException(409,"sso_provider_disabled","请先启用身份源。");
                        await ClearDefault(row.OrganizationId,row.Id,commandToken);row.IsDefault=true;break;
                    case SsoProviderOperation.Rotate:
                        row.AuthRevision++;await PinSecret(row,commandToken);break;
                    default:throw new ApiException(422,"invalid_sso_operation","无效的身份源操作。");
                }
                row.Revision++;row.UpdatedAt=clock.GetUtcNow();
                return new CommandResult<SsoProviderDto>(await View(row,commandToken),RevisionTag.Format(row.Revision));
            },token);
        },ct);
    public async Task<CommandResult<SsoTestDto>> TestAsync(Guid id,string? tag,CommandRequestContext command,ActorContext actor,CancellationToken ct=default)
    {
        var identity=Identity(actor,command,"test:"+id);var request=CanonicalJson.Serialize(new{id,tag});
        SsoProvider snapshot;
        await using(var transaction=await db.Database.BeginTransactionAsync(ct))
        {
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(8901202)",ct);await Require(actor,ct);
            var replay=await idempotency.TryReplayAsync<CommandResult<SsoTestDto>>(identity,request,ct);if(replay.Found)return replay.Value!;
            snapshot=await db.Set<SsoProvider>().AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,ct)??throw Missing();
            RevisionTag.Require(tag,snapshot.Revision);await transaction.CommitAsync(ct);
        }
        var stages=new List<SsoTestStageDto>();
        try
        {
            await secrets.ResolveAsync(snapshot.SecretRef,ct);stages.Add(new("secretReference",true,"部署引用可解析；未提交客户端认证。"));
            if(Callback(snapshot.Id).Length==0)throw new ApiException(422,"sso_public_url_required","请配置可信的平台公开地址。");
            var discovery=await metadata.GetAsync(snapshot.Issuer,true,ct);stages.Add(new("discovery",true,"Discovery、Issuer、端点与公开签名密钥通过验证。"));
            stages.Add(new("code",discovery.CodeSupported,"授权码流程声明"));
            stages.Add(new("pkce",discovery.PkceS256Supported,"PKCE S256 声明"));
            stages.Add(new("clientSecretPost",discovery.ClientSecretPostSupported,"client_secret_post 声明"));
        }
        catch(ApiException error){stages.Add(new(error.Code,false,error.Message));}
        stages.Add(new("loginNotVerified",true,"客户端认证与用户登录尚未验证。"));
        return await commands.ExecuteAsync(actor,Platform,"settings.sso.provider.test",async(_,token)=>{
            await Require(actor,token);
            return await idempotency.ExecuteAsync(identity,request,async commandToken=>{
                var row=await Find(id,commandToken);RevisionTag.Require(tag,row.Revision);
                var result=new SsoProviderTest{ProviderId=id,ProviderRevision=row.Revision,TestedAt=clock.GetUtcNow(),Status=stages.All(x=>x.Passed)?"Passed":"Failed",StagesJson=JsonSerializer.Serialize(stages,CanonicalJson.Options)};
                db.Add(result);return new CommandResult<SsoTestDto>(TestView(result),RevisionTag.Format(row.Revision));
            },token);
        },ct);
    }
    public async Task<IReadOnlyList<PublicSsoProviderDto>> PublicAsync(Guid? organizationId,CancellationToken ct=default)
    {
        if(organizationId.HasValue&&!await db.Set<Organization>().AnyAsync(x=>x.Id==organizationId&&x.Status=="Active",ct))throw new ApiException(404,"sso_entry_unavailable","企业登录入口不可用。");
        return await db.Set<SsoProvider>().AsNoTracking().Where(x=>x.Enabled&&(x.OrganizationId==null||organizationId.HasValue&&x.OrganizationId==organizationId))
            .OrderByDescending(x=>x.IsDefault&&x.OrganizationId!=null).ThenByDescending(x=>x.IsDefault).ThenBy(x=>x.Name).ThenBy(x=>x.Id)
            .Select(x=>new PublicSsoProviderDto(x.Id,x.Name,x.IsDefault)).ToArrayAsync(ct);
    }
    private async Task PinSecret(SsoProvider row,CancellationToken ct)
    {
        if(!row.Enabled){row.ProtectedSecretFingerprint=null;return;}
        secretVersion.Pin(row,await secrets.ResolveAsync(row.SecretRef,ct));
    }
    private CommandIdentity Identity(ActorContext actor,CommandRequestContext command,string operation)=>new(actor.UserId,Platform,"sso.provider."+operation,command.IdempotencyKey);
    private SaveSsoProviderRequest Validate(SaveSsoProviderRequest request)
    {
        var value=SsoProviderValidator.Parse(CanonicalJson.Serialize(request),environment.IsDevelopment()&&options.Value.FixtureEnabled);
        if(new Uri(value.Issuer).Scheme=="http")
        {
            var uri=new Uri(value.Issuer);
            if(!(uri.Host=="localhost"||System.Net.IPAddress.TryParse(uri.Host,out var ip)&&System.Net.IPAddress.IsLoopback(ip))||
                !options.Value.AllowedOrigins.Any(origin=>Uri.TryCreate(origin,UriKind.Absolute,out var allowed)&&OidcAddressPolicy.SameOrigin(allowed,uri)))
                throw new ApiException(422,"invalid_sso_configuration","HTTP 身份源仅支持明确批准的本机验收地址。");
        }
        return value;
    }
    private async Task Organization(Guid? id,CancellationToken ct){if(id.HasValue&&!await db.Set<Organization>().AnyAsync(x=>x.Id==id&&x.Status=="Active",ct))throw new ApiException(422,"sso_organization_unavailable","所选组织不可用。");}
    private async Task ClearDefault(Guid? organizationId,Guid except,CancellationToken ct)
    {
        var defaults=await db.Set<SsoProvider>().Where(x=>x.OrganizationId==organizationId&&x.Id!=except&&x.IsDefault).ToArrayAsync(ct);
        foreach(var other in defaults){other.IsDefault=false;other.Revision++;other.UpdatedAt=clock.GetUtcNow();}
        // Release the immediate partial unique index while retaining tracked originals for audit.
        foreach(var other in defaults)await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE sso_providers SET is_default = FALSE WHERE id = {other.Id}",ct);
    }
    private static void Apply(SsoProvider row,SaveSsoProviderRequest value)
    {
        row.Name=value.Name;row.OrganizationId=value.OrganizationId;row.Issuer=value.Issuer;row.ClientId=value.ClientId;row.SecretRef=value.SecretRef;
        row.ScopesJson=JsonSerializer.Serialize(value.Scopes,CanonicalJson.Options);row.ClaimMappingJson=JsonSerializer.Serialize(value.ClaimMapping,CanonicalJson.Options);
    }
    private static bool JsonEquals<T>(string stored,T value)
    {
        using var document=JsonDocument.Parse(stored);
        return CanonicalJson.Serialize(document.RootElement).AsSpan().SequenceEqual(CanonicalJson.Serialize(value));
    }
    private async Task<SsoProviderDto> View(SsoProvider row,CancellationToken ct)
    {
        var latest=await db.Set<SsoProviderTest>().AsNoTracking().Where(x=>x.ProviderId==row.Id).OrderByDescending(x=>x.TestedAt).ThenByDescending(x=>x.Id).FirstOrDefaultAsync(ct);
        return new(row.Id,row.OrganizationId,row.Name,row.ProviderType,row.Issuer,row.ClientId,row.SecretRef,
            JsonSerializer.Deserialize<string[]>(row.ScopesJson,CanonicalJson.Options)!,JsonSerializer.Deserialize<SsoClaimMapping>(row.ClaimMappingJson,CanonicalJson.Options)!,
            row.Enabled,row.IsDefault,row.Revision,row.AuthRevision,latest is null?null:TestView(latest),Callback(row.Id));
    }
    private string Callback(Guid id)
    {
        if(!Uri.TryCreate(options.Value.PublicBaseUrl,UriKind.Absolute,out var uri)||uri.UserInfo.Length!=0||uri.Query.Length!=0||uri.Fragment.Length!=0||uri.AbsolutePath!="/"||
           uri.Scheme!="https"&&!(environment.IsDevelopment()&&options.Value.FixtureEnabled&&uri.Scheme=="http"&&(uri.Host=="localhost"||System.Net.IPAddress.TryParse(uri.Host,out var ip)&&System.Net.IPAddress.IsLoopback(ip))))return "";
        return uri.GetLeftPart(UriPartial.Authority)+"/auth/oidc/callback/"+id;
    }
    private static SsoTestDto TestView(SsoProviderTest test)=>new(test.Id,test.ProviderRevision,test.TestedAt,test.Status,JsonSerializer.Deserialize<SsoTestStageDto[]>(test.StagesJson,CanonicalJson.Options)!);
    private static ApiException Missing()=>new(404,"sso_provider_not_found","身份源不存在。");
}
