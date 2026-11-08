using System.Runtime.ExceptionServices;
using WebApi.Infrastructure.Persistence;

namespace WebApi.Integration.Tests.Support;

// Legacy transaction/connection assertions now exercise the coordinator while
// retaining their original exception assertions. The journal is a test double;
// durability is covered separately by real-journal tests.
internal static class MigrationRunnerAssertions
{
    public static async Task RunAsync(WebApiDbContext db, MigrationBudget budget, CancellationToken ct = default)
    {
        var result = await MigrationAttemptCoordinator.RunAsync(db, budget, new Journal(), ct);
        if (result.Cause is not null) ExceptionDispatchInfo.Capture(result.Cause).Throw();
        if (result.Outcome != MigrationEvidenceOutcome.Committed)
            throw new InvalidOperationException("test_migration_outcome_" + result.Outcome);
    }

    private sealed class Journal : IMigrationEvidenceJournal
    {
        public Task<IReadOnlyList<Guid>> ReadCommittedAttemptsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<Guid>>([]);
        public Task StartCheckedAsync(MigrationAttemptStart start, IReadOnlyList<Guid> checkedCommittedAttempts, CancellationToken ct) => StartAsync(start, ct);
        public Task StartAsync(MigrationAttemptStart start, CancellationToken ct) => Task.CompletedTask;
        public Task AppendAsync(Guid attemptId, MigrationEvidenceRecord record, CancellationToken ct) => Task.CompletedTask;
        public Task<MigrationEvidenceRead> ReadAsync(Guid attemptId, CancellationToken ct) =>
            Task.FromResult(new MigrationEvidenceRead(MigrationEvidenceReadState.Missing, null, []));
    }
}
