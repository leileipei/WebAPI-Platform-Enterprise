using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using WebApi.Contracts.Common;

namespace WebApi.Domain.CredentialSecurity;

// These contracts are deliberately separate: a trusted clock does not prove
// that security history survived restore, and a ledger witness is not a clock.
// An adapter must not infer either claim from the platform database itself.
public interface IIndependentTimeEvidenceSource
{
    // Return an unread stream owned by the inspector; adapters must enforce
    // transport limits too and must not buffer unbounded responses internally.
    Task<Stream?> RequestAsync(EvidenceChallenge challenge, CancellationToken ct = default);
}

public interface IIndependentSecurityHistoryWitness
{
    Task<Stream?> RequestAsync(EvidenceChallenge challenge, CancellationToken ct = default);
}

public sealed record EvidenceScope(string Purpose, Guid EnvironmentId, Guid NodeId, Guid InstanceId,
    Guid SecurityGeneration, long Epoch, string HeadHash);

public sealed record EvidenceChallenge(
    [property: JsonRequired] string Purpose,
    [property: JsonRequired] Guid EnvironmentId,
    [property: JsonRequired] Guid NodeId,
    [property: JsonRequired] Guid InstanceId,
    [property: JsonRequired] Guid SecurityGeneration,
    [property: JsonRequired] long Epoch,
    [property: JsonRequired] string HeadHash,
    [property: JsonRequired] string Nonce);

public sealed record TimeEvidencePayload(
    [property: JsonRequired] EvidenceChallenge Challenge,
    [property: JsonRequired] string SourceId,
    [property: JsonRequired] string KeyId,
    [property: JsonRequired] DateTimeOffset Lower,
    [property: JsonRequired] DateTimeOffset Upper,
    [property: JsonRequired] DateTimeOffset ValidUntil);

public sealed record HistoryEvidencePayload(
    [property: JsonRequired] EvidenceChallenge Challenge,
    [property: JsonRequired] string SourceId,
    [property: JsonRequired] string KeyId,
    [property: JsonRequired] string State,
    [property: JsonRequired] long Sequence);

public sealed record SignedTimeEvidence([property: JsonRequired] TimeEvidencePayload Payload, [property: JsonRequired] string Signature);
public sealed record SignedHistoryEvidence([property: JsonRequired] HistoryEvidencePayload Payload, [property: JsonRequired] string Signature);

// Pins are independent even if one external system implements both protocols.
public sealed record IndependentEvidenceTrust(string TimeSourceId, string TimeKeyId, string TimePublicKey,
    string HistorySourceId, string HistoryKeyId, string HistoryPublicKey);
public sealed record IndependentEvidenceLimits(TimeSpan MaximumTimeWidth, TimeSpan MaximumValidity, TimeSpan MaximumProcessing);

// Inspection is intentionally not an authorization capability. In particular,
// it cannot prove two-phase witness durability or gateway recovery provenance.
public sealed record IndependentEvidenceInspection(DateTimeOffset LowerAtSource, DateTimeOffset UpperAtSource,
    DateTimeOffset ConservativeUpperAtInspection, TimeSpan ProcessingElapsed, long HistorySequence);

