using System.Buffers.Text;
using System.Text;
using WebApi.Domain.CredentialSecurity;
using WebApi.Infrastructure.CredentialSecurity;
using Xunit;

namespace WebApi.Domain.Tests;

public sealed class GovernanceCommitCoordinatorTests
{
    private static readonly GovernanceJournalId Journal = new(Guid.NewGuid(), Guid.NewGuid());

    [Theory]
    [InlineData("prepare-lost")]
    [InlineData("before-commit")]
    [InlineData("commit-lost")]
    [InlineData("before-confirm")]
    [InlineData("confirm-lost")]
    [InlineData("before-mark")]
    [InlineData("mark-lost")]
    public async Task RecoveryAdvancesExactlyOneOriginalIntentAcrossEveryUncertainBoundary(string fault)
    {
        var fixture = new Fixture { Fault = fault };
        var intent = Intent(fixture.Local.Position);
        await Assert.ThrowsAsync<IOException>(() => fixture.Coordinator.ExecuteAsync(intent));

        fixture.Fault = null;
        var restarted = new GovernanceCommitCoordinator(Journal, fixture, fixture);
        await restarted.RecoverAsync();
        Assert.Equal(GovernanceCommitResult.Replayed, await restarted.ExecuteAsync(intent));
        Assert.Equal(["prepare", "commit", "confirm", "mark"], fixture.Writes);
        Assert.Equal(new(GovernanceJournalProtocol.NextPosition(Journal, intent), null), fixture.Local);
        Assert.Equal(fixture.Local.Position, fixture.Confirmed);
        Assert.Null(fixture.Pending);
    }

