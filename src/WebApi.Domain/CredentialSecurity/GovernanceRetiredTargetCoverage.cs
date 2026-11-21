using System.Text.Json.Serialization;
using WebApi.Contracts.Security;

namespace WebApi.Domain.CredentialSecurity;

// The original operation and challenge remain immutable. The current journal,
// ledger and ingress bind a short-lived observation, not a replacement ACK.
public sealed record GovernanceRetiredTargetScope(GovernanceJournalId Journal, GovernanceJournalPosition Position,
    GovernanceOperationConfirmation Operation, Guid CredentialId, Guid OrganizationId, Guid ProjectId, Guid EnvironmentId,
    Guid SecurityGeneration, long Epoch, string HeadHash, long RevocationEpoch, string RevocationHeadHash,
    Guid NodeId, Guid InstanceId, Guid ChallengeNonce, DateTimeOffset? LastLeaseUntil, string CurrentIngressScopeHash);

public sealed record GovernanceRetiredTargetCoverage([property: JsonRequired] GovernanceRetiredTargetScope Scope,
    [property: JsonRequired] string ProofHash, [property: JsonRequired] bool Complete,
    [property: JsonRequired] bool HasPendingWrite, [property: JsonRequired] bool IssuanceClosed,
    [property: JsonRequired] bool IngressIsolated, [property: JsonRequired] DateTimeOffset Lower,
    [property: JsonRequired] DateTimeOffset Upper, [property: JsonRequired] DateTimeOffset ValidUntil,
    [property: JsonRequired] IReadOnlyList<GovernanceLeaseReservation> Reservations);

public static class GovernanceRetiredTargetProtocol
{
    public const string Purpose = "retired-revocation-target";
    public static readonly TimeSpan MaximumClockSkew = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan MaximumObservationAge = TimeSpan.FromSeconds(5);

    public static bool ValidScope(GovernanceRetiredTargetScope? scope) =>
        scope is { Journal: { } journal, Operation: { } operation } && journal.AuthorityId != Guid.Empty &&
        journal.Generation != Guid.Empty && GovernanceJournalProtocol.ValidPosition(scope.Position) &&
        operation.OperationId != Guid.Empty && GovernanceJournalProtocol.ValidPosition(operation.Position) &&
        operation.Position.Sequence > 0 && operation.Position.Sequence <= scope.Position.Sequence &&
        scope.CredentialId != Guid.Empty && scope.OrganizationId != Guid.Empty && scope.ProjectId != Guid.Empty &&
        scope.EnvironmentId != Guid.Empty && scope.SecurityGeneration != Guid.Empty && scope.RevocationEpoch > 0 &&
        scope.Epoch >= scope.RevocationEpoch && SecuritySignedEncoding.HexHash(scope.HeadHash) &&
        SecuritySignedEncoding.HexHash(scope.RevocationHeadHash) && scope.NodeId != Guid.Empty &&
        scope.InstanceId != Guid.Empty && scope.ChallengeNonce != Guid.Empty &&
        (scope.LastLeaseUntil is not DateTimeOffset last || Utc(last)) &&
        SecuritySignedEncoding.HexHash(scope.CurrentIngressScopeHash);

    public static bool Valid(GovernanceRetiredTargetCoverage? proof) =>
        proof is not null && ValidScope(proof.Scope) && SecuritySignedEncoding.HexHash(proof.ProofHash) &&
        Utc(proof.Lower) && Utc(proof.Upper) && proof.Lower <= proof.Upper &&
        proof.Upper - proof.Lower <= TimeSpan.FromSeconds(4) && Utc(proof.ValidUntil) &&
        proof.ValidUntil > proof.Upper && proof.ValidUntil - proof.Lower <= TimeSpan.FromSeconds(30) &&
        (!proof.IngressIsolated || proof.ValidUntil - proof.Upper <= TimeSpan.FromSeconds(15)) &&
        proof.Reservations is not null && proof.Reservations.All(row => ValidReservation(row, proof)) &&
        proof.Reservations.Select(row => row.LeaseId).SequenceEqual(proof.Reservations.Select(row => row.LeaseId).Distinct().Order());

    private static bool ValidReservation(GovernanceLeaseReservation? row, GovernanceRetiredTargetCoverage proof) =>
        row is not null && row.LeaseId != Guid.Empty && row.OrganizationId == proof.Scope.OrganizationId &&
        row.ProjectId == proof.Scope.ProjectId && row.EnvironmentId == proof.Scope.EnvironmentId &&
        row.NodeId == proof.Scope.NodeId && row.InstanceId == proof.Scope.InstanceId && row.SecurityGeneration != Guid.Empty &&
        row.Epoch >= 0 && (row.SecurityGeneration != proof.Scope.SecurityGeneration || row.Epoch <= proof.Scope.Epoch) &&
        SecuritySignedEncoding.KeyId(row.SigningKeyId) && SecuritySignedEncoding.HexHash(row.CheckpointHash) &&
        SecuritySignedEncoding.HexHash(row.LeasePayloadHash) && Utc(row.IssuedAt) && Utc(row.FreshUntil) &&
        row.IssuedAt <= proof.Upper && row.FreshUntil > row.IssuedAt && row.FreshUntil - row.IssuedAt <= TimeSpan.FromSeconds(30);

    private static bool Utc(DateTimeOffset value) => value.Offset == TimeSpan.Zero &&
        value > DateTimeOffset.MinValue && value < DateTimeOffset.MaxValue;
}