public sealed class IndependentEvidenceSession
{
    public const string TimeDomain = "webapi.independent-time-evidence.v1";
    public const string HistoryDomain = "webapi.security-history-evidence.v1";
    internal const int MaxWireBytes = 32 * 1024;
    private static readonly JsonSerializerOptions Strict = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        NumberHandling = JsonNumberHandling.Strict,
        MaxDepth = 16
    };
    private readonly TimeProvider clock;
    private readonly long start;
    private readonly long frequency;
    private int consumed;
    public EvidenceChallenge Challenge { get; }

    private IndependentEvidenceSession(EvidenceScope scope, TimeProvider clock, long start, long frequency)
    {
        this.clock = clock;
        this.start = start;
        this.frequency = frequency;
        Challenge = new(scope.Purpose, scope.EnvironmentId, scope.NodeId, scope.InstanceId, scope.SecurityGeneration,
            scope.Epoch, scope.HeadHash, Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)));
    }

    public static IndependentEvidenceSession Start(EvidenceScope scope, TimeProvider clock)
    {
        if (scope is null || clock is null || scope.Purpose is not ("control-plane-issuance" or "gateway-admission") ||
            scope.EnvironmentId == Guid.Empty || scope.NodeId == Guid.Empty || scope.InstanceId == Guid.Empty ||
            scope.SecurityGeneration == Guid.Empty || scope.Epoch < 0 || !SecuritySignedEncoding.HexHash(scope.HeadHash))
            throw Invalid();
        try
        {
            var frequency = clock.TimestampFrequency;
            if (frequency <= 0) throw Invalid();
            return new(scope, clock, clock.GetTimestamp(), frequency);
        }
        catch (Exception error) when (IsRecoverable(error)) { throw Invalid(error); }
    }

    public IndependentEvidenceInspection Inspect(byte[]? timeWire, byte[]? historyWire, IndependentEvidenceTrust trust, IndependentEvidenceLimits limits)
    {
        // A failed or successful challenge is never retried or reusable.
        if (Interlocked.Exchange(ref consumed, 1) != 0) throw Invalid();
        if (trust is null || !ValidLimits(limits)) throw Invalid();

        var time = Read<SignedTimeEvidence>(timeWire);
        var history = Read<SignedHistoryEvidence>(historyWire);
        var t = time.Payload ?? throw Invalid();
        var h = history.Payload ?? throw Invalid();
        if (t.Challenge != Challenge || h.Challenge != Challenge ||
            t.SourceId != trust.TimeSourceId || t.KeyId != trust.TimeKeyId ||
            h.SourceId != trust.HistorySourceId || h.KeyId != trust.HistoryKeyId ||
            h.State != "Confirmed" || h.Sequence <= 0 ||
            !SecuritySignedEncoding.KeyId(t.SourceId) || !SecuritySignedEncoding.KeyId(t.KeyId) ||
            !SecuritySignedEncoding.KeyId(h.SourceId) || !SecuritySignedEncoding.KeyId(h.KeyId)) throw Invalid();
        Verify(TimeDomain, t, time.Signature, trust.TimePublicKey);
        Verify(HistoryDomain, h, history.Signature, trust.HistoryPublicKey);

        if (t.Lower.Offset != TimeSpan.Zero || t.Upper.Offset != TimeSpan.Zero || t.ValidUntil.Offset != TimeSpan.Zero ||
            t.Lower <= DateTimeOffset.MinValue || t.Upper < t.Lower || t.ValidUntil <= t.Upper ||
            t.Upper - t.Lower > limits.MaximumTimeWidth || t.ValidUntil - t.Lower > limits.MaximumValidity) throw Invalid();
        TimeSpan elapsed;
        DateTimeOffset conservativeUpper;
        try
        {
            if (clock.TimestampFrequency != frequency) throw Invalid();
            var end = clock.GetTimestamp();
            if (end < start) throw Invalid();
            elapsed = clock.GetElapsedTime(start, end);
            conservativeUpper = t.Upper.Add(elapsed);
            if (elapsed < TimeSpan.Zero || elapsed > limits.MaximumProcessing || conservativeUpper >= t.ValidUntil) throw Invalid();
        }
        catch (Exception error) when (IsRecoverable(error)) { throw Invalid(error); }
        return new(t.Lower, t.Upper, conservativeUpper, elapsed, h.Sequence);
    }

    internal static bool ValidLimits(IndependentEvidenceLimits? limits) => limits is not null &&
        limits.MaximumTimeWidth >= TimeSpan.Zero && limits.MaximumTimeWidth <= TimeSpan.FromSeconds(4) &&
        limits.MaximumValidity > TimeSpan.Zero && limits.MaximumValidity <= TimeSpan.FromSeconds(30) &&
        limits.MaximumProcessing > TimeSpan.Zero && limits.MaximumProcessing <= TimeSpan.FromSeconds(10) &&
        limits.MaximumProcessing <= limits.MaximumValidity;

    private static T Read<T>(byte[]? bytes)
    {
        if (bytes is null || bytes.Length is < 2 or > MaxWireBytes) throw Invalid();
        try
        {
            // Decode a snapshot rather than retaining provider-owned mutable bytes.
            var copy = bytes.ToArray();
            using var document = JsonDocument.Parse(copy, new JsonDocumentOptions { MaxDepth = 16 });
            RequireUniqueNames(document.RootElement);
            var value = JsonSerializer.Deserialize<T>(copy, Strict) ?? throw Invalid();
            if (!copy.AsSpan().SequenceEqual(CanonicalJson.Serialize(value))) throw Invalid();
            return value;
        }
        catch (JsonException error) { throw Invalid(error); }
    }

    private static void RequireUniqueNames(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw Invalid();
                RequireUniqueNames(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RequireUniqueNames(item);
    }

    private static void Verify<T>(string domain, T payload, string? signature, string? encodedKey)
    {
        if (string.IsNullOrEmpty(signature) || string.IsNullOrEmpty(encodedKey) || signature.Length > 256 || encodedKey.Length > 1024) throw Invalid();
        try
        {
            using var key = ECDsa.Create();
            var publicKey = Convert.FromBase64String(encodedKey);
            if (Convert.ToBase64String(publicKey) != encodedKey) throw Invalid();
            key.ImportSubjectPublicKeyInfo(publicKey, out var read);
            if (read != publicKey.Length) throw Invalid();
            SecuritySignedEncoding.RequireP256(key);
            var raw = Convert.FromBase64String(signature);
            if (Convert.ToBase64String(raw) != signature || raw.Length != 64 || !key.VerifyData(SecuritySignedEncoding.Unsigned(domain, payload), raw,
                HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) throw Invalid();
        }
        catch (Exception error) when (error is CryptographicException or FormatException or ArgumentException) { throw Invalid(error); }
    }

    private static bool IsRecoverable(Exception error) => error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException;
    private static InvalidDataException Invalid(Exception? inner = null) => new("independent_security_evidence_unavailable", inner);
}

// Adapter coordination is likewise only an inspection. Neither a successful
// response nor this helper enables security leases or gateway admission.
public static class IndependentEvidenceInspector
{
    // A provider can ignore cancellation or block before returning a Task.
    // Hold its slot until both calls and cancellation callbacks have actually
    // finished; exhausted capacity fails closed instead of spawning forever.
    private static readonly SemaphoreSlim AdapterSlots = new(4, 4);

    public static Task<IndependentEvidenceInspection> InspectAsync(EvidenceScope scope, TimeProvider clock,
        IIndependentTimeEvidenceSource? timeSource, IIndependentSecurityHistoryWitness? historyWitness,
        IndependentEvidenceTrust? trust, IndependentEvidenceLimits? limits, CancellationToken ct = default) =>
        InspectCoreAsync(scope, clock, timeSource, historyWitness, trust, limits, ct, null, null);

    private static async Task<IndependentEvidenceInspection> InspectCoreAsync(EvidenceScope scope, TimeProvider clock,
        IIndependentTimeEvidenceSource? timeSource, IIndependentSecurityHistoryWitness? historyWitness,
        IndependentEvidenceTrust? trust, IndependentEvidenceLimits? limits, CancellationToken ct,
        Action<Task>? observeSources, Action<Task, Task>? observeRelease)
    {
        if (timeSource is null || historyWitness is null || trust is null || !IndependentEvidenceSession.ValidLimits(limits))
            throw new InvalidDataException("independent_security_evidence_unavailable");
        ct.ThrowIfCancellationRequested();
        var session = IndependentEvidenceSession.Start(scope, clock);
        if (!await AdapterSlots.WaitAsync(0, ct)) throw new InvalidDataException("independent_security_evidence_unavailable");
        // Do not link this CTS to the caller: linked cancellation would run
        // adapter callbacks synchronously on the caller's cancellation thread,
        // and a second Cancel could return before those callbacks had drained.
        // WaitAsync observes ct; the one background Cancel in finally then
        // owns all adapter callbacks until the bounded slot can be released.
        var linked = new CancellationTokenSource();
        Task<byte[]?[]>? both = null;
        try
        {
            // Supply one fresh challenge to both independent trust authorities.
            // Task.Run also bounds the caller when an adapter blocks before it
            // returns its Task; cancellation cannot forcibly stop such code.
            var timeTask = Task.Run(async () => await ReadWireAsync(
                timeSource.RequestAsync(session.Challenge, linked.Token) ?? throw new InvalidDataException(), linked.Token));
            var historyTask = Task.Run(async () => await ReadWireAsync(
                historyWitness.RequestAsync(session.Challenge, linked.Token) ?? throw new InvalidDataException(), linked.Token));
            both = Task.WhenAll(timeTask, historyTask);
            // The public entry point always supplies null. Tests can observe
            // the actual source-drain task to verify the limiter's lifetime.
            observeSources?.Invoke(both);
            var responses = await both.WaitAsync(limits!.MaximumProcessing, ct);
            return session.Inspect(responses[0], responses[1], trust, limits);
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        {
            // An adapter can include confidential URLs or response data in an
            // exception; do not propagate it across this security boundary.
            throw SanitizeFailure(error, ct);
        }
        finally
        {
            ReleaseWhenFinished(both, linked, observeRelease);
        }
    }

    private static Exception SanitizeFailure(Exception error, CancellationToken ct) =>
        error is OperationCanceledException && ct.IsCancellationRequested
            ? new OperationCanceledException(ct)
            : new InvalidDataException("independent_security_evidence_unavailable");

    private static async Task<byte[]?> ReadWireAsync(Task<Stream?> response, CancellationToken ct)
    {
        var stream = await response;
        if (stream is null) return null;
        await using (stream)
        {
            var buffer = new byte[IndependentEvidenceSession.MaxWireBytes + 1];
            var length = 0;
            while (length < buffer.Length)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(length), ct);
                if (count == 0) return buffer.AsSpan(0, length).ToArray();
                if (count < 0 || count > buffer.Length - length) throw new InvalidDataException();
                length += count;
            }
            throw new InvalidDataException("independent_security_evidence_too_large");
        }
    }

    private static void ReleaseWhenFinished(Task? sources, CancellationTokenSource linked, Action<Task, Task>? observeRelease)
    {
        // Cancellation callbacks are adapter-controlled too; do not block the
        // caller or release the bounded slot until they have returned.
        var cancelled = Task.Run(() =>
        {
            try { linked.Cancel(); }
            catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException) { }
        });
        var drained = (sources ?? Task.CompletedTask).ContinueWith(t => _ = t.Exception,
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        var releaseGate = Task.WhenAll(cancelled, drained);
        _ = releaseGate.ContinueWith(_ =>
        {
            try { linked.Dispose(); }
            finally { AdapterSlots.Release(); }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        observeRelease?.Invoke(drained, releaseGate);
    }
}
