using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using WebApi.Infrastructure.Sso;
namespace WebApi.ControlPlane.Security;
public sealed class PlatformSessionIssuer(TimeProvider clock)
{
    public Task IssueAsync(HttpContext context,Guid userId,string securityStamp,int ttlMinutes,SsoSessionGrant? sso,CancellationToken ct=default)
    {
        ct.ThrowIfCancellationRequested();var claims=new List<Claim>{new(ClaimTypes.NameIdentifier,userId.ToString()),new("security_stamp",securityStamp)};
        if(sso is not null){claims.Add(new("sso_provider",sso.ProviderId.ToString()));claims.Add(new("sso_auth_revision",sso.AuthRevision.ToString(System.Globalization.CultureInfo.InvariantCulture)));claims.Add(new("sso_binding",sso.BindingId.ToString()));}
        var issued=clock.GetUtcNow();context.Response.Headers.CacheControl="no-store";
        return context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,new ClaimsPrincipal(new ClaimsIdentity(claims,CookieAuthenticationDefaults.AuthenticationScheme)),
            new AuthenticationProperties{IsPersistent=false,IssuedUtc=issued,ExpiresUtc=issued.AddMinutes(ttlMinutes)});
    }
}
