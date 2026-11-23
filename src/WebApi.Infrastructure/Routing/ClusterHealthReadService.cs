using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Catalog;
using WebApi.Contracts.Common;
using WebApi.Contracts.Gateway;
using WebApi.Infrastructure.Observability;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;

namespace WebApi.Infrastructure.Routing;

public sealed class ClusterHealthReadService(WebApiDbContext db, ObservationSourceClient client, ObservationSourceSettings settings)
{
    public bool Enabled => settings.Enabled;
    private sealed record Sample(string Node, Guid Instance, long Sequence, Guid Cluster, Guid Destination, string Target, string Component, string State);

    public async Task<ClusterDto[]> ReadAsync(Guid environment, ClusterDto[] clusters, CancellationToken ct)
    {
        if (clusters.Length == 0 || !settings.Enabled) return clusters;
        var nodes = await db.Set<GatewayNode>().AsNoTracking().Where(n=>n.EnvironmentId==environment && n.Enabled)
            .Select(n=>new { n.NodeName,n.InstanceId,n.CurrentDeploymentSequence,n.LastHeartbeatAt,n.Status }).ToArrayAsync(ct);
        if (nodes.Length == 0) return clusters;
        var matcher = "{webapi_environment_id=\"" + environment + "\",webapi_cluster_id=~\"" + string.Join('|', clusters.Select(c=>c.Id)) + "\"}";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            using var response = await client.GetAsync(settings.PrometheusUrl, "api/v1/query",
                new Dictionary<string,string>{["query"]="last_over_time(webapi_destination_health_component"+matcher+"[30s]) and on (webapi_environment_id,service_instance_id) "
                    +"(time()-webapi_telemetry_last_observed_timestamp_seconds{webapi_environment_id=\""+environment+"\"}<30)"}, timeout.Token);
            var root = response.RootElement;
            if (root.GetProperty("status").GetString() != "success" || root.GetProperty("data").GetProperty("resultType").GetString() != "vector") return clusters;
            var rows = root.GetProperty("data").GetProperty("result");
            if (rows.GetArrayLength() > 10000) return clusters;
            var samples = new List<Sample>();
            foreach (var row in rows.EnumerateArray())
            {
                var labels = row.GetProperty("metric");
                string? Label(string key) => labels.TryGetProperty(key, out var value) ? value.GetString() : null;
                if (Label("webapi_environment_id") != environment.ToString() || Label("webapi_health_component") is not ("active" or "passive") ||
                    !Guid.TryParse(Label("webapi_node_instance"),out var instance) || !long.TryParse(Label("webapi_deployment_sequence"),out var seq) ||
                    !Guid.TryParse(Label("webapi_cluster_id"),out var cluster) || !Guid.TryParse(Label("webapi_destination_id"),out var destination)) continue;
                var state = row.GetProperty("value")[1].GetString() switch { "1"=>"Healthy","0"=>"Unhealthy","-2"=>"Disabled",_=>"Unknown" };
                samples.Add(new(Label("service_instance_id")??"", instance, seq, cluster, destination, Label("webapi_health_target") ?? "", Label("webapi_health_component")!, state));
            }
            var lookup = samples.ToLookup(s=>(s.Node,s.Instance.ToString(),s.Sequence,s.Cluster,s.Destination,s.Target,s.Component),s=>s.State);
            var threshold = DateTimeOffset.UtcNow.AddSeconds(-30);
            return clusters.Select(cluster =>
            {
                var healthy = 0;
                var uncertain = false;
                var destinations = cluster.Destinations.Select(d =>
                {
                    var target = GatewayProbeTarget.Fingerprint(d.Address,cluster.HealthCheckPath);
                    var states = nodes.Select(n =>
                    {
                        string Get(string component) => n.Status=="Ready" && n.LastHeartbeatAt>=threshold
                            ? Aggregate(lookup[(n.NodeName,n.InstanceId,n.CurrentDeploymentSequence,cluster.Id,d.Id,target,component)]) : "Unknown";
                        return (Active:Get("active"), Passive:Get("passive"));
                    }).ToArray();
                    if (d.Enabled)
                    {
                        if (states.All(s=>s.Active!="Unhealthy" && s.Passive!="Unhealthy"))
                        {
                            if (states.All(s=>s.Active=="Healthy" || s.Passive=="Healthy")) healthy++;
                            else uncertain = true;
                        }
                    }
                    return d with { ActiveHealth=Aggregate(states.Select(s=>s.Active)), PassiveHealth=Aggregate(states.Select(s=>s.Passive)) };
                }).ToArray();
                return cluster with { Destinations=destinations, HealthyDestinationCount=uncertain?null:healthy };
            }).ToArray();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return clusters; }
        catch (Exception ex) when (ex is ApiException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException or IndexOutOfRangeException)
        { return clusters; }
    }

    private static string Aggregate(IEnumerable<string> values)
    {
        var states = values.Distinct().ToArray();
        if (states.Contains("Unhealthy")) return "Unhealthy";
        return states.Length == 1 ? states[0] : "Unknown";
    }
}
