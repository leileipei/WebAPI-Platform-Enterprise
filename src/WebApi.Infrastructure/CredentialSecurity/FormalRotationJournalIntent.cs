using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using WebApi.Contracts.Applications;
using WebApi.Contracts.Common;
using WebApi.Domain.CredentialSecurity;
using WebApi.Infrastructure.Persistence.Entities;

namespace WebApi.Infrastructure.CredentialSecurity;

internal static class FormalRotationJournalIntent
{
    internal const string Schedule = "FormalRotationSchedule";
    internal const string DeadlineRevocation = "FormalRotationDeadlineRevocation";

    private static readonly JsonSerializerOptions Strict = new(CanonicalJson.Options)
    {
        PropertyNameCaseInsensitive = false,
        NumberHandling = JsonNumberHandling.Strict,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16
    };

    internal static string? Kind(GovernanceWriteIntent intent)
    {
        try
        {
            using var json = JsonDocument.Parse(Base64Url.DecodeFromChars(intent.RecoveryMaterial),
                new JsonDocumentOptions { MaxDepth = Strict.MaxDepth });
            if (json.RootElement.ValueKind != JsonValueKind.Object) throw Invalid();
            if (!json.RootElement.TryGetProperty("kind", out var kind)) return null;
            return kind.GetString() is Schedule or DeadlineRevocation ? kind.GetString() : throw Invalid();
        }
        catch (Exception error) when (error is FormatException or JsonException or InvalidOperationException) { throw Invalid(); }
    }

    internal static GovernanceWriteIntent Create<T>(T plan, Guid operationId, GovernanceJournalPosition previous)
    {
        var bytes = CanonicalJson.Serialize(plan);
        if (bytes.Length > GovernanceJournalProtocol.MaxRecoveryBytes) throw Invalid();
        return new(operationId, previous, Base64Url.EncodeToString(bytes));
    }

    internal static T Read<T>(GovernanceWriteIntent intent, Predicate<T> validate)
    {
        try
        {
            var bytes = Base64Url.DecodeFromChars(intent.RecoveryMaterial);
            var plan = JsonSerializer.Deserialize<T>(bytes, Strict);
            if (plan is null || !validate(plan) || !bytes.AsSpan().SequenceEqual(CanonicalJson.Serialize(plan))) throw Invalid();
            return plan;
        }
        catch (Exception error) when (error is FormatException or JsonException) { throw Invalid(); }
    }

    internal static string Hash(GovernanceWriteIntent intent) => Convert.ToHexStringLower(
        SHA256.HashData(SecuritySignedEncoding.Unsigned("webapi.formal-rotation.intent.v1", intent)));

    internal static Guid OperationId<T>(string purpose, T identity) => new(SHA256.HashData(
        SecuritySignedEncoding.Unsigned(purpose, identity)).AsSpan(0, 16));

    internal static bool ValidReason(string? reason) => reason is "credential_retired" or "owner_request" or "policy_change";

    internal static bool Instant(DateTimeOffset time) => time > DateTimeOffset.MinValue && time < DateTimeOffset.MaxValue &&
        time.Offset == TimeSpan.Zero && time.Ticks % 10 == 0;

    internal static InvalidDataException Invalid() => new("formal_rotation_intent_invalid");
}

internal sealed record FormalRotationScheduleIntent(Guid CredentialId, Guid ApplicationId, Guid ActorId,
    Guid OrganizationId, Guid ProjectId, FormalRotationPreviewRequest Window, long CredentialRevision, DateTimeOffset RecordedAt,
    string Reason, string PreviewHash, string RequestHash, FormalRotationPreviewTarget[] Targets)
{
    public string Kind => FormalRotationJournalIntent.Schedule;

    internal static FormalRotationScheduleIntent Read(GovernanceWriteIntent intent) =>
        FormalRotationJournalIntent.Read<FormalRotationScheduleIntent>(intent, value => value.IsValid());

    private bool IsValid() => CredentialId != Guid.Empty && ApplicationId != Guid.Empty && ActorId != Guid.Empty &&
        OrganizationId != Guid.Empty && ProjectId != Guid.Empty && CredentialRevision > 0 &&
        Window is { } && Window.EnvironmentId != Guid.Empty && Window.ReplacementCredentialId is Guid replacementId &&
        replacementId != Guid.Empty && replacementId != CredentialId &&
        FormalRotationJournalIntent.Instant(RecordedAt) && FormalRotationJournalIntent.Instant(Window.NewValidFrom) &&
        FormalRotationJournalIntent.Instant(Window.OverlapUntil) && FormalRotationJournalIntent.Instant(Window.HardDeadline) &&
        FormalRotationJournalIntent.Instant(Window.NewExpiresAt) && Window.NewValidFrom <= Window.OverlapUntil &&
        RecordedAt < Window.OverlapUntil && Window.OverlapUntil <= Window.HardDeadline && Window.HardDeadline < Window.NewExpiresAt &&
        FormalRotationJournalIntent.ValidReason(Reason) && SecuritySignedEncoding.HexHash(PreviewHash) &&
        SecuritySignedEncoding.HexHash(RequestHash) && Targets is { Length: >= 2 } &&
        Targets.All(item => item is not null && item.NodeId != Guid.Empty && item.InstanceId != Guid.Empty) &&
        Targets.Select(item => item.NodeId).SequenceEqual(Targets.Select(item => item.NodeId).Distinct().Order());

    internal FormalRotationReceipt Receipt(Guid id, Guid? revocationId, bool replayed) => new(id, CredentialId,
        Window.ReplacementCredentialId!.Value, Window.EnvironmentId, RecordedAt, Window.HardDeadline,
        revocationId is null ? "Scheduled" : "RevocationRecorded", revocationId, replayed);
}

