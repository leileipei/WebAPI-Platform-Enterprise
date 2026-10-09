using WebApi.HttpSecurity;
using WebApi.Gateway.Observability;
using WebApi.Gateway.Forwarding;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WebApi.Gateway.Policies;
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
        var b=WebApplication.CreateBuilder(args);configure?.Invoke(b);b.Services.AddTrustedProxyBoundary(b.Configuration);var settings=GatewaySettings.Read(b.Configuration);b.Services.AddSingleton(settings);b.Services.AddSingleton<JwtTokenVerifier>();b.Services.AddSingleton<AdmissionGate>();b.Services.AddSingleton<RuntimeGenerationStore>();b.Services.AddSingleton<LkgStore>();b.Services.AddSingleton<SnapshotActivation>();b.Services.AddSingleton<NodeClient>();b.Services.AddSingleton<ConfigWatcher>();b.Services.AddSingleton<EnterpriseProxyConfigProvider>();b.Services.AddSingleton<IProxyConfigProvider>(sp=>sp.GetRequiredService<EnterpriseProxyConfigProvider>());b.Services.AddSingleton<IConfigChangeListener>(sp=>sp.GetRequiredService<EnterpriseProxyConfigProvider>());
        b.Services.AddSingleton(new UpstreamAddressPolicy(UpstreamAddressPolicy.ReadAllowedOrigins(b.Configuration)));b.Services.AddSingleton(new RedisSnapshotStore(b.Configuration["Redis:Connection"]??"redis:6379,abortConnect=false,connectTimeout=1000,asyncTimeout=1000",b.Configuration["Redis:Prefix"]??"webapi:runtime"));
        b.Services.AddSingleton(GatewayCacheSettings.Read(b.Configuration));b.Services.AddSingleton<CacheKeyBuilder>();b.Services.TryAddSingleton<IResponseCacheStore,RedisResponseCacheStore>();
        b.Services.AddSingleton(TrafficPolicySettings.Read(b.Configuration));b.Services.TryAddSingleton<IRateLimitStore,RedisTokenBucketStore>();b.Services.AddSingleton<CircuitStateRegistry>();b.Services.AddSingleton<ForwardAttemptRegistry>();b.Services.AddSingleton<WeightedDestinationSelector>();b.Services.AddSingleton<GatewayForwardingAdapter>();b.Services.AddSingleton<RetryCoordinator>();
        b.Services.AddGatewayTelemetry(b.Configuration,settings);b.Services.AddRequestTimeouts();b.Services.AddReverseProxy().ConfigureHttpClient((_,handler)=>{handler.AllowAutoRedirect=false;handler.UseCookies=false;});b.Services.AddHostedService<LkgRestoreWorker>();b.Services.AddHostedService(sp=>sp.GetRequiredService<ConfigWatcher>());b.Services.AddHostedService<HeartbeatWorker>();var app=b.Build();app.UseTrustedProxyBoundary();
        if(app.Services.GetRequiredService<TelemetrySettings>().Enabled) {
            var recorder=app.Services.GetRequiredService<GatewayTelemetryRecorder>();
            app.Services.GetRequiredService<CircuitStateRegistry>().Transitioned+=(_,policy,_,next)=>recorder.RecordPolicy(policy.SourcePolicyId!.Value,"circuit_breaker",next switch {WebApi.Domain.Policies.CircuitStatus.Open=>"Opened",WebApi.Domain.Policies.CircuitStatus.HalfOpen=>"HalfOpened",_=>"Closed"});
            app.UseMiddleware<RequestTelemetryMiddleware>();
        }
        app.Use(async(ctx,next)=>{var path=ctx.Request.Path.Value??"";if(path.Equals("/health/live",StringComparison.OrdinalIgnoreCase)||path.Equals("/health/ready",StringComparison.OrdinalIgnoreCase)) {await next();return;}using var ticket=await ctx.RequestServices.GetRequiredService<AdmissionGate>().EnterAsync(ctx.RequestAborted);ctx.Items[AdmissionGate.Item]=ticket;if(ctx.RequestServices.GetRequiredService<RuntimeGenerationStore>().Current is null) {ctx.Response.StatusCode=503;await ctx.Response.WriteAsJsonAsync(new {code="gateway_not_ready"});return;}await next();});
        app.UseRouting();app.UseRequestTimeouts();app.MapGet("/health/live",()=>Results.Ok(new {status="live"})).WithOrder(int.MinValue);app.MapGet("/health/ready",(RuntimeGenerationStore store)=>store.Current is { } current?Results.Ok(new {status="ready",configVersion=current.Envelope.ConfigVersion,deploymentSequence=current.Envelope.DeploymentSequence}):Results.Json(new {status="not_ready"},statusCode:503)).WithOrder(int.MinValue);
        app.MapReverseProxy(proxy=>{proxy.UseMiddleware<GatewayPlatformHeadersMiddleware>();proxy.UseMiddleware<ApiKeyMiddleware>();proxy.UseMiddleware<TrafficPolicyMiddleware>();proxy.UseMiddleware<CacheResponseMiddleware>();proxy.UseMiddleware<CircuitPolicyMiddleware>();proxy.UseMiddleware<RetryForwardingMiddleware>();proxy.UseMiddleware<WeightedDestinationMiddleware>();proxy.UseLoadBalancing();proxy.UsePassiveHealthChecks();});return app;
    }
}
