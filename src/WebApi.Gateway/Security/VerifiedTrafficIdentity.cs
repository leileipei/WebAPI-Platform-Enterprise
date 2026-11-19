using WebApi.Contracts.Policies;
namespace WebApi.Gateway.Security;

// Sensitive values stay request-local; no public getters or record-generated ToString.
public sealed class JwtVerifiedClaims
{
    private readonly decimal issuedAt;
    private readonly decimal expiresAt;
    private readonly decimal? notBefore;
    private readonly int clockSkewSeconds;
    internal string Issuer { get; }
    internal string Subject { get; }
    internal string ApplicationClaimValue { get; }
    internal string ValidatedBearer { get; }
    internal JwtVerifiedClaims(string issuer, string subject, string applicationClaimValue, string validatedBearer,
        decimal issuedAt, decimal expiresAt, decimal? notBefore, int clockSkewSeconds)
    {
        Issuer = issuer;
        Subject = subject;
        ApplicationClaimValue = applicationClaimValue;
        ValidatedBearer = validatedBearer;
        this.issuedAt = issuedAt;
        this.expiresAt = expiresAt;
        this.notBefore = notBefore;
        this.clockSkewSeconds = clockSkewSeconds;
    }

    internal bool IsValidAt(DateTimeOffset now)
    {
        var current = now.ToUnixTimeMilliseconds() / 1000m;
        return issuedAt <= current + clockSkewSeconds && expiresAt > current - clockSkewSeconds &&
            (notBefore is null || notBefore <= current + clockSkewSeconds);
    }
}
public sealed class VerifiedTrafficIdentity(AuthenticationMode mode, Guid? applicationId, JwtVerifiedClaims? jwt)
{
    public AuthenticationMode Mode { get; } = mode;
    public Guid? ApplicationId { get; } = applicationId;
    internal JwtVerifiedClaims? Jwt { get; } = jwt;
}
