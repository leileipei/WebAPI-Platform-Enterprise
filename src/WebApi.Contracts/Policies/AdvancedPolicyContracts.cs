using System.Text.Json;
using System.Text.Json.Serialization;

namespace WebApi.Contracts.Policies;

[JsonConverter(typeof(JsonStringEnumConverter<AuthenticationMode>))]
public enum AuthenticationMode { ApiKey, Anonymous, JWT }

public sealed record JwtApplicationMapping(string ClaimValue, Guid ApplicationId);
public sealed record JwtAuthenticationConfiguration(string Issuer, IReadOnlyList<string> Audiences,
    IReadOnlyList<string> AllowedAlgorithms, IReadOnlyList<string> AllowedTokenTypes,
    int ClockSkewSeconds, int MaxTokenLifetimeSeconds, string ApplicationClaim,
    IReadOnlyList<JwtApplicationMapping> ApplicationMappings, JsonElement Jwks, bool ForwardBearer)
{
    public AuthenticationMode Mode => AuthenticationMode.JWT;
}
public sealed record AuthenticationConfiguration(AuthenticationMode Mode, JwtAuthenticationConfiguration? Jwt = null);
public sealed record RetryConfiguration(int MaxAttempts, int PerAttemptTimeoutMs, int BaseDelayMs,
    int MaxDelayMs, int JitterPercent, IReadOnlyList<int> RetryStatusCodes, bool RetryConnectionFailures);
public sealed record CacheConfiguration(int TtlSeconds, int MaxEntryBytes, IReadOnlyList<string> VaryHeaders,
    string IdentityPartition, string RedisFailureMode);
