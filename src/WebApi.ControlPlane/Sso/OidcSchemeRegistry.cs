using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using WebApi.Contracts.Common;
using WebApi.Contracts.Sso;
using WebApi.ControlPlane.Security;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Sso;
namespace WebApi.ControlPlane.Sso;
public sealed class OidcSchemeRegistry(IServiceScopeFactory scopes,IAuthenticationSchemeProvider schemes,
    IOptionsMonitorCache<OpenIdConnectOptions> cache,IEnumerable<IPostConfigureOptions<OpenIdConnectOptions>> postConfigure,
    IOidcMetadataClient metadata,ISsoSecretResolver secrets,IOptions<SsoOptions> settings,IHostEnvironment environment,TimeProvider clock,IHttpContextAccessor accessor,SsoSecretVersion secretVersion):IDisposable
{
    private readonly SemaphoreSlim gate=new(1,1);
    private sealed class Entry(DateTimeOffset touched,OpenIdConnectOptions options){public DateTimeOffset Touched=touched;public int Leases;public OpenIdConnectOptions Options=options;}
    private readonly Dictionary<string,Entry> registered=new(StringComparer.Ordinal);
    public async Task<string> PrepareAsync(Guid providerId,CancellationToken ct=default)
    {
        await using var scope=scopes.CreateAsyncScope();var db=scope.ServiceProvider.GetRequiredService<WebApiDbContext>();
        var provider=await db.Set<SsoProvider>().AsNoTracking().SingleOrDefaultAsync(x=>x.Id==providerId&&x.Enabled,ct)??throw Unavailable();
        if(provider.OrganizationId.HasValue&&!await db.Set<Organization>().AnyAsync(x=>x.Id==provider.OrganizationId&&x.Status=="Active",ct))throw Unavailable();
        var secret=await secrets.ResolveAsync(provider.SecretRef,ct);
        if(!secretVersion.Matches(provider,secret))throw Unavailable();
        var name=$"oidc:{provider.Id:N}:{provider.AuthRevision}";await gate.WaitAsync(ct);
        try
        {
            var now=clock.GetUtcNow();
            foreach(var old in registered.Where(x=>x.Value.Leases==0&&now-x.Value.Touched>TimeSpan.FromMinutes(30)).Select(x=>x.Key).ToArray())Remove(old);
            if(registered.TryGetValue(name,out var existing)){existing.Touched=now;Lease(name,existing);return name;}
            var limit=Math.Clamp(settings.Value.MaxCachedSchemes,1,256);
            while(registered.Count>=limit)
            {
                var idle=registered.Where(x=>x.Value.Leases==0).OrderBy(x=>x.Value.Touched).FirstOrDefault();
                if(idle.Key is null)throw new ApiException(503,"sso_busy","企业登录繁忙，请稍后重试。");
                Remove(idle.Key);
            }
            var discovery=await metadata.GetAsync(provider.Issuer,false,ct);
            if(!discovery.CodeSupported||!discovery.PkceS256Supported||!discovery.ClientSecretPostSupported)throw Unavailable();
            var callback=Callback(provider.Id);
            var options=new OpenIdConnectOptions{
                ClientId=provider.ClientId,ClientSecret=secret,Authority=provider.Issuer,CallbackPath="/auth/oidc/callback/"+provider.Id,
                ResponseType=OpenIdConnectResponseType.Code,ResponseMode=OpenIdConnectResponseMode.Query,UsePkce=true,
                PushedAuthorizationBehavior=PushedAuthorizationBehavior.Disable,MapInboundClaims=false,SaveTokens=false,GetClaimsFromUserInfoEndpoint=false,
                SignInScheme=CookieAuthenticationDefaults.AuthenticationScheme,RequireHttpsMetadata=!(environment.IsDevelopment()&&settings.Value.FixtureEnabled),
                RefreshOnIssuerKeyNotFound=false,RemoteAuthenticationTimeout=TimeSpan.FromMinutes(5),UseTokenLifetime=false,
                ConfigurationManager=new StaticConfigurationManager<OpenIdConnectConfiguration>(discovery.Configuration),
                Backchannel=new HttpClient(new SsoBackchannelHandler(scope.ServiceProvider.GetRequiredService<IOidcAddressPolicy>())){Timeout=Timeout.InfiniteTimeSpan},
                TokenHandler=new StrictOidcTokenHandler(metadata,provider.Issuer,accessor){MapInboundClaims=false},
                TokenValidationParameters=new(){
                    ValidateIssuer=true,ValidIssuer=provider.Issuer,ValidateAudience=true,ValidAudience=provider.ClientId,
                    ValidateLifetime=true,RequireExpirationTime=true,RequireSignedTokens=true,ValidateIssuerSigningKey=true,
                    ValidAlgorithms=discovery.SigningAlgorithms,ClockSkew=TimeSpan.FromSeconds(60),TryAllIssuerSigningKeys=false,
                    NameClaimType="name",RoleClaimType="__external_roles_ignored"
                }
            };
            options.Scope.Clear();foreach(var value in JsonSerializer.Deserialize<string[]>(provider.ScopesJson,CanonicalJson.Options)!)options.Scope.Add(value);
            options.ClaimActions.Clear();
            var development=environment.IsDevelopment()&&settings.Value.FixtureEnabled&&new Uri(callback).Scheme=="http";
            foreach(var cookie in new[]{options.CorrelationCookie,options.NonceCookie})
            {cookie.HttpOnly=true;cookie.SameSite=SameSiteMode.Lax;cookie.SecurePolicy=development?CookieSecurePolicy.SameAsRequest:CookieSecurePolicy.Always;cookie.Path=options.CallbackPath;cookie.Expiration=TimeSpan.FromMinutes(5);}
            options.CorrelationCookie.Name=".WebApi.Oidc.Correlation."+provider.Id.ToString("N")+".";
            options.NonceCookie.Name=".WebApi.Oidc.Nonce."+provider.Id.ToString("N")+".";
            options.Events=new OpenIdConnectEvents{
                OnRedirectToIdentityProvider=context=>{context.ProtocolMessage.RedirectUri=callback;context.ProtocolMessage.ResponseMode=OpenIdConnectResponseMode.Query;if(context.Properties.Items.TryGetValue("ssoReauthenticate",out var force)&&force=="true")context.ProtocolMessage.Prompt="login";return Task.CompletedTask;},
                OnMessageReceived=context=>{
                    if(!HttpMethods.IsGet(context.Request.Method)||!string.IsNullOrEmpty(context.ProtocolMessage.IdToken)||!string.IsNullOrEmpty(context.ProtocolMessage.AccessToken))
                        context.Fail("Unsupported OIDC response.");
                    return Task.CompletedTask;
                },
                OnAuthorizationCodeReceived=async context=>{
                    if(!TryAttempt(context.Properties,out var attempt,out var expected))throw Unavailable();
                    await context.HttpContext.RequestServices.GetRequiredService<SsoLoginCoordinator>().ClaimAsync(attempt,provider.Id,expected,context.HttpContext.RequestAborted);
                    context.HttpContext.Items["WebApi.Oidc.ClaimedAttempt"]=attempt;
                    context.TokenEndpointRequest!.RedirectUri=callback;
                },
                OnTokenValidated=context=>{
                    var jwt=(System.IdentityModel.Tokens.Jwt.JwtSecurityToken)context.SecurityToken;
                    using var payload=JsonDocument.Parse(Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Decode(jwt.RawPayload));var root=payload.RootElement;
                    if(!root.TryGetProperty("iat",out var issued)||!issued.TryGetInt64(out var seconds)||seconds>clock.GetUtcNow().ToUnixTimeSeconds()+60||
                        !root.TryGetProperty("sub",out var subject)||subject.ValueKind!=JsonValueKind.String||string.IsNullOrWhiteSpace(subject.GetString())||subject.GetString()!.Length>255)throw Unavailable();
                    if(root.TryGetProperty("azp",out var azp)&&(azp.ValueKind!=JsonValueKind.String||azp.GetString()!=provider.ClientId))throw Unavailable();
                    if(root.TryGetProperty("aud",out var aud)&&aud.ValueKind==JsonValueKind.Array&&aud.GetArrayLength()>1&&!root.TryGetProperty("azp",out _))throw Unavailable();
                    var mapping=JsonSerializer.Deserialize<SsoClaimMapping>(provider.ClaimMappingJson,CanonicalJson.Options)!;
                    foreach(var claim in new[]{mapping.DisplayName,mapping.Email})
                        if(claim is not null&&root.TryGetProperty(claim,out var value)&&value.ValueKind!=JsonValueKind.String)throw Unavailable();
                    var claims=root.EnumerateObject().Where(property=>property.Value.ValueKind==JsonValueKind.String).ToDictionary(property=>property.Name,property=>property.Value.GetString()!,StringComparer.Ordinal);
                    context.HttpContext.Items["WebApi.Oidc.ValidatedIdentity"]=new ValidatedOidcIdentity(jwt.Issuer,subject.GetString()!,claims);
                    return Task.CompletedTask;
                },
                OnTicketReceived=async context=>{
                    var coordinator=context.HttpContext.RequestServices.GetRequiredService<SsoLoginCoordinator>();var attempt=Guid.Empty;
                    try
                    {
                        if(!TryAttempt(context.Properties,out attempt,out _)||context.HttpContext.Items["WebApi.Oidc.ValidatedIdentity"] is not ValidatedOidcIdentity identity)throw Unavailable();
                        var grant=await coordinator.CompleteAsync(attempt,identity,context.HttpContext.TraceIdentifier,context.HttpContext.Connection.RemoteIpAddress,context.HttpContext.RequestAborted);
                        await context.HttpContext.RequestServices.GetRequiredService<PlatformSessionIssuer>().IssueAsync(context.HttpContext,grant.UserId,grant.SecurityStamp,grant.TtlMinutes,grant,context.HttpContext.RequestAborted);
                        context.HandleResponse();context.Response.StatusCode=303;context.Response.Headers.Location="/auth/sso/complete?returnPath="+Uri.EscapeDataString(grant.ReturnPath);
                    }
                    catch(ApiException)
                    {
                        if(attempt!=Guid.Empty)await coordinator.FailAsync(attempt,"protocol_error",context.HttpContext.RequestAborted);
                        context.HandleResponse();Failure(context.HttpContext,"protocol_error");
                    }
                },
                OnAuthenticationFailed=context=>Task.CompletedTask,
                OnRemoteFailure=async context=>{
                    if(TryAttempt(context.Properties,out var attempt,out _))
                    {
                        var coordinator=context.HttpContext.RequestServices.GetRequiredService<SsoLoginCoordinator>();
                        if(context.HttpContext.Items["WebApi.Oidc.ClaimedAttempt"] is Guid owned&&owned==attempt)await coordinator.FailAsync(attempt,"protocol_error",context.HttpContext.RequestAborted);
                        else await coordinator.FailPendingAsync(attempt,"protocol_error",context.HttpContext.RequestAborted);
                    }
                    else {var failureDb=context.HttpContext.RequestServices.GetRequiredService<WebApiDbContext>();failureDb.Add(new AuditLog{Action="auth.sso.failed",ResourceType="SsoProvider",ResourceId=provider.Id.ToString(),TraceId=context.HttpContext.TraceIdentifier,AfterJson="{\"code\":\"protocol_error\"}"});await failureDb.SaveChangesAsync(context.HttpContext.RequestAborted);}
                    context.HandleResponse();Failure(context.HttpContext,"protocol_error");
                },
                OnAccessDenied=async context=>{
                    if(TryAttempt(context.Properties,out var attempt,out _))await context.HttpContext.RequestServices.GetRequiredService<SsoLoginCoordinator>().FailPendingAsync(attempt,"cancelled",context.HttpContext.RequestAborted);
                    context.HandleResponse();Failure(context.HttpContext,"cancelled");
                }
            };
            foreach(var configure in postConfigure)configure.PostConfigure(name,options);
            cache.TryAdd(name,options);schemes.AddScheme(new(name,provider.Name,typeof(OpenIdConnectHandler)));
            var entry=new Entry(now,options);registered[name]=entry;Lease(name,entry);return name;
        }
        finally{gate.Release();}
    }
    private void Remove(string name){if(registered.Remove(name,out var entry))entry.Options.Backchannel.Dispose();schemes.RemoveScheme(name);cache.TryRemove(name);}
    private void Lease(string name,Entry entry)
    {
        var context=accessor.HttpContext;if(context is null)return;
        if(context.Items["WebApi.Oidc.Leases"] is not HashSet<string> leased){leased=new(StringComparer.Ordinal);context.Items["WebApi.Oidc.Leases"]=leased;}
        if(!leased.Add(name))return;entry.Leases++;
        context.Response.OnCompleted(async()=>{
            await gate.WaitAsync();
            try{entry.Leases--;}
            finally{gate.Release();}
        });
    }
    public void Dispose(){foreach(var entry in registered.Values)entry.Options.Backchannel.Dispose();gate.Dispose();}
    private string Callback(Guid id)
    {
        if(!Uri.TryCreate(settings.Value.PublicBaseUrl,UriKind.Absolute,out var uri)||uri.UserInfo.Length!=0||uri.Query.Length!=0||uri.Fragment.Length!=0||uri.AbsolutePath!="/"||
           uri.Scheme!="https"&&!(environment.IsDevelopment()&&settings.Value.FixtureEnabled&&uri.Scheme=="http"&&(uri.Host=="localhost"||System.Net.IPAddress.TryParse(uri.Host,out var address)&&System.Net.IPAddress.IsLoopback(address))))throw Unavailable();
        return uri.GetLeftPart(UriPartial.Authority)+"/auth/oidc/callback/"+id;
    }
    private static bool TryAttempt(AuthenticationProperties? properties,out Guid attempt,out long revision)
    {
        attempt=Guid.Empty;revision=0;
        return properties is not null&&properties.Items.TryGetValue("ssoAttempt",out var id)&&Guid.TryParse(id,out attempt)&&
            properties.Items.TryGetValue("ssoRevision",out var value)&&long.TryParse(value,NumberStyles.None,CultureInfo.InvariantCulture,out revision)&&revision>0;
    }
    public static void Failure(HttpContext context,string code)
    {context.Response.StatusCode=303;context.Response.Headers.CacheControl="no-store";context.Response.Headers["Referrer-Policy"]="no-referrer";context.Response.Headers.Location="/login?ssoError="+code+"&traceId="+Uri.EscapeDataString(context.TraceIdentifier);}
    private static ApiException Unavailable()=>new(401,"sso_login_failed","企业登录未完成，请重试或联系管理员。");
}
