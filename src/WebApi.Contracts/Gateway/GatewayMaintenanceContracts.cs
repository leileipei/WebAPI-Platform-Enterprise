using System.Security.Cryptography;
using System.Text;

namespace WebApi.Contracts.Gateway;

public sealed record NodeDiagnostics(double? CpuPercent, long WorkingSetBytes, long? LastKnownGoodVersion);
public sealed record NodeDiagnosticsView(Guid InstanceId, DateTimeOffset ObservedAt, double? CpuPercent,
    long WorkingSetBytes, long? LastKnownGoodVersion);
public sealed record NodeOperationClaim(Guid InstanceId);
public sealed record NodeOperationResult(Guid InstanceId, string Outcome, int? HttpStatus = null, long? DurationMs = null);
public sealed record NodeOperation(Guid Id, Guid NodeId, Guid InstanceId, string Action, DateTimeOffset RequestedAt,
    DateTimeOffset ExpiresAt, string State, Guid? ClusterId = null, Guid? DestinationId = null,
    string? TargetFingerprint = null, string? Outcome = null, int? HttpStatus = null, long? DurationMs = null);

public static class GatewayProbeTarget
{
    public static string Fingerprint(string address, string healthPath) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(address.TrimEnd('/') + "\n" + healthPath)));
}
