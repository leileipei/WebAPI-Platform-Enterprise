using System.Security.Cryptography;
using System.Reflection;
using System.Text;
using WebApi.Contracts.Common;
using WebApi.Domain.CredentialSecurity;
using Xunit;

namespace WebApi.Domain.Tests;

[CollectionDefinition("IndependentEvidenceAdapterIsolation", DisableParallelization = true)]
public sealed class IndependentEvidenceAdapterIsolation { }

[Collection("IndependentEvidenceAdapterIsolation")]
public sealed class IndependentSecurityEvidenceTests
{
    [Fact]
    public void IndependentlyPinnedProofsBindOneChallengeAndExposeOnlyInspectionFacts()
    {
        using var fixture = new Fixture();
        var (time, history) = fixture.Signed();
        fixture.Clock.Move(500);
        var inspected = fixture.Session.Inspect(time, history, fixture.Trust, fixture.Limits);
        Assert.Equal(7, inspected.HistorySequence);
        Assert.Equal(fixture.Lower, inspected.LowerAtSource);
        Assert.Equal(fixture.Upper, inspected.UpperAtSource);
        Assert.Equal(fixture.Upper.AddMilliseconds(500), inspected.ConservativeUpperAtInspection);
        Assert.Equal(TimeSpan.FromMilliseconds(500), inspected.ProcessingElapsed);
    }

    [Theory]
    [InlineData("missing-time")]
    [InlineData("missing-history")]
    [InlineData("wrong-time-source")]
    [InlineData("wrong-time-key")]
    [InlineData("bad-time-signature")]
    [InlineData("wrong-history-source")]
    [InlineData("wrong-history-key")]
    [InlineData("bad-history-signature")]
    [InlineData("unknown-time-key")]
    [InlineData("unknown-history-key")]
    [InlineData("different-environment")]
    [InlineData("different-node")]
    [InlineData("different-instance")]
    [InlineData("different-purpose")]
    [InlineData("different-generation")]
    [InlineData("different-epoch")]
    [InlineData("different-head")]
    [InlineData("different-nonce")]
    [InlineData("unknown-history")]
    [InlineData("zero-sequence")]
    [InlineData("reversed-time")]
    [InlineData("excessive-time-width")]
    [InlineData("expired-at-source")]
    [InlineData("expired-during-processing")]
    [InlineData("excessive-validity")]
    [InlineData("non-utc-time")]
    [InlineData("processing-timeout")]
    [InlineData("clock-regression")]
    [InlineData("clock-frequency-change")]
    public void MissingForgedMismatchedOrUnboundedEvidenceIsRejected(string scenario)
    {
        using var fixture = new Fixture();
        var time = fixture.Time;
        var history = fixture.History;
        var trust = fixture.Trust;
        var binding = fixture.Session.Challenge;
        switch (scenario)
        {
            case "wrong-time-source": time = time with { SourceId = "other" }; break;
            case "wrong-time-key": time = time with { KeyId = "other" }; break;
            case "wrong-history-source": history = history with { SourceId = "other" }; break;
            case "wrong-history-key": history = history with { KeyId = "other" }; break;
            case "unknown-time-key": trust = trust with { TimePublicKey = fixture.HistoryPublicKey }; break;
            case "unknown-history-key": trust = trust with { HistoryPublicKey = fixture.TimePublicKey }; break;
            case "different-environment": history = history with { Challenge = binding with { EnvironmentId = Guid.NewGuid() } }; break;
            case "different-node": history = history with { Challenge = binding with { NodeId = Guid.NewGuid() } }; break;
            case "different-instance": history = history with { Challenge = binding with { InstanceId = Guid.NewGuid() } }; break;
            case "different-purpose": history = history with { Challenge = binding with { Purpose = "gateway-admission" } }; break;
            case "different-generation": history = history with { Challenge = binding with { SecurityGeneration = Guid.NewGuid() } }; break;
            case "different-epoch": history = history with { Challenge = binding with { Epoch = 1 } }; break;
            case "different-head": history = history with { Challenge = binding with { HeadHash = new string('b', 64) } }; break;
            case "different-nonce": time = time with { Challenge = binding with { Nonce = new string('c', 64) } }; break;
            case "unknown-history": history = history with { State = "Unknown" }; break;
            case "zero-sequence": history = history with { Sequence = 0 }; break;
            case "reversed-time": time = time with { Lower = fixture.Upper, Upper = fixture.Lower }; break;
            case "excessive-time-width": time = time with { Lower = fixture.Lower.AddSeconds(-10) }; break;
            case "expired-at-source": time = time with { ValidUntil = fixture.Upper }; break;
            case "expired-during-processing": time = time with { ValidUntil = fixture.Upper.AddMilliseconds(150) }; fixture.Clock.Move(200); break;
            case "excessive-validity": time = time with { ValidUntil = fixture.Upper.AddMinutes(2) }; break;
            case "non-utc-time": time = time with { Lower = fixture.Lower.ToOffset(TimeSpan.FromHours(1)) }; break;
            case "processing-timeout": fixture.Clock.Move(2500); break;
            case "clock-regression": fixture.Clock.Move(-1); break;
            case "clock-frequency-change": fixture.Clock.Frequency = 2000; break;
        }
        var timeBytes = scenario == "missing-time" ? null : fixture.SignTime(time, scenario == "bad-time-signature");
        var historyBytes = scenario == "missing-history" ? null : fixture.SignHistory(history, scenario == "bad-history-signature");
        Assert.Throws<InvalidDataException>(() => fixture.Session.Inspect(timeBytes, historyBytes, trust, fixture.Limits));
    }

