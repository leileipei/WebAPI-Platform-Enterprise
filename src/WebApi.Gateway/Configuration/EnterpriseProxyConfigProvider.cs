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
        var routes=snapshot.Routes.Select(r=>new RouteConfig {RouteId=seq+":"+r.Id,ClusterId=ClusterId(r.ClusterId),Match=new() {Path=r.Path,Methods=Array.AsReadOnly(r.Methods.ToArray())},Order=r.MatchOrder,Timeout=TimeSpan.FromMilliseconds(r.TimeoutMs),Metadata=Metadata(("deploymentSequence",seq),("runtimeRouteId",r.Id.ToString()),("runtimeClusterId",r.ClusterId.ToString()))}).ToArray();
        var clusters=snapshot.Clusters.Select(c=>new ClusterConfig {ClusterId=ClusterId(c.Id),LoadBalancingPolicy=c.LoadBalancingPolicy,HttpClient=new() {DangerousAcceptAnyServerCertificate=false},HealthCheck=new() {Active=new() {Enabled=c.HealthCheckEnabled,Interval=TimeSpan.FromSeconds(c.HealthCheckIntervalSec),Timeout=TimeSpan.FromSeconds(Math.Min(5,c.HealthCheckIntervalSec)),Path=c.HealthCheckPath,Policy="ConsecutiveFailures"}},Destinations=new ReadOnlyDictionary<string,DestinationConfig>(c.Destinations.ToDictionary(d=>d.Id.ToString(),d=>new DestinationConfig {Address=d.Address}))}).ToArray();
        return new(Array.AsReadOnly(routes),Array.AsReadOnly(clusters));
    }
    public Task<bool> SwitchAsync(Config next) {var prior=Interlocked.Exchange(ref current,next);prior.Source.Cancel();return next.Applied.Task;}
    public void ConfigurationLoadingFailed(IProxyConfigProvider provider,Exception error) {if(provider==this) current.Applied.TrySetResult(false);}
    public void ConfigurationLoaded(IReadOnlyList<IProxyConfig> configs) {}
    public void ConfigurationApplyingFailed(IReadOnlyList<IProxyConfig> configs,Exception error) {foreach(var config in configs.OfType<Config>()) config.Applied.TrySetResult(false);}
    public void ConfigurationApplied(IReadOnlyList<IProxyConfig> configs) {foreach(var config in configs.OfType<Config>()) config.Applied.TrySetResult(true);}
}