internal sealed record FormalRotationDeadlineTarget(Guid NodeId, Guid InstanceId, Guid ChallengeNonce,
    bool ProtocolSupported, DateTimeOffset? LastLeaseUntil)
{
    internal SecurityOperationTarget ToRow(Guid operationId, Guid environmentId, DateTimeOffset time) => new()
    {
        OperationId = operationId, EnvironmentId = environmentId, NodeId = NodeId, InstanceGuid = InstanceId,
        ChallengeNonce = ChallengeNonce, CreatedAt = time, ProtocolSupportedAtFreeze = ProtocolSupported,
        TargetState = InstanceId == Guid.Empty ? "AwaitingRegistration" : "Pending", LastLeaseUntil = LastLeaseUntil
    };
}

internal sealed record FormalRotationDeadlineIntent(Guid ScheduleId, string ScheduleHash, Guid CredentialId,
    Guid ApplicationId, Guid ActorId, Guid OrganizationId, Guid ProjectId, Guid EnvironmentId,
    Guid SecurityGeneration, long PreviousEpoch, string PreviousHash, DateTimeOffset HardDeadline,
    DateTimeOffset EventTime, FormalRotationDeadlineTarget[] Targets)
{
    internal const string Reason = "formal_rotation_hard_deadline";
    public string Kind => FormalRotationJournalIntent.DeadlineRevocation;

    internal static FormalRotationDeadlineIntent Read(GovernanceWriteIntent intent) =>
        FormalRotationJournalIntent.Read<FormalRotationDeadlineIntent>(intent, value => value.IsValid());

    private bool IsValid() => ScheduleId != Guid.Empty && SecuritySignedEncoding.HexHash(ScheduleHash) &&
        CredentialId != Guid.Empty && ApplicationId != Guid.Empty && ActorId != Guid.Empty &&
        OrganizationId != Guid.Empty && ProjectId != Guid.Empty && EnvironmentId != Guid.Empty &&
        SecurityGeneration != Guid.Empty && PreviousEpoch is >= 0 and < long.MaxValue && SecuritySignedEncoding.HexHash(PreviousHash) &&
        FormalRotationJournalIntent.Instant(HardDeadline) && FormalRotationJournalIntent.Instant(EventTime) &&
        EventTime >= HardDeadline && Targets is not null && Targets.All(item => item is not null &&
            item.NodeId != Guid.Empty && item.ChallengeNonce != Guid.Empty &&
            (item.InstanceId != Guid.Empty || !item.ProtocolSupported && item.LastLeaseUntil is null) &&
            (item.LastLeaseUntil is not DateTimeOffset expiry || FormalRotationJournalIntent.Instant(expiry))) &&
        Targets.Select(item => (item.NodeId, item.InstanceId)).SequenceEqual(
            Targets.Select(item => (item.NodeId, item.InstanceId)).Distinct().Order()) &&
        Targets.Select(item => item.ChallengeNonce).Distinct().Count() == Targets.Length;

    internal static Guid OperationId(GovernanceJournalId journal, Guid scheduleId) =>
        FormalRotationJournalIntent.OperationId("webapi.formal-rotation.deadline-operation.v1", new { journal, scheduleId });

    internal SecurityEvent Event(Guid operationId)
    {
        var fact = new SecurityEvent
        {
            OperationId = operationId, OrganizationId = OrganizationId, ProjectId = ProjectId, EnvironmentId = EnvironmentId,
            SecurityGeneration = SecurityGeneration, Epoch = checked(PreviousEpoch + 1), CredentialId = CredentialId,
            OccurredAt = EventTime, PreviousHash = PreviousHash
        };
        fact.EventHash = SecurityEventHash.Compute(fact);
        return fact;
    }
}
