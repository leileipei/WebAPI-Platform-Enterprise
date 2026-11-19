using System.Text.Json;
using WebApi.Domain.CredentialSecurity;
using WebApi.Infrastructure.Persistence.Entities;

namespace WebApi.Infrastructure.CredentialSecurity;

internal static class CredentialRevocationEvents
{
    internal const string Original = "CredentialRevoked";
    internal const string Inherited = "CredentialRevocationInherited";
    internal const string Archived = "CredentialRevokedArchived";

    internal static bool IsRevocation(string type) => type is Original or Inherited or Archived;

    internal static string? CoverageState(SecurityEvent fact) => fact.EventType == Archived ? "SealedNoIngressAtCommit" : null;

    internal static bool ValidMaterial(SecurityEvent fact)
    {
        if (fact.EventType != Archived) return fact.PolicyJson is null;
        try
        {
            return fact.PolicyJson is not null && SecuritySignedEncoding.HexHash(JsonSerializer.Deserialize<string>(fact.PolicyJson));
        }
        catch (JsonException) { return false; }
    }
}
