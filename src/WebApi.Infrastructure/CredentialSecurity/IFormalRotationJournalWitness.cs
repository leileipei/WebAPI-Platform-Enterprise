using WebApi.Domain.CredentialSecurity;

namespace WebApi.Infrastructure.CredentialSecurity;

/// <summary>Independent, deadline-aware preparation on the same authority that orders lease reservations.</summary>
public interface IFormalRotationJournalWitness : IGovernanceJournalWitness
{
    // In one ordered durable decision, match the exact confirmed position and require no competing intent.
    // Decode the formal recovery intent and require its scope and deadline to match the request.
    // Verify complete Allow issuance and reservation history for the environment, including retired instances and
    // ambiguous reservations. Use trusted time and the preparation budget to reject an unsafe or unknown horizon.
    // Every potentially live Allow lease must expire by hardDeadline minus two seconds. Reserve that cutoff with
    // the exact pending intent. Pending blocks reservations. After confirmation, enforce the cutoff on every
    // environment Allow reservation until the corresponding authenticated tombstone is durably confirmed.
    Task<GovernanceWitnessState> PrepareFormalRotationAsync(GovernanceJournalId journal, GovernanceWriteIntent intent,
        Guid environmentId, DateTimeOffset hardDeadline, CancellationToken ct);

    // Require the exact confirmed schedule, scope and deadline in the decoded intent and under that same ordering.
    // Only prepare once independently trusted time's lower bound reaches the deadline. Keep the cutoff until confirming this exact
    // tombstone and preserve any constraints imposed by other schedules. Ordinary PrepareAsync must reject both
    // formal mutations. Recovery of an already prepared mutation uses ObserveAsync and ConfirmAsync.
    Task<GovernanceWitnessState> PrepareFormalRotationRevocationAsync(GovernanceJournalId journal, GovernanceWriteIntent intent,
        Guid scheduleId, Guid environmentId, DateTimeOffset hardDeadline, CancellationToken ct);
}
