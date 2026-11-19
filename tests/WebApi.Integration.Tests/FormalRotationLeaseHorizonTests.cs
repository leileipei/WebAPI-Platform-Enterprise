using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Security;
using WebApi.Domain.CredentialSecurity;
using WebApi.Infrastructure.CredentialSecurity;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;

namespace WebApi.Integration.Tests;

public sealed class FormalRotationLeaseHorizonTests(MigratedDatabaseTemplateFixture template) : IClassFixture<MigratedDatabaseTemplateFixture>
{
    private static readonly DateTimeOffset Now = new(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Deadline = Now.AddSeconds(20);

    [Fact]
    public async Task OnlyAnIndependentCompleteReportCanConfirmAnEmptyHorizon()
    {
        await using var database = await template.Template.CreateDatabaseAsync();
        var scope = await SeedAsync(database);
        var evidence = Evidence(scope);
        await AssertRejectedAsync("formal_rotation_evidence_unavailable", () => VerifyAsync(database, scope, null));
        await AssertRejectedAsync("formal_rotation_evidence_unavailable", () => VerifyAsync(database, scope, new(null)));
        Assert.Equal(evidence, await VerifyAsync(database, scope, new(evidence)));
    }

    [Theory]
    [InlineData("incomplete")]
    [InlineData("pending")]
    [InlineData("authority")]
    [InlineData("position")]
    [InlineData("ledger-head")]
    [InlineData("proof-digest")]
    [InlineData("reversed-clock")]
    [InlineData("out-of-range-database-clock")]
    [InlineData("excess-clock-uncertainty")]
    [InlineData("non-utc-clock")]
    [InlineData("stale-clock")]
    [InlineData("short-clock-validity")]
    [InlineData("excess-clock-validity")]
    [InlineData("non-utc-horizon")]
    public async Task UnknownOrInconsistentEvidenceCannotQualifyThePreview(string fault)
    {
        await using var database = await template.Template.CreateDatabaseAsync();
        var scope = await SeedAsync(database);
        var evidence = Evidence(scope);
        evidence = fault switch
        {
            "incomplete" => evidence with { Complete = false },
            "pending" => evidence with { HasPendingWrite = true },
            "authority" => evidence with { Scope = scope with { Journal = scope.Journal with { AuthorityId = Guid.NewGuid() } } },
            "position" => evidence with { Scope = scope with { Position = new(1, new string('c', 64)) } },
            "ledger-head" => evidence with { Scope = scope with { HeadHash = new string('c', 64) } },
            "proof-digest" => evidence with { ProofHash = new string('A', 64) },
            "reversed-clock" => evidence with { Lower = evidence.Upper.AddTicks(1) },
            "out-of-range-database-clock" => evidence with { Lower = Now.AddTicks(1) },
            "excess-clock-uncertainty" => evidence with { Lower = Now.AddSeconds(-5) },
            "non-utc-clock" => evidence with { Upper = evidence.Upper.ToOffset(TimeSpan.FromHours(1)) },
            "stale-clock" => evidence with { ValidUntil = Now },
            "short-clock-validity" => evidence with { ValidUntil = evidence.Upper.AddSeconds(5) },
            "excess-clock-validity" => evidence with { ValidUntil = Now.AddMinutes(1) },
            "non-utc-horizon" => evidence with { LatestAllowExpiry = Now.AddSeconds(2).ToOffset(TimeSpan.FromHours(1)) },
            _ => throw new InvalidOperationException("Unknown fixture fault.")
        };

        await AssertRejectedAsync("formal_rotation_evidence_unavailable", () => VerifyAsync(database, scope, new(evidence)));
    }

    [Fact]
    public async Task RequiresAnExclusiveWriterFenceAndExactLocalLedgerBeforeConsultingTheWitness()
    {
        await using var database = await template.Template.CreateDatabaseAsync();
        var scope = await SeedAsync(database);
        var witness = new Witness(Evidence(scope));
        await AssertRejectedAsync("formal_rotation_evidence_unavailable", () => VerifyAsync(database, scope, witness, sharedLock: true));
        await AssertRejectedAsync("formal_rotation_evidence_unavailable", () => VerifyAsync(database, scope with { Epoch = 1 }, witness));
        Assert.Equal(0, witness.Requests);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(8)]
    public async Task AReplacementDoesNotRemoveAGatewayInstancesKnownLeaseHorizon(int? reportedExpirySeconds)
    {
        await using var database = await template.Template.CreateDatabaseAsync();
        var expiry = Now.AddSeconds(9);
        var scope = await SeedAsync(database, expiry, trackedExpiry: Now.AddSeconds(4));
        var witness = new Witness(Evidence(scope) with { LatestAllowExpiry = reportedExpirySeconds is int seconds ? Now.AddSeconds(seconds) : null });

        await AssertRejectedAsync("formal_rotation_evidence_unavailable", () => VerifyAsync(database, scope, witness));
        witness.Evidence = Evidence(scope) with { LatestAllowExpiry = expiry };
        Assert.Equal(witness.Evidence, await VerifyAsync(database, scope, witness));
    }

    [Theory]
    [InlineData(-2, false)]
    [InlineData(-3, true)]
    public async Task SkewIsCountedFromTheIndependentLowerTimeBound(int expirySeconds, bool expired)
    {
        await using var database = await template.Template.CreateDatabaseAsync();
        var scope = await SeedAsync(database, Now.AddSeconds(expirySeconds));
        var witness = new Witness(Evidence(scope));
        if (expired) Assert.Equal(witness.Evidence, await VerifyAsync(database, scope, witness));
        else await AssertRejectedAsync("formal_rotation_evidence_unavailable", () => VerifyAsync(database, scope, witness));
    }

    [Fact]
    public async Task CompleteEvidenceStillRejectsADeadlineThatCannotAbsorbLeaseSkewOrPrepareBudget()
    {
        await using var database = await template.Template.CreateDatabaseAsync();
        var scope = await SeedAsync(database);
        var cutoff = Deadline.AddSeconds(-2);
        var witness = new Witness(Evidence(scope) with { LatestAllowExpiry = cutoff });
        Assert.Equal(witness.Evidence, await VerifyAsync(database, scope, witness));
        witness.Evidence = Evidence(scope) with { LatestAllowExpiry = cutoff.AddTicks(1) };
        await AssertRejectedAsync("formal_rotation_deadline_unavailable", () => VerifyAsync(database, scope, witness));

        witness.Evidence = Evidence(scope);
        // Upper is Now + 1s. Subtracting skew leaves precisely the 5s delivery budget.
        await AssertRejectedAsync("formal_rotation_deadline_unavailable", () => VerifyAsync(database, scope, witness, Now.AddSeconds(8)));
    }

    private static async Task AssertRejectedAsync(string code, Func<Task> action)
    {
        var exception = await Assert.ThrowsAsync<ApiException>(action);
        Assert.Equal(code, exception.Code);
        Assert.Equal(code == "formal_rotation_evidence_unavailable" ? 503 : 409, exception.Status);
    }

    private static FormalRotationLeaseHorizonEvidence Evidence(FormalRotationLeaseScope scope) =>
        new(scope, new string('b', 64), true, false, Now.AddSeconds(-1), Now.AddSeconds(1), Now.AddSeconds(15), null);

    private static async Task<FormalRotationLeaseHorizonEvidence> VerifyAsync(PostgresDatabase database, FormalRotationLeaseScope scope,
        Witness? witness, DateTimeOffset? deadline = null, bool sharedLock = false)
    {
        await using var db = database.Context();
        await using var tx = await db.Database.BeginTransactionAsync();
        if (sharedLock) await db.Database.ExecuteSqlRawAsync("SELECT pg_catalog.pg_advisory_xact_lock_shared(8901202)");
        else await db.Database.ExecuteSqlRawAsync("SELECT pg_catalog.pg_advisory_xact_lock(8901202)");
        return await FormalRotationLeaseHorizon.RequireAsync(db, scope, witness, Now, deadline ?? Deadline, default);
    }

    private static async Task<FormalRotationLeaseScope> SeedAsync(PostgresDatabase database, DateTimeOffset? expiry = null,
        DateTimeOffset? trackedExpiry = null)
    {
        await using var db = database.Context();
        var org = new Organization { Code = "ROTATION-ORG", Name = "Isolated rotation" };
        var project = new Project { OrganizationId = org.Id, Code = "ROTATION-PROJ", Name = "Isolated rotation" };
        var environment = new EnvironmentRecord { ProjectId = project.Id, Code = "ROTATION", Name = "Isolated rotation" };
        var state = new EnvironmentSecurityState { EnvironmentId = environment.Id, ProjectId = project.Id, OrganizationId = org.Id, UpdatedAt = Now };
        db.AddRange(org, project, environment, new EnvironmentSecurityInitialization { EnvironmentId = environment.Id,
            ProjectId = project.Id, OrganizationId = org.Id, State = "FreshNonStrict", FreshProofId = Guid.NewGuid() });
        await db.SaveChangesAsync();
        db.Add(state);
        if (expiry is DateTimeOffset until)
        {
            var node = new GatewayNode { EnvironmentId = environment.Id, NodeName = "retired-instance", InstanceId = Guid.NewGuid().ToString(),
                IdentityHash = new string('a', 64), Enabled = false };
            var previousInstance = Guid.NewGuid();
            var payload = new SecurityNodeLeasePayload(Guid.NewGuid(), SecuritySignedEncoding.Protocol, environment.Id, state.SecurityGeneration,
                0, new string('d', 64), node.Id, previousInstance, SecuritySignedEncoding.Audience, Now.AddSeconds(-10), until, "rotation-fixture");
            // A mutable row may have regressed without deleting the older immutable issuance.
            var tracked = trackedExpiry is DateTimeOffset shorter
                ? payload with { LeaseId = Guid.NewGuid(), IssuedAt = payload.IssuedAt.AddSeconds(1), FreshUntil = shorter }
                : payload;
            db.AddRange(node, new GatewaySecurityLease { LeaseId = tracked.LeaseId, NodeId = node.Id, InstanceId = previousInstance,
                EnvironmentId = environment.Id, ProjectId = project.Id, OrganizationId = org.Id, SecurityGeneration = state.SecurityGeneration,
                CheckpointHash = tracked.CheckpointHash, IssuedAt = tracked.IssuedAt, FreshUntil = tracked.FreshUntil,
                LastIssuedLeaseUntil = tracked.FreshUntil, SigningKeyId = tracked.KeyId }, SecurityLeaseIssuanceEvidence.Create(payload, org.Id, project.Id));
            if (tracked != payload) db.Add(SecurityLeaseIssuanceEvidence.Create(tracked, org.Id, project.Id));
        }
        await db.SaveChangesAsync();
        return new(new(Guid.NewGuid(), Guid.NewGuid()), GovernanceJournalProtocol.Genesis, org.Id, project.Id, environment.Id,
            state.SecurityGeneration, state.Epoch, state.HeadHash);
    }

    private sealed class Witness(FormalRotationLeaseHorizonEvidence? evidence) : IFormalRotationLeaseHorizonWitness
    {
        public FormalRotationLeaseHorizonEvidence? Evidence { get; set; } = evidence;
        public int Requests { get; private set; }

        public FormalRotationLeaseHorizonEvidence? GetVerifiedHorizon(FormalRotationLeaseScope scope)
        {
            Requests++;
            return Evidence;
        }
    }
}
