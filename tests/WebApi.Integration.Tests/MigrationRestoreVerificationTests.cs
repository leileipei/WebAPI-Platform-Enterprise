using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using WebApi.Infrastructure.Persistence;
using WebApi.Integration.Tests.Support;
using Xunit;

namespace WebApi.Integration.Tests;

[SupportedOSPlatform("linux")]
[Collection("MigrationIsolation")]
public sealed class MigrationRestoreVerificationTests
{
    private static readonly MigrationBudget Budget = new(TimeSpan.FromMinutes(15), 65536, 16384, 100000, 67108864);

    [Fact]
    public async Task RestoreCannotTreatAnUnknownReceiptIdentityAsVerified()
    {
        var database = new PostgresDatabase(retain: true);await database.InitializeAsync();
        var root = Environment.GetEnvironmentVariable("WEBAPI_SOURCE_ROOT") ?? throw new InvalidOperationException("Isolated source root required.");
        var directory = Path.Combine(root, ".superpowers", "sdd", "2026-11-04-migration-budget-trace", "journal-fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Console.WriteLine("isolated-unknown-identity-evidence-retained=" + directory);
        var journal = new MigrationEvidenceJournal(new MigrationEvidenceOptions(directory, 8192, 16777216, 1048576));
        var id = Guid.NewGuid();
        try
        {
            await journal.StartAsync(new MigrationAttemptStart(id, MigrationReceiptEntry.Managed, DateTimeOffset.UtcNow, Budget), CancellationToken.None);
            await using var db = database.Context();
            await db.Database.MigrateAsync();
            await using (var transaction = await db.Database.BeginTransactionAsync())
            {
                var receipt = new MigrationReceipt(id, MigrationReceiptEntry.Managed, null, "20261104090000_Governance24MigrationReceipts",
                    null, null, new string('a', 64), MigrationReceiptValidation.Unknown, null, null, null,
                    Budget, null, null, DateTimeOffset.UtcNow, 100000);
                await MigrationReceiptStore.InsertAsync(db, receipt, CancellationToken.None);
                await transaction.CommitAsync();
            }
            await journal.AppendAsync(id, new MigrationEvidenceRecord(MigrationEvidencePhase.Migration,
                MigrationEvidenceOutcome.Committed, MigrationEvidenceReason.None, DateTimeOffset.UtcNow), CancellationToken.None);
            var contents = await journal.InspectDirectoryAsync(CancellationToken.None);
            var archive = new MigrationEvidenceArchiveInspection(contents.Attempts, contents.PendingAttempts, contents.ContentHash);
            await Assert.ThrowsAsync<InvalidOperationException>(() => MigrationRestoreVerifier.VerifyAsync(db, journal, archive, CancellationToken.None));
            Assert.Equal(1, await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM public.migration_validation_receipts").SingleAsync());
        }
        finally { NpgsqlConnection.ClearAllPools();Console.WriteLine("isolated-unknown-identity-db-and-evidence-retained"); }
    }

    [Fact]
    public async Task RestoredJournalAndDatabaseMustMatchTheVerifiedArchive()
    {
        var original = new PostgresDatabase(retain: true);
        var missing = new PostgresDatabase(retain: true);
        await original.InitializeAsync();await missing.InitializeAsync();
        var root = Environment.GetEnvironmentVariable("WEBAPI_SOURCE_ROOT") ?? throw new InvalidOperationException("Isolated source root required.");
        var directory = Path.Combine(root, ".superpowers", "sdd", "2026-11-04-migration-budget-trace", "journal-fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Console.WriteLine("isolated-restore-evidence-retained=" + directory);
        var journal = new MigrationEvidenceJournal(new MigrationEvidenceOptions(directory, 8192, 16777216, 1048576));
        WebApiDbContext Context(PostgresDatabase database)
        {
            var options = new DbContextOptionsBuilder<WebApiDbContext>().UseNpgsql(database.ConnectionString);
            GovernanceMigrationRunner.Configure(options);return new WebApiDbContext(options.Options);
        }
        try
        {
            await using (var initial = Context(original))
                Assert.Equal(MigrationEvidenceOutcome.Committed, (await MigrationAttemptCoordinator.RunAsync(initial, Budget, journal)).Outcome);
            NpgsqlConnection.ClearAllPools();
            var archive = directory + "-restore-check.tar";
            var start = new ProcessStartInfo("tar") { UseShellExecute = false, RedirectStandardError = true };
            foreach (var arg in new[] { "--numeric-owner", "-cf", archive, "-C", directory, "." }) start.ArgumentList.Add(arg);
            using (var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot create isolated archive."))
            {
                var error = await process.StandardError.ReadToEndAsync();await process.WaitForExitAsync();Assert.True(process.ExitCode == 0, error);
            }
            Console.WriteLine("isolated-restore-archive-retained=" + archive);
            MigrationEvidenceArchiveInspection inspected;
            await using (var file = File.OpenRead(archive)) inspected = await MigrationEvidenceJournal.InspectArchiveAsync(file, CancellationToken.None);
            await using (var matching = Context(original))
            {
                var result = await MigrationRestoreVerifier.VerifyAsync(matching, journal, inspected, CancellationToken.None);
                Assert.Equal(1, result.CommittedAttempts);
                Assert.Equal(inspected.ContentHash, result.ContentHash);
            }
            await using (var different = Context(missing))
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => MigrationRestoreVerifier.VerifyAsync(different, journal, inspected, CancellationToken.None));
                Assert.True(await different.Database.SqlQueryRaw<bool>("SELECT pg_catalog.to_regclass('public.\"__EFMigrationsHistory\"') IS NULL AS \"Value\"").SingleAsync());
            }
            await using (var matching = Context(original))
                await Assert.ThrowsAsync<InvalidOperationException>(() => MigrationRestoreVerifier.VerifyAsync(matching, journal, inspected with { ContentHash = new string('a', 64) }, CancellationToken.None));

            async Task<(int Exit, string Output)> VerifyCommandAsync(PostgresDatabase database, string expectedHash, bool digestOnly = false)
            {
                var settings = new NpgsqlConnectionStringBuilder(database.ConnectionString);
                var command = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                command.ArgumentList.Add(Environment.GetEnvironmentVariable("WEBAPI_MIGRATOR_DLL") ?? throw new InvalidOperationException("Isolated migrator required."));
                command.ArgumentList.Add(digestOnly ? "--verify-evidence-restore-digest" : "--verify-evidence-restore");
                command.Environment.Clear();
                command.Environment["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "";
                command.Environment["DOTNET_ROOT"] = Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? "/usr/share/dotnet";
                command.Environment["WEBAPI_DB_HOST"] = settings.Host!;
                command.Environment["WEBAPI_DB_NAME"] = settings.Database!;
                command.Environment["WEBAPI_DB_USER"] = settings.Username!;
                command.Environment["WEBAPI_DB_PASSWORD_FILE"] = "/run/secrets/postgres-password";
                if (digestOnly)
                {
                    command.Environment["WEBAPI_MIGRATION_ARCHIVE_CONTENT_SHA256"] = expectedHash;
                    command.Environment["WEBAPI_MIGRATION_ARCHIVE_ATTEMPTS"] = inspected.Attempts.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    command.Environment["WEBAPI_MIGRATION_ARCHIVE_PENDING"] = inspected.PendingAttempts.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
                else
                {
                    command.Environment["WEBAPI_MIGRATION_ARCHIVE_FILE"] = archive;
                    command.Environment["WEBAPI_MIGRATION_ARCHIVE_SHA256"] = expectedHash;
                }
                command.Environment["WEBAPI_MIGRATION_EVIDENCE_DIR"] = directory;
                command.Environment["WEBAPI_MIGRATION_EVIDENCE_RECORD_MAX_BYTES"] = "8192";
                command.Environment["WEBAPI_MIGRATION_EVIDENCE_QUOTA_BYTES"] = "16777216";
                command.Environment["WEBAPI_MIGRATION_EVIDENCE_MIN_FREE_BYTES"] = "1048576";
                using var child = Process.Start(command) ?? throw new InvalidOperationException("Cannot verify isolated restore.");
                var stdout = child.StandardOutput.ReadToEndAsync();
                var stderr = child.StandardError.ReadToEndAsync();
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
                try { await child.WaitForExitAsync(deadline.Token); }
                catch (OperationCanceledException)
                {
                    if (!child.HasExited) child.Kill(entireProcessTree: true);
                    await child.WaitForExitAsync();
                    throw;
                }
                return (child.ExitCode, (await stdout) + (await stderr));
            }
            var archiveHash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(archive)));
            var correct = await VerifyCommandAsync(original, archiveHash);
            Assert.Equal(0, correct.Exit);
            using (var json = JsonDocument.Parse(correct.Output))
            {
                Assert.Equal(1, json.RootElement.GetProperty("schemaVersion").GetInt32());
                Assert.Equal(1, json.RootElement.GetProperty("committedAttempts").GetInt32());
                Assert.Equal(inspected.ContentHash, json.RootElement.GetProperty("contentHash").GetString());
                Assert.Equal("Unknown", json.RootElement.GetProperty("continuity").GetString());
            }
            Assert.NotEqual(0, (await VerifyCommandAsync(missing, archiveHash)).Exit);
            Assert.NotEqual(0, (await VerifyCommandAsync(original, new string(archiveHash[0] == '0' ? 'f' : '0', 64))).Exit);
            var digestResult = await VerifyCommandAsync(original, inspected.ContentHash, digestOnly: true);
            Assert.Equal(0, digestResult.Exit);
            Assert.Contains("\"continuity\":\"Unknown\"", digestResult.Output);
            Assert.NotEqual(0, (await VerifyCommandAsync(missing, inspected.ContentHash, digestOnly: true)).Exit);
            Assert.NotEqual(0, (await VerifyCommandAsync(original, new string('0', 64), digestOnly: true)).Exit);
            await using (var different = Context(missing))
                Assert.True(await different.Database.SqlQueryRaw<bool>("SELECT pg_catalog.to_regclass('public.\"__EFMigrationsHistory\"') IS NULL AS \"Value\"").SingleAsync());
        }
        finally { NpgsqlConnection.ClearAllPools();Console.WriteLine("isolated-restore-databases-and-evidence-retained"); }
    }
}
