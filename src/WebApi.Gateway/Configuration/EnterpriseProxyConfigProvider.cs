using System.Collections.ObjectModel;
using System.Globalization;
using Microsoft.Extensions.Primitives;
using WebApi.Contracts.Runtime;
using Yarp.ReverseProxy.Configuration;
namespace WebApi.Gateway.Configuration;
public sealed class EnterpriseProxyConfigProvider : IProxyConfigProvider,IConfigChangeListener
{
    public sealed class Config(IReadOnlyList<RouteConfig> routes,IReadOnlyList<ClusterConfig> clusters) : IProxyConfig
    {public IReadOnlyList<RouteConfig> Routes {get;}=routes;public IReadOnlyList<ClusterConfig> Clusters {get;}=clusters;internal CancellationTokenSource Source {get;}=new();public IChangeToken ChangeToken=>new CancellationChangeToken(Source.Token);internal TaskCompletionSource<bool> Applied {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);}
    private Config current=new([],[]);public IProxyConfig GetConfig()=>Volatile.Read(ref current);
    public Config Current=>Volatile.Read(ref current);
    private static IReadOnlyDictionary<string,string> Metadata(params (string Key,string Value)[] values)=>new ReadOnlyDictionary<string,string>(values.ToDictionary(v=>v.Key,v=>v.Value));
    public Config Build(RuntimeSnapshot snapshot,long sequence)
    {
        var seq=sequence.ToString(CultureInfo.InvariantCulture);string ClusterId(Guid id)=>seq+":"+id;
        var routes=new List<RouteConfig>(snapshot.Routes.Count*2);
        var cors=snapshot.Policies.Where(p=>p.Type=="cors").Select(p=>p.Id).ToHashSet();
        foreach(var route in snapshot.Routes)
        {
            var original=new RouteConfig {RouteId=seq+":"+route.Id,ClusterId=ClusterId(route.ClusterId),
                Match=new() {Path=route.Path,Methods=Array.AsReadOnly(route.Methods.ToArray())},Order=route.MatchOrder,
                Timeout=TimeSpan.FromMilliseconds(route.TimeoutMs),Metadata=Metadata(("deploymentSequence",seq),("runtimeRouteId",route.Id.ToString()),("runtimeClusterId",route.ClusterId.ToString()))};
            routes.Add(original);
            if((route.PolicyBindings??[]).Any(b=>cors.Contains(b.PolicyId)))
                routes.Add(original with {RouteId=original.RouteId+":cors-preflight",Metadata=Metadata(("deploymentSequence",seq),("runtimeRouteId",route.Id.ToString()),("runtimeClusterId",route.ClusterId.ToString()),(CorsPreflightMatcherPolicy.MetadataKey,"true")),Match=new() {Path=route.Path,Methods=Array.AsReadOnly(new[]{"OPTIONS"}),Headers=Array.AsReadOnly(new[]{
                    new RouteHeader {Name="Origin",Mode=HeaderMatchMode.Exists},
                    new RouteHeader {Name="Access-Control-Request-Method",Mode=HeaderMatchMode.ExactHeader,Values=Array.AsReadOnly(route.Methods.ToArray()),IsCaseSensitive=true}
                })}});
        }
        var clusters=snapshot.Clusters.Select(c=>new ClusterConfig {ClusterId=ClusterId(c.Id),LoadBalancingPolicy=c.LoadBalancingPolicy,HttpClient=new() {DangerousAcceptAnyServerCertificate=false},HealthCheck=new() {Active=new() {Enabled=c.HealthCheckEnabled,Interval=TimeSpan.FromSeconds(c.HealthCheckIntervalSec),Timeout=TimeSpan.FromSeconds(Math.Min(5,c.HealthCheckIntervalSec)),Path=c.HealthCheckPath,Policy="ConsecutiveFailures"}},Destinations=new ReadOnlyDictionary<string,DestinationConfig>(c.Destinations.ToDictionary(d=>d.Id.ToString(),d=>new DestinationConfig {Address=d.Address}))}).ToArray();
        return new(routes.AsReadOnly(),Array.AsReadOnly(clusters));
    }
    public Task<bool> SwitchAsync(Config next) {var prior=Interlocked.Exchange(ref current,next);prior.Source.Cancel();return next.Applied.Task;}
    public void ConfigurationLoadingFailed(IProxyConfigProvider provider,Exception error) {if(provider==this) current.Applied.TrySetResult(false);}
    public void ConfigurationLoaded(IReadOnlyList<IProxyConfig> configs) {}
    public void ConfigurationApplyingFailed(IReadOnlyList<IProxyConfig> configs,Exception error) {foreach(var config in configs.OfType<Config>()) config.Applied.TrySetResult(false);}
    public void ConfigurationApplied(IReadOnlyList<IProxyConfig> configs) {foreach(var config in configs.OfType<Config>()) config.Applied.TrySetResult(true);}
}
