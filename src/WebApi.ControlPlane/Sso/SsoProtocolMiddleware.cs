namespace WebApi.ControlPlane.Sso;
public sealed class SsoProtocolMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context,OidcSchemeRegistry registry)
    {
        if(!context.Request.Path.StartsWithSegments("/auth/oidc/callback",out var rest)){await next(context);return;}
        context.Response.Headers.CacheControl="no-store";context.Response.Headers["Referrer-Policy"]="no-referrer";
        if(!HttpMethods.IsGet(context.Request.Method)){context.Response.StatusCode=405;context.Response.Headers.Allow="GET";return;}
        if(!Guid.TryParse(rest.Value?.TrimStart('/'),out var providerId)){context.Response.StatusCode=404;return;}
        try {context.Items[OidcSchemeProvider.CallbackSchemeItem]=await registry.PrepareAsync(providerId,context.RequestAborted);}
        catch(WebApi.Contracts.Common.ApiException){OidcSchemeRegistry.Failure(context,"configuration_unavailable");return;}
        await next(context);
    }
}
