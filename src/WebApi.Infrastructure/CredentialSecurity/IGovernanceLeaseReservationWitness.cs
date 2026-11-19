using WebApi.Domain.CredentialSecurity;

namespace WebApi.Infrastructure.CredentialSecurity;

/// <summary>Independent ordering and durable storage of allow-lease reservations.</summary>
public interface IGovernanceLeaseReservationWitness
{
    /// <summary>
    /// The witness must atomically require the exact confirmed position and no pending write, and durably persist
    /// the immutable lease ID, reservation and expiry in the same ordering as journal preparation before acknowledging.
    /// The witness must use independently trusted time to reject expired or premature leases at that decision.
    /// A duplicate lease ID must match the exact reservation and the same confirmed position, with no pending write.
    /// An ambiguous outcome requires an exact retry with the same lease ID. It cannot authorize signing.
    /// </summary>
    Task<VerifiedGovernanceLeaseReservation> ReserveAllowLeaseAsync(GovernanceJournalId journal,
        GovernanceJournalPosition expectedConfirmed, GovernanceLeaseReservation reservation, CancellationToken ct = default);
}
