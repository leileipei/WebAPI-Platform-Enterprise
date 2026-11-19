using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using WebApi.Contracts.Applications;
using WebApi.Contracts.Common;
using WebApi.Domain.CredentialSecurity;
using WebApi.Infrastructure.Persistence.Entities;

namespace WebApi.Infrastructure.CredentialSecurity;

internal sealed record CredentialRevocationEnvironmentIntent(long AuthorizationRevision,
    CredentialRevocationCoverageScope Coverage, Guid[] Challenges)
{
    public bool IsValid(DateTimeOffset eventTime) => AuthorizationRevision > 0 &&
        Coverage is { Nodes: { Count: >= 2 } } && Coverage.OrganizationId != Guid.Empty && Coverage.ProjectId != Guid.Empty &&
        Coverage.EnvironmentId != Guid.Empty && Coverage.SecurityGeneration != Guid.Empty && Coverage.Epoch is >= 0 and < long.MaxValue &&
        SecuritySignedEncoding.HexHash(Coverage.HeadHash) && Coverage.ReleaseId != Guid.Empty && SecuritySignedEncoding.HexHash(Coverage.SnapshotHash) &&
        Coverage.DeploymentSequence > 0 && Coverage.ConfigVersion > 0 &&
        Coverage.Nodes.All(x => x is not null && x.NodeId != Guid.Empty && x.InstanceId != Guid.Empty &&
            x.LeaseId != Guid.Empty && SecuritySignedEncoding.HexHash(x.LeasePayloadHash) && SecuritySignedEncoding.HexHash(x.CheckpointHash) &&
            x.FreshUntil > eventTime && x.LastLeaseUntil >= x.FreshUntil && x.FreshUntil.Offset == TimeSpan.Zero && x.LastLeaseUntil.Offset == TimeSpan.Zero) &&
        Coverage.Nodes.Select(x => x.NodeId).SequenceEqual(Coverage.Nodes.Select(x => x.NodeId).Distinct().Order()) &&
        Challenges is not null && Challenges.Length == Coverage.Nodes.Count &&
        Challenges.All(x => x != Guid.Empty) && Challenges.Distinct().Count() == Challenges.Length;
}

internal sealed record CredentialRevocationIntent(Guid CredentialId, Guid ApplicationId, Guid ActorId, long CredentialRevision,
    long ApplicationRevision, long ApplicationAuthorizationRevision, long EnvironmentAuthorizationRevision,
    string Reason, string RequestHash, DateTimeOffset EventTime,
    CredentialRevocationCoverageScope Coverage, Guid[] Challenges,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CredentialRevocationEnvironmentIntent[]? AdditionalGlobalEnvironments = null)
{
    private static readonly JsonSerializerOptions Strict = new(CanonicalJson.Options)
    {
        PropertyNameCaseInsensitive = false,
        NumberHandling = JsonNumberHandling.Strict,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16
    };

    // Absence preserves the original environment-bound journal bytes. An empty
    // array explicitly identifies a global revocation with one affected environment.
    [JsonIgnore] public bool IsGlobal => AdditionalGlobalEnvironments is not null;
    [JsonIgnore] public Guid? ExpectedEnvironmentId => IsGlobal ? null : Coverage.EnvironmentId;

    public CredentialRevocationEnvironmentIntent[] Environments() =>
        [new(EnvironmentAuthorizationRevision, Coverage, Challenges), .. AdditionalGlobalEnvironments ?? []];

    public GovernanceWriteIntent Create(Guid operationId, GovernanceJournalPosition previous)
    {
        var bytes = CanonicalJson.Serialize(this);
        if (bytes.Length > GovernanceJournalProtocol.MaxRecoveryBytes) throw LegacyGlobalRevocationScope.Unknown();
        return new(operationId, previous, Base64Url.EncodeToString(bytes));
    }

    public static CredentialRevocationIntent Read(GovernanceWriteIntent intent)
    {
        try
        {
            var bytes = Base64Url.DecodeFromChars(intent.RecoveryMaterial);
            var plan = JsonSerializer.Deserialize<CredentialRevocationIntent>(bytes, Strict);
            if (plan is null || !bytes.AsSpan().SequenceEqual(CanonicalJson.Serialize(plan)) || !plan.Valid()) throw Invalid();
            return plan;
        }
        catch (Exception error) when (error is FormatException or JsonException) { throw Invalid(); }
    }

    private bool Valid()
    {
        if (CredentialId == Guid.Empty || ApplicationId == Guid.Empty || ActorId == Guid.Empty ||
            CredentialRevision <= 0 || ApplicationRevision <= 0 || ApplicationAuthorizationRevision <= 0 ||
            Reason is not { Length: >= 1 and <= 1000 } || Reason.Any(char.IsControl) || !SecuritySignedEncoding.HexHash(RequestHash) ||
            EventTime <= DateTimeOffset.MinValue || EventTime.Offset != TimeSpan.Zero || EventTime.Ticks % 10 != 0)
            return false;
        var environments = Environments();
        if (environments.Any(e => e is null || !e.IsValid(EventTime) || e.Coverage.OrganizationId != Coverage.OrganizationId)) return false;
        var ids = environments.Select(e => e.Coverage.EnvironmentId).ToArray();
        var challenges = environments.SelectMany(e => e.Challenges).ToArray();
        return ids.SequenceEqual(ids.Distinct().Order()) && challenges.Distinct().Count() == challenges.Length;
    }

    public SecurityEvent Event(Guid id, CredentialRevocationCoverageScope scope)
    {
        var fact = new SecurityEvent { OperationId = id, OrganizationId = scope.OrganizationId, ProjectId = scope.ProjectId,
            EnvironmentId = scope.EnvironmentId, SecurityGeneration = scope.SecurityGeneration, Epoch = checked(scope.Epoch + 1),
            CredentialId = CredentialId, OccurredAt = EventTime, PreviousHash = scope.HeadHash };
        fact.EventHash = SecurityEventHash.Compute(fact);
        return fact;
    }

    public Guid OutboxId(Guid operationId, Guid environmentId) => environmentId == Coverage.EnvironmentId ? operationId :
        new Guid(SHA256.HashData(SecuritySignedEncoding.Unsigned("webapi.credential-revoke.outbox.v1", new { operationId, environmentId })).AsSpan(0, 16));

    public RevocationOperationDto Receipt(Guid id, bool replayed)
    {
        var environments = Environments();
        return new(id, CredentialId, EventTime, 1, "Unconfirmed", $"/api/v1/credential-revocations/{id}",
            environments.Select(e => Event(id, e.Coverage)).Select(fact =>
                new RevocationEnvironmentDto(fact.EnvironmentId, fact.SecurityGeneration, fact.Epoch, fact.EventHash)).ToArray(),
            environments.SelectMany(e => e.Coverage.Nodes.Select(node => new RevocationTargetDto(e.Coverage.EnvironmentId,
                node.NodeId, node.InstanceId, "Pending", LastLeaseUntil: node.LastLeaseUntil))).ToArray(), replayed);
    }

    private static InvalidDataException Invalid() => new("credential_revocation_intent_invalid");
}
