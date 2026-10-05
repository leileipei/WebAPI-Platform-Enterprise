using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
namespace WebApi.ControlPlane.Sso;
public sealed class OidcSchemeProvider(IOptions<AuthenticationOptions> options,IHttpContextAccessor accessor):AuthenticationSchemeProvider(options)
{
    public const string CallbackSchemeItem="WebApi.Oidc.CallbackScheme";
    public override async Task<IEnumerable<AuthenticationScheme>> GetRequestHandlerSchemesAsync()
    {
        if(accessor.HttpContext?.Items[CallbackSchemeItem] is not string name)return [];
        var scheme=await GetSchemeAsync(name);return scheme is null?[]:[scheme];
    }
}
