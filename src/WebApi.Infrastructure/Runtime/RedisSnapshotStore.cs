using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using StackExchange.Redis;
using WebApi.Contracts.Common;
using WebApi.Contracts.Runtime;
using WebApi.Domain.Runtime;
namespace WebApi.Infrastructure.Runtime;
public sealed class RedisSnapshotStore(string connection,string keyPrefix="webapi:runtime") : IDisposable
{
    private ConnectionMultiplexer? mux;
    private readonly SemaphoreSlim connectionLock=new(1,1);
    private async Task<ConnectionMultiplexer> ConnectionAsync(CancellationToken ct)
    {if(mux is not null) return mux;await connectionLock.WaitAsync(ct);try {return mux??=await ConnectionMultiplexer.ConnectAsync(connection).WaitAsync(ct);}finally {connectionLock.Release();}}
    private string Key(Guid env)=>$"{keyPrefix}:{{{env}}}:desired";
    public string Channel(Guid env)=>$"{keyPrefix}:{{{env}}}:changed";
    // Compare positive int64 sequences as decimal strings: Lua numbers lose precision above 2^53.
    private const string Script="""
        local old=redis.call('HGET',KEYS[1],'sequence')
        local new=ARGV[1]
        if old then
          if #old>#new or (#old==#new and old>new) then return -1 end
          if old==new then
            if redis.call('HGET',KEYS[1],'envelope')~=ARGV[2] or redis.call('HGET',KEYS[1],'payload')~=ARGV[3] then return -2 end
          end
        end
        redis.call('HSET',KEYS[1],'sequence',new,'envelope',ARGV[2],'payload',ARGV[3])
        redis.call('PUBLISH',ARGV[4],ARGV[2])
        return 1
        """;
    public async Task PutAsync(Guid envId,SnapshotEnvelope envelope,ReadOnlyMemory<byte> payload,CancellationToken ct=default)
    {
        Validate(envId,envelope,payload.Span);var client=await ConnectionAsync(ct);var result=(long)await client.GetDatabase().ScriptEvaluateAsync(Script,[Key(envId)], [envelope.DeploymentSequence.ToString(CultureInfo.InvariantCulture),JsonSerializer.Serialize(envelope,CanonicalJson.Options),payload.ToArray(),Channel(envId)]).WaitAsync(ct);
        if(result==-2) throw new InvalidOperationException("Same deployment sequence has conflicting snapshot content.");
    }
    public async Task<DesiredConfigResponse?> GetDesiredAsync(Guid envId,CancellationToken ct=default)
    {
        var client=await ConnectionAsync(ct);var data=await client.GetDatabase().HashGetAsync(Key(envId),["envelope","payload"]).WaitAsync(ct);if(data.All(v=>v.IsNull)) return null;if(data.Any(v=>v.IsNull)) throw new InvalidDataException("Incomplete runtime cache.");
        var envelope=JsonSerializer.Deserialize<SnapshotEnvelope>(data[0].ToString(),CanonicalJson.Options)??throw new InvalidDataException("Missing envelope.");var bytes=(byte[])data[1]!;Validate(envId,envelope,bytes);return new(envelope,bytes);
    }
    public async Task SubscribeAsync(Guid envId,Action changed,CancellationToken ct=default)
    {var client=await ConnectionAsync(ct);await client.GetSubscriber().SubscribeAsync(RedisChannel.Literal(Channel(envId)),(_,_)=>changed()).WaitAsync(ct);}
    public static void Validate(Guid envId,SnapshotEnvelope envelope,ReadOnlySpan<byte> payload)
    {
        if(envelope.ReleaseId==Guid.Empty||envelope.DeploymentSequence<=0||envelope.ConfigVersion<=0||payload.Length!=envelope.SizeBytes||payload.Length>16*1024*1024||Convert.ToHexStringLower(SHA256.HashData(payload))!=envelope.PayloadHash) throw new InvalidDataException("Invalid snapshot envelope or payload hash.");
        var snapshot=SnapshotValidator.ParsePayload(payload,envId);if(snapshot.ConfigVersion!=envelope.ConfigVersion) throw new InvalidDataException("Snapshot version mismatch.");SnapshotValidator.Validate(snapshot,envId);
    }
    public void Dispose() {mux?.Dispose();connectionLock.Dispose();}
}
