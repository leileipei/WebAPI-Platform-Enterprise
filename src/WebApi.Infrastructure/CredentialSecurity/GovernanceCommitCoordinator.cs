using WebApi.Domain.CredentialSecurity;

namespace WebApi.Infrastructure.CredentialSecurity;

// Implementations must provide fresh, signed evidence from an independent
// durable authority. A transport error is an unknown outcome, even for writes.
public interface IGovernanceJournalWitness
{
    Task<GovernanceWitnessState> ObserveAsync(GovernanceJournalId journal, Guid? operationId, CancellationToken ct);
    Task<GovernanceWitnessState> PrepareAsync(GovernanceJournalId journal, GovernanceWriteIntent intent, CancellationToken ct);
    Task<GovernanceWitnessState> ConfirmAsync(GovernanceJournalId journal, GovernanceWriteIntent intent, CancellationToken ct);
}

public sealed record GovernanceLocalJournalState(GovernanceJournalPosition Position, GovernanceWriteIntent? AwaitingConfirmation);

public interface IGovernanceCommitStore
{
    // Own the real database writer fence across transaction commits. The fence
    // also protects authorization, affected ledger scopes and lease issuance.
    Task<IGovernanceCommitSession> OpenLockedAsync(GovernanceJournalId journal, CancellationToken ct);
}

public interface IGovernanceCommitSession : IAsyncDisposable
{
    // Read durable, verified facts under the fence, including the full affected
    // ledger state. A mutable position or phase row alone is insufficient.
    Task<GovernanceLocalJournalState> ReadAsync(CancellationToken ct);
    Task AuthorizeAsync(GovernanceWriteIntent intent, CancellationToken ct);
    Task AuthorizeRecoveryAsync(CancellationToken ct);
    // Recheck current revisions, scope, permission and lease constraints
    // before externalizing a new, irreversible preparation.
    Task ValidateNewMutationAsync(GovernanceWriteIntent intent, CancellationToken ct);
    Task<bool> VerifyCommittedMutationAsync(GovernanceWriteIntent intent, CancellationToken ct);
    // Revalidate the prepared plan against the fenced database. If an authority,
    // lease or ledger precondition changed after preparation, block recovery.
    // Persist the complete mutation, new position and marker in one transaction.
    Task CommitPreparedAsync(GovernanceWriteIntent intent, CancellationToken ct);
    Task MarkConfirmedAsync(Guid operationId, GovernanceJournalPosition position, CancellationToken ct);
}

public enum GovernanceCommitResult { Committed, Replayed, Conflict }

public sealed class GovernanceCommitCoordinator
{
    private readonly GovernanceJournalId journal;
    private readonly IGovernanceCommitStore store;
    private readonly IGovernanceJournalWitness witness;

    public GovernanceCommitCoordinator(GovernanceJournalId journal, IGovernanceCommitStore store, IGovernanceJournalWitness witness)
    {
        GovernanceJournalProtocol.RequireJournal(journal);
        this.journal = journal;
        this.store = store;
        this.witness = witness;
    }

    public async Task<GovernanceCommitResult> ExecuteAsync(GovernanceWriteIntent intent, CancellationToken ct = default)
    {
        var next = GovernanceJournalProtocol.NextPosition(journal, intent);
        await using var session = await store.OpenLockedAsync(journal, ct);
        await session.AuthorizeAsync(intent, ct);
        var local = await ReadLocalAsync(session, ct);
        var observed = await witness.ObserveAsync(journal, intent.OperationId, ct);

        if (observed.OperationConfirmation is { } confirmation)
        {
            if (confirmation != new GovernanceOperationConfirmation(intent.OperationId, next)) return GovernanceCommitResult.Conflict;
            return await ReplayConfirmedAsync(session, local, observed, intent, ct);
        }

        if (observed.Pending is { } pending)
        {
            if (pending != intent) return GovernanceCommitResult.Conflict;
            await FinishPreparedAsync(session, local, observed, next, ct);
            return GovernanceCommitResult.Replayed;
        }

        Require(local.Position == observed.Confirmed);
        Require(local.AwaitingConfirmation is null);
        if (local.Position != intent.Previous) return GovernanceCommitResult.Conflict;
        await session.ValidateNewMutationAsync(intent, ct);
        var prepared = await witness.PrepareAsync(journal, intent, ct);
        Require(prepared.Pending == intent);
        await FinishPreparedAsync(session, local, prepared, next, ct);
        return GovernanceCommitResult.Committed;
    }

