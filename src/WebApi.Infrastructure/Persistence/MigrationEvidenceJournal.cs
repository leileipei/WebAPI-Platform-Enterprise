using System.Text.Json;
using System.Text.Json.Serialization;
using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using Microsoft.Win32.SafeHandles;

namespace WebApi.Infrastructure.Persistence;

/// <summary>Append-only, bounded local evidence; it is not an independent anti-rollback witness.</summary>
public sealed class MigrationEvidenceJournal : IMigrationEvidenceJournal
{
    private const string LockName = ".journal.lock";
    private const string ConfigName = ".journal.config";
    private const int SlotsPerAttempt = 4;
    private const int MaximumEntries = 131072;
    private readonly MigrationEvidenceOptions options;
    private readonly IMigrationEvidenceDurability durability;
    private static readonly JsonSerializerOptions Json = CreateJson();

    public static async Task<MigrationEvidenceArchiveInspection> InspectArchiveAsync(Stream source, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead || source.CanSeek && (source.Length > 2L * 1024 * 1024 * 1024 || source.Length < 1024))
            throw new InvalidOperationException("migration_evidence_archive_invalid");
        var names = new HashSet<string>(StringComparer.Ordinal);
        var attempts = new Dictionary<Guid, Attempt>();
        var content = new SortedDictionary<string, (int Length, byte[] Hash)>(StringComparer.Ordinal);
        JournalConfig? configuration = null;
        long total = 0;
        var largest = 0;
        using var archive = new TarReader(source, leaveOpen: true);
        TarEntry? entry;
        while ((entry = await archive.GetNextEntryAsync(copyData: false, cancellationToken: ct)) is not null)
        {
            ct.ThrowIfCancellationRequested();
            if (names.Count >= MaximumEntries + 1 || !names.Add(entry.Name))
                throw new InvalidOperationException("migration_evidence_archive_invalid");
            if (entry.Name == "./")
            {
                if (entry.EntryType != TarEntryType.Directory || entry.Length != 0)
                    throw new InvalidOperationException("migration_evidence_archive_invalid");
                continue;
            }
            if (!entry.Name.StartsWith("./", StringComparison.Ordinal) ||
                entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) ||
                entry.Length is < 0 or > 65536 || entry.DataStream is null && entry.Length != 0)
                throw new InvalidOperationException("migration_evidence_archive_invalid");
            var name = entry.Name[2..];
            var length = checked((int)entry.Length);
            total = checked(total + length);
            if (total > 1073807360L) throw new InvalidOperationException("migration_evidence_archive_invalid");
            largest = Math.Max(largest, length);
            var bytes = new byte[length];
            if (length != 0) await entry.DataStream!.ReadExactlyAsync(bytes, ct);
            content.Add(name, (length, SHA256.HashData(bytes)));
            if (name == LockName)
            {
                if (length != 0) throw new InvalidOperationException("migration_evidence_archive_invalid");
                continue;
            }
            if (name == ConfigName)
            {
                configuration = Decode<JournalConfig>(bytes);
                continue;
            }
            if (!TryParseName(name, out var id, out var kind))
                throw new InvalidOperationException("migration_evidence_archive_invalid");
            var envelope = Decode<JournalEnvelope>(bytes);
            if (envelope.Version != 1 || envelope.AttemptId != id)
                throw new InvalidOperationException("migration_evidence_record_invalid");
            if (!attempts.TryGetValue(id, out var attempt)) attempts[id] = attempt = new Attempt();
            if (kind == "start")
            {
                if (envelope.Start is null || envelope.Record is not null || envelope.Start.AttemptId != id || attempt.Start is not null)
                    throw new InvalidOperationException("migration_evidence_record_invalid");
                envelope.Start.Validate();
                attempt.Start = envelope.Start;
            }
            else
            {
                if (envelope.Start is not null || envelope.Record is null || PhaseName(envelope.Record.Phase) != kind || !attempt.Records.TryAdd(envelope.Record.Phase, envelope.Record))
                    throw new InvalidOperationException("migration_evidence_record_invalid");
                envelope.Record.Validate();
            }
        }
        if (!names.Contains("./") || !names.Contains("./" + LockName) || configuration is null || configuration.Version != 1 || attempts.Count == 0)
            throw new InvalidOperationException("migration_evidence_archive_invalid");
        var options = new MigrationEvidenceOptions("/evidence", configuration.RecordMaxBytes, configuration.DirectoryQuotaBytes, configuration.MinimumFreeBytes);
        options.Validate();
        if (largest > options.RecordMaxBytes || checked((long)attempts.Count * SlotsPerAttempt * options.RecordMaxBytes) > options.DirectoryQuotaBytes)
            throw new InvalidOperationException("migration_evidence_archive_invalid");
        foreach (var attempt in attempts.Values)
        {
            if (attempt.Start is null) throw new InvalidOperationException("migration_evidence_orphan_record");
            foreach (var record in attempt.Records.Values) CheckTransition(attempt, record);
        }
        return new MigrationEvidenceArchiveInspection(attempts.Count, attempts.Values.Count(attempt => !IsComplete(attempt)), ContentHash(content));
    }

    public async Task<MigrationEvidenceDirectoryInspection> InspectDirectoryAsync(CancellationToken ct)
    {
        using var deadline = Deadline(ct);
        using var directory = MigrationEvidenceLinux.OpenDirectory(options.DirectoryPath);
        var identity = MigrationEvidenceLinux.CheckDirectory(directory);
        using var gate = await OpenGateAsync(directory, create: false, deadline.Token)
            ?? throw new InvalidOperationException("migration_evidence_lock_missing");
        await MigrationEvidenceLinux.LockAsync(gate, deadline.Token);
        VerifyDirectory(identity);
        VerifyGate(directory, gate);
        var configuration = await EnsureConfigAsync(directory, create: false, deadline.Token)
            ?? throw new InvalidOperationException("migration_evidence_configuration_missing");
        var content = new SortedDictionary<string, (int Length, byte[] Hash)>(StringComparer.Ordinal)
        {
            [LockName] = (0, SHA256.HashData([]))
        };
        var attempts = await ScanAsync(directory, deadline.Token,
            (name, bytes) => content.Add(name, (bytes.Length, SHA256.HashData(bytes))));
        using (var config = MigrationEvidenceLinux.OpenExisting(directory, ConfigName)
            ?? throw new InvalidOperationException("migration_evidence_configuration_missing"))
        {
            if (MigrationEvidenceLinux.CheckFile(config, options.RecordMaxBytes).Identity != configuration)
                throw new InvalidOperationException("migration_evidence_configuration_changed");
            var bytes = await ReadFileAsync(config, deadline.Token);
            content.Add(ConfigName, (bytes.Length, SHA256.HashData(bytes)));
        }
        VerifyGate(directory, gate);
        await VerifyConfigAsync(directory, configuration, deadline.Token);
        VerifyDirectory(identity);
        if (attempts.Count == 0) throw new InvalidOperationException("migration_evidence_archive_invalid");
        return new MigrationEvidenceDirectoryInspection(attempts.Count, attempts.Values.Count(attempt => !IsComplete(attempt)), ContentHash(content));
    }

    private static string ContentHash(SortedDictionary<string, (int Length, byte[] Hash)> content)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (name, item) in content)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(name));
            hash.AppendData([0]);
            hash.AppendData(Encoding.ASCII.GetBytes(item.Length.ToString(CultureInfo.InvariantCulture)));
            hash.AppendData([0]);
            hash.AppendData(item.Hash);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    public MigrationEvidenceJournal(MigrationEvidenceOptions options) : this(options, new LinuxMigrationEvidenceDurability()) { }
    internal MigrationEvidenceJournal(MigrationEvidenceOptions options, IMigrationEvidenceDurability durability)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(durability);
        options.Validate();
        this.options = options;
        this.durability = durability;
    }

    public async Task<IReadOnlyList<Guid>> ReadCommittedAttemptsAsync(CancellationToken ct)
    {
        using var deadline = Deadline(ct);
        using var directory = MigrationEvidenceLinux.OpenDirectory(options.DirectoryPath);
        var identity = MigrationEvidenceLinux.CheckDirectory(directory);
        using var gate = await OpenGateAsync(directory, create: false, deadline.Token);
        if (gate is null)
        {
            if (Directory.EnumerateFileSystemEntries(MigrationEvidenceLinux.AnchoredPath(directory)).Any())
                throw new InvalidOperationException("migration_evidence_lock_missing");
            VerifyDirectory(identity);
            return [];
        }
        await MigrationEvidenceLinux.LockAsync(gate, deadline.Token);
        VerifyDirectory(identity);
        VerifyGate(directory, gate);
        var configuration = await EnsureConfigAsync(directory, create: false, deadline.Token)
            ?? throw new InvalidOperationException("migration_evidence_configuration_missing");
        var attempts = await ScanAsync(directory, deadline.Token);
        if (attempts.Values.Any(attempt => !IsComplete(attempt)))
            throw new InvalidOperationException("migration_evidence_pending_review");
        VerifyGate(directory, gate);
        await VerifyConfigAsync(directory, configuration, deadline.Token);
        VerifyDirectory(identity);
        return attempts.Where(pair => pair.Value.Records.TryGetValue(MigrationEvidencePhase.Migration, out var record) &&
            record.Outcome == MigrationEvidenceOutcome.Committed).Select(pair => pair.Key).Order().ToArray();
    }

    public Task StartAsync(MigrationAttemptStart start, CancellationToken ct) => StartCoreAsync(start, null, ct);

    public Task StartCheckedAsync(MigrationAttemptStart start, IReadOnlyList<Guid> checkedCommittedAttempts, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(checkedCommittedAttempts);
        var snapshot = checkedCommittedAttempts.ToArray();
        if (snapshot.Any(id => id == Guid.Empty) || snapshot.Distinct().Count() != snapshot.Length)
            throw new InvalidOperationException("migration_evidence_history_invalid");
        return StartCoreAsync(start, snapshot, ct);
    }

    private async Task StartCoreAsync(MigrationAttemptStart start, Guid[]? checkedCommittedAttempts, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(start);
        start.Validate();
        using var deadline = Deadline(ct);
        using var directory = MigrationEvidenceLinux.OpenDirectory(options.DirectoryPath);
        var identity = MigrationEvidenceLinux.CheckDirectory(directory);
        using var gate = await OpenGateAsync(directory, create: true, deadline.Token)
            ?? throw new InvalidOperationException("migration_evidence_lock_missing");
        await MigrationEvidenceLinux.LockAsync(gate, deadline.Token);
        VerifyDirectory(identity);
        VerifyGate(directory, gate);
        var configuration = await EnsureConfigAsync(directory, create: true, deadline.Token)
            ?? throw new InvalidOperationException("migration_evidence_configuration_missing");
        VerifyGate(directory, gate);
        var attempts = await ScanAsync(directory, deadline.Token);
        if (attempts.ContainsKey(start.AttemptId)) throw new InvalidOperationException("migration_evidence_attempt_exists");
        CheckCapacity(directory, attempts, addingAttempt: true);
        // An incomplete or explicitly unknown result may represent a committed
        // transaction. Do not turn a restart into another migration attempt.
        if (attempts.Values.Any(attempt => !IsComplete(attempt)))
            throw new InvalidOperationException("migration_evidence_pending_review");
        if (checkedCommittedAttempts is not null && !attempts.Where(pair =>
                pair.Value.Records.TryGetValue(MigrationEvidencePhase.Migration, out var record) && record.Outcome == MigrationEvidenceOutcome.Committed)
            .Select(pair => pair.Key).Order().SequenceEqual(checkedCommittedAttempts.Order()))
            throw new InvalidOperationException("migration_evidence_pending_review");
        var envelope = new JournalEnvelope(1, start.AttemptId, start, null);
        VerifyGate(directory, gate);
        await VerifyConfigAsync(directory, configuration, deadline.Token);
        await WriteNewAsync(directory, Name(start.AttemptId, "start"), Encode(envelope), deadline.Token);
        VerifyGate(directory, gate);
        await VerifyConfigAsync(directory, configuration, deadline.Token);
        VerifyDirectory(identity);
    }

    public async Task AppendAsync(Guid attemptId, MigrationEvidenceRecord record, CancellationToken ct)
    {
        if (attemptId == Guid.Empty) throw new InvalidOperationException("migration_evidence_attempt_invalid");
        ArgumentNullException.ThrowIfNull(record);
        record.Validate();
        using var deadline = Deadline(ct);
        using var directory = MigrationEvidenceLinux.OpenDirectory(options.DirectoryPath);
        var identity = MigrationEvidenceLinux.CheckDirectory(directory);
        using var gate = await OpenGateAsync(directory, create: false, deadline.Token)
            ?? throw new InvalidOperationException("migration_evidence_lock_missing");
        await MigrationEvidenceLinux.LockAsync(gate, deadline.Token);
        VerifyDirectory(identity);
        VerifyGate(directory, gate);
        var configuration = await EnsureConfigAsync(directory, create: false, deadline.Token)
            ?? throw new InvalidOperationException("migration_evidence_configuration_missing");
        VerifyGate(directory, gate);
        var attempts = await ScanAsync(directory, deadline.Token);
        if (!attempts.TryGetValue(attemptId, out var attempt) || attempt.Start is null)
            throw new InvalidOperationException("migration_evidence_attempt_missing");
        if (attempt.Records.ContainsKey(record.Phase)) throw new InvalidOperationException("migration_evidence_phase_exists");
        CheckTransition(attempt, record);
        CheckCapacity(directory, attempts, addingAttempt: false);
        var envelope = new JournalEnvelope(1, attemptId, null, record);
        VerifyGate(directory, gate);
        await VerifyConfigAsync(directory, configuration, deadline.Token);
        await WriteNewAsync(directory, Name(attemptId, PhaseName(record.Phase)), Encode(envelope), deadline.Token);
        VerifyGate(directory, gate);
        await VerifyConfigAsync(directory, configuration, deadline.Token);
        VerifyDirectory(identity);
    }

    public async Task<MigrationEvidenceRead> ReadAsync(Guid attemptId, CancellationToken ct)
    {
        if (attemptId == Guid.Empty) throw new InvalidOperationException("migration_evidence_attempt_invalid");
        using var deadline = Deadline(ct);
        using var directory = MigrationEvidenceLinux.OpenDirectory(options.DirectoryPath);
        var identity = MigrationEvidenceLinux.CheckDirectory(directory);
        using var gate = await OpenGateAsync(directory, create: false, deadline.Token);
        if (gate is null)
        {
            var empty = !Directory.EnumerateFileSystemEntries(MigrationEvidenceLinux.AnchoredPath(directory)).Any();
            VerifyDirectory(identity);
            return new MigrationEvidenceRead(empty ? MigrationEvidenceReadState.Missing : MigrationEvidenceReadState.Corrupt, null, []);
        }
        await MigrationEvidenceLinux.LockAsync(gate, deadline.Token);
        VerifyDirectory(identity);
        try
        {
            VerifyGate(directory, gate);
            var configuration = await EnsureConfigAsync(directory, create: false, deadline.Token);
            if (configuration is null)
                return new MigrationEvidenceRead(MigrationEvidenceReadState.Corrupt, null, []);
            var attempts = await ScanAsync(directory, deadline.Token);
            VerifyGate(directory, gate);
            await VerifyConfigAsync(directory, configuration.Value, deadline.Token);
            VerifyDirectory(identity);
            if (!attempts.TryGetValue(attemptId, out var attempt)) return new MigrationEvidenceRead(MigrationEvidenceReadState.Missing, null, []);
            if (attempt.Start is null) return new MigrationEvidenceRead(MigrationEvidenceReadState.Corrupt, null, []);
            var records = attempt.Records.OrderBy(pair => pair.Key).Select(pair => pair.Value).ToArray();
            return new MigrationEvidenceRead(IsComplete(attempt) ? MigrationEvidenceReadState.Complete : MigrationEvidenceReadState.Incomplete, attempt.Start, records);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or IOException)
        {
            return new MigrationEvidenceRead(MigrationEvidenceReadState.Corrupt, null, []);
        }
    }

    private async Task<SafeFileHandle?> OpenGateAsync(SafeFileHandle directory, bool create, CancellationToken ct)
    {
        var gate = MigrationEvidenceLinux.OpenExisting(directory, LockName, writable: true);
        if (gate is null && create)
        {
            gate = MigrationEvidenceLinux.CreateNew(directory, LockName);
            if (gate is not null)
            {
                try
                {
                    MigrationEvidenceLinux.CheckFile(gate, 0);
                    await durability.SyncFileAsync(gate, ct);
                    await durability.SyncDirectoryAsync(directory, ct);
                }
                catch { gate.Dispose(); throw; }
            }
            else gate = MigrationEvidenceLinux.OpenExisting(directory, LockName, writable: true);
        }
        if (gate is not null)
        {
            try { MigrationEvidenceLinux.CheckFile(gate, 0); }
            catch { gate.Dispose(); throw; }
        }
        return gate;
    }

    private async Task<MigrationEvidenceLinux.Identity?> EnsureConfigAsync(SafeFileHandle directory, bool create, CancellationToken ct)
    {
        var expected = new JournalConfig(1, options.RecordMaxBytes, options.DirectoryQuotaBytes, options.MinimumFreeBytes);
        using var file = MigrationEvidenceLinux.OpenExisting(directory, ConfigName);
        if (file is null)
        {
            if (!create) return null;
            foreach (var entry in Directory.EnumerateFileSystemEntries(MigrationEvidenceLinux.AnchoredPath(directory)))
            {
                ct.ThrowIfCancellationRequested();
                if (Path.GetFileName(entry) != LockName)
                    throw new InvalidOperationException("migration_evidence_configuration_missing");
            }
            var created = await WriteNewAsync(directory, ConfigName, Encode(expected), ct);
            await VerifyConfigAsync(directory, created, ct);
            return created;
        }
        var identity = MigrationEvidenceLinux.CheckFile(file, options.RecordMaxBytes).Identity;
        var config = Decode<JournalConfig>(await ReadFileAsync(file, ct));
        if (config != expected) throw new InvalidOperationException("migration_evidence_configuration_changed");
        return identity;
    }

    private async Task VerifyConfigAsync(SafeFileHandle directory, MigrationEvidenceLinux.Identity expected, CancellationToken ct)
    {
        using var file = MigrationEvidenceLinux.OpenExisting(directory, ConfigName)
            ?? throw new InvalidOperationException("migration_evidence_configuration_changed");
        if (MigrationEvidenceLinux.CheckFile(file, options.RecordMaxBytes).Identity != expected)
            throw new InvalidOperationException("migration_evidence_configuration_changed");
        var config = Decode<JournalConfig>(await ReadFileAsync(file, ct));
        if (config != new JournalConfig(1, options.RecordMaxBytes, options.DirectoryQuotaBytes, options.MinimumFreeBytes))
            throw new InvalidOperationException("migration_evidence_configuration_changed");
    }

    private async Task<Dictionary<Guid, Attempt>> ScanAsync(SafeFileHandle directory, CancellationToken ct, Action<string, byte[]>? observe = null)
    {
        var result = new Dictionary<Guid, Attempt>();
        var seen = 0;
        foreach (var entry in Directory.EnumerateFileSystemEntries(MigrationEvidenceLinux.AnchoredPath(directory)))
        {
            ct.ThrowIfCancellationRequested();
            if (++seen > MaximumEntries) throw new InvalidOperationException("migration_evidence_entry_limit");
            var name = Path.GetFileName(entry);
            if (name is LockName or ConfigName) continue;
            if (!TryParseName(name, out var id, out var kind)) throw new InvalidOperationException("migration_evidence_unknown_entry");
            using var file = MigrationEvidenceLinux.OpenExisting(directory, name)
                ?? throw new InvalidOperationException("migration_evidence_file_disappeared");
            var bytes = await ReadFileAsync(file, ct);
            observe?.Invoke(name, bytes);
            var envelope = Decode<JournalEnvelope>(bytes);
            if (envelope.Version != 1 || envelope.AttemptId != id) throw new InvalidOperationException("migration_evidence_record_invalid");
            if (!result.TryGetValue(id, out var attempt)) result[id] = attempt = new Attempt();
            if (kind == "start")
            {
                if (envelope.Start is null || envelope.Record is not null || envelope.Start.AttemptId != id || attempt.Start is not null)
                    throw new InvalidOperationException("migration_evidence_record_invalid");
                envelope.Start.Validate();
                attempt.Start = envelope.Start;
            }
            else
            {
                if (envelope.Start is not null || envelope.Record is null || PhaseName(envelope.Record.Phase) != kind || !attempt.Records.TryAdd(envelope.Record.Phase, envelope.Record))
                    throw new InvalidOperationException("migration_evidence_record_invalid");
                envelope.Record.Validate();
            }
        }
        foreach (var attempt in result.Values)
        {
            if (attempt.Start is null) throw new InvalidOperationException("migration_evidence_orphan_record");
            foreach (var record in attempt.Records.Values) CheckTransition(attempt, record);
        }
        return result;
    }

    private void CheckCapacity(SafeFileHandle directory, Dictionary<Guid, Attempt> attempts, bool addingAttempt)
    {
        var groups = checked((long)attempts.Count + (addingAttempt ? 1 : 0));
        if (checked(groups * SlotsPerAttempt * options.RecordMaxBytes) > options.DirectoryQuotaBytes)
            throw new InvalidOperationException("migration_evidence_quota_exceeded");
        var futureSlots = attempts.Values.Sum(attempt => (long)(SlotsPerAttempt - 1 - attempt.Records.Count)) + (addingAttempt ? SlotsPerAttempt : 0);
        var required = checked(options.MinimumFreeBytes + futureSlots * options.RecordMaxBytes);
        if (MigrationEvidenceLinux.FreeBytes(directory) < required) throw new InvalidOperationException("migration_evidence_space_insufficient");
    }

    private async Task<MigrationEvidenceLinux.Identity> WriteNewAsync<T>(SafeFileHandle directory, string name, T value, CancellationToken ct)
    {
        var bytes = value is byte[] encoded ? encoded : Encode(value);
        using var file = MigrationEvidenceLinux.CreateNew(directory, name)
            ?? throw new InvalidOperationException("migration_evidence_file_exists");
        var identity = MigrationEvidenceLinux.CheckFile(file, 0).Identity;
        await using (var stream = new FileStream(file, FileAccess.Write, 4096, isAsync: false))
        {
            await stream.WriteAsync(bytes, ct);
            await stream.FlushAsync(ct);
            await durability.SyncFileAsync(file, ct);
            MigrationEvidenceLinux.CheckFile(file, options.RecordMaxBytes);
        }
        await durability.SyncDirectoryAsync(directory, ct);
        return identity;
    }

    private async Task<byte[]> ReadFileAsync(SafeFileHandle file, CancellationToken ct)
    {
        var before = MigrationEvidenceLinux.CheckFile(file, options.RecordMaxBytes);
        var bytes = new byte[checked((int)before.Size + 1)];
        await using var stream = new FileStream(file, FileAccess.Read, 4096, isAsync: false);
        var length = 0;
        while (length < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(length), ct);
            if (read == 0) break;
            length += read;
        }
        var after = MigrationEvidenceLinux.CheckFile(file, options.RecordMaxBytes);
        if (length != before.Size || after != before) throw new InvalidOperationException("migration_evidence_file_changed");
        return bytes[..length];
    }

    private byte[] Encode<T>(T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (bytes.Length > options.RecordMaxBytes) throw new InvalidOperationException("migration_evidence_record_too_large");
        return bytes;
    }

    private static T Decode<T>(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
        CheckUniqueFields(document.RootElement);
        return JsonSerializer.Deserialize<T>(bytes, Json) ?? throw new InvalidOperationException("migration_evidence_record_invalid");
    }

    private static void CheckUniqueFields(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidOperationException("migration_evidence_duplicate_field");
                CheckUniqueFields(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) CheckUniqueFields(child);
    }

    private void VerifyDirectory(MigrationEvidenceLinux.Identity expected)
    {
        using var current = MigrationEvidenceLinux.OpenDirectory(options.DirectoryPath);
        if (MigrationEvidenceLinux.CheckDirectory(current) != expected) throw new InvalidOperationException("migration_evidence_directory_changed");
    }

    private static void VerifyGate(SafeFileHandle directory, SafeFileHandle gate)
    {
        var held = MigrationEvidenceLinux.CheckFile(gate, 0).Identity;
        using var current = MigrationEvidenceLinux.OpenExisting(directory, LockName, writable: true)
            ?? throw new InvalidOperationException("migration_evidence_lock_changed");
        if (MigrationEvidenceLinux.CheckFile(current, 0).Identity != held)
            throw new InvalidOperationException("migration_evidence_lock_changed");
    }

    private static void CheckTransition(Attempt attempt, MigrationEvidenceRecord record)
    {
        if (attempt.Start is null) throw new InvalidOperationException("migration_evidence_attempt_missing");
        var phase = record.Phase;
        if (phase == MigrationEvidencePhase.Migration) return;
        if (!attempt.Records.TryGetValue(MigrationEvidencePhase.Migration, out var migration) || migration.Outcome != MigrationEvidenceOutcome.Committed)
            throw new InvalidOperationException("migration_evidence_migration_not_committed");
        if (phase == MigrationEvidencePhase.Bootstrap && !attempt.Start.BootstrapRequested ||
            phase == MigrationEvidencePhase.Seed && (!attempt.Start.SeedRequested || attempt.Start.BootstrapRequested && !attempt.Records.ContainsKey(MigrationEvidencePhase.Bootstrap)))
            throw new InvalidOperationException("migration_evidence_phase_not_requested");
        if (phase == MigrationEvidencePhase.Seed && attempt.Start.BootstrapRequested &&
            attempt.Records[MigrationEvidencePhase.Bootstrap].Outcome != MigrationEvidenceOutcome.Completed &&
            record.Outcome is not (MigrationEvidenceOutcome.Skipped or MigrationEvidenceOutcome.Unknown))
            throw new InvalidOperationException("migration_evidence_prior_phase_not_completed");
    }

    private static bool IsComplete(Attempt attempt)
    {
        if (!attempt.Records.TryGetValue(MigrationEvidencePhase.Migration, out var migration) || migration.Outcome == MigrationEvidenceOutcome.Unknown) return false;
        if (migration.Outcome == MigrationEvidenceOutcome.Failed) return true;
        if (attempt.Start!.BootstrapRequested && (!attempt.Records.TryGetValue(MigrationEvidencePhase.Bootstrap, out var bootstrap) || bootstrap.Outcome == MigrationEvidenceOutcome.Unknown)) return false;
        if (attempt.Start.SeedRequested && (!attempt.Records.TryGetValue(MigrationEvidencePhase.Seed, out var seed) || seed.Outcome == MigrationEvidenceOutcome.Unknown)) return false;
        return true;
    }

    private static CancellationTokenSource Deadline(CancellationToken ct)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        return deadline;
    }

    private static string Name(Guid id, string kind) => id.ToString("N") + "." + kind + ".json";
    private static string PhaseName(MigrationEvidencePhase phase) => phase switch
    {
        MigrationEvidencePhase.Migration => "migration",
        MigrationEvidencePhase.Bootstrap => "bootstrap",
        MigrationEvidencePhase.Seed => "seed",
        _ => throw new InvalidOperationException("migration_evidence_phase_invalid")
    };

    private static bool TryParseName(string name, out Guid id, out string kind)
    {
        id = default;
        kind = string.Empty;
        var parts = name.Split('.');
        if (parts.Length != 3 || parts[2] != "json" || parts[0].Length != 32 ||
            !Guid.TryParseExact(parts[0], "N", out id) || id == Guid.Empty || id.ToString("N") != parts[0] ||
            parts[1] is not ("start" or "migration" or "bootstrap" or "seed")) return false;
        kind = parts[1];
        return true;
    }

    private static JsonSerializerOptions CreateJson()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            PropertyNameCaseInsensitive = false,
            RespectRequiredConstructorParameters = true,
            MaxDepth = 16
        };
        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
        return options;
    }

    private sealed class Attempt
    {
        public MigrationAttemptStart? Start { get; set; }
        public Dictionary<MigrationEvidencePhase, MigrationEvidenceRecord> Records { get; } = [];
    }

    private sealed record JournalConfig(int Version, int RecordMaxBytes, long DirectoryQuotaBytes, long MinimumFreeBytes);
    private sealed record JournalEnvelope(int Version, Guid AttemptId, MigrationAttemptStart? Start, MigrationEvidenceRecord? Record);
}
