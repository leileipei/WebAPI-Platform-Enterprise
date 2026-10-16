using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.CredentialUsage;
using WebApi.Infrastructure.Gateway;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.CredentialUsage;

public sealed class UsageCollectorRegistry(WebApiDbContext db,UsageIngestBudget budget,TimeProvider clock)
{
    public Task<UsageReceipt> BindAsync(NodeIdentity identity,UsageCollectorRequest request,CancellationToken ct=default)
        =>ReceiveAsync(identity,_=>Task.FromResult(request),ct);
    public async Task<UsageReceipt> ReceiveAsync(NodeIdentity identity,Func<CancellationToken,Task<UsageCollectorRequest>> read,CancellationToken ct=default)
    {
        using var lease=budget.Enter();using var timeout=budget.Timeout(ct);
        try{return await BindCoreAsync(identity,await read(timeout.Token),timeout.Token);}
        catch(OperationCanceledException) when(!ct.IsCancellationRequested){throw new ApiException(503,"usage_ingest_timeout","统计接收超过处理预算。");}
    }
    private async Task<UsageReceipt> BindCoreAsync(NodeIdentity identity,UsageCollectorRequest request,CancellationToken ct)
    {
        if(request.Protocol!="1.0"||request.CollectorId==Guid.Empty||request.SenderInstanceId==Guid.Empty)throw Invalid();
        await using var transaction=await db.Database.BeginTransactionAsync(ct);
        await UsageNodeLock.AcquireAsync(db,identity,request.SenderInstanceId,ct);
        var collector=await db.Set<CredentialUsageCollector>().FromSqlInterpolated($"SELECT * FROM credential_usage_collectors WHERE node_id={identity.NodeId} AND collector_id={request.CollectorId} FOR UPDATE").SingleOrDefaultAsync(ct);
        var now=clock.GetUtcNow();
        if(collector is null){collector=new(){NodeId=identity.NodeId,CollectorId=request.CollectorId,EnvironmentId=identity.EnvironmentId,CreatedAt=now};db.Add(collector);}
        if(collector.EnvironmentId!=identity.EnvironmentId||collector.Protocol!="1.0"||collector.ClosedAt is not null)throw new ApiException(409,"usage_collector_closed","统计来源已关闭或身份不匹配。");
        if(!await db.Set<CredentialUsageCollectorInstance>().AnyAsync(x=>x.NodeId==identity.NodeId&&x.CollectorId==request.CollectorId&&x.InstanceId==request.SenderInstanceId,ct))
        {
            db.Add(new CredentialUsageCollectorInstance{NodeId=identity.NodeId,CollectorId=request.CollectorId,InstanceId=request.SenderInstanceId,RegisteredAt=now,IdentityProof="current"});
            db.Add(new GatewayNodeEvent{GatewayNodeId=identity.NodeId,EventType="usage.collector.registered",Message="凭证统计1.0来源绑定",CreatedAt=now,Detail=JsonSerializer.Serialize(new{protocol="1.0",collectorId=request.CollectorId,instanceId=request.SenderInstanceId})});
        }
        await db.SaveChangesAsync(ct);await transaction.CommitAsync(ct);return new("Registered",UsageNodeLock.Next(collector.AcceptedSequence),collector.AcceptedThrough);
    }
    private static ApiException Invalid()=>new(422,"invalid_usage_collector","统计来源协议或身份不合法。");
}

internal static class UsageNodeLock
{
    internal static async Task<GatewayNode> AcquireAsync(WebApiDbContext db,NodeIdentity identity,Guid sender,CancellationToken ct)
    {
        var node=await db.Set<GatewayNode>().FromSqlInterpolated($"SELECT * FROM gateway_nodes WHERE id={identity.NodeId} FOR UPDATE").SingleOrDefaultAsync(ct);
        if(node is null||!node.Enabled||node.EnvironmentId!=identity.EnvironmentId||node.IdentityHash!=identity.Hash)throw NodeIdentityService.Denied();
        if(sender==Guid.Empty||node.InstanceId!=sender.ToString())throw new ApiException(409,"node_instance_changed","节点实例已改变。");return node;
    }
    internal static long Next(long accepted)=>accepted<long.MaxValue?accepted+1:throw new ApiException(409,"usage_sequence_exhausted","统计来源序列已耗尽，需要建立新的采集来源。");
}
