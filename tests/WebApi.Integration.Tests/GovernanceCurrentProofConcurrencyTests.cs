using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using WebApi.Contracts.Common;
using WebApi.Contracts.Gateway;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.CredentialSecurity;
using WebApi.Infrastructure.Gateway;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;

namespace WebApi.Integration.Tests;

public sealed class GovernanceCurrentProofConcurrencyTests
{
    [Fact]
    public async Task ProofWaitingBehindLedgerChangeMustReadTheCommittedAuthority()
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        await using var setup = database.Context();
        await setup.Database.MigrateAsync();
        var organization = new Organization { Code = "RACE-ORG", Name = "Organization" };
        var project = new Project { OrganizationId = organization.Id, Code = "RACE-PRJ", Name = "Project" };
        var environment = new EnvironmentRecord { ProjectId = project.Id, Code = "TEST", Name = "Test" };
        var user = new UserRecord { Username = "race-fixture", DisplayName = "Race fixture" };
        setup.AddRange(organization, project, environment, user, new EnvironmentSecurityInitialization { EnvironmentId = environment.Id,
            ProjectId = project.Id, OrganizationId = organization.Id, State = "FreshNonStrict", FreshProofId = Guid.NewGuid() });
        await setup.SaveChangesAsync();
        const string secret = "test-only-enrollment-secret-0123456789abcdef";
        var enrollments = new NodeEnrollmentSettings([new NodeEnrollment(environment.Id, "first", NodeIdentityService.Hash(secret))]);
        var identities = new NodeIdentityService(setup, enrollments);
        var instance = Guid.NewGuid();
        var registered = await new NodeRegistry(setup, identities).RegisterAsync(new RegisterNodeRequest(environment.Id, "first", instance, "test",
            ["2.0", "2.1", "2.2", "2.3", "2.4"], ["security-ledger-v1"]), secret);
        var identity = new NodeIdentity(registered.NodeId, environment.Id, NodeIdentityService.Hash(secret));
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var keyFile = Path.Combine(Path.GetTempPath(), "gov24-race-signing-" + Guid.NewGuid().ToString("N"));
        try
        {
            await File.WriteAllTextAsync(keyFile, key.ExportPkcs8PrivateKeyPem());
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(keyFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var settings = new SecuritySigningSettings("test-key", keyFile,
                new Dictionary<string, string> { ["test-key"] = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) });
            // Test material is deliberately produced by the untrusted isolated
            // algorithm; the production issuer remains unavailable.
            var material = await new UntrustedGenesisLeaseMaterialBuilder(setup, identities, new SecurityLedgerReader(setup), settings).IssueAsync(identity, instance);

            await using var writer = database.Context();
            await using var transaction = await writer.Database.BeginTransactionAsync();
            await writer.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(8901202)");
            await writer.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM environments WHERE id={environment.Id} FOR NO KEY UPDATE");
            var state = await writer.Set<EnvironmentSecurityState>().SingleAsync(x => x.EnvironmentId == environment.Id);
            var initialization = await writer.Set<EnvironmentSecurityInitialization>().SingleAsync(x => x.EnvironmentId == environment.Id);
            var at = await writer.Database.SqlQueryRaw<DateTimeOffset>("SELECT clock_timestamp() AS \"Value\"").SingleAsync();
            var operation = new SecurityOperation { OrganizationId = organization.Id, Kind = "EnvironmentPolicyCutover", RequestedBy = user.Id,
                Reason = "isolated concurrency fixture", CommittedAt = at };
            var change = new SecurityEvent { EnvironmentId = environment.Id, OrganizationId = organization.Id, ProjectId = project.Id,
                SecurityGeneration = state.SecurityGeneration, Epoch = 1, OperationId = operation.Id, EventType = "EnvironmentPolicyChanged",
                PolicyJson = "{\"requireEnvironmentCredentials\":false,\"minimumSnapshotSchema\":\"2.4\"}", OccurredAt = at, PreviousHash = state.HeadHash };
            change.EventHash = SecurityEventHash.Compute(change);
            state.Epoch = 1; state.HeadHash = change.EventHash; state.MinimumSnapshotSchema = "2.4"; state.Revision++; state.UpdatedAt = at;
            initialization.State = "Governed"; initialization.Revision++;
            writer.Add(operation); writer.Add(change);
            await writer.SaveChangesAsync();

            var applicationName = "gov24-proof-race-" + Guid.NewGuid().ToString("N");
            var connectionString = new NpgsqlConnectionStringBuilder(database.ConnectionString) { ApplicationName = applicationName }.ConnectionString;
            await using var proofDb = new WebApiDbContext(new DbContextOptionsBuilder<WebApiDbContext>().UseNpgsql(connectionString).Options);
            var proofIdentities = new NodeIdentityService(proofDb, enrollments);
            var proof = new CurrentSecurityProofService(proofDb, proofIdentities, new SecurityLedgerReader(proofDb), settings);
            var proofTask = proof.RecordAsync(registered.NodeId, new CurrentSecurityProof(instance, material.Checkpoint, material.Lease,
                DateTimeOffset.UtcNow, true, true), identity);
            await using var observer = await database.OpenAsync();
            var waiting = false;
            for (var i = 0; i < 250 && !waiting; i++)
            {
                await using var query = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE application_name=@app AND wait_event_type='Lock')", observer);
                query.Parameters.AddWithValue("app", applicationName);
                waiting = (bool)(await query.ExecuteScalarAsync())!;
                if (!waiting) await Task.Delay(20);
            }
            Assert.True(waiting, "The proof request must actually wait behind the ledger transaction.");
            await transaction.CommitAsync();
            var error = await Assert.ThrowsAsync<ApiException>(() => proofTask.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(409, error.Status);
            await using var check = database.Context();
            Assert.Empty(await check.Set<GatewaySecurityCurrentReceipt>().AsNoTracking().ToArrayAsync());
        }
        finally { File.Delete(keyFile); }
    }
}
