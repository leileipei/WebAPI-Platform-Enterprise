using System.Security.Cryptography;using System.Text;using System.Text.Json;using WebApi.Contracts.Common;using WebApi.Contracts.Observability;
namespace WebApi.Infrastructure.Observability;
public sealed record CursorBoundary(long Nanoseconds,IReadOnlyList<Guid> BoundaryIds);
public sealed class ObservationCursorCodec(ObservationSourceSettings settings)
{
    public const int MaximumBoundaryIds=64;
    private sealed record Payload(string Kind,Guid Actor,string Scope,long Start,long End,string Filter,long Nano,Guid[] Ids,long Expires);
    public string Encode(string kind,Guid actorId,TrustedObservationScope scope,TimeRange range,string filterHash,long boundaryNanoseconds,IReadOnlyList<Guid> boundaryIds)
    {
        if(boundaryIds.Count>MaximumBoundaryIds)throw Invalid();var body=JsonSerializer.SerializeToUtf8Bytes(new Payload(kind,actorId,ScopeHash(scope),range.Start.UtcTicks,range.End.UtcTicks,filterHash,boundaryNanoseconds,boundaryIds.Distinct().Order().ToArray(),DateTimeOffset.UtcNow.AddMinutes(15).ToUnixTimeSeconds()));
        var token=Base64(body)+"."+Base64(HMACSHA256.HashData(Key(kind),body));if(token.Length>4096)throw Invalid();return token;
    }
    public CursorBoundary Decode(string cursor,string kind,Guid actorId,TrustedObservationScope scope,TimeRange range,string filterHash)
    {
        try{
            if(cursor.Length>4096)throw Invalid();var parts=cursor.Split('.');if(parts.Length!=2)throw Invalid();var body=Bytes(parts[0]);var signature=Bytes(parts[1]);
            if(!CryptographicOperations.FixedTimeEquals(signature,HMACSHA256.HashData(Key(kind),body)))throw Invalid();var p=JsonSerializer.Deserialize<Payload>(body)??throw Invalid();
            if(p.Kind!=kind||p.Actor!=actorId||p.Scope!=ScopeHash(scope)||p.Start!=range.Start.UtcTicks||p.End!=range.End.UtcTicks||p.Filter!=filterHash||p.Expires<=DateTimeOffset.UtcNow.ToUnixTimeSeconds()||p.Expires>DateTimeOffset.UtcNow.AddMinutes(15).AddSeconds(30).ToUnixTimeSeconds()||p.Ids.Length>MaximumBoundaryIds||p.Nano<LokiLogSource.Nano(range.Start)||p.Nano>=LokiLogSource.Nano(range.End))throw Invalid();
            return new(p.Nano,p.Ids);
        }catch(Exception e)when(e is FormatException or JsonException or ArgumentException or NullReferenceException){throw Invalid();}
    }
    public static string Hash(object value)=>Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
    private static string ScopeHash(TrustedObservationScope scope)=>Hash(new{scope.OrganizationId,scope.ProjectId,Environments=scope.EnvironmentIds.Order().ToArray()});
    private byte[] Key(string kind){try{var key=Convert.FromBase64String(File.ReadAllText(settings.CursorSigningSecretFile!).Trim());if(key.Length<32)throw new FormatException();return key;}catch(Exception e)when(e is IOException or UnauthorizedAccessException or ArgumentException or FormatException){throw ObservationSourceSettings.Unavailable(kind=="traces"?"traces":"logs");}}
    private static string Base64(byte[] bytes)=>Convert.ToBase64String(bytes).TrimEnd('=').Replace('+','-').Replace('/','_');
    private static byte[] Bytes(string text){text=text.Replace('-','+').Replace('_','/');return Convert.FromBase64String(text+new string('=',(4-text.Length%4)%4));}
    private static ApiException Invalid()=>new(422,"invalid_observation_cursor","游标已过期或与当前用户、范围及筛选不匹配，请重新查询。");
}
