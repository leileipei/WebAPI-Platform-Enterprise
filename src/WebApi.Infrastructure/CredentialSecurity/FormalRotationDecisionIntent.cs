using WebApi.Contracts.Applications;
using WebApi.Domain.CredentialSecurity;
using WebApi.Infrastructure.Persistence.Entities;

namespace WebApi.Infrastructure.CredentialSecurity;

internal sealed record FormalRotationAbortAck(Guid NodeId, Guid InstanceId, Guid ChallengeNonce, Guid LeaseId,
    long Epoch, string CheckpointHash, DateTimeOffset ReceivedAt);

internal sealed record FormalRotationDecisionIntent(Guid ScheduleId, string ScheduleHash, Guid EnvironmentId,
    Guid ActorId, string Decision, string Reason, string RequestHash, DateTimeOffset HardDeadline, DateTimeOffset RecordedAt,
    Guid SecurityGeneration, long PreviousEpoch, string PreviousHash, Guid ReleaseId, long ConfigVersion,
    long DeploymentSequence, string SnapshotHash, FormalRotationPreviewTarget[] CurrentTargets,
    FormalRotationDeadlineTarget[] RevocationTargets, FormalRotationRequestProof[] RequestProofs,
    Guid? AbortOperationId, FormalRotationAbortAck[] AbortAcks)
{
    internal const string Complete = "Complete";
    internal const string AbortRequested = "AbortRequested";
    internal const string AbortConfirmed = "AbortConfirmed";

    public string Kind => FormalRotationJournalIntent.Decision;

    internal static FormalRotationDecisionIntent Read(GovernanceWriteIntent intent) =>
        FormalRotationJournalIntent.Read<FormalRotationDecisionIntent>(intent, value => value.IsValid());

    private bool IsValid()
    {
        if (ScheduleId == Guid.Empty || EnvironmentId == Guid.Empty || ActorId == Guid.Empty || SecurityGeneration == Guid.Empty ||
            !SecuritySignedEncoding.HexHash(ScheduleHash) || !SecuritySignedEncoding.HexHash(RequestHash) ||
            !SecuritySignedEncoding.HexHash(PreviousHash) || !SecuritySignedEncoding.HexHash(SnapshotHash) ||
            !FormalRotationJournalIntent.Instant(RecordedAt) || !FormalRotationJournalIntent.Instant(HardDeadline) || RecordedAt >= HardDeadline ||
            !FormalRotationJournalIntent.ValidReason(Reason) || PreviousEpoch is < 0 or long.MaxValue ||
            ReleaseId == Guid.Empty || ConfigVersion <= 0 || DeploymentSequence <= 0 ||
            CurrentTargets is not { Length: >= 2 } || CurrentTargets.Any(x => x is null || x.NodeId == Guid.Empty || x.InstanceId == Guid.Empty) ||
            !CurrentTargets.Select(x => x.NodeId).SequenceEqual(CurrentTargets.Select(x => x.NodeId).Distinct().Order()) ||
            RevocationTargets is null || RequestProofs is null || AbortAcks is null)
            return false;

        var current = CurrentTargets.Select(x => (x.NodeId, x.InstanceId));
        if (Decision == AbortConfirmed)
            return AbortOperationId is Guid operation && operation != Guid.Empty && RevocationTargets.Length == 0 && RequestProofs.Length == 0 &&
                AbortAcks.All(x => x is not null && x.LeaseId != Guid.Empty && x.ChallengeNonce != Guid.Empty &&
                    x.Epoch >= 1 && x.Epoch <= PreviousEpoch && x.ReceivedAt <= RecordedAt &&
                    FormalRotationJournalIntent.Instant(x.ReceivedAt) && SecuritySignedEncoding.HexHash(x.CheckpointHash)) &&
                AbortAcks.Select(x => (x.NodeId, x.InstanceId)).SequenceEqual(current);

        if (AbortOperationId is not null || AbortAcks.Length != 0 || RevocationTargets.Any(x => x is null ||
            x.NodeId == Guid.Empty || x.ChallengeNonce == Guid.Empty ||
            x.InstanceId == Guid.Empty && (x.ProtocolSupported || x.LastLeaseUntil is not null) ||
            x.LastLeaseUntil is DateTimeOffset expiry && !FormalRotationJournalIntent.Instant(expiry)))
            return false;
        var frozen = RevocationTargets.Select(x => (x.NodeId, x.InstanceId));
        if (!frozen.SequenceEqual(frozen.Distinct().Order()) ||
            RevocationTargets.Select(x => x.ChallengeNonce).Distinct().Count() != RevocationTargets.Length ||
            current.Except(RevocationTargets.Where(x => x.ProtocolSupported).Select(x => (x.NodeId, x.InstanceId))).Any())
            return false;
        return Decision switch
        {
            Complete => RequestProofs.All(x => x is not null) && RequestProofs.Select(x => (x.NodeId, x.InstanceId)).SequenceEqual(current),
            AbortRequested => RequestProofs.Length == 0,
            _ => false
        };
    }

    internal SecurityEvent Event(Guid operationId, FormalRotationScheduleIntent schedule)
    {
        var fact = new SecurityEvent
        {
            OperationId = operationId, OrganizationId = schedule.OrganizationId, ProjectId = schedule.ProjectId,
            EnvironmentId = EnvironmentId, SecurityGeneration = SecurityGeneration, Epoch = checked(PreviousEpoch + 1),
            CredentialId = Decision == Complete ? schedule.CredentialId : schedule.Window.ReplacementCredentialId,
            OccurredAt = RecordedAt, PreviousHash = PreviousHash
        };
        fact.EventHash = SecurityEventHash.Compute(fact);
        return fact;
    }
}
