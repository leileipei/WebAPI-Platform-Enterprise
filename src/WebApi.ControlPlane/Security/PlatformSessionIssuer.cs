using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using WebApi.Infrastructure.Sso;
namespace WebApi.ControlPlane.Security;
public sealed class PlatformSessionIssuer(TimeProvider clock)
{
    public Task IssueAsync(HttpContext context,Guid userId,string securityStamp,int ttlMinutes,SsoSessionGrant? sso,CancellationToken ct=default)
        =>IssueCoreAsync(context,userId,securityStamp,ttlMinutes,sso,null,ct);
    public Task IssueRestrictedAsync(HttpContext context,Guid userId,string securityStamp,int systemTtlMinutes,CancellationToken ct=default)
        =>IssueCoreAsync(context,userId,securityStamp,Math.Min(systemTtlMinutes,10),null,false,ct);
    private Task IssueCoreAsync(HttpContext context,Guid userId,string securityStamp,int ttlMinutes,SsoSessionGrant? sso,bool? allowRefresh,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();var claims=new List<Claim>{new(ClaimTypes.NameIdentifier,userId.ToString()),new("security_stamp",securityStamp)};
        if(sso is not null){claims.Add(new("sso_provider",sso.ProviderId.ToString()));claims.Add(new("sso_auth_revision",sso.AuthRevision.ToString(System.Globalization.CultureInfo.InvariantCulture)));claims.Add(new("sso_binding",sso.BindingId.ToString()));}
        var issued=clock.GetUtcNow();context.Response.Headers.CacheControl="no-store";
        return context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,new ClaimsPrincipal(new ClaimsIdentity(claims,CookieAuthenticationDefaults.AuthenticationScheme)),
            new AuthenticationProperties{IsPersistent=false,AllowRefresh=allowRefresh,IssuedUtc=issued,ExpiresUtc=issued.AddMinutes(ttlMinutes)});
    }
}
