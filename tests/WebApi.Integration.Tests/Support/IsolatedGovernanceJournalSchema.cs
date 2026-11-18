using Microsoft.EntityFrameworkCore;
using WebApi.Domain.CredentialSecurity;
using WebApi.Infrastructure.Persistence;

namespace WebApi.Integration.Tests.Support;

// The production migration installs the schema. Tests bind only their own journal.
internal static class IsolatedGovernanceJournalSchema
{
    internal static async Task InstallAsync(WebApiDbContext db, GovernanceJournalId journal, CancellationToken ct = default)
    {
        GovernanceJournalProtocol.RequireJournal(journal);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SET LOCAL synchronous_commit = on; SELECT pg_catalog.pg_advisory_xact_lock(8901202)", ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO credential_governance.journal_root (singleton, authority_id, generation, genesis_hash)
                VALUES (true, {journal.AuthorityId}, {journal.Generation}, {GovernanceJournalProtocol.Genesis.Hash})
            """, ct);
        await tx.CommitAsync(ct);
    }
}
