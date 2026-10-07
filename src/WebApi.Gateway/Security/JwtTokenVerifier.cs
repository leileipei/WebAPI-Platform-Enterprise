using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using WebApi.Contracts.Policies;

namespace WebApi.Gateway.Security;

public sealed class JwtVerificationResult
{
    public bool Success { get; }
    public string? FailureCode { get; }
    internal JwtVerifiedClaims? Claims { get; }
    internal JwtVerificationResult(JwtVerifiedClaims? claims)
    { Claims = claims; Success = claims is not null; FailureCode = Success ? null : "invalid_jwt"; }
}

public sealed class JwtTokenVerifier
{
    public const int MaximumTokenBytes = 16384;
    private const int MaximumJsonDepth = 8;
    private const int MaximumJsonMembers = 256;
    private static readonly JwtVerificationResult Invalid = new(null);

    public JwtVerificationResult Verify(string token, JwtAuthenticationConfiguration configuration, DateTimeOffset now)
    {
        try
        {
            if (string.IsNullOrEmpty(token) || token.Length > MaximumTokenBytes) return Invalid;
            var segments = token.Split('.');
            if (segments.Length != 3) return Invalid;
            var decoded = new byte[3][];
            for (var i = 0; i < 3; i++)
            {
                if (!TryDecode(segments[i], out decoded[i])) return Invalid;
            }
            using var header = JsonDocument.Parse(decoded[0], new JsonDocumentOptions { MaxDepth = MaximumJsonDepth });
            using var payload = JsonDocument.Parse(decoded[1], new JsonDocumentOptions { MaxDepth = MaximumJsonDepth });
            var count = 0;
            if (header.RootElement.ValueKind != JsonValueKind.Object || payload.RootElement.ValueKind != JsonValueKind.Object
                || !BoundedUnique(header.RootElement, ref count) || !BoundedUnique(payload.RootElement, ref count)) return Invalid;
            var h = header.RootElement;
            foreach (var name in new[] { "jku", "x5u", "jwk", "crit", "b64", "enc", "zip", "x5c", "x5t", "x5t#S256" })
                if (h.TryGetProperty(name, out _)) return Invalid;
            var algorithm = ReadString(h, "alg", 8);
            var type = ReadString(h, "typ", 8);
            var kid = ReadString(h, "kid", 256);
            if (algorithm is not ("RS256" or "ES256") || type is not ("JWT" or "at+jwt") || kid is null
                || !configuration.AllowedAlgorithms.Contains(algorithm, StringComparer.Ordinal)
                || !configuration.AllowedTokenTypes.Contains(type, StringComparer.Ordinal)) return Invalid;
            var keys = configuration.Jwks.GetProperty("keys").EnumerateArray()
                .Where(k => ReadString(k, "kid", 256) == kid).ToArray();
            if (keys.Length != 1 || ReadString(keys[0], "alg", 8) != algorithm) return Invalid;
            if (algorithm == "RS256" && ReadString(keys[0], "kty", 3) != "RSA"
                || algorithm == "ES256" && (ReadString(keys[0], "kty", 3) != "EC" || ReadString(keys[0], "crv", 8) != "P-256")) return Invalid;
            // Only a selected, published public key. No resolver, discovery or network configuration.
            var key = new JsonWebKey(keys[0].GetRawText());
            var parameters = new TokenValidationParameters
            {
                RequireSignedTokens = true, ValidateIssuerSigningKey = true, IssuerSigningKey = key,
                TryAllIssuerSigningKeys = false, ValidAlgorithms = configuration.AllowedAlgorithms,
                ValidTypes = configuration.AllowedTokenTypes, ValidateIssuer = true, ValidIssuer = configuration.Issuer,
                ValidateAudience = true, RequireAudience = true, ValidAudiences = configuration.Audiences,
                IgnoreTrailingSlashWhenValidatingAudience = false,
                ValidateLifetime = false, RequireExpirationTime = true,
                IncludeTokenOnFailedValidation = false, LogTokenId = false, LogValidationExceptions = false,
                SaveSigninToken = false
            };
            var handler = new JsonWebTokenHandler { MaximumTokenSizeInBytes = MaximumTokenBytes, MapInboundClaims = false };
            var validation = handler.ValidateTokenAsync(token, parameters).ConfigureAwait(false).GetAwaiter().GetResult();
            if (!validation.IsValid) return Invalid;
            var p = payload.RootElement;
            var issuer = ReadString(p, "iss", 512);
            var subject = ReadString(p, "sub", MaximumTokenBytes);
            var app = ReadString(p, configuration.ApplicationClaim, 256);
            if (issuer != configuration.Issuer || subject is null || app is null || !ExactAudience(p, configuration.Audiences)) return Invalid;
            if (!NumericDate(p, "iat", out var issued) || !NumericDate(p, "exp", out var expires)) return Invalid;
            var current = now.ToUnixTimeMilliseconds() / 1000m;
            var skew = configuration.ClockSkewSeconds;
            if (expires <= issued || expires - issued > configuration.MaxTokenLifetimeSeconds
                || issued > current + skew || expires <= current - skew) return Invalid;
            if (p.TryGetProperty("nbf", out _) && (!NumericDate(p, "nbf", out var notBefore)
                || notBefore > expires || notBefore > current + skew)) return Invalid;
            return new(new(issuer, subject, app, token));
        }
        catch (Exception exception) when (exception is JsonException or FormatException or ArgumentException
            or InvalidOperationException or CryptographicException or SecurityTokenException or OverflowException)
        { return Invalid; }
    }
    private static bool TryDecode(string segment, out byte[] value)
    {
        value = [];
        if (segment.Length == 0 || segment.Length % 4 == 1
            || segment.Any(c => !(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_'))) return false;
        value = Convert.FromBase64String(segment.Replace('-', '+').Replace('_', '/') + new string('=', (4 - segment.Length % 4) % 4));
        return Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_') == segment;
    }
    private static bool BoundedUnique(JsonElement element, ref int count)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (++count > MaximumJsonMembers || !names.Add(property.Name) || !BoundedUnique(property.Value, ref count)) return false;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                if (++count > MaximumJsonMembers || !BoundedUnique(item, ref count)) return false;
        }
        return true;
    }
    private static string? ReadString(JsonElement element, string name, int maximum)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String) return null;
        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) || text.Length > maximum ? null : text;
    }
    private static bool NumericDate(JsonElement element, string name, out decimal value)
    {
        value = 0;
        return element.TryGetProperty(name, out var date) && date.ValueKind == JsonValueKind.Number
            && date.TryGetDecimal(out value) && value >= -62135596800m && value <= 253402300799m;
    }
    private static bool ExactAudience(JsonElement payload, IReadOnlyList<string> allowed)
    {
        if (!payload.TryGetProperty("aud", out var audience)) return false;
        if (audience.ValueKind == JsonValueKind.String) return allowed.Contains(audience.GetString()!, StringComparer.Ordinal);
        if (audience.ValueKind != JsonValueKind.Array || audience.GetArrayLength() == 0) return false;
        var matched = false;
        foreach (var item in audience.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString())) return false;
            matched |= allowed.Contains(item.GetString()!, StringComparer.Ordinal);
        }
        return matched;
    }
}
