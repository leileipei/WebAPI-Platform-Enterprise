using System.Text.Json;
using WebApi.Contracts.Policies;
using static WebApi.Domain.Policies.PolicyConfigurationValidator;

namespace WebApi.Domain.Policies;

internal static class JwtPolicyConfiguration
{
    private static readonly HashSet<string> ReservedClaims = new(StringComparer.Ordinal)
        { "iss", "sub", "aud", "exp", "nbf", "iat", "jti", "auth_time", "nonce", "acr", "amr", "sid", "cnf" };
    internal static JwtAuthenticationConfiguration Parse(JsonElement root)
    {
        Fields(Names(root), "mode", "issuer", "audiences", "allowedAlgorithms", "allowedTokenTypes", "clockSkewSeconds", "maxTokenLifetimeSeconds", "applicationClaim", "applicationMappings", "jwks", "forwardBearer");
        if (Text(root, "mode") != "JWT") throw Invalid();
        var issuer = BoundedText(root, "issuer", 512);
        if (issuer.Any(char.IsWhiteSpace) || !Uri.TryCreate(issuer, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("https" or "http") || string.IsNullOrEmpty(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo) || issuer.Contains('?') || issuer.Contains('#')) throw Invalid();
        var audiences = StringArray(root, "audiences", 1, 8, 256);
        var algorithms = StringArray(root, "allowedAlgorithms", 1, 2, 16);
        if (algorithms.Any(a => a is not ("RS256" or "ES256"))) throw Invalid();
        var types = StringArray(root, "allowedTokenTypes", 1, 2, 16);
        if (types.Any(t => t is not ("JWT" or "at+jwt"))) throw Invalid();
        var claim = BoundedText(root, "applicationClaim", 64);
        if (ReservedClaims.Contains(claim) || claim.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))) throw Invalid();
        var mappings = root.GetProperty("applicationMappings");
        if (mappings.ValueKind != JsonValueKind.Array || mappings.GetArrayLength() is < 1 or > 128) throw Invalid();
        var values = new HashSet<string>(StringComparer.Ordinal); var list = new List<JwtApplicationMapping>();
        foreach (var mapping in mappings.EnumerateArray())
        {
            Fields(Names(mapping), "claimValue", "applicationId");
            var value = BoundedText(mapping, "claimValue", 256);
            if (!values.Add(value) || !Guid.TryParse(Text(mapping, "applicationId"), out var applicationId) || applicationId == Guid.Empty) throw Invalid();
            list.Add(new(value, applicationId));
        }
        var jwks = root.GetProperty("jwks");
        ValidateJwks(jwks, algorithms);
        return new(issuer, audiences, algorithms, types, Integer(root, "clockSkewSeconds", 0, 120),
            Integer(root, "maxTokenLifetimeSeconds", 60, 86400), claim, list.AsReadOnly(), jwks.Clone(), Boolean(root, "forwardBearer"));
    }
    private static void ValidateJwks(JsonElement jwks, IReadOnlyList<string> algorithms)
    {
        Fields(Names(jwks), "keys"); var keys = jwks.GetProperty("keys");
        if (keys.ValueKind != JsonValueKind.Array || keys.GetArrayLength() is < 1 or > 8) throw Invalid();
        var kids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in keys.EnumerateArray())
        {
            var names = Names(key);
            if (!kids.Add(BoundedText(key, "kid", 256)) || Text(key, "use") != "sig") throw Invalid();
            var algorithm = Text(key, "alg"); var kty = Text(key, "kty");
            if (!algorithms.Contains(algorithm, StringComparer.Ordinal)) throw Invalid();
            HashSet<string> allowed;
            if (algorithm == "RS256" && kty == "RSA")
            {
                allowed = new(StringComparer.Ordinal) { "kid", "kty", "alg", "use", "n", "e" };
                var n = DecodeUrl(BoundedText(key, "n", 684), 256, 512);
                // JWK unsigned integers use a minimal representation; no leading zero padding.
                var significantBits = n.Length * 8 - System.Numerics.BitOperations.LeadingZeroCount((uint)n[0]) + 24;
                if (n[0] == 0 || significantBits is < 2048 or > 4096 || (n[^1] & 1) == 0) throw Invalid();
                var e = DecodeUrl(BoundedText(key, "e", 684), 1, n.Length);
                var exponent = new System.Numerics.BigInteger(e, isUnsigned: true, isBigEndian: true);
                var modulus = new System.Numerics.BigInteger(n, isUnsigned: true, isBigEndian: true);
                if (e[0] == 0 || exponent < 3 || exponent.IsEven || exponent >= modulus) throw Invalid();
            }
            else if (algorithm == "ES256" && kty == "EC")
            {
                allowed = new(StringComparer.Ordinal) { "kid", "kty", "alg", "use", "crv", "x", "y" };
                if (Text(key, "crv") != "P-256") throw Invalid();
                DecodeUrl(BoundedText(key, "x", 43), 32, 32); DecodeUrl(BoundedText(key, "y", 43), 32, 32);
            }
            else throw Invalid();
            allowed.UnionWith(["key_ops", "x5c", "x5t", "x5t#S256"]);
            if (!names.IsSubsetOf(allowed)) throw Invalid();
            if (key.TryGetProperty("key_ops", out _))
            {
                var operations = StringArray(key, "key_ops", 1, 1, 16);
                if (operations[0] != "verify") throw Invalid();
            }
            foreach (var field in new[] { "x5t", "x5t#S256" })
                if (key.TryGetProperty(field, out _)) DecodeUrl(BoundedText(key, field, 128), 1, 96);
            if (key.TryGetProperty("x5c", out _))
                foreach (var certificate in StringArray(key, "x5c", 1, 8, 8192))
                {
                    byte[] decoded;
                    try { decoded = Convert.FromBase64String(certificate); } catch (FormatException) { throw Invalid(); }
                    if (decoded.Length == 0 || Convert.ToBase64String(decoded) != certificate) throw Invalid();
                }
        }
    }
    private static byte[] DecodeUrl(string text, int minBytes, int maxBytes)
    {
        if (text.Length % 4 == 1 || text.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))) throw Invalid();
        var padded = text.Replace('-', '+').Replace('_', '/') + new string('=', (4 - text.Length % 4) % 4);
        byte[] bytes; try { bytes = Convert.FromBase64String(padded); } catch (FormatException) { throw Invalid(); }
        var canonical = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        if (bytes.Length < minBytes || bytes.Length > maxBytes || canonical != text) throw Invalid();
        return bytes;
    }
}
