using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using Microsoft.Win32.SafeHandles;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Notifications;
using Xunit;

namespace WebApi.Integration.Tests;

[SupportedOSPlatform("linux")]
public sealed class MigrationEvidenceJournalTests
{
    private static readonly MigrationBudget Budget = new(TimeSpan.FromMinutes(15), 65536, 16384, 100000, 67108864);

    private static string NewDirectory()
    {
        var root = Environment.GetEnvironmentVariable("WEBAPI_SOURCE_ROOT") ?? throw new InvalidOperationException("Isolated source root is required.");
        var path = Path.Combine(root, ".superpowers", "sdd", "2026-11-04-migration-budget-trace", "journal-fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private static MigrationEvidenceOptions Options(string path, long quota = 16 * 1024 * 1024) => new(path, 8192, quota, 1024 * 1024);
    private static MigrationAttemptStart Start(Guid id) => new(id, MigrationReceiptEntry.Managed, DateTimeOffset.UtcNow, Budget);

    [Theory]
    [InlineData("settings_too_large", MigrationBudgetCause.SettingsTooLarge)]
    [InlineData("rule_too_large", MigrationBudgetCause.RuleTooLarge)]
    [InlineData("rule_count_exceeded", MigrationBudgetCause.RuleCountExceeded)]
    [InlineData("total_bytes_exceeded", MigrationBudgetCause.TotalBytesExceeded)]
    [InlineData("synthetic-secret-should-never-be-recorded", MigrationBudgetCause.Unspecified)]
    public async Task BudgetFailurePreservesOnlyItsBoundedSafeCause(string reason, MigrationBudgetCause expected)
    {
        var path = NewDirectory();
        var journal = new MigrationEvidenceJournal(Options(path));
        var id = Guid.NewGuid();
        await journal.StartAsync(Start(id), CancellationToken.None);
        var failure = new NotificationMigrationBudgetException(reason);
        var record = MigrationEvidenceRecord.FromException(MigrationEvidencePhase.Migration, failure, rollbackConfirmed: true);
        Assert.Equal(MigrationEvidenceReason.Budget, record.Reason);
        Assert.Equal(expected, record.BudgetCause);
        await journal.AppendAsync(id, record, CancellationToken.None);
        var read = await journal.ReadAsync(id, CancellationToken.None);
        Assert.Equal(expected, Assert.Single(read.Records).BudgetCause);
        Assert.DoesNotContain("synthetic-secret", await File.ReadAllTextAsync(Path.Combine(path, id.ToString("N") + ".migration.json")));
        Assert.Throws<InvalidOperationException>(() => (record with { Reason = MigrationEvidenceReason.Database }).Validate());
    }

    [Fact]
    public async Task OlderRecordWithoutBudgetCauseRemainsReadable()
    {
        var path = NewDirectory();
        var journal = new MigrationEvidenceJournal(Options(path));
        var id = Guid.NewGuid();
        await journal.StartAsync(Start(id), CancellationToken.None);
        await journal.AppendAsync(id, new MigrationEvidenceRecord(MigrationEvidencePhase.Migration,
            MigrationEvidenceOutcome.Failed, MigrationEvidenceReason.Budget, DateTimeOffset.UtcNow), CancellationToken.None);
        var recordPath = Path.Combine(path, id.ToString("N") + ".migration.json");
        File.Copy(recordPath, path + "-original-record");
        var json = JsonNode.Parse(await File.ReadAllTextAsync(recordPath))!.AsObject();
        Assert.True(json["record"]!.AsObject().Remove("budgetCause"));
        await File.WriteAllTextAsync(recordPath, json.ToJsonString());
        var read = await journal.ReadAsync(id, CancellationToken.None);
        Assert.Equal(MigrationEvidenceReadState.Complete, read.State);
        Assert.Null(Assert.Single(read.Records).BudgetCause);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnresolvedAttemptBlocksNewAttemptWithoutChangingExistingEvidence(bool hasUnknownResult)
    {
        var path = NewDirectory();
        var journal = new MigrationEvidenceJournal(Options(path));
        var prior = Guid.NewGuid();
        await journal.StartAsync(Start(prior), CancellationToken.None);
        if (hasUnknownResult)
            await journal.AppendAsync(prior, new MigrationEvidenceRecord(MigrationEvidencePhase.Migration,
                MigrationEvidenceOutcome.Unknown, MigrationEvidenceReason.Database, DateTimeOffset.UtcNow), CancellationToken.None);
        var before = Directory.EnumerateFiles(path).Order().ToDictionary(file => Path.GetFileName(file)!, File.ReadAllBytes);
        var next = Guid.NewGuid();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => journal.StartAsync(Start(next), CancellationToken.None));
        Assert.Equal("migration_evidence_pending_review", error.Message);
        Assert.False(File.Exists(Path.Combine(path, next.ToString("N") + ".start.json")));
        Assert.Equal(before.Keys, Directory.EnumerateFiles(path).Select(Path.GetFileName).Order());
        foreach (var (name, content) in before) Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(path, name!)));
    }