    [Fact]
    public void ChallengeIsOneUseEvenAfterFailureAndCannotBeReplayedAcrossSessions()
    {
        using var first = new Fixture();
        var (time, history) = first.Signed();
        first.Session.Inspect(time, history, first.Trust, first.Limits);
        Assert.Throws<InvalidDataException>(() => first.Session.Inspect(time, history, first.Trust, first.Limits));
        var next = IndependentEvidenceSession.Start(first.Scope, first.Clock);
        Assert.NotEqual(first.Session.Challenge.Nonce, next.Challenge.Nonce);
        Assert.Throws<InvalidDataException>(() => next.Inspect(time, history, first.Trust, first.Limits));
        Assert.Throws<InvalidDataException>(() => next.Inspect(time, history, first.Trust, first.Limits));
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("tampered")]
    [InlineData("numeric-string")]
    [InlineData("signature-whitespace")]
    public void AmbiguousOrTamperedWireIsRejected(string scenario)
    {
        using var fixture = new Fixture();
        var (time, history) = fixture.Signed();
        var json = Encoding.UTF8.GetString(time);
        if (scenario == "numeric-string")
        {
            var historyJson = Encoding.UTF8.GetString(history);
            Assert.Contains("\"sequence\":7", historyJson);
            history = Encoding.UTF8.GetBytes(historyJson.Replace("\"sequence\":7", "\"sequence\":\"7\"", StringComparison.Ordinal));
        }
        json = scenario switch
        {
            "duplicate" => json.Replace("\"payload\":", "\"payload\":null,\"Payload\":", StringComparison.Ordinal),
            "unknown" => json.Insert(1, "\"unknown\":true,"),
            "signature-whitespace" => json.Replace("\"signature\":\"", "\"signature\":\" ", StringComparison.Ordinal),
            "numeric-string" => json,
            _ => json.Replace("time-source", "fake-source", StringComparison.Ordinal)
        };
        Assert.Throws<InvalidDataException>(() => fixture.Session.Inspect(Encoding.UTF8.GetBytes(json), history, fixture.Trust, fixture.Limits));
    }