    [Theory]
    [InlineData("witness-behind")]
    [InlineData("database-behind")]
    [InlineData("same-sequence-different-head")]
    [InlineData("different-confirmation")]
    [InlineData("unavailable")]
    public async Task InconsistentOrUnavailableHistoryIsNeverReinitializedOrMarkedComplete(string fault)
    {
        var fixture = new Fixture();
        var genesis = fixture.Local.Position;
        var first = Intent(genesis);
        Assert.Equal(GovernanceCommitResult.Committed, await fixture.Coordinator.ExecuteAsync(first));
        fixture.Writes.Clear();
        switch (fault)
        {
            case "witness-behind": fixture.Confirmed = genesis; break;
            case "database-behind": fixture.Local = new(genesis, null); break;
            case "same-sequence-different-head": fixture.Confirmed = fixture.Confirmed with { Hash = new string('b', 64) }; break;
            case "different-confirmation": fixture.Local = fixture.Local with { AwaitingConfirmation = first with { OperationId = Guid.NewGuid() } }; break;
            case "unavailable": fixture.Fault = "observe"; break;
        }
        if (fault == "unavailable") await Assert.ThrowsAsync<IOException>(() => fixture.Coordinator.RecoverAsync());
        else await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Coordinator.RecoverAsync());
        Assert.Empty(fixture.Writes);
    }

    [Fact]
    public async Task ConcurrentCommandsDoNotBothUseTheSamePreviousPosition()
    {
        var fixture = new Fixture { BeforePrepare = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var first = Intent(fixture.Local.Position);
        var second = Intent(fixture.Local.Position);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var firstCall = fixture.Coordinator.ExecuteAsync(first, deadline.Token);
        await fixture.PrepareStarted.Task.WaitAsync(deadline.Token);
        var secondCall = fixture.Coordinator.ExecuteAsync(second, deadline.Token);
        Assert.False(secondCall.IsCompleted);
        fixture.BeforePrepare.SetResult();
        Assert.Equal(GovernanceCommitResult.Committed, await firstCall);
        Assert.Equal(GovernanceCommitResult.Conflict, await secondCall);
        Assert.Equal(["prepare", "commit", "confirm", "mark"], fixture.Writes);
    }

    [Fact]
    public async Task ReplayRequiresCurrentAuthorizationAndExactDurableIdentityEvenAfterAnotherCommit()
    {
        var fixture = new Fixture();
        var first = Intent(fixture.Local.Position);
        await fixture.Coordinator.ExecuteAsync(first);
        await fixture.Coordinator.ExecuteAsync(Intent(fixture.Local.Position));
        fixture.Writes.Clear();
        Assert.Equal(GovernanceCommitResult.Replayed, await fixture.Coordinator.ExecuteAsync(first));
        Assert.Equal(GovernanceCommitResult.Conflict,
            await fixture.Coordinator.ExecuteAsync(first with { RecoveryMaterial = Material("different command") }));
        fixture.Authorized = false;
        var reads = fixture.WitnessReads;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Coordinator.ExecuteAsync(first));
        Assert.Equal(reads, fixture.WitnessReads);
        Assert.Empty(fixture.Writes);
    }

    [Fact]
    public async Task PendingIntentCannotBeReplacedAndIncompleteLocalWritesAreNotConfirmed()
    {
        var fixture = new Fixture { Fault = "prepare-lost" };
        var first = Intent(fixture.Local.Position);
        await Assert.ThrowsAsync<IOException>(() => fixture.Coordinator.ExecuteAsync(first));
        fixture.Fault = null;
        Assert.Equal(GovernanceCommitResult.Conflict,
            await fixture.Coordinator.ExecuteAsync(first with { RecoveryMaterial = Material("different command") }));
        Assert.Equal(GovernanceCommitResult.Conflict, await fixture.Coordinator.ExecuteAsync(Intent(fixture.Local.Position)));
        fixture.CommitWritesMarker = false;
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Coordinator.RecoverAsync());
        Assert.Equal(["prepare", "commit"], fixture.Writes);
        Assert.Equal(first, fixture.Pending);
        Assert.Equal(first.Previous, fixture.Confirmed);
    }

    [Fact]
    public async Task RecoveryAfterExternalConfirmationStillRequiresTheExactCommittedMutation()
    {
        var fixture = new Fixture { Fault = "confirm-lost" };
        var intent = Intent(fixture.Local.Position);
        await Assert.ThrowsAsync<IOException>(() => fixture.Coordinator.ExecuteAsync(intent));
        fixture.Fault = null;
        fixture.LoseMutation(intent);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Coordinator.RecoverAsync());
        Assert.Equal(intent, fixture.Local.AwaitingConfirmation);
        Assert.Equal(["prepare", "commit", "confirm"], fixture.Writes);
    }

    [Fact]
    public async Task CleanRecoveryRechecksTheLocalFenceAfterObservingIndependentHistory()
    {
        var fixture = new Fixture { Fault = "fence-lost-during-observe" };
        await Assert.ThrowsAsync<IOException>(() => fixture.Coordinator.RecoverAsync());
        Assert.Empty(fixture.Writes);
    }

    [Theory]
    [InlineData("prepare-lost")]
    [InlineData("commit-lost")]
    public async Task ConfirmedReplayDoesNotChangeAnotherOperationInRecovery(string fault)
    {
        var fixture = new Fixture();
        var first = Intent(fixture.Local.Position);
        Assert.Equal(GovernanceCommitResult.Committed, await fixture.Coordinator.ExecuteAsync(first));
        fixture.Fault = fault;
        var second = Intent(fixture.Local.Position);
        await Assert.ThrowsAsync<IOException>(() => fixture.Coordinator.ExecuteAsync(second));
        fixture.Fault = null;
        var position = fixture.Local;
        fixture.Writes.Clear();
        Assert.Equal(GovernanceCommitResult.Replayed, await fixture.Coordinator.ExecuteAsync(first));
        Assert.Equal(position, fixture.Local);
        Assert.Equal(second, fixture.Pending);
        Assert.Empty(fixture.Writes);
    }

    private static GovernanceWriteIntent Intent(GovernanceJournalPosition previous) => new(Guid.NewGuid(), previous, Material("command"));
    private static string Material(string value) => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(value));

    // Both ports are synthetic. The gate and copies model process restarts,
    // observable commit state and atomic transitions at each adapter boundary.
    private sealed class Fixture : IGovernanceJournalWitness, IGovernanceCommitStore, IGovernanceCommitSession
    {
        private readonly SemaphoreSlim gate = new(1);
        private readonly Dictionary<Guid, GovernanceWriteIntent> committed = [];
        private readonly Dictionary<Guid, GovernanceJournalPosition> confirmedOperations = [];
        public GovernanceJournalPosition Confirmed = GovernanceJournalProtocol.Genesis;
        public GovernanceLocalJournalState Local = new(GovernanceJournalProtocol.Genesis, null);
        public GovernanceWriteIntent? Pending;
        public string? Fault;
        public bool Authorized = true;
        public bool CommitWritesMarker = true;
        public int WitnessReads;
        public List<string> Writes { get; } = [];
        public TaskCompletionSource? BeforePrepare { get; init; }
        public TaskCompletionSource PrepareStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public GovernanceCommitCoordinator Coordinator => new(Journal, this, this);

        public void LoseMutation(GovernanceWriteIntent intent) => committed.Remove(intent.OperationId);

        public async Task<IGovernanceCommitSession> OpenLockedAsync(GovernanceJournalId journal, CancellationToken ct)
        {
            Assert.Equal(Journal, journal);
            await gate.WaitAsync(ct);
            return this;
        }

        public ValueTask DisposeAsync()
        {
            gate.Release();
            return ValueTask.CompletedTask;
        }

        public Task<GovernanceLocalJournalState> ReadAsync(CancellationToken ct)
        {
            Fail("fence-lost");
            return Task.FromResult(Local);
        }

        public Task AuthorizeAsync(GovernanceWriteIntent intent, CancellationToken ct) =>
            Authorized ? Task.CompletedTask : Task.FromException(new UnauthorizedAccessException());

        public Task AuthorizeRecoveryAsync(CancellationToken ct) =>
            Authorized ? Task.CompletedTask : Task.FromException(new UnauthorizedAccessException());

        public Task ValidateNewMutationAsync(GovernanceWriteIntent intent, CancellationToken ct)
        {
            Assert.Equal(Local.Position, intent.Previous);
            Assert.Null(Local.AwaitingConfirmation);
            return Task.CompletedTask;
        }

        public Task<bool> VerifyCommittedMutationAsync(GovernanceWriteIntent intent, CancellationToken ct) =>
            Task.FromResult(committed.GetValueOrDefault(intent.OperationId) == intent);

        public Task CommitPreparedAsync(GovernanceWriteIntent intent, CancellationToken ct)
        {
            Fail("before-commit");
            Assert.Equal(Pending, intent);
            Assert.Equal(Local.Position, intent.Previous);
            committed.Add(intent.OperationId, intent);
            Local = new(GovernanceJournalProtocol.NextPosition(Journal, intent), CommitWritesMarker ? intent : null);
            Writes.Add("commit");
            Fail("commit-lost");
            return Task.CompletedTask;
        }

        public Task MarkConfirmedAsync(Guid operationId, GovernanceJournalPosition position, CancellationToken ct)
        {
            Fail("before-mark");
            Assert.Equal(new(position, committed[operationId]), Local);
            Assert.Equal(position, confirmedOperations[operationId]);
            Local = new(position, null);
            Writes.Add("mark");
            Fail("mark-lost");
            return Task.CompletedTask;
        }

        public Task<GovernanceWitnessState> ObserveAsync(GovernanceJournalId journal, Guid? operationId, CancellationToken ct)
        {
            Assert.Equal(Journal, journal);
            WitnessReads++;
            Fail("observe");
            if (Fault == "fence-lost-during-observe") Fault = "fence-lost";
            var confirmation = operationId is Guid id && confirmedOperations.TryGetValue(id, out var position)
                ? new GovernanceOperationConfirmation(id, position) : null;
            return Task.FromResult(new GovernanceWitnessState(Confirmed, Pending, confirmation));
        }

        public async Task<GovernanceWitnessState> PrepareAsync(GovernanceJournalId journal, GovernanceWriteIntent intent, CancellationToken ct)
        {
            PrepareStarted.TrySetResult();
            if (BeforePrepare is { } signal) await signal.Task.WaitAsync(ct);
            Assert.Equal(Confirmed, intent.Previous);
            Assert.Null(Pending);
            Pending = intent;
            Writes.Add("prepare");
            Fail("prepare-lost");
            return await ObserveAsync(journal, intent.OperationId, ct);
        }

        public Task<GovernanceWitnessState> ConfirmAsync(GovernanceJournalId journal, GovernanceWriteIntent intent, CancellationToken ct)
        {
            Fail("before-confirm");
            Assert.Equal(Pending, intent);
            Confirmed = GovernanceJournalProtocol.NextPosition(journal, intent);
            confirmedOperations.Add(intent.OperationId, Confirmed);
            Pending = null;
            Writes.Add("confirm");
            Fail("confirm-lost");
            return ObserveAsync(journal, intent.OperationId, ct);
        }

        private void Fail(string stage)
        {
            if (Fault == stage) throw new IOException("Injected loss of the adapter response.");
        }
    }
}
