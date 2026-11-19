using WebApi.Domain.CredentialSecurity;
using WebApi.Infrastructure.Persistence;

namespace WebApi.Infrastructure.CredentialSecurity;

public sealed record GovernanceContinuity(GovernanceJournalId Journal, GovernanceJournalPosition Position, SecurityLedgerMaterial Ledger);

public interface IGovernanceRecoveryContinuity
{
    // Verify independent journal and environment history in the protected action's existing
    // ReadCommitted transaction and exclusive PostgreSQL governance writer fence.
    Task<GovernanceContinuity> RequireAsync(WebApiDbContext db, Guid environmentId, CancellationToken ct = default);
}
