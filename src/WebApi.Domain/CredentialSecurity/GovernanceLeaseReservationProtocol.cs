using System.Security.Cryptography;
using System.Text.Json.Serialization;
using WebApi.Contracts.Common;

namespace WebApi.Domain.CredentialSecurity;

// SecuritySignedEncoding.LeaseHash commits to the unsigned lease claims. The reservation
// carries neither credentials nor signed lease material.
public sealed record GovernanceLeaseReservation(Guid LeaseId, Guid OrganizationId, Guid ProjectId,
    Guid EnvironmentId, Guid NodeId, Guid InstanceId, Guid SecurityGeneration, long Epoch, string SigningKeyId,
    string CheckpointHash, string LeasePayloadHash, DateTimeOffset IssuedAt, DateTimeOffset FreshUntil);

public sealed record GovernanceLeaseReservationRequest(GovernanceJournalId Journal, string Action, string Nonce,
    GovernanceJournalPosition ExpectedConfirmed, GovernanceLeaseReservation Reservation);

public sealed record GovernanceLeaseReservationPayload([property: JsonRequired] string RequestHash,
    [property: JsonRequired] string SourceId, [property: JsonRequired] string KeyId,
    [property: JsonRequired] GovernanceJournalPosition Confirmed, [property: JsonRequired] bool HasPendingWrite,
    [property: JsonRequired] GovernanceLeaseReservation Reservation, [property: JsonRequired] bool Durable);

public sealed record SignedGovernanceLeaseReservationResponse([property: JsonRequired] GovernanceLeaseReservationPayload Payload,
    [property: JsonRequired] string Signature);

public sealed class VerifiedGovernanceLeaseReservation
{
    internal VerifiedGovernanceLeaseReservation(GovernanceLeaseReservationRequest request)
    {
        Journal = request.Journal;
        Confirmed = request.ExpectedConfirmed;
        Reservation = request.Reservation;
    }

    public GovernanceJournalId Journal { get; }
    public GovernanceJournalPosition Confirmed { get; }
    public GovernanceLeaseReservation Reservation { get; }
}

public static class GovernanceLeaseReservationProtocol
{
    public const string RequestDomain = "webapi.credential-governance.lease-reservation-request.v1";
    public const string ResponseDomain = "webapi.credential-governance.lease-reservation-response.v1";
    public const int MaxWireBytes = 8 * 1024;
    private const string ReserveAction = "reserveAllowLease";
    private const string Unavailable = "credential_governance_witness_unavailable";

    public static GovernanceLeaseReservationRequest Reserve(GovernanceJournalId journal,
        GovernanceJournalPosition expectedConfirmed, GovernanceLeaseReservation reservation) =>
        new(journal, ReserveAction, Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)), expectedConfirmed, reservation);

    public static byte[] EncodeRequest(GovernanceLeaseReservationRequest request, DateTimeOffset sendTimeUtc)
    {
        ValidateRequest(request);
        RequireCurrent(request.Reservation, sendTimeUtc);
        return CanonicalJson.Serialize(request);
    }

    public static string RequestHash(GovernanceLeaseReservationRequest request)
    {
        ValidateRequest(request);
        return Convert.ToHexStringLower(SHA256.HashData(SecuritySignedEncoding.Unsigned(RequestDomain, request)));
    }

    public static VerifiedGovernanceLeaseReservation VerifyResponse(GovernanceLeaseReservationRequest request,
        byte[] response, GovernanceWitnessTrust trust, DateTimeOffset verificationTimeUtc)
    {
        var requestHash = RequestHash(request);
        RequireCurrent(request.Reservation, verificationTimeUtc);

        var wire = IndependentEvidenceWire.Read<SignedGovernanceLeaseReservationResponse>(response, MaxWireBytes, Unavailable);
        var payload = wire.Payload;
        if (trust is null || payload is null || payload.RequestHash != requestHash || payload.SourceId != trust.SourceId ||
            payload.KeyId != trust.KeyId || !SecuritySignedEncoding.KeyId(payload.SourceId) ||
            !SecuritySignedEncoding.KeyId(payload.KeyId)) throw Invalid();
        IndependentEvidenceWire.Verify(ResponseDomain, payload, wire.Signature, trust.PublicKey, Unavailable);

        if (!payload.Durable || payload.HasPendingWrite || payload.Confirmed != request.ExpectedConfirmed ||
            !ValidReservation(payload.Reservation) || payload.Reservation != request.Reservation) throw Invalid();
        return new(request);
    }

    private static void ValidateRequest(GovernanceLeaseReservationRequest request)
    {
        if (request is null || request.Action != ReserveAction || !SecuritySignedEncoding.HexHash(request.Nonce) ||
            !GovernanceJournalProtocol.ValidPosition(request.ExpectedConfirmed) || !ValidReservation(request.Reservation))
            throw Invalid();
        GovernanceJournalProtocol.RequireJournal(request.Journal);
    }

    private static bool ValidReservation(GovernanceLeaseReservation? reservation) =>
        reservation is not null && reservation.LeaseId != Guid.Empty && reservation.OrganizationId != Guid.Empty &&
        reservation.ProjectId != Guid.Empty && reservation.EnvironmentId != Guid.Empty && reservation.NodeId != Guid.Empty &&
        reservation.InstanceId != Guid.Empty && reservation.SecurityGeneration != Guid.Empty && reservation.Epoch >= 0 &&
        SecuritySignedEncoding.KeyId(reservation.SigningKeyId) && SecuritySignedEncoding.HexHash(reservation.CheckpointHash) &&
        SecuritySignedEncoding.HexHash(reservation.LeasePayloadHash) && reservation.IssuedAt > DateTimeOffset.MinValue &&
        reservation.IssuedAt.Offset == TimeSpan.Zero && reservation.FreshUntil.Offset == TimeSpan.Zero &&
        reservation.FreshUntil > reservation.IssuedAt && reservation.FreshUntil - reservation.IssuedAt <= TimeSpan.FromSeconds(30);

    private static void RequireCurrent(GovernanceLeaseReservation reservation, DateTimeOffset nowUtc)
    {
        // Two seconds is the existing signed lease protocol's maximum clock skew.
        if (nowUtc.Offset != TimeSpan.Zero || nowUtc == DateTimeOffset.MinValue || reservation.FreshUntil <= nowUtc ||
            reservation.IssuedAt - nowUtc > TimeSpan.FromSeconds(2)) throw Invalid();
    }

    private static InvalidDataException Invalid() => new(Unavailable);
}
