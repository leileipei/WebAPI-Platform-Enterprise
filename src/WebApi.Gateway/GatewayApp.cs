using WebApi.Gateway.Configuration;
using WebApi.Gateway.Security;
using WebApi.Gateway.Storage;
using WebApi.Gateway.Workers;
using WebApi.Infrastructure.Routing;
using WebApi.Infrastructure.Runtime;
using Yarp.ReverseProxy.Configuration;
namespace WebApi.Gateway;
public static class GatewayApp
{
    public static WebApplication Build(string[] args,Action<WebApplicationBuilder>? configure=null)
    {
        var b=WebApplication.CreateBuilder(args);configure?.Invoke(b);var settings=GatewaySettings.Read(b.Configuration);b.Services.AddSingleton(settings);b.Services.AddSingleton<AdmissionGate>();b.Services.AddSingleton<RuntimeGenerationStore>();b.Services.AddSingleton<LkgStore>();b.Services.AddSingleton<SnapshotActivation>();b.Services.AddSingleton<NodeClient>();b.Services.AddSingleton<ConfigWatcher>();b.Services.AddSingleton<EnterpriseProxyConfigProvider>();b.Services.AddSingleton<IProxyConfigProvider>(sp=>sp.GetRequiredService<EnterpriseProxyConfigProvider>());b.Services.AddSingleton<IConfigChangeListener>(sp=>sp.GetRequiredService<EnterpriseProxyConfigProvider>());
        b.Services.AddSingleton(new UpstreamAddressPolicy(b.Configuration.GetSection("Upstream:AllowedOrigins").Get<string[]>()??["http://test-backend:8080"]));b.Services.AddSingleton(new RedisSnapshotStore(b.Configuration["Redis:Connection"]??"redis:6379,abortConnect=false,connectTimeout=1000,asyncTimeout=1000",b.Configuration["Redis:Prefix"]??"webapi:runtime"));
        b.Services.AddRequestTimeouts();b.Services.AddReverseProxy().ConfigureHttpClient((_,handler)=>{handler.AllowAutoRedirect=false;handler.UseCookies=false;});b.Services.AddHostedService<LkgRestoreWorker>();b.Services.AddHostedService(sp=>sp.GetRequiredService<ConfigWatcher>());b.Services.AddHostedService<HeartbeatWorker>();var app=b.Build();
        app.Use(async(ctx,next)=>{var path=ctx.Request.Path.Value??"";if(path.Equals("/health/live",StringComparison.OrdinalIgnoreCase)||path.Equals("/health/ready",StringComparison.OrdinalIgnoreCase)) {await next();return;}using var ticket=await ctx.RequestServices.GetRequiredService<AdmissionGate>().EnterAsync(ctx.RequestAborted);ctx.Items[AdmissionGate.Item]=ticket;if(ctx.RequestServices.GetRequiredService<RuntimeGenerationStore>().Current is null) {ctx.Response.StatusCode=503;await ctx.Response.WriteAsJsonAsync(new {code="gateway_not_ready"});return;}await next();});
        app.UseRouting();app.UseRequestTimeouts();app.MapGet("/health/live",()=>Results.Ok(new {status="live"})).WithOrder(int.MinValue);app.MapGet("/health/ready",(RuntimeGenerationStore store)=>store.Current is { } current?Results.Ok(new {status="ready",configVersion=current.Envelope.ConfigVersion,deploymentSequence=current.Envelope.DeploymentSequence}):Results.Json(new {status="not_ready"},statusCode:503)).WithOrder(int.MinValue);
        app.MapReverseProxy(proxy=>{proxy.UseMiddleware<ApiKeyMiddleware>();proxy.UseMiddleware<WeightedDestinationMiddleware>();proxy.UseLoadBalancing();proxy.UsePassiveHealthChecks();});return app;
    }
}