    [Fact]
    public async Task OptionalAdaptersAreBothRequiredAndReceiveTheSameFreshChallenge()
    {
        using var fixture = new Fixture();
        EvidenceChallenge? timeChallenge = null, historyChallenge = null;
        var time = new TimeSource((challenge, _) =>
        {
            timeChallenge = challenge;
            return Task.FromResult<byte[]?>(fixture.SignTime(fixture.Time with { Challenge = challenge }));
        });
        var history = new HistorySource((challenge, _) =>
        {
            historyChallenge = challenge;
            return Task.FromResult<byte[]?>(fixture.SignHistory(fixture.History with { Challenge = challenge }));
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => IndependentEvidenceInspector.InspectAsync(
            fixture.Scope, fixture.Clock, time, null, fixture.Trust, fixture.Limits));
        Assert.Null(timeChallenge);
        var inspection = await IndependentEvidenceInspector.InspectAsync(fixture.Scope, fixture.Clock, time, history, fixture.Trust, fixture.Limits);
        Assert.Equal(7, inspection.HistorySequence);
        Assert.NotNull(timeChallenge);
        Assert.Equal(timeChallenge, historyChallenge);
        Assert.NotEqual(fixture.Session.Challenge.Nonce, timeChallenge!.Nonce);
        await WaitForAdapterSlotsAsync();
    }

    [Fact]
    public async Task AdapterFailureOrTimeoutCannotProduceAnInspection()
    {
        using var fixture = new Fixture();
        var time = new TimeSource((challenge, _) => Task.FromResult<byte[]?>(fixture.SignTime(fixture.Time with { Challenge = challenge })));
        var failed = new HistorySource((_, _) => throw new IOException("test adapter failure"));
        await Assert.ThrowsAsync<InvalidDataException>(() => IndependentEvidenceInspector.InspectAsync(
            fixture.Scope, fixture.Clock, time, failed, fixture.Trust, fixture.Limits));
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hanging = new HistorySource(async (_, token) =>
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
            return null;
        });
        var limits = fixture.Limits with { MaximumProcessing = TimeSpan.FromMilliseconds(100) };
        await Assert.ThrowsAsync<InvalidDataException>(() => IndependentEvidenceInspector.InspectAsync(
            fixture.Scope, fixture.Clock, time, hanging, fixture.Trust, limits).WaitAsync(TimeSpan.FromSeconds(3)));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await WaitForAdapterSlotsAsync();
    }

    [Fact]
    public async Task SynchronouslyBlockingAdapterCannotHoldTheInspectionCallerPastItsBudget()
    {
        using var fixture = new Fixture();
        using var release = new ManualResetEventSlim(false);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocked = new TimeSource((_, _) =>
        {
            started.TrySetResult();
            release.Wait();
            finished.TrySetResult();
            return Task.FromResult<byte[]?>(null);
        });
        var history = new HistorySource((_, _) => Task.FromResult<byte[]?>(null));
        var limits = fixture.Limits with { MaximumProcessing = TimeSpan.FromMilliseconds(100) };
        try
        {
            var inspection = Task.Run(() => IndependentEvidenceInspector.InspectAsync(fixture.Scope, fixture.Clock, blocked, history, fixture.Trust, limits));
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await Assert.ThrowsAsync<InvalidDataException>(() => inspection.WaitAsync(TimeSpan.FromSeconds(2)));
        }
        finally { release.Set(); await finished.Task.WaitAsync(TimeSpan.FromSeconds(3)); await WaitForAdapterSlotsAsync(); }
    }

    [Fact]
    public async Task NonCooperativeAdaptersKeepBoundedSlotsAndFurtherInspectionsFailClosed()
    {
        await WaitForAdapterSlotsAsync();
        using var fixture = new Fixture();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        var hanging = new TimeSource(async (_, _) =>
        {
            if (Interlocked.Increment(ref count) == 4) started.TrySetResult();
            await release.Task;
            return null;
        });
        var history = new HistorySource((_, _) => Task.FromResult<byte[]?>(null));
        var limits = fixture.Limits with { MaximumProcessing = TimeSpan.FromMilliseconds(150) };
        try
        {
            var pending = Enumerable.Range(0, 4).Select(_ => IndependentEvidenceInspector.InspectAsync(
                fixture.Scope, fixture.Clock, hanging, history, fixture.Trust, limits)).ToArray();
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            foreach (var task in pending)
                await Assert.ThrowsAsync<InvalidDataException>(() => task.WaitAsync(TimeSpan.FromSeconds(3)));
            var extraCalls = 0;
            var extra = new TimeSource((_, _) => { Interlocked.Increment(ref extraCalls); return Task.FromResult<byte[]?>(null); });
            await Assert.ThrowsAsync<InvalidDataException>(() => IndependentEvidenceInspector.InspectAsync(
                fixture.Scope, fixture.Clock, extra, history, fixture.Trust, limits));
            Assert.Equal(0, extraCalls);
        }
        finally { release.TrySetResult(); await WaitForAdapterSlotsAsync(); }
    }

