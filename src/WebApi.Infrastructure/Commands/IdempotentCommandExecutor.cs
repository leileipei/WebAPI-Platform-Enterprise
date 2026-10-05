using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Commands;
public sealed class IdempotentCommandExecutor(WebApiDbContext db)
{
    public async Task<T> ExecuteAsync<T>(CommandIdentity identity,ReadOnlyMemory<byte> normalizedRequest,Func<CancellationToken,Task<T>> command,CancellationToken ct=default)
    {
        var replay=await TryReplayAsync<T>(identity,normalizedRequest,ct);
        if(replay.Found)return replay.Value!;
        var scope=$"{identity.Scope.OrganizationId}:{identity.Scope.ProjectId}:{identity.Scope.EnvironmentId}";var hash=Convert.ToHexStringLower(SHA256.HashData(normalizedRequest.Span));
        var result=await command(ct);db.Add(new IdempotencyRecord {ActorId=identity.ActorId,ScopeKey=scope,Operation=identity.Operation,Key=identity.Key,RequestHash=hash,ResponseBytes=JsonSerializer.SerializeToUtf8Bytes(result,CanonicalJson.Options)});return result;
    }
    public async Task<(bool Found,T? Value)> TryReplayAsync<T>(CommandIdentity identity,ReadOnlyMemory<byte> normalizedRequest,CancellationToken ct=default)
    {
        if(db.Database.CurrentTransaction is null) throw new InvalidOperationException("Idempotency must participate in the business transaction.");
        if(string.IsNullOrWhiteSpace(identity.Key)||identity.Key.Length>128||!Regex.IsMatch(identity.Key,"^[A-Za-z0-9_.:-]+$")) throw new ApiException(422,"idempotency_required","该命令需要有效的Idempotency-Key。");
        var scope=$"{identity.Scope.OrganizationId}:{identity.Scope.ProjectId}:{identity.Scope.EnvironmentId}";var hash=Convert.ToHexStringLower(SHA256.HashData(normalizedRequest.Span));
        var prior=await db.Set<IdempotencyRecord>().SingleOrDefaultAsync(r=>r.ActorId==identity.ActorId&&r.ScopeKey==scope&&r.Operation==identity.Operation&&r.Key==identity.Key,ct);
        if(prior is not null) {if(prior.RequestHash!=hash) throw new ApiException(409,"idempotency_conflict","相同幂等键不能用于不同请求。");return (true,JsonSerializer.Deserialize<T>(prior.ResponseBytes!,CanonicalJson.Options)!);}
        return (false,default);
    }
}
