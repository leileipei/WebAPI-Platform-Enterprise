using WebApi.Contracts.Policies;
namespace WebApi.Gateway.Security;

// Sensitive values stay request-local; no public getters or record-generated ToString.
public sealed class JwtVerifiedClaims
{
    internal string Issuer { get; }
    internal string Subject { get; }
    internal string ApplicationClaimValue { get; }
    internal string ValidatedBearer { get; }
    internal JwtVerifiedClaims(string issuer, string subject, string applicationClaimValue, string validatedBearer)
    { Issuer = issuer; Subject = subject; ApplicationClaimValue = applicationClaimValue; ValidatedBearer = validatedBearer; }
}
public sealed class VerifiedTrafficIdentity(AuthenticationMode mode, Guid? applicationId, JwtVerifiedClaims? jwt)
{
    public AuthenticationMode Mode { get; } = mode;
    public Guid? ApplicationId { get; } = applicationId;
    internal JwtVerifiedClaims? Jwt { get; } = jwt;
}