    [Fact]
    public async Task CallerCancellationIsNotHeldByAnAdapterCancellationCallback()
    {
        using var fixture = new Fixture();
        using var caller = new CancellationTokenSource();
        using var release = new ManualResetEventSlim(false);
        var adapterReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var time = new TimeSource(async (_, token) =>
        {
            token.Register(() =>
            {
                callbackStarted.TrySetResult();
                release.Wait();
                callbackFinished.TrySetResult();
            });
            adapterReady.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return null;
        });
        var history = new HistorySource((_, _) => Task.FromResult<byte[]?>(null));
        var inspection = IndependentEvidenceInspector.InspectAsync(fixture.Scope, fixture.Clock, time, history, fixture.Trust, fixture.Limits, caller.Token);
        await adapterReady.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var cancelTask = Task.Run(caller.Cancel);
        try
        {
            await callbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await cancelTask.WaitAsync(TimeSpan.FromSeconds(1));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => inspection.WaitAsync(TimeSpan.FromSeconds(3)));
        }
        finally
        {
            release.Set();
            await callbackFinished.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await cancelTask.WaitAsync(TimeSpan.FromSeconds(3));
            await WaitForAdapterSlotsAsync();
        }
    }

    [Fact]
    public async Task CallerCancellationDoesNotReleaseSlotsUntilBlockedCallbacksFinish()
    {
        await WaitForAdapterSlotsAsync();
        using var fixture = new Fixture();
        using var callbacksRelease = new ManualResetEventSlim(false);
        var sourcesRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allCallbacksStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allSourcesObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allReleasesObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sourceTasks = new List<Task>();
        var releases = new List<(Task Drained, Task Gate)>();
        var started = 0; var callbacks = 0;
        void ObserveSources(Task task)
        {
            lock (sourceTasks)
            {
                sourceTasks.Add(task);
                if (sourceTasks.Count == 4) allSourcesObserved.TrySetResult();
            }
        }
        void ObserveRelease(Task drained, Task gate)
        {
            lock (releases)
            {
                releases.Add((drained, gate));
                if (releases.Count == 4) allReleasesObserved.TrySetResult();
            }
        }
        var time = new TimeSource(async (_, token) =>
        {
            token.Register(() =>
            {
                if (Interlocked.Increment(ref callbacks) == 4) allCallbacksStarted.TrySetResult();
                callbacksRelease.Wait();
            });
            if (Interlocked.Increment(ref started) == 4) allStarted.TrySetResult();
            await sourcesRelease.Task;
            return null;
        });
        var history = new HistorySource((_, _) => Task.FromResult<byte[]?>(null));
        var limits = fixture.Limits with { MaximumProcessing = TimeSpan.FromSeconds(8) };
        var callers = Enumerable.Range(0, 4).Select(_ => new CancellationTokenSource()).ToArray();
        try
        {
            var pending = callers.Select(caller => InspectWithSourceTask(fixture.Scope, fixture.Clock, time, history,
                fixture.Trust, limits, caller.Token, ObserveSources, ObserveRelease)).ToArray();
            await allStarted.Task.WaitAsync(TimeSpan.FromSeconds(8));
            await allSourcesObserved.Task.WaitAsync(TimeSpan.FromSeconds(8));
            await Task.WhenAll(callers.Select(caller => Task.Run(caller.Cancel))).WaitAsync(TimeSpan.FromSeconds(8));
            await allCallbacksStarted.Task.WaitAsync(TimeSpan.FromSeconds(8));
            foreach (var task in pending)
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(8)));
            await allReleasesObserved.Task.WaitAsync(TimeSpan.FromSeconds(8));
            sourcesRelease.TrySetResult();
            await Task.WhenAll(sourceTasks).WaitAsync(TimeSpan.FromSeconds(8));
            await Task.WhenAll(releases.Select(release => release.Drained)).WaitAsync(TimeSpan.FromSeconds(8));
            Assert.All(releases, release => Assert.False(release.Gate.IsCompleted));
            var extraCalls = 0;
            var extra = new TimeSource((_, _) => { Interlocked.Increment(ref extraCalls); return Task.FromResult<byte[]?>(null); });
            await Assert.ThrowsAsync<InvalidDataException>(() => IndependentEvidenceInspector.InspectAsync(
                fixture.Scope, fixture.Clock, extra, history, fixture.Trust, limits));
            Assert.Equal(0, extraCalls);
        }
        finally
        {
            sourcesRelease.TrySetResult();
            callbacksRelease.Set();
            await WaitForAdapterSlotsAsync();
            foreach (var caller in callers) caller.Dispose();
        }
    }

    [Fact]
    public async Task CallerCancellationDoesNotExposeAdapterExceptionText()
    {
        using var fixture = new Fixture();
        using var caller = new CancellationTokenSource();
        var time = new TimeSource((_, _) =>
        {
            caller.Cancel();
            throw new OperationCanceledException("private-adapter-url-and-response");
        });
        var history = new HistorySource((_, _) => Task.FromResult<byte[]?>(null));
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => IndependentEvidenceInspector.InspectAsync(
            fixture.Scope, fixture.Clock, time, history, fixture.Trust, fixture.Limits, caller.Token));
        Assert.DoesNotContain("private-adapter-url-and-response", error.ToString(), StringComparison.Ordinal);
        await WaitForAdapterSlotsAsync();
    }

    [Fact]
    public void ProviderCancellationExceptionIsSanitizedWhenCallerHasCancelled()
    {
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        var method = typeof(IndependentEvidenceInspector).GetMethod("SanitizeFailure", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var result = Assert.IsType<OperationCanceledException>(method.Invoke(null,
            [new OperationCanceledException("private-adapter-url-and-response"), caller.Token]));
        Assert.Equal(caller.Token, result.CancellationToken);
        Assert.DoesNotContain("private-adapter-url-and-response", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OversizeEvidenceStreamIsStoppedAtTheWireLimitAndDisposed()
    {
        using var fixture = new Fixture();
        var stream = new CountingStream(new byte[1024 * 1024]);
        var time = new StreamingTimeSource((_, _) => Task.FromResult<Stream?>(stream));
        var history = new HistorySource((challenge, _) => Task.FromResult<byte[]?>(fixture.SignHistory(fixture.History with { Challenge = challenge })));
        await Assert.ThrowsAsync<InvalidDataException>(() => IndependentEvidenceInspector.InspectAsync(
            fixture.Scope, fixture.Clock, time, history, fixture.Trust, fixture.Limits));
        Assert.True(stream.Disposed);
        Assert.InRange(stream.ReadBytes, 1, 32 * 1024 + 1);
        await WaitForAdapterSlotsAsync();
    }

    private static async Task WaitForAdapterSlotsAsync()
    {
        // Inspect the actual limiter in this resource-lifecycle test so one
        // timed-out fake cannot leave capacity occupied for a later test.
        var field = typeof(IndependentEvidenceInspector).GetField("AdapterSlots", BindingFlags.Static | BindingFlags.NonPublic);
        var slots = Assert.IsType<SemaphoreSlim>(field?.GetValue(null));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (slots.CurrentCount != 4) await Task.Delay(10, timeout.Token);
    }

    private static Task<IndependentEvidenceInspection> InspectWithSourceTask(EvidenceScope scope, TimeProvider clock,
        IIndependentTimeEvidenceSource time, IIndependentSecurityHistoryWitness history, IndependentEvidenceTrust trust,
        IndependentEvidenceLimits limits, CancellationToken ct, Action<Task> observer, Action<Task, Task> releaseObserver)
    {
        var method = typeof(IndependentEvidenceInspector).GetMethod("InspectCoreAsync", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return Assert.IsAssignableFrom<Task<IndependentEvidenceInspection>>(method.Invoke(null, [scope, clock, time, history, trust, limits, ct, observer, releaseObserver]));
    }

    private sealed class TimeSource(Func<EvidenceChallenge, CancellationToken, Task<byte[]?>> request) : IIndependentTimeEvidenceSource
    {
        public async Task<Stream?> RequestAsync(EvidenceChallenge challenge, CancellationToken ct = default)
        {
            var data = await request(challenge, ct);
            return data is null ? null : new MemoryStream(data, writable: false);
        }
    }

    private sealed class HistorySource(Func<EvidenceChallenge, CancellationToken, Task<byte[]?>> request) : IIndependentSecurityHistoryWitness
    {
        public async Task<Stream?> RequestAsync(EvidenceChallenge challenge, CancellationToken ct = default)
        {
            var data = await request(challenge, ct);
            return data is null ? null : new MemoryStream(data, writable: false);
        }
    }

    private sealed class StreamingTimeSource(Func<EvidenceChallenge, CancellationToken, Task<Stream?>> request) : IIndependentTimeEvidenceSource
    {
        public Task<Stream?> RequestAsync(EvidenceChallenge challenge, CancellationToken ct = default) => request(challenge, ct);
    }

    private sealed class CountingStream(byte[] content) : MemoryStream(content, writable: false)
    {
        public int ReadBytes { get; private set; }
        public bool Disposed { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var count = await base.ReadAsync(buffer, cancellationToken);
            ReadBytes += count;
            return count;
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override ValueTask DisposeAsync() { Disposed = true; return base.DisposeAsync(); }
    }

    private sealed class Clock : TimeProvider
    {
        private long timestamp = 1000;
        public long Frequency { get; set; } = 1000;
        public override long TimestampFrequency => Frequency;
        public override long GetTimestamp() => timestamp;
        public void Move(long milliseconds) => timestamp += milliseconds;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly ECDsa timeKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly ECDsa historyKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public readonly Clock Clock = new();
        public readonly DateTimeOffset Lower = new(2026, 11, 4, 0, 0, 0, TimeSpan.Zero);
        public DateTimeOffset Upper => Lower.AddSeconds(1);
        public string TimePublicKey => Convert.ToBase64String(timeKey.ExportSubjectPublicKeyInfo());
        public string HistoryPublicKey => Convert.ToBase64String(historyKey.ExportSubjectPublicKeyInfo());
        public EvidenceScope Scope { get; } = new("control-plane-issuance", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 0, new string('a', 64));
        public IndependentEvidenceSession Session { get; }
        public IndependentEvidenceTrust Trust => new("time-source", "time-key", TimePublicKey, "history-source", "history-key", HistoryPublicKey);
        public IndependentEvidenceLimits Limits { get; } = new(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(2));
        public TimeEvidencePayload Time => new(Session.Challenge, "time-source", "time-key", Lower, Upper, Upper.AddSeconds(5));
        public HistoryEvidencePayload History => new(Session.Challenge, "history-source", "history-key", "Confirmed", 7);
        public Fixture() => Session = IndependentEvidenceSession.Start(Scope, Clock);
        public (byte[] Time, byte[] History) Signed() => (SignTime(Time), SignHistory(History));
        public byte[] SignTime(TimeEvidencePayload payload, bool corrupt = false) => CanonicalJson.Serialize(new SignedTimeEvidence(payload,
            Sign(IndependentEvidenceSession.TimeDomain, payload, corrupt ? historyKey : timeKey)));
        public byte[] SignHistory(HistoryEvidencePayload payload, bool corrupt = false) => CanonicalJson.Serialize(new SignedHistoryEvidence(payload,
            Sign(IndependentEvidenceSession.HistoryDomain, payload, corrupt ? timeKey : historyKey)));
        private static string Sign<T>(string domain, T payload, ECDsa key) => Convert.ToBase64String(key.SignData(
            SecuritySignedEncoding.Unsigned(domain, payload), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        public void Dispose() { timeKey.Dispose(); historyKey.Dispose(); }
    }
}
