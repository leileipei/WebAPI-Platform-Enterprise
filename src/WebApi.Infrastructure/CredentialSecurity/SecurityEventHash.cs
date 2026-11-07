using System.Security.Cryptography;
using System.Text.Json;
using WebApi.Contracts.Common;
using WebApi.Infrastructure.Persistence.Entities;

namespace WebApi.Infrastructure.CredentialSecurity;

// Keep the governance event-chain format stable across the protocol integration.
public static class SecurityEventHash
{
    public static readonly string Genesis = Convert.ToHexStringLower(SHA256.HashData(CanonicalJson.Serialize(Array.Empty<object>())));

    public static string Compute(SecurityEvent e) => Convert.ToHexStringLower(SHA256.HashData(CanonicalJson.Serialize(new
    {
        e.OrganizationId, e.ProjectId, e.EnvironmentId, e.SecurityGeneration, e.Epoch, e.OperationId,
        e.EventType, e.CredentialId,
        PolicyJson = e.PolicyJson is null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(e.PolicyJson),
        e.OccurredAt, e.PreviousHash
    })));
}