    public async Task RecoverAsync(CancellationToken ct = default)
    {
        await using var session = await store.OpenLockedAsync(journal, ct);
        await session.AuthorizeRecoveryAsync(ct);
        var local = await ReadLocalAsync(session, ct);
        var observed = await witness.ObserveAsync(journal, local.AwaitingConfirmation?.OperationId, ct);
        if (observed.Pending is { } pending)
        {
            await FinishPreparedAsync(session, local, observed, GovernanceJournalProtocol.NextPosition(journal, pending), ct);
            return;
        }

        // A confirmed witness ahead of restored PostgreSQL cannot be rebuilt
        // from older local facts. Recovery requires separate verified material.
        Require(local.Position == observed.Confirmed);
        if (local.AwaitingConfirmation is { } intent)
        {
            Require(await session.VerifyCommittedMutationAsync(intent, ct));
            await FinishMarkerAsync(session, local, observed, ct);
        }
        else Require(await ReadLocalAsync(session, ct) == local);
    }

    private async Task<GovernanceCommitResult> ReplayConfirmedAsync(IGovernanceCommitSession session, GovernanceLocalJournalState local,
        GovernanceWitnessState observed, GovernanceWriteIntent intent, CancellationToken ct)
    {
        if (observed.Pending is { } pending)
        {
            Require(pending.Previous == observed.Confirmed && pending.OperationId != intent.OperationId);
            if (local.AwaitingConfirmation is not null)
                Require(local.AwaitingConfirmation == pending && await session.VerifyCommittedMutationAsync(pending, ct));
            else Require(local.Position == observed.Confirmed);
        }
        else Require(local.Position == observed.Confirmed);

        Require(await session.VerifyCommittedMutationAsync(intent, ct));
        if (observed.Pending is null && local.AwaitingConfirmation == intent)
            await FinishMarkerAsync(session, local, observed, ct);
        return GovernanceCommitResult.Replayed;
    }

    private async Task FinishPreparedAsync(IGovernanceCommitSession session, GovernanceLocalJournalState local,
        GovernanceWitnessState prepared, GovernanceJournalPosition next, CancellationToken ct)
    {
        var intent = prepared.Pending ?? throw Unavailable();
        Require(prepared.Confirmed == intent.Previous && prepared.OperationConfirmation is null);
        var committed = new GovernanceLocalJournalState(next, intent);
        if (local == new GovernanceLocalJournalState(intent.Previous, null))
        {
            await session.CommitPreparedAsync(intent, ct);
            local = await ReadLocalAsync(session, ct);
        }

        Require(local == committed && await session.VerifyCommittedMutationAsync(intent, ct));
        var confirmed = await witness.ConfirmAsync(journal, intent, ct);
        Require(confirmed.Pending is null && confirmed.Confirmed == next);
        await FinishMarkerAsync(session, local, confirmed, ct);
    }

    private async Task FinishMarkerAsync(IGovernanceCommitSession session, GovernanceLocalJournalState local,
        GovernanceWitnessState confirmed, CancellationToken ct)
    {
        var operationId = local.AwaitingConfirmation?.OperationId ?? throw Unavailable();
        Require(confirmed.OperationConfirmation == new GovernanceOperationConfirmation(operationId, local.Position));
        await session.MarkConfirmedAsync(operationId, local.Position, ct);
        Require(await ReadLocalAsync(session, ct) == new GovernanceLocalJournalState(local.Position, null));
    }

    private async Task<GovernanceLocalJournalState> ReadLocalAsync(IGovernanceCommitSession session, CancellationToken ct)
    {
        var state = await session.ReadAsync(ct);
        Require(GovernanceJournalProtocol.ValidPosition(state.Position) && (state.AwaitingConfirmation is not { } pending ||
            GovernanceJournalProtocol.NextPosition(journal, pending) == state.Position));
        return state;
    }

    private static void Require(bool condition)
    {
        if (!condition) throw Unavailable();
    }

    private static InvalidDataException Unavailable() => new("credential_governance_journal_inconsistent");
}
