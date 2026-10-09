using WebApi.HttpSecurity;
using Microsoft.Extensions.FileProviders;
using Yarp.ReverseProxy.Configuration;
namespace WebApi.ConsoleHost;
public static class ConsoleHostApp
{
    public static WebApplication Build(string[] args,Action<WebApplicationBuilder>? configure=null)
    {
        var builder=WebApplication.CreateBuilder(args);configure?.Invoke(builder);builder.Services.AddTrustedProxyBoundary(builder.Configuration);
        var directory=builder.Configuration["Console:DistDirectory"]??throw new InvalidOperationException("Explicit console dist directory required.");
        var target=builder.Configuration["Console:ControlPlaneUrl"]??throw new InvalidOperationException("Explicit control plane URL required.");
        builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics",LogLevel.Warning);
        builder.Logging.AddFilter("Yarp.ReverseProxy",LogLevel.None);
        builder.Services.AddReverseProxy().LoadFromMemory(
            [new RouteConfig{RouteId="management-api",ClusterId="control-plane",Match=new RouteMatch{Path="/api/{**rest}"},Transforms=[new Dictionary<string,string>{{"RequestHeaderOriginalHost","true"}}]},
             new RouteConfig{RouteId="oidc-callback",ClusterId="control-plane",Match=new RouteMatch{Path="/auth/oidc/callback/{**rest}"},Transforms=[new Dictionary<string,string>{{"RequestHeaderOriginalHost","true"}}]}],
            [new ClusterConfig{ClusterId="control-plane",Destinations=new Dictionary<string,DestinationConfig>{{"control-plane",new(){Address=target}}}}]);
        var app=builder.Build();app.UseTrustedProxyBoundary();var files=new PhysicalFileProvider(Path.GetFullPath(directory));
        app.UseDefaultFiles(new DefaultFilesOptions{FileProvider=files});app.UseStaticFiles(new StaticFileOptions{FileProvider=files});app.MapReverseProxy();
        app.MapGet("/health/live",()=>Results.Ok(new{status="live"}));
        app.MapFallback(async context=>{
            if(context.Request.Path.StartsWithSegments("/api")){context.Response.StatusCode=404;return;}
            context.Response.ContentType="text/html; charset=utf-8";await context.Response.SendFileAsync(Path.Combine(directory,"index.html"));
        });return app;
    }
}
