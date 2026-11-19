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
    // tombstone and preserve any constraints imposed by other schedules. Ordinary PrepareAsync must reject every
    // formal mutation. Recovery of an already prepared mutation uses ObserveAsync and ConfirmAsync.
    Task<GovernanceWitnessState> PrepareFormalRotationRevocationAsync(GovernanceJournalId journal, GovernanceWriteIntent intent,
        Guid scheduleId, Guid environmentId, DateTimeOffset hardDeadline, CancellationToken ct);

    // In the same durable order, match the confirmed schedule and the decoded intent, including its decision,
    // scope, deadline and exact previous position. A trusted UTC upper bound must cover the durable decision
    // and be strictly before the deadline. Pending keeps the cutoff and blocks new lease reservations.
    // AbortRequested keeps the cutoff after confirmation. AbortConfirmed requires an already confirmed
    // AbortRequested, the new credential's durable revocation and complete ACK evidence. Preparing it
    // irrevocably decides the abort. Confirm verifies local synchronization and then releases this schedule's
    // cutoff without reconsidering the deadline. Complete releases its cutoff only after confirming local
    // synchronization of the old credential's tombstone. Keep every cutoff imposed by another schedule.
    Task<GovernanceWitnessState> PrepareFormalRotationDecisionAsync(GovernanceJournalId journal, GovernanceWriteIntent intent,
        Guid scheduleId, Guid environmentId, DateTimeOffset hardDeadline, string decision, CancellationToken ct) =>
        Task.FromException<GovernanceWitnessState>(new InvalidDataException("credential_governance_witness_unavailable"));

    // Sign a fresh observation with the original durable specialized preparation
    // and its time bound. Generic preparation records cannot establish this evidence.
    Task<GovernanceWitnessState> ObserveFormalRotationDecisionAsync(GovernanceJournalId journal, GovernanceWriteIntent intent,
        Guid scheduleId, Guid environmentId, DateTimeOffset hardDeadline, string decision, CancellationToken ct) =>
        Task.FromException<GovernanceWitnessState>(new InvalidDataException("credential_governance_witness_unavailable"));
}
