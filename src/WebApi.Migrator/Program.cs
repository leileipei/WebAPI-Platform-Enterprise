using Microsoft.EntityFrameworkCore;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Persistence;
using WebApi.Migrator;
using System.Security.Cryptography;
using System.Text.Json;

static async Task<MigrationEvidenceArchiveInspection> InspectArchiveFromEnvironmentAsync(CancellationToken ct)
{
    var file = Environment.GetEnvironmentVariable("WEBAPI_MIGRATION_ARCHIVE_FILE");
    var expected = Environment.GetEnvironmentVariable("WEBAPI_MIGRATION_ARCHIVE_SHA256");
    if (string.IsNullOrEmpty(file) || !Path.IsPathFullyQualified(file) || expected is null || expected.Length != 64 ||
        expected.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        throw new InvalidOperationException("migration_evidence_archive_configuration_invalid");
    await using var archive = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
    if (archive.Length is < 1024 or > 2L * 1024 * 1024 * 1024 ||
        !CryptographicOperations.FixedTimeEquals(await SHA256.HashDataAsync(archive, ct), Convert.FromHexString(expected)))
        throw new InvalidOperationException("migration_evidence_archive_hash_mismatch");
    archive.Position = 0;
    var inspected = await MigrationEvidenceJournal.InspectArchiveAsync(archive, ct);
    archive.Position = 0;
    if (!CryptographicOperations.FixedTimeEquals(await SHA256.HashDataAsync(archive, ct), Convert.FromHexString(expected)))
        throw new InvalidOperationException("migration_evidence_archive_hash_mismatch");
    return inspected;
}

static async Task<MigrationEvidenceArchiveInspection> InspectArchiveFromStandardInputAsync(CancellationToken ct)
{
    var expected = Environment.GetEnvironmentVariable("WEBAPI_MIGRATION_ARCHIVE_SHA256");
    if (expected is null || expected.Length != 64 || expected.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        throw new InvalidOperationException("migration_evidence_archive_configuration_invalid");
    await using var input = new BoundedHashingReadStream(Console.OpenStandardInput(), 2L * 1024 * 1024 * 1024);
    var inspected = await MigrationEvidenceJournal.InspectArchiveAsync(input, ct);
    var remainder = new byte[65536];
    while (await input.ReadAsync(remainder, ct) != 0) { }
    if (!CryptographicOperations.FixedTimeEquals(input.FinishHash(), Convert.FromHexString(expected)))
        throw new InvalidOperationException("migration_evidence_archive_hash_mismatch");
    return inspected;
}

static MigrationEvidenceArchiveInspection InspectionFromVerifiedDigest()
{
    var hash = Environment.GetEnvironmentVariable("WEBAPI_MIGRATION_ARCHIVE_CONTENT_SHA256");
    var attemptsText = Environment.GetEnvironmentVariable("WEBAPI_MIGRATION_ARCHIVE_ATTEMPTS");
    var pendingText = Environment.GetEnvironmentVariable("WEBAPI_MIGRATION_ARCHIVE_PENDING");
    if (hash is null || hash.Length != 64 || hash.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')) ||
        attemptsText is null || pendingText is null || attemptsText.Length is < 1 or > 6 || pendingText.Length is < 1 or > 6 ||
        attemptsText.Any(c => c is < '0' or > '9') || pendingText.Any(c => c is < '0' or > '9') ||
        !int.TryParse(attemptsText, out var attempts) || !int.TryParse(pendingText, out var pending) ||
        attempts is < 1 or > 131072 || pending != 0)
        throw new InvalidOperationException("migration_evidence_archive_configuration_invalid");
    return new MigrationEvidenceArchiveInspection(attempts, pending, hash);
}

try
{
    var inspectArchive = args.Contains("--inspect-evidence-archive", StringComparer.Ordinal);
    var inspectStream = args.Contains("--inspect-evidence-archive-stdin", StringComparer.Ordinal);
    var verifyRestore = args.Contains("--verify-evidence-restore", StringComparer.Ordinal);
    var verifyDigest = args.Contains("--verify-evidence-restore-digest", StringComparer.Ordinal);
    if (inspectArchive || inspectStream || verifyRestore || verifyDigest)
    {
        if (args.Length != 1) throw new InvalidOperationException("migration_evidence_archive_configuration_invalid");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var inspected = verifyDigest ? InspectionFromVerifiedDigest() : inspectStream
            ? await InspectArchiveFromStandardInputAsync(deadline.Token) : await InspectArchiveFromEnvironmentAsync(deadline.Token);
        if (inspectArchive)
            Console.WriteLine(JsonSerializer.Serialize(new { schemaVersion = 1, attempts = inspected.Attempts, pendingAttempts = inspected.PendingAttempts }));
        else if (inspectStream)
            Console.WriteLine(JsonSerializer.Serialize(new { schemaVersion = 1, attempts = inspected.Attempts, pendingAttempts = inspected.PendingAttempts, contentHash = inspected.ContentHash }));
        else
        {
            var restoreJournal = new MigrationEvidenceJournal(MigrationEvidenceOptions.FromEnvironment());
            var options = new DbContextOptionsBuilder<WebApiDbContext>().UseNpgsql(DatabaseSettings.ConnectionString());
            await using var db = new WebApiDbContext(options.Options);
            var verified = await MigrationRestoreVerifier.VerifyAsync(db, restoreJournal, inspected, deadline.Token);
            Console.WriteLine(JsonSerializer.Serialize(new { schemaVersion = 1, attempts = verified.Attempts,
                committedAttempts = verified.CommittedAttempts, contentHash = verified.ContentHash, continuity = "Unknown" }));
        }
        return;
    }
    var sqlMode = args.Contains("--run-sql", StringComparer.Ordinal);
    var bootstrapRequested = args.Contains("--bootstrap", StringComparer.Ordinal);
    var seedRequested = args.Contains("--seed-catalog", StringComparer.Ordinal);
    if (sqlMode && (bootstrapRequested || seedRequested))
        throw new InvalidOperationException("migration_sql_post_actions_not_supported");
    var journal = new MigrationEvidenceJournal(MigrationEvidenceOptions.FromEnvironment());
    MigrationAttemptResult result;
    if (sqlMode)
    {
        var budget = MigrationSqlBudget.FromEnvironment();
        result = await MigrationSqlAttemptCoordinator.RunAsync(DatabaseSettings.ConnectionString(), budget, journal,
            Environment.GetEnvironmentVariable("WEBAPI_MIGRATION_SQL_FILE") ?? "",
            Environment.GetEnvironmentVariable("WEBAPI_MIGRATION_SQL_SHA256") ?? "");
    }
    else
    {
        var budget = MigrationBudget.FromEnvironment();
        var options = new DbContextOptionsBuilder<WebApiDbContext>().UseNpgsql(DatabaseSettings.ConnectionString());
        GovernanceMigrationRunner.Configure(options);
        await using var db = new WebApiDbContext(options.Options);
        Func<CancellationToken, Task>? bootstrap = bootstrapRequested
            ? ct => BootstrapAccounts.RunAsync(db, Environment.GetEnvironmentVariable("WEBAPI_BOOTSTRAP_USERNAME") ?? "",
                Environment.GetEnvironmentVariable("WEBAPI_BOOTSTRAP_PASSWORD_FILE") ?? "", ct)
            : null;
        Func<CancellationToken, Task>? seed = seedRequested ? ct => PermissionCatalog.SeedAsync(db, ct) : null;
        result = await MigrationAttemptCoordinator.RunWithPostActionsAsync(db, budget, journal, bootstrap, seed);
    }
    Console.WriteLine($"Migration attempt {result.AttemptId:D}: {result.Outcome}; reason={result.Reason}; budgetCause={result.BudgetCause?.ToString() ?? "Unspecified"}; bootstrap={result.BootstrapOutcome?.ToString() ?? "NotRequested"}; seed={result.SeedOutcome?.ToString() ?? "NotRequested"}.");
    if (result.Outcome != MigrationEvidenceOutcome.Committed ||
        bootstrapRequested && result.BootstrapOutcome != MigrationEvidenceOutcome.Completed ||
        seedRequested && result.SeedOutcome != MigrationEvidenceOutcome.Completed)
        Environment.ExitCode = result.Outcome == MigrationEvidenceOutcome.Failed ? 2 : 3;
}
catch (Exception)
{
    // Configuration and provider errors can contain connection details or secrets.
    Console.Error.WriteLine("migration_start_failed: configuration or runtime error");
    Environment.ExitCode = 2;
}
