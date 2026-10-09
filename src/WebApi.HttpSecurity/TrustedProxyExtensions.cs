using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
namespace WebApi.HttpSecurity;
public static class TrustedProxyExtensions
{
    public static IServiceCollection AddTrustedProxyBoundary(this IServiceCollection services,IConfiguration configuration)
    {
        var settings=TrustedProxySettings.Read(configuration);services.AddSingleton(settings);
        services.Configure<ForwardedHeadersOptions>(options=>{
            options.ForwardedHeaders=ForwardedHeaders.XForwardedFor;options.ForwardLimit=4;
            options.KnownProxies.Clear();options.KnownIPNetworks.Clear();
            foreach(var ip in settings.Ips)options.KnownProxies.Add(ip);
            foreach(var network in settings.Networks)options.KnownIPNetworks.Add(network);
        });return services;
    }
    public static IApplicationBuilder UseTrustedProxyBoundary(this IApplicationBuilder app)
    {
        // Empty ASP.NET allowlists mean trust-all: never enable forwarded parsing for empty settings.
        if(app.ApplicationServices.GetRequiredService<TrustedProxySettings>().Enabled)app.UseForwardedHeaders();
        return app.Use(async(context,next)=>{ForwardingHeaderSanitizer.Remove(context.Request.Headers);await next();});
    }
}
