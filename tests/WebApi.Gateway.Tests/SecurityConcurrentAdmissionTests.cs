using WebApi.Gateway.Tests.Support;
using WebApi.Contracts.Security;
using WebApi.Gateway.Security;
using WebApi.Gateway.Storage;
using Xunit;

namespace WebApi.Gateway.Tests;

public sealed class SecurityConcurrentAdmissionTests
{
    [Fact]
    public async Task ConcurrentAdmissionsFinishWithoutBlockingAvailableWorkers()
    {
        var gate = new SecurityAdmissionGate();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = 0;
        var scheduler = new ConcurrentExclusiveSchedulerPair(TaskScheduler.Default, maxConcurrencyLevel: 2);
        var readers = Enumerable.Range(0, 128).Select(_ => Task.Factory.StartNew(async () =>
        {
            await start.Task;
            for (var i = 0; i < 200; i++)
            {
                using var ticket = await gate.EnterReadAsync(timeout.Token);
                await Task.Yield();
                Interlocked.Increment(ref completed);
            }
        }, CancellationToken.None, TaskCreationOptions.None, scheduler.ConcurrentScheduler).Unwrap()).ToArray();
        start.SetResult();
        await Task.WhenAll(readers).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(25_600, completed);
        using var writer = await gate.EnterWriteAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task UnchangedSecurityStateAllowsConcurrentAuthenticatedAdmissions()
    {
        using var fixture = new SecurityFixture();
        var pair = fixture.Pair(0, []);
        Assert.True((await fixture.State.ApplyAsync(pair.Checkpoint, pair.Lease)).Applied);
        using var first = await fixture.State.EnterAdmissionAsync();
        var second = fixture.State.EnterAdmissionAsync();
        var concurrent = await Task.WhenAny(second, Task.Delay(200)) == second;
        // Release before asserting on RED so the fixture and pending ticket always settle.
        if (!concurrent) first.Dispose();
        using var other = await second.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(concurrent, "Independent authentication reads were serialized despite unchanged security authority.");
    }

    [Fact]
    public async Task RevocationWaitsForAllReadersAndStopsLaterAdmissions()
    {
        using var fixture = new SecurityFixture();
        var pair = fixture.Pair(0, []);
        Assert.True((await fixture.State.ApplyAsync(pair.Checkpoint, pair.Lease)).Applied);
        using var first = await fixture.State.EnterAdmissionAsync();
        using var second = await fixture.State.EnterAdmissionAsync().WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Clock.Advance(1);
        var revoked = Guid.NewGuid();
        var next = fixture.Pair(1, [revoked]);
        var publishing = fixture.State.ApplyAsync(next.Checkpoint, next.Lease);
        var later = fixture.State.EnterAdmissionAsync();
        Assert.False(publishing.IsCompleted);
        Assert.False(later.IsCompleted);
        first.Dispose();
        Assert.False(publishing.IsCompleted);
        second.Dispose();
        Assert.True((await publishing.WaitAsync(TimeSpan.FromSeconds(5))).Applied);
        using var resumed = await later.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(SecurityAdmissionDecision.KnownRevoked, fixture.State.CanAdmit(revoked, fixture.Clock.GetUtcNow()));
    }

    [Fact]
    public async Task CancellingQueuedWriterDoesNotBlockReadersOrConsumeAuthority()
    {
        using var fixture = new SecurityFixture();
        var pair = fixture.Pair(0, []);
        Assert.True((await fixture.State.ApplyAsync(pair.Checkpoint, pair.Lease)).Applied);
        using var first = await fixture.State.EnterAdmissionAsync();
        using var cancel = new CancellationTokenSource();
        fixture.Clock.Advance(1);
        var revoked = Guid.NewGuid();
        var next = fixture.Pair(1, [revoked]);
        var publishing = fixture.State.ApplyAsync(next.Checkpoint, next.Lease, cancel.Token);
        var later = fixture.State.EnterAdmissionAsync();
        Assert.False(later.IsCompleted);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => publishing);
        using var resumed = await later.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, fixture.State.CurrentEpoch);
        Assert.Equal(SecurityAdmissionDecision.Allow, fixture.State.CanAdmit(revoked, fixture.Clock.GetUtcNow()));
    }

    [Fact]
    public async Task CancellingReaderBehindPublicationDoesNotRetainAnAdmission()
    {
        using var fixture = new SecurityFixture();
        var disk = new ControlledDisk(fixture.Disk);
        await using var state = new SecurityStateStore(new(fixture.Env, () => fixture.Node, fixture.Instance), disk, fixture.Clock);
        var first = fixture.Pair(0, []);
        Assert.True((await state.ApplyAsync(first.Checkpoint, first.Lease)).Applied);
        fixture.Clock.Advance(1);
        var revoked = Guid.NewGuid();
        var next = fixture.Pair(1, [revoked]);
        disk.Delay = true;
        var publishing = state.ApplyAsync(next.Checkpoint, next.Lease);
        await disk.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancel = new CancellationTokenSource();
        var reader = state.EnterAdmissionAsync(cancel.Token);
        Assert.False(reader.IsCompleted);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader);
        disk.Release.TrySetResult();
        Assert.True((await publishing.WaitAsync(TimeSpan.FromSeconds(5))).Applied);
        using var resumed = await state.EnterAdmissionAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(SecurityAdmissionDecision.KnownRevoked, state.CanAdmit(revoked, fixture.Clock.GetUtcNow()));
    }

    [Fact]
    public async Task RepeatedReaderDisposalCannotPublishPastAnotherReader()
    {
        using var fixture = new SecurityFixture();
        var pair = fixture.Pair(0, []);
        Assert.True((await fixture.State.ApplyAsync(pair.Checkpoint, pair.Lease)).Applied);
        using var first = await fixture.State.EnterAdmissionAsync();
        using var second = await fixture.State.EnterAdmissionAsync().WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Clock.Advance(1);
        var next = fixture.Pair(1, [Guid.NewGuid()]);
        var publishing = fixture.State.ApplyAsync(next.Checkpoint, next.Lease);
        first.Dispose();
        first.Dispose();
        Assert.False(publishing.IsCompleted);
        second.Dispose();
        Assert.True((await publishing.WaitAsync(TimeSpan.FromSeconds(5))).Applied);
    }

    [Fact]
    public async Task HealthyRenewalPublicationWaitsForEveryConcurrentReader()
    {
        using var fixture = new SecurityFixture();
        var disk = new ControlledDisk(fixture.Disk);
        await using var state = new SecurityStateStore(new(fixture.Env, () => fixture.Node, fixture.Instance), disk, fixture.Clock);
        var first = fixture.Pair(0, []);
        Assert.True((await state.ApplyAsync(first.Checkpoint, first.Lease)).Applied);
        fixture.Clock.Advance(1);
        var renewal = fixture.Pair(0, []);
        disk.Delay = true;
        var publishing = state.ApplyAsync(renewal.Checkpoint, renewal.Lease);
        await disk.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var readerA = await state.EnterAdmissionAsync();
        using var readerB = await state.EnterAdmissionAsync().WaitAsync(TimeSpan.FromSeconds(5));
        disk.Release.TrySetResult();
        await disk.Persisted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(first.Lease.Payload.LeaseId, state.CurrentAdmissionLeaseId);
        readerA.Dispose();
        Assert.False(publishing.IsCompleted);
        readerB.Dispose();
        Assert.True((await publishing.WaitAsync(TimeSpan.FromSeconds(5))).Applied);
        Assert.Equal(renewal.Lease.Payload.LeaseId, state.CurrentAdmissionLeaseId);
    }

    [Fact]
    public async Task MandatoryFailureCloseSurvivesCancellationAndConcurrentReaders()
    {
        using var fixture = new SecurityFixture();
        var disk = new ControlledDisk(fixture.Disk);
        await using var state = new SecurityStateStore(new(fixture.Env, () => fixture.Node, fixture.Instance), disk, fixture.Clock);
        var revoked = Guid.NewGuid();
        var first = fixture.Pair(1, [revoked]);
        Assert.True((await state.ApplyAsync(first.Checkpoint, first.Lease)).Applied);
        fixture.Clock.Advance(1);
        var renewal = fixture.Pair(1, [revoked]);
        disk.Delay = disk.Fail = true;
        using var cancel = new CancellationTokenSource();
        var publishing = state.ApplyAsync(renewal.Checkpoint, renewal.Lease, cancel.Token);
        await disk.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var readerA = await state.EnterAdmissionAsync();
        using var readerB = await state.EnterAdmissionAsync().WaitAsync(TimeSpan.FromSeconds(5));
        disk.Release.TrySetResult();
        await disk.Failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancel.Cancel();
        readerA.Dispose();
        Assert.False(publishing.IsCompleted);
        readerB.Dispose();
        var result = await publishing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(result.Applied);
        Assert.Equal("security_lkg_write_denied", result.ErrorCode);
        using var resumed = await state.EnterAdmissionAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(SecurityAdmissionDecision.Unavailable, state.CanAdmit(Guid.NewGuid(), fixture.Clock.GetUtcNow()));
        Assert.Equal(SecurityAdmissionDecision.KnownRevoked, state.CanAdmit(revoked, fixture.Clock.GetUtcNow()));
    }

    private sealed class ControlledDisk(ISecurityLkgStore actual) : ISecurityLkgStore
    {
        public bool Delay, Fail;
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Persisted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Failed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ISecurityLkgOwner AcquireOwnership() => new Owner(this, actual.AcquireOwnership());
        private sealed class Owner(ControlledDisk parent, ISecurityLkgOwner actual) : ISecurityLkgOwner
        {
            public Task<SecurityNodeMaterial?> ReadAsync(CancellationToken ct = default) => actual.ReadAsync(ct);
            public async Task WriteAtomicAsync(SecurityNodeMaterial material, CancellationToken ct = default)
            {
                if (parent.Delay) { parent.Entered.TrySetResult(); await parent.Release.Task.WaitAsync(ct); }
                if (parent.Fail) { parent.Failed.TrySetResult(); throw new IOException("injected renewal persistence failure"); }
                await actual.WriteAtomicAsync(material, ct);
                if (parent.Delay) parent.Persisted.TrySetResult();
            }
            public Task CompleteCleanlyAsync(CancellationToken ct = default) => actual.CompleteCleanlyAsync(ct);
            public ValueTask DisposeAsync() => actual.DisposeAsync();
        }
    }
}
