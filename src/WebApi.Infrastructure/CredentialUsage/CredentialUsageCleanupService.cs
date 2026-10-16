using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using WebApi.Infrastructure.Persistence;
namespace WebApi.Infrastructure.CredentialUsage;

public sealed class CredentialUsageCleanupService(WebApiDbContext db,CredentialUsageRetentionOptions options)
{
    public async Task<int> CleanupAsync(DateTimeOffset now,CancellationToken ct=default)
    {
        now=now.ToUniversalTime();var hour=new DateTimeOffset(now.Year,now.Month,now.Day,now.Hour,0,0,TimeSpan.Zero);
        await using var transaction=await db.Database.BeginTransactionAsync(ct);var connection=(NpgsqlConnection)db.Database.GetDbConnection();
        var deleted=0;
        async Task Delete(string table,string alias,string predicate,string order,DateTimeOffset cutoff)
        {
            var remaining=1000-deleted;if(remaining<=0)return;
            var sql=$"DELETE FROM {table} WHERE ctid IN (SELECT {alias}.ctid FROM {table} {alias} WHERE {predicate} ORDER BY {order} LIMIT @maximum FOR UPDATE SKIP LOCKED)";
            await using var command=new NpgsqlCommand(sql,connection,(NpgsqlTransaction)transaction.GetDbTransaction());
            command.Parameters.AddWithValue("cutoff",cutoff);command.Parameters.AddWithValue("maximum",remaining);command.CommandTimeout=10;
            deleted+=await command.ExecuteNonQueryAsync(ct);
        }
        await Delete("credential_usage_hourly","h","h.hour_start < @cutoff","h.hour_start",hour.AddDays(-options.RetentionDays));
        await Delete("credential_usage_coverage","c","c.hour_start < @cutoff","c.hour_start",hour.AddDays(-options.RetentionDays));
        await Delete("credential_usage_batches","b","b.applied_at < @cutoff AND EXISTS (SELECT 1 FROM credential_usage_collectors k WHERE k.node_id=b.node_id AND k.collector_id=b.collector_id AND k.accepted_sequence>=b.sequence)","b.applied_at",now.AddHours(-options.BatchHashHours));
        // A source instance proves historical idle coverage as well as batch replay identity.
        var sourceCutoff=hour.AddDays(-options.RetentionDays);var replayCutoff=now.AddHours(-options.BatchHashHours);
        if(replayCutoff<sourceCutoff)sourceCutoff=replayCutoff;
        await Delete("credential_usage_collector_instances","i","i.ended_at < @cutoff AND NOT EXISTS (SELECT 1 FROM gateway_nodes n WHERE n.id=i.node_id AND n.instance_id=i.instance_id::text)","i.ended_at",sourceCutoff);
        await transaction.CommitAsync(ct);return deleted;
    }
}
