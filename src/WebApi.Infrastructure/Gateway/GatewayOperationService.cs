using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Gateway;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Commands;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Routing;
using WebApi.Infrastructure.Security;

namespace WebApi.Infrastructure.Gateway;

public sealed class GatewayOperationService(WebApiDbContext db, AuthorizationService auth, ScopeResolver scopes,
    AuditedCommandExecutor commands, NodeIdentityService identities, UpstreamAddressPolicy addresses, IdempotentCommandExecutor idempotency, CommandRequestContext request)
{
    private sealed record Stored(Guid ActorId, NodeOperation Operation);
    private const string Event = "maintenance";
    private static Stored Decode(GatewayNodeEvent row) => JsonSerializer.Deserialize<Stored>(row.Detail, CanonicalJson.Options)!;
    private static string Encode(Stored stored) => JsonSerializer.Serialize(stored, CanonicalJson.Options);
    private static bool Live(NodeOperation operation) => operation.State is "Queued" or "Running" && operation.ExpiresAt > DateTimeOffset.UtcNow;
    private static NodeOperation Display(NodeOperation op) => op.State is "Queued" or "Running" && !Live(op) ? op with { State = "Expired" } : op;
    private static ApiException Unavailable() => new(409, "gateway_operation_unavailable", "当前没有可执行此操作的在线网关实例。");
    private static ApiException Stale() => new(409, "gateway_operation_stale", "运维操作已过期或目标发生变化。");
    private IQueryable<GatewayNodeEvent> Operations => db.Set<GatewayNodeEvent>().Where(e => e.EventType == Event);
    private Task LockAsync(Guid env, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM environments WHERE id={env} FOR UPDATE", ct);

    public async Task<NodeOperation> RetryAsync(Guid id, string? etag, ActorContext actor, CancellationToken ct)
    {
        var env = await db.Set<GatewayNode>().Where(n => n.Id == id).Select(n => (Guid?)n.EnvironmentId).SingleOrDefaultAsync(ct) ?? throw ScopeResolver.Missing();
        var scope = await scopes.EnvironmentAsync(env, ct);
        return await commands.ExecuteAsync(actor, scope, "gateway.retry", async (_, token) =>
        {
            await auth.RequireAsync(actor, "gateway.operate", new("node", id, scope), token);
            return await idempotency.ExecuteAsync(new(actor.UserId,scope,"gateway.retry",request.IdempotencyKey),
                CanonicalJson.Serialize(new{id,etag}),async inner=>
            {
                await LockAsync(env, inner);
                var node = await db.Set<GatewayNode>().SingleAsync(n => n.Id == id, inner);
                if (etag != $"\"{node.InstanceId}:{node.Enabled}\"") throw new ApiException(412, "stale_revision", "节点实例或启用状态已变化。");
                return await EnqueueAsync(node, actor, "Retry", null, null, null, inner);
            },token);
        }, ct);
    }

    public async Task<NodeOperation> ProbeAsync(Guid clusterId, Guid destinationId, string? etag, ActorContext actor, CancellationToken ct)
    {
        var env = await db.Set<UpstreamCluster>().Where(c => c.Id == clusterId).Select(c => (Guid?)c.EnvironmentId).SingleOrDefaultAsync(ct) ?? throw ScopeResolver.Missing();
        var scope = await scopes.EnvironmentAsync(env, ct);
        return await commands.ExecuteAsync(actor, scope, "cluster.probe", async (_, token) =>
        {
            await auth.RequireAsync(actor, "cluster.write", new("cluster", clusterId, scope), token);
            return await idempotency.ExecuteAsync(new(actor.UserId,scope,"cluster.probe",request.IdempotencyKey),
                CanonicalJson.Serialize(new{clusterId,destinationId,etag}),async inner=>
            {
                await LockAsync(env, inner);
                var target = await ProbeTargetAsync(env, clusterId, destinationId, inner) ?? throw ScopeResolver.Missing();
                RevisionTag.Require(etag, target.Revision);
                var threshold = DateTimeOffset.UtcNow.AddSeconds(-30);
                var busy = Operations.Where(e=>e.CreatedAt>=threshold && (EF.Functions.JsonContains(e.Detail,"{\"operation\":{\"state\":\"Queued\"}}") ||
                        EF.Functions.JsonContains(e.Detail,"{\"operation\":{\"state\":\"Running\"}}"))).Select(e=>e.GatewayNodeId);
                var node = await db.Set<GatewayNode>().Where(n => n.EnvironmentId == env && n.Enabled && n.Status == "Ready" &&
                        n.LastHeartbeatAt >= threshold && !busy.Contains(n.Id) && EF.Functions.JsonExists(n.Metadata!, "processDiagnostics"))
                    .OrderBy(n => n.NodeName).FirstOrDefaultAsync(inner) ?? throw Unavailable();
                return await EnqueueAsync(node, actor, "Probe", clusterId, destinationId, target.Fingerprint, inner);
            },token);
        }, ct);
    }

    private async Task<(long Revision, string Fingerprint)?> ProbeTargetAsync(Guid env, Guid? clusterId, Guid? destinationId, CancellationToken ct)
    {
        var target = await (from c in db.Set<UpstreamCluster>() join d in db.Set<UpstreamDestination>() on c.Id equals d.ClusterId
            where c.EnvironmentId == env && c.Id == clusterId && c.Status == "Active" && d.Id == destinationId && d.Enabled
            select new { d.Revision, d.Address, c.HealthCheckPath }).SingleOrDefaultAsync(ct);
        return target == null ? null : (target.Revision, GatewayProbeTarget.Fingerprint(addresses.Validate(target.Address), target.HealthCheckPath));
    }

    private async Task<NodeOperation> EnqueueAsync(GatewayNode node, ActorContext actor, string action, Guid? cluster,
        Guid? destination, string? fingerprint, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (!node.Enabled || NodeDiagnosticReport.Read(node) is not { } report || report.ObservedAt < now.AddSeconds(-30)) throw Unavailable();
        var previous = await LatestAsync(node.Id, ct);
        if (previous != null)
        {
            var prior=Decode(previous).Operation;
            if (prior.InstanceId == report.InstanceId && Live(prior))
                throw new ApiException(409, "gateway_operation_busy", "该节点已有待执行运维操作。");
        }
        var op = new NodeOperation(Guid.NewGuid(), node.Id, report.InstanceId, action, now, now.AddSeconds(30), "Queued", cluster, destination, fingerprint);
        db.Add(new GatewayNodeEvent { GatewayNodeId = node.Id, EventType = Event, Message = action=="Retry"?"重新获取并应用配置":"立即探测已发布实例",
            Detail = Encode(new(actor.UserId, op)), CreatedAt = now });
        return op;
    }

    internal static Task<bool> HasPendingAsync(WebApiDbContext db,Guid node,Guid instance,CancellationToken ct)
    {
        var since=DateTimeOffset.UtcNow.AddSeconds(-30);
        var queued=JsonSerializer.Serialize(new {operation=new {instanceId=instance,state="Queued"}});
        var running=JsonSerializer.Serialize(new {operation=new {instanceId=instance,state="Running"}});
        return db.Set<GatewayNodeEvent>().AnyAsync(e=>e.GatewayNodeId==node&&e.EventType==Event&&e.CreatedAt>since&&
            (EF.Functions.JsonContains(e.Detail,queued)||EF.Functions.JsonContains(e.Detail,running)),ct);
    }

    internal static async Task CancelAsync(WebApiDbContext db,Guid node,CancellationToken ct)
    {
        var since=DateTimeOffset.UtcNow.AddSeconds(-30);
        var row=await db.Set<GatewayNodeEvent>().Where(e=>e.GatewayNodeId==node&&e.EventType==Event&&e.CreatedAt>since)
            .OrderByDescending(e=>e.Id).FirstOrDefaultAsync(ct);
        if(row==null) return;
        var stored=Decode(row);
        if(Live(stored.Operation)) row.Detail=Encode(stored with { Operation=stored.Operation with { State="Completed",Outcome="target_changed" } });
    }

    private Task<GatewayNodeEvent?> LatestAsync(Guid node, CancellationToken ct)
    {
        var since = DateTimeOffset.UtcNow.AddSeconds(-31);
        return Operations.Where(e => e.GatewayNodeId == node && e.CreatedAt >= since).OrderByDescending(e => e.Id).FirstOrDefaultAsync(ct);
    }

    public async Task<NodeOperation?> ClaimAsync(NodeIdentity identity, Guid instance, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_catalog.pg_advisory_xact_lock_shared(8901202)", ct);
        await LockAsync(identity.EnvironmentId, ct);
        await identities.RevalidateAsync(identity, instance, ct);
        var entry = await LatestAsync(identity.NodeId, ct);
        if (entry == null) return null;
        var stored = Decode(entry);
        var op = stored.Operation;
        if (!Live(op) || op.InstanceId != instance) return null;
        var scope = await scopes.EnvironmentAsync(identity.EnvironmentId, ct);
        var permission = op.Action == "Retry" ? "gateway.operate" : "cluster.write";
        var authorized = await auth.CanAsync(new(stored.ActorId, op.Id.ToString()), permission, new("environment", identity.EnvironmentId, scope), ct);
        var executable = authorized && (op.Action == "Retry" ||
            (await ProbeTargetAsync(identity.EnvironmentId, op.ClusterId, op.DestinationId, ct))?.Fingerprint == op.TargetFingerprint);
        op = executable ? op with { State = "Running" } : op with { State = "Completed", Outcome = "target_changed" };
        entry.Detail = Encode(stored with { Operation = op });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return executable ? op : null;
    }

    public async Task CompleteAsync(NodeIdentity identity, Guid id, NodeOperationResult receipt, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(identity.EnvironmentId, ct);
        await identities.RevalidateAsync(identity, receipt.InstanceId, ct);
        var entry = await LatestAsync(identity.NodeId, ct) ?? throw Stale();
        var stored = Decode(entry);
        var op = stored.Operation;
        if (op.Id != id || op.InstanceId != receipt.InstanceId) throw Stale();
        if (op.State == "Completed" && op.Outcome == receipt.Outcome && op.HttpStatus == receipt.HttpStatus && op.DurationMs == receipt.DurationMs) return;
        if (!Live(op) || op.State != "Running") throw Stale();
        var allowed = op.Action == "Retry" ? receipt.Outcome is "applied" or "rejected" or "no_configuration" or "unavailable" :
            receipt.Outcome is "healthy" or "unhealthy" or "unavailable" or "timeout" or "target_changed";
        var validEvidence = receipt.Outcome switch
        {
            "healthy" => receipt.HttpStatus is >=200 and <300 && receipt.DurationMs.HasValue,
            "unhealthy" => receipt.HttpStatus is >=100 and <200 or >=300 and <=599 && receipt.DurationMs.HasValue,
            _ => receipt.HttpStatus == null && receipt.DurationMs == null
        };
        if (!allowed || !validEvidence || receipt.HttpStatus is < 100 or > 599 || receipt.DurationMs is < 0 or > 30000)
            throw new ApiException(422, "invalid_operation_result", "节点运维回执无效。");
        entry.Detail = Encode(stored with { Operation = op with { State = "Completed", Outcome = receipt.Outcome,
            HttpStatus = receipt.HttpStatus, DurationMs = receipt.DurationMs } });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task<NodeOperation> GetAsync(Guid objectId, Guid id, bool cluster, ActorContext actor, CancellationToken ct)
    {
        var env = cluster
            ? await db.Set<UpstreamCluster>().Where(c => c.Id == objectId).Select(c => (Guid?)c.EnvironmentId).SingleOrDefaultAsync(ct)
            : await db.Set<GatewayNode>().Where(n => n.Id == objectId).Select(n => (Guid?)n.EnvironmentId).SingleOrDefaultAsync(ct);
        if (env == null || !await auth.CanAsync(actor, cluster ? "cluster.read" : "gateway.read",
            new("environment", env.Value, await scopes.EnvironmentAsync(env.Value, ct)), ct)) throw ScopeResolver.Missing();
        var events = Operations.Where(e => db.Set<GatewayNode>().Any(n => n.Id == e.GatewayNodeId && n.EnvironmentId == env));
        var match = cluster ? new { operation = (object)new { id, clusterId = objectId } } : new { operation = (object)new { id, nodeId = objectId } };
        var json = JsonSerializer.Serialize(match);
        var row = await events.AsNoTracking().SingleOrDefaultAsync(e => EF.Functions.JsonContains(e.Detail, json), ct) ?? throw ScopeResolver.Missing();
        return Display(Decode(row).Operation);
    }
}