    [Fact]
    public async Task ConfirmedFailureAllowsANewExplicitAttempt()
    {
        var path = NewDirectory();
        var journal = new MigrationEvidenceJournal(Options(path));
        var prior = Guid.NewGuid();
        await journal.StartAsync(Start(prior), CancellationToken.None);
        await journal.AppendAsync(prior, new MigrationEvidenceRecord(MigrationEvidencePhase.Migration,
            MigrationEvidenceOutcome.Failed, MigrationEvidenceReason.Database, DateTimeOffset.UtcNow), CancellationToken.None);
        var next = Guid.NewGuid();
        await journal.StartAsync(Start(next), CancellationToken.None);
        Assert.Equal(MigrationEvidenceReadState.Complete, (await journal.ReadAsync(prior, CancellationToken.None)).State);
        Assert.Equal(MigrationEvidenceReadState.Incomplete, (await journal.ReadAsync(next, CancellationToken.None)).State);
    }

    [Fact]
    public async Task ConcurrentCommittedAttemptInvalidatesTheCheckedHistoryBeforeAStart()
    {
        var path = NewDirectory();
        var journal = new MigrationEvidenceJournal(Options(path));
        var observed = await journal.ReadCommittedAttemptsAsync(CancellationToken.None);
        Assert.Empty(observed);
        var other = Guid.NewGuid();
        await journal.StartAsync(Start(other), CancellationToken.None);
        await journal.AppendAsync(other, new MigrationEvidenceRecord(MigrationEvidencePhase.Migration,
            MigrationEvidenceOutcome.Committed, MigrationEvidenceReason.None, DateTimeOffset.UtcNow), CancellationToken.None);
        var next = Guid.NewGuid();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => journal.StartCheckedAsync(Start(next), observed, CancellationToken.None));
        Assert.Equal("migration_evidence_pending_review", error.Message);
        Assert.False(File.Exists(Path.Combine(path, next.ToString("N") + ".start.json")));
        Assert.Equal(MigrationEvidenceReadState.Complete, (await journal.ReadAsync(other, CancellationToken.None)).State);
    }

    [Fact]
    public void JournalOptionsRequireExplicitBoundedConfiguration()
    {
        var path = NewDirectory();
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["WEBAPI_MIGRATION_EVIDENCE_DIR"] = path,
            ["WEBAPI_MIGRATION_EVIDENCE_RECORD_MAX_BYTES"] = "8192",
            ["WEBAPI_MIGRATION_EVIDENCE_QUOTA_BYTES"] = "16777216",
            ["WEBAPI_MIGRATION_EVIDENCE_MIN_FREE_BYTES"] = "1048576"
        };
        Assert.Equal(Options(path), MigrationEvidenceOptions.FromEnvironment(key => values.GetValueOrDefault(key)));
        foreach (var (key, value) in new[]
        {
            ("WEBAPI_MIGRATION_EVIDENCE_DIR", "/tmp/not-durable"),
            ("WEBAPI_MIGRATION_EVIDENCE_RECORD_MAX_BYTES", "0"),
            ("WEBAPI_MIGRATION_EVIDENCE_QUOTA_BYTES", "32767"),
            ("WEBAPI_MIGRATION_EVIDENCE_MIN_FREE_BYTES", "-1")
        })
        {
            var candidate = new Dictionary<string, string>(values, StringComparer.Ordinal) { [key] = value };
            Assert.Throws<InvalidOperationException>(() => MigrationEvidenceOptions.FromEnvironment(name => candidate.GetValueOrDefault(name)));
        }
        values.Remove("WEBAPI_MIGRATION_EVIDENCE_DIR");
        Assert.Throws<InvalidOperationException>(() => MigrationEvidenceOptions.FromEnvironment(key => values.GetValueOrDefault(key)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(path));
    }

    [Fact]
    public void SqlStartRequiresOnlyItsActualBudgetAndSqlHash()
    {
        var id = Guid.NewGuid();
        var sqlBudget = new MigrationSqlBudget(TimeSpan.FromMinutes(15), 100000);
        var valid = new MigrationAttemptStart(id, MigrationReceiptEntry.Sql, DateTimeOffset.UtcNow, null,
            SqlHash: new string('a', 64), SqlBudget: sqlBudget);
        valid.Validate();
        Assert.Throws<InvalidOperationException>(() => (valid with { SqlHash = null }).Validate());
        Assert.Throws<InvalidOperationException>(() => (valid with { Budget = Budget }).Validate());
        Assert.Throws<InvalidOperationException>(() => (valid with { BootstrapRequested = true }).Validate());
        Assert.Throws<InvalidOperationException>(() => (Start(id) with { SqlHash = new string('a', 64) }).Validate());
    }

    [Fact]
    public async Task JournalNeverOverwritesAndRejectsUnsafeDirectory()
    {
        var path = NewDirectory();
        var journal = new MigrationEvidenceJournal(Options(path));
        var id = Guid.NewGuid();
        await journal.StartAsync(Start(id), CancellationToken.None);
        var startedPath = Path.Combine(path, id.ToString("N") + ".start.json");
        var original = await File.ReadAllBytesAsync(startedPath);
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.StartAsync(Start(id), CancellationToken.None));
        Assert.Equal(original, await File.ReadAllBytesAsync(startedPath));
        var terminal = new MigrationEvidenceRecord(MigrationEvidencePhase.Migration, MigrationEvidenceOutcome.Committed, MigrationEvidenceReason.None, DateTimeOffset.UtcNow);
        await journal.AppendAsync(id, terminal, CancellationToken.None);
        var resultPath = Path.Combine(path, id.ToString("N") + ".migration.json");
        var result = await File.ReadAllBytesAsync(resultPath);
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.AppendAsync(id, terminal, CancellationToken.None));
        Assert.Equal(result, await File.ReadAllBytesAsync(resultPath));
        Assert.Equal(MigrationEvidenceReadState.Complete, (await journal.ReadAsync(id, CancellationToken.None)).State);
        Assert.Equal(MigrationEvidenceReadState.Missing, (await journal.ReadAsync(Guid.NewGuid(), CancellationToken.None)).State);

        var link = path + "-link";
        Directory.CreateSymbolicLink(link, path);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new MigrationEvidenceJournal(Options(link)).StartAsync(Start(Guid.NewGuid()), CancellationToken.None));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.OtherRead);
        try { await Assert.ThrowsAsync<InvalidOperationException>(() => journal.StartAsync(Start(Guid.NewGuid()), CancellationToken.None)); }
        finally { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        Assert.Equal(original, await File.ReadAllBytesAsync(startedPath));
    }

    [Fact]
    public async Task JournalConcurrentQuotaIsBounded()
    {
        var path = NewDirectory();
        var options = Options(path, 4 * 8192);
        var first = new MigrationEvidenceJournal(options);
        var second = new MigrationEvidenceJournal(options);
        var attempts = await Task.WhenAll(
            Record.ExceptionAsync(() => first.StartAsync(Start(Guid.NewGuid()), CancellationToken.None)),
            Record.ExceptionAsync(() => second.StartAsync(Start(Guid.NewGuid()), CancellationToken.None)));
        Assert.Single(attempts, error => error is null);
        Assert.Single(attempts, error => error is InvalidOperationException);
        Assert.Single(Directory.EnumerateFiles(path, "*.start.json"));
    }

    [Fact]
    public async Task JournalRejectsInsufficientSpaceBeforeStart()
    {
        var path = Path.Combine("/journal-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var id = Guid.NewGuid();
        // The largest configured minimum-free value (the option's 2^40 cap) exceeds
        // any real filesystem headroom, so the capacity gate trips deterministically
        // instead of depending on the container's actual disk usage.
        var options = new MigrationEvidenceOptions(path, 8192, 16 * 1024 * 1024, 1099511627776);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => new MigrationEvidenceJournal(options).StartAsync(Start(id), CancellationToken.None));
        Assert.Contains("space_insufficient", failure.Message);
        Assert.False(File.Exists(Path.Combine(path, id.ToString("N") + ".start.json")));
    }

    [Fact]
    public async Task JournalHonorsCrossProcessLock()
    {
        var path = NewDirectory();
        var lockPath = Path.Combine(path, ".journal.lock");
        await File.WriteAllBytesAsync(lockPath, []);
        File.SetUnixFileMode(lockPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var start = new ProcessStartInfo("flock") { UseShellExecute = false };
        foreach (var arg in new[] { "--no-fork", "-x", lockPath, "/bin/sleep", "10" }) start.ArgumentList.Add(arg);
        using var holder = Process.Start(start) ?? throw new InvalidOperationException("Cannot start isolated lock fixture.");
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (true)
            {
                var probe = new ProcessStartInfo("flock") { UseShellExecute = false };
                foreach (var arg in new[] { "-n", "-x", lockPath, "/usr/bin/true" }) probe.ArgumentList.Add(arg);
                using var process = Process.Start(probe) ?? throw new InvalidOperationException("Cannot inspect isolated lock fixture.");
                await process.WaitForExitAsync(deadline.Token);
                if (process.ExitCode == 1) break;
                Assert.Equal(0, process.ExitCode);
                await Task.Delay(25, deadline.Token);
            }
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new MigrationEvidenceJournal(Options(path)).StartAsync(Start(Guid.NewGuid()), cancellation.Token));
        }
        finally
        {
            if (!holder.HasExited) holder.Kill();
            await holder.WaitForExitAsync();
        }
        await new MigrationEvidenceJournal(Options(path)).StartAsync(Start(Guid.NewGuid()), CancellationToken.None);
    }

    [Fact]
    public async Task JournalFlushFailureStopsBeforeMigration()
    {
        var path = NewDirectory();
        var id = Guid.NewGuid();
        var journal = new MigrationEvidenceJournal(Options(path), new FailingDurability());
        await Assert.ThrowsAsync<IOException>(() => journal.StartAsync(Start(id), CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(path, id.ToString("N") + ".start.json")));
    }

    [Fact]
    public async Task ReplacedLockFileStopsBeforeAttemptIsRecorded()
    {
        var path = NewDirectory();
        var id = Guid.NewGuid();
        var journal = new MigrationEvidenceJournal(Options(path), new ReplacingLockDurability(path));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => journal.StartAsync(Start(id), CancellationToken.None));
        Assert.Contains("lock_changed", error.Message);
        Assert.False(File.Exists(Path.Combine(path, id.ToString("N") + ".start.json")));
        Assert.True(File.Exists(path + "-displaced-lock"));
    }

    [Fact]
    public async Task MissingJournalConfigurationBlocksAppendAndReportsCorruption()
    {
        var path = NewDirectory();
        var journal = new MigrationEvidenceJournal(Options(path));
        var id = Guid.NewGuid();
        await journal.StartAsync(Start(id), CancellationToken.None);
        File.Move(Path.Combine(path, ".journal.config"), path + "-displaced-config");
        var record = new MigrationEvidenceRecord(MigrationEvidencePhase.Migration, MigrationEvidenceOutcome.Committed, MigrationEvidenceReason.None, DateTimeOffset.UtcNow);
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.AppendAsync(id, record, CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(path, id.ToString("N") + ".migration.json")));
        Assert.Equal(MigrationEvidenceReadState.Corrupt, (await journal.ReadAsync(id, CancellationToken.None)).State);
    }

    [Fact]
    public async Task ExistingAttemptsWithoutConfigurationCannotBeReinitialized()
    {
        var path = NewDirectory();
        var id = Guid.NewGuid();
        await new MigrationEvidenceJournal(Options(path)).StartAsync(Start(id), CancellationToken.None);
        File.Move(Path.Combine(path, ".journal.config"), path + "-displaced-config");
        var newId = Guid.NewGuid();
        var changed = new MigrationEvidenceJournal(Options(path, 32 * 1024 * 1024));
        await Assert.ThrowsAsync<InvalidOperationException>(() => changed.StartAsync(Start(newId), CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(path, ".journal.config")));
        Assert.False(File.Exists(Path.Combine(path, newId.ToString("N") + ".start.json")));
        Assert.True(File.Exists(Path.Combine(path, id.ToString("N") + ".start.json")));
    }

    [Theory]
    [InlineData("start")]
    [InlineData("config")]
    public async Task DuplicateJsonFieldsAreCorruptAndCannotBeAppended(string kind)
    {
        var path = NewDirectory();
        var journal = new MigrationEvidenceJournal(Options(path));
        var id = Guid.NewGuid();
        await journal.StartAsync(Start(id), CancellationToken.None);
        var target = kind == "start" ? Path.Combine(path, id.ToString("N") + ".start.json") : Path.Combine(path, ".journal.config");
        File.Copy(target, path + "-original-" + kind);
        var json = await File.ReadAllTextAsync(target);
        Assert.StartsWith("{\"version\":1,", json);
        await File.WriteAllTextAsync(target, json.Insert(1, "\"version\":1,"));
        Assert.Equal(MigrationEvidenceReadState.Corrupt, (await journal.ReadAsync(id, CancellationToken.None)).State);
        var record = new MigrationEvidenceRecord(MigrationEvidencePhase.Migration, MigrationEvidenceOutcome.Committed, MigrationEvidenceReason.None, DateTimeOffset.UtcNow);
        await Assert.ThrowsAnyAsync<Exception>(() => journal.AppendAsync(id, record, CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(path, id.ToString("N") + ".migration.json")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LinkedEvidenceFilesAreNeverTrusted(bool hardLink)
    {
        var path = NewDirectory();
        var journal = new MigrationEvidenceJournal(Options(path));
        var id = Guid.NewGuid();
        await journal.StartAsync(Start(id), CancellationToken.None);
        var originalPath = Path.Combine(path, id.ToString("N") + ".start.json");
        var displaced = path + "-displaced-start";
        File.Move(originalPath, displaced);
        var original = await File.ReadAllBytesAsync(displaced);
        if (hardLink)
        {
            var start = new ProcessStartInfo("ln") { UseShellExecute = false };
            start.ArgumentList.Add(displaced);
            start.ArgumentList.Add(originalPath);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start isolated hard-link fixture.");
            await process.WaitForExitAsync();
            Assert.Equal(0, process.ExitCode);
        }
        else File.CreateSymbolicLink(originalPath, displaced);
        Assert.Equal(MigrationEvidenceReadState.Corrupt, (await journal.ReadAsync(id, CancellationToken.None)).State);
        var record = new MigrationEvidenceRecord(MigrationEvidencePhase.Migration, MigrationEvidenceOutcome.Committed, MigrationEvidenceReason.None, DateTimeOffset.UtcNow);
        await Assert.ThrowsAnyAsync<Exception>(() => journal.AppendAsync(id, record, CancellationToken.None));
        Assert.Equal(original, await File.ReadAllBytesAsync(displaced));
    }

    [Fact]
    public async Task SeedCannotSucceedAfterBootstrapFailure()
    {
        var path = NewDirectory();
        var journal = new MigrationEvidenceJournal(Options(path));
        var id = Guid.NewGuid();
        await journal.StartAsync(Start(id) with { BootstrapRequested = true, SeedRequested = true }, CancellationToken.None);
        await journal.AppendAsync(id, new MigrationEvidenceRecord(MigrationEvidencePhase.Migration, MigrationEvidenceOutcome.Committed, MigrationEvidenceReason.None, DateTimeOffset.UtcNow), CancellationToken.None);
        await journal.AppendAsync(id, new MigrationEvidenceRecord(MigrationEvidencePhase.Bootstrap, MigrationEvidenceOutcome.Failed, MigrationEvidenceReason.Unexpected, DateTimeOffset.UtcNow), CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.AppendAsync(id, new MigrationEvidenceRecord(MigrationEvidencePhase.Seed, MigrationEvidenceOutcome.Completed, MigrationEvidenceReason.None, DateTimeOffset.UtcNow), CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(path, id.ToString("N") + ".seed.json")));
        await journal.AppendAsync(id, new MigrationEvidenceRecord(MigrationEvidencePhase.Seed, MigrationEvidenceOutcome.Skipped, MigrationEvidenceReason.None, DateTimeOffset.UtcNow), CancellationToken.None);
        Assert.Equal(MigrationEvidenceReadState.Complete, (await journal.ReadAsync(id, CancellationToken.None)).State);
    }

    [Fact]
    public async Task MissingRequiredBudgetIsReportedAsCorrupt()
    {
        var path = NewDirectory();
        var journal = new MigrationEvidenceJournal(Options(path));
        var id = Guid.NewGuid();
        await journal.StartAsync(Start(id), CancellationToken.None);
        var target = Path.Combine(path, id.ToString("N") + ".start.json");
        File.Copy(target, path + "-original-start");
        var node = JsonNode.Parse(await File.ReadAllTextAsync(target)) ?? throw new InvalidOperationException("Synthetic fixture is invalid.");
        node["start"]!["budget"] = null;
        await File.WriteAllTextAsync(target, node.ToJsonString());
        Assert.Equal(MigrationEvidenceReadState.Corrupt, (await journal.ReadAsync(id, CancellationToken.None)).State);
    }

    [Fact]
    public async Task MissingRequiredEntryPointIsCorruptRatherThanManaged()
    {
        var path = NewDirectory();
        var journal = new MigrationEvidenceJournal(Options(path));
        var id = Guid.NewGuid();
        await journal.StartAsync(Start(id), CancellationToken.None);
        var target = Path.Combine(path, id.ToString("N") + ".start.json");
        File.Copy(target, path + "-original-start");
        var node = JsonNode.Parse(await File.ReadAllTextAsync(target))!;
        Assert.True(node["start"]!.AsObject().Remove("entryPoint"));
        await File.WriteAllTextAsync(target, node.ToJsonString());
        Assert.Equal(MigrationEvidenceReadState.Corrupt, (await journal.ReadAsync(id, CancellationToken.None)).State);
    }

    [Fact]
    public async Task ReplacedConfigurationStopsBeforeAttemptIsRecorded()
    {
        var path = NewDirectory();
        var id = Guid.NewGuid();
        var journal = new MigrationEvidenceJournal(Options(path), new ReplacingConfigurationDurability(path));
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.StartAsync(Start(id), CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(path, id.ToString("N") + ".start.json")));
        Assert.True(File.Exists(path + "-displaced-config"));
    }

    [Fact]
    public async Task ReplacedDirectoryStopsAndPreservesDisplacedFiles()
    {
        var path = NewDirectory();
        var id = Guid.NewGuid();
        var journal = new MigrationEvidenceJournal(Options(path), new ReplacingDirectoryDurability(path));
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.StartAsync(Start(id), CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(path, id.ToString("N") + ".start.json")));
        Assert.True(File.Exists(Path.Combine(path + "-displaced", ".journal.config")));
    }

    [Fact]
    public async Task JournalContainsNoSensitiveMaterial()
    {
        var path = NewDirectory();
        var journal = new MigrationEvidenceJournal(Options(path));
        var id = Guid.NewGuid();
        await journal.StartAsync(Start(id), CancellationToken.None);
        const string secret = "password=synthetic-canary; https://example.test/private; vault://secret/ref";
        var record = MigrationEvidenceRecord.FromException(MigrationEvidencePhase.Migration, new InvalidOperationException(secret), rollbackConfirmed: true);
        await journal.AppendAsync(id, record, CancellationToken.None);
        var read = await journal.ReadAsync(id, CancellationToken.None);
        Assert.Equal(MigrationEvidenceReadState.Complete, read.State);
        Assert.Equal(MigrationEvidenceOutcome.Failed, Assert.Single(read.Records).Outcome);
        foreach (var file in Directory.EnumerateFiles(path, "*.json"))
        {
            var content = await File.ReadAllTextAsync(file);
            foreach (var fragment in new[] { "password", "synthetic-canary", "https://example.test", "vault://" })
                Assert.DoesNotContain(fragment, content);
        }
    }

    [Fact]
    public async Task ArchiveInspectionUsesRealJournalSemanticsAndRetainsUnknown()
    {
        var directory = NewDirectory();
        var journal = new MigrationEvidenceJournal(Options(directory));
        var id = Guid.NewGuid();
        await journal.StartAsync(Start(id), CancellationToken.None);
        async Task<string> ArchiveAsync()
        {
            var output = directory + "-" + Guid.NewGuid().ToString("N") + ".tar";
            var command = new ProcessStartInfo("tar") { UseShellExecute = false, RedirectStandardError = true };
            foreach (var argument in new[] { "--numeric-owner", "-cf", output, "-C", directory, "." }) command.ArgumentList.Add(argument);
            using var child = Process.Start(command) ?? throw new InvalidOperationException("Cannot create isolated archive.");
            var error = await child.StandardError.ReadToEndAsync();
            await child.WaitForExitAsync();
            Assert.True(child.ExitCode == 0, error);
            Console.WriteLine("isolated-retained-archive=" + output);
            return output;
        }
        async Task<(int Exit, string Output)> InspectCommandAsync(string archive)
        {
            var command = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            command.ArgumentList.Add(Environment.GetEnvironmentVariable("WEBAPI_MIGRATOR_DLL") ?? throw new InvalidOperationException("Isolated migrator required."));
            command.ArgumentList.Add("--inspect-evidence-archive");
            command.Environment.Clear();
            command.Environment["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "";
            command.Environment["DOTNET_ROOT"] = Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? "/usr/share/dotnet";
            command.Environment["WEBAPI_MIGRATION_ARCHIVE_FILE"] = archive;
            command.Environment["WEBAPI_MIGRATION_ARCHIVE_SHA256"] = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(archive)));
            using var child = Process.Start(command) ?? throw new InvalidOperationException("Cannot inspect isolated archive.");
            var stdout = child.StandardOutput.ReadToEndAsync();
            var stderr = child.StandardError.ReadToEndAsync();
            await child.WaitForExitAsync();
            return (child.ExitCode, (await stdout) + (await stderr));
        }
        async Task<(int Exit, string Output)> InspectStreamCommandAsync(string archive, bool corruptHash = false)
        {
            var command = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            command.ArgumentList.Add(Environment.GetEnvironmentVariable("WEBAPI_MIGRATOR_DLL") ?? throw new InvalidOperationException("Isolated migrator required."));
            command.ArgumentList.Add("--inspect-evidence-archive-stdin");
            command.Environment.Clear();
            command.Environment["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "";
            command.Environment["DOTNET_ROOT"] = Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? "/usr/share/dotnet";
            command.Environment["WEBAPI_MIGRATION_ARCHIVE_SHA256"] = corruptHash ? new string('0', 64) : Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(archive)));
            using var child = Process.Start(command) ?? throw new InvalidOperationException("Cannot inspect isolated archive stream.");
            var stdout = child.StandardOutput.ReadToEndAsync();var stderr = child.StandardError.ReadToEndAsync();
            await using (var input = File.OpenRead(archive)) await input.CopyToAsync(child.StandardInput.BaseStream);
            child.StandardInput.Close();await child.WaitForExitAsync();
            return (child.ExitCode, (await stdout) + (await stderr));
        }
        var incomplete = await ArchiveAsync();
        await using (var file = File.OpenRead(incomplete))
        {
            var result = await MigrationEvidenceJournal.InspectArchiveAsync(file, CancellationToken.None);
            Assert.Equal(1, result.Attempts);
            Assert.Equal(1, result.PendingAttempts);
            var directoryResult = await journal.InspectDirectoryAsync(CancellationToken.None);
            Assert.Equal(result.ContentHash, directoryResult.ContentHash);
            Assert.Equal(result.PendingAttempts, directoryResult.PendingAttempts);
        }
        var incompleteCommand = await InspectCommandAsync(incomplete);
        Assert.Equal(0, incompleteCommand.Exit);
        Assert.Contains("\"pendingAttempts\":1", incompleteCommand.Output);
        var streamCommand = await InspectStreamCommandAsync(incomplete);
        Assert.Equal(0, streamCommand.Exit);
        Assert.Contains("\"pendingAttempts\":1", streamCommand.Output);
        Assert.Contains("\"contentHash\":", streamCommand.Output);
        Assert.NotEqual(0, (await InspectStreamCommandAsync(incomplete, corruptHash: true)).Exit);
        var config = Path.Combine(directory, ".journal.config");
        File.Copy(config, directory + "-original-config");
        await File.WriteAllTextAsync(config, "{}");
        var corrupt = await ArchiveAsync();
        await using (var file = File.OpenRead(corrupt))
            await Assert.ThrowsAnyAsync<Exception>(() => MigrationEvidenceJournal.InspectArchiveAsync(file, CancellationToken.None));
        Assert.NotEqual(0, (await InspectCommandAsync(corrupt)).Exit);
        File.Copy(directory + "-original-config", config, overwrite: true);
        await journal.AppendAsync(id, new MigrationEvidenceRecord(MigrationEvidencePhase.Migration,
            MigrationEvidenceOutcome.Committed, MigrationEvidenceReason.None, DateTimeOffset.UtcNow), CancellationToken.None);
        File.Move(Path.Combine(directory, id.ToString("N") + ".start.json"), directory + "-orphaned-start");
        var orphan = await ArchiveAsync();
        await using (var file = File.OpenRead(orphan))
            await Assert.ThrowsAnyAsync<Exception>(() => MigrationEvidenceJournal.InspectArchiveAsync(file, CancellationToken.None));
        Assert.NotEqual(0, (await InspectCommandAsync(orphan)).Exit);
    }

    [Fact]
    public async Task EvidenceArchiveAndRestorePreserveTheReadableJournal()
    {
        var path = NewDirectory();
        var journal = new MigrationEvidenceJournal(Options(path));
        var id = Guid.NewGuid();
        await journal.StartAsync(Start(id), CancellationToken.None);
        await journal.AppendAsync(id, new MigrationEvidenceRecord(MigrationEvidencePhase.Migration,
            MigrationEvidenceOutcome.Committed, MigrationEvidenceReason.None, DateTimeOffset.UtcNow), CancellationToken.None);
        var archive = path + "-backup.tar";
        var root = Environment.GetEnvironmentVariable("WEBAPI_SOURCE_ROOT") ?? throw new InvalidOperationException("Isolated source root is required.");
        var backup = new ProcessStartInfo("bash") { UseShellExecute = false, RedirectStandardError = true };
        foreach (var argument in new[] { Path.Combine(root, "deploy", "runtime", "archive-migration-evidence.sh"), path, archive }) backup.ArgumentList.Add(argument);
        using (var process = Process.Start(backup) ?? throw new InvalidOperationException("Cannot start isolated archive fixture."))
        {
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.True(process.ExitCode == 0, error);
        }
        var restored = NewDirectory();
        var extract = new ProcessStartInfo("tar") { UseShellExecute = false, RedirectStandardError = true };
        foreach (var argument in new[] { "-xf", archive, "-C", restored }) extract.ArgumentList.Add(argument);
        using (var process = Process.Start(extract) ?? throw new InvalidOperationException("Cannot start isolated restore fixture."))
        {
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.True(process.ExitCode == 0, error);
        }
        foreach (var file in Directory.EnumerateFiles(path))
            Assert.Equal(await File.ReadAllBytesAsync(file), await File.ReadAllBytesAsync(Path.Combine(restored, Path.GetFileName(file))));
        Assert.Equal(MigrationEvidenceReadState.Complete,
            (await new MigrationEvidenceJournal(Options(restored)).ReadAsync(id, CancellationToken.None)).State);
    }

    [Fact]
    public async Task EvidenceArchiveDoesNotOverwriteAndWaitsForJournalLock()
    {
        var path = NewDirectory();
        var journal = new MigrationEvidenceJournal(Options(path));
        await journal.StartAsync(Start(Guid.NewGuid()), CancellationToken.None);
        var root = Environment.GetEnvironmentVariable("WEBAPI_SOURCE_ROOT") ?? throw new InvalidOperationException("Isolated source root is required.");
        var helper = Path.Combine(root, "deploy", "runtime", "archive-migration-evidence.sh");
        var existing = path + "-existing.tar";
        await File.WriteAllTextAsync(existing, "retain-this-existing-file");
        async Task<int> ArchiveAsync(string output)
        {
            var command = new ProcessStartInfo("bash") { UseShellExecute = false, RedirectStandardError = true };
            foreach (var arg in new[] { helper, path, output }) command.ArgumentList.Add(arg);
            using var child = Process.Start(command) ?? throw new InvalidOperationException("Cannot start isolated archive fixture.");
            _ = await child.StandardError.ReadToEndAsync();
            await child.WaitForExitAsync();
            return child.ExitCode;
        }
        Assert.NotEqual(0, await ArchiveAsync(existing));
        Assert.Equal("retain-this-existing-file", await File.ReadAllTextAsync(existing));

        var lockPath = Path.Combine(path, ".journal.lock");
        var holderArgs = new ProcessStartInfo("flock") { UseShellExecute = false };
        foreach (var arg in new[] { "--no-fork", "-x", lockPath, "/bin/sleep", "10" }) holderArgs.ArgumentList.Add(arg);
        using var holder = Process.Start(holderArgs) ?? throw new InvalidOperationException("Cannot start isolated lock fixture.");
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (true)
            {
                var probeArgs = new ProcessStartInfo("flock") { UseShellExecute = false };
                foreach (var arg in new[] { "-n", "-x", lockPath, "/usr/bin/true" }) probeArgs.ArgumentList.Add(arg);
                using var probe = Process.Start(probeArgs) ?? throw new InvalidOperationException("Cannot inspect isolated lock fixture.");
                await probe.WaitForExitAsync(deadline.Token);
                if (probe.ExitCode == 1) break;
                Assert.Equal(0, probe.ExitCode);
                await Task.Delay(25, deadline.Token);
            }
            var blocked = path + "-blocked.tar";
            Assert.NotEqual(0, await ArchiveAsync(blocked));
            Assert.False(File.Exists(blocked));
        }
        finally
        {
            if (!holder.HasExited) holder.Kill();
            await holder.WaitForExitAsync();
        }
    }

    private sealed class FailingDurability : IMigrationEvidenceDurability
    {
        public Task SyncFileAsync(SafeFileHandle handle, CancellationToken ct) => throw new IOException("synthetic sync failure");
        public Task SyncDirectoryAsync(SafeFileHandle handle, CancellationToken ct) => throw new IOException("synthetic sync failure");
    }

    private sealed class ReplacingLockDurability(string path) : IMigrationEvidenceDurability
    {
        private int directorySyncs;
        public Task SyncFileAsync(SafeFileHandle handle, CancellationToken ct) => Task.CompletedTask;
        public Task SyncDirectoryAsync(SafeFileHandle handle, CancellationToken ct)
        {
            if (++directorySyncs == 2)
            {
                var lockPath = Path.Combine(path, ".journal.lock");
                File.Move(lockPath, path + "-displaced-lock");
                File.WriteAllBytes(lockPath, []);
                File.SetUnixFileMode(lockPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            return Task.CompletedTask;
        }
    }

    private sealed class ReplacingConfigurationDurability(string path) : IMigrationEvidenceDurability
    {
        private int directorySyncs;
        public Task SyncFileAsync(SafeFileHandle handle, CancellationToken ct) => Task.CompletedTask;
        public Task SyncDirectoryAsync(SafeFileHandle handle, CancellationToken ct)
        {
            if (++directorySyncs == 2)
            {
                var target = Path.Combine(path, ".journal.config");
                File.Move(target, path + "-displaced-config");
                File.WriteAllText(target, "{}");
                File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            return Task.CompletedTask;
        }
    }

    private sealed class ReplacingDirectoryDurability(string path) : IMigrationEvidenceDurability
    {
        private int directorySyncs;
        public Task SyncFileAsync(SafeFileHandle handle, CancellationToken ct) => Task.CompletedTask;
        public Task SyncDirectoryAsync(SafeFileHandle handle, CancellationToken ct)
        {
            if (++directorySyncs == 2)
            {
                Directory.Move(path, path + "-displaced");
                Directory.CreateDirectory(path);
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            return Task.CompletedTask;
        }
    }
}
