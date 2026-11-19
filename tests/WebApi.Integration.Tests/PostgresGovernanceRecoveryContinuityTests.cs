using System.Data;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using WebApi.Contracts.Applications;
using WebApi.Contracts.Common;
using WebApi.Domain.CredentialSecurity;
using WebApi.Infrastructure.CredentialSecurity;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;

namespace WebApi.Integration.Tests;

public sealed class PostgresGovernanceRecoveryContinuityTests(MigratedDatabaseTemplateFixture template)
    : IClassFixture<MigratedDatabaseTemplateFixture>
{
    private static readonly DateTimeOffset At = new(2026, 11, 19, 0, 0, 0, TimeSpan.Zero);
    private const string GenesisHead = "4f53cda18c2baa0c0354bb5f9a3ecbe5ed12ab4d8e11ba873c2f11161202b945";

    [Fact]
    public async Task UsesTheCallerTransactionAndKeepsItsExclusiveWriterFence()
    {
        await using var database = await template.Template.CreateDatabaseAsync();
        var fixture = await SeedAsync(database);
        await using var db = database.Context();
        await using var tx = await db.Database.BeginTransactionAsync();
        await TakeFenceAsync(db);
        var changed = await AdvancePolicyAsync(db, fixture);
        using var history = new HistoryAuthority(fixture.History with { Checkpoint = changed });
        var service = new PostgresGovernanceRecoveryContinuity(fixture.Journal, new JournalAuthority(), history.Client);

        var verified = await service.RequireAsync(db, fixture.EnvironmentId);

        Assert.Same(tx, db.Database.CurrentTransaction);
        Assert.Equal(fixture.Journal, verified.Journal);
        Assert.Equal(GovernanceJournalProtocol.Genesis, verified.Position);
        Assert.Equal(changed.HeadHash, verified.Ledger.HeadHash);
        Assert.Equal(1, verified.Ledger.Epoch);
        await using var other = await database.OpenAsync();
        await using var probe = new NpgsqlCommand("SELECT pg_catalog.pg_try_advisory_xact_lock(8901202)", other);
        Assert.False(Assert.IsType<bool>(await probe.ExecuteScalarAsync()));
    }

    [Theory]
    [InlineData("shared")]
    [InlineData("different-session")]
    [InlineData("old-snapshot")]
    public async Task RejectsAContextThatCannotProveACurrentExclusiveTransaction(string fault)
    {
        await using var database = await template.Template.CreateDatabaseAsync();
        var fixture = await SeedAsync(database);
        using var history = new HistoryAuthority(fixture.History);
        var service = new PostgresGovernanceRecoveryContinuity(fixture.Journal, new JournalAuthority(), history.Client);
        await using var db = database.Context();
        await using var tx = await db.Database.BeginTransactionAsync(fault == "old-snapshot" ? IsolationLevel.RepeatableRead : IsolationLevel.ReadCommitted);
        await using var other = database.Context();
        await using var otherTx = await other.Database.BeginTransactionAsync();
        if (fault == "shared") await db.Database.ExecuteSqlRawAsync("SELECT pg_catalog.pg_advisory_xact_lock_shared(8901202)");
        else await TakeFenceAsync(fault == "different-session" ? other : db);

        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await AssertUnavailableAsync(() => service.RequireAsync(db, fixture.EnvironmentId, budget.Token));
        Assert.Equal(0, history.Requests);
    }

    [Fact]
    public async Task AnUnchangedJournalCannotQualifyARestoredLegacyEnvironmentBehindIndependentHistory()
    {
        await using var current = await template.Template.CreateDatabaseAsync();
        var fixture = await SeedAsync(current, legacy: true);
        await using var pool = new NpgsqlConnection(current.ConnectionString);
        NpgsqlConnection.ClearPool(pool);
        await using var restored = new PostgresDatabase();
        await restored.InitializeFromTemplateAsync(current.Name);

        GovernanceEnvironmentHistoryCheckpoint advanced;
        await using (var db = current.Context())
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            await TakeFenceAsync(db);
            advanced = await AdvancePolicyAsync(db, fixture);
            await tx.CommitAsync();
        }
        using var history = new HistoryAuthority(fixture.History with { Checkpoint = advanced });
        var service = new PostgresGovernanceRecoveryContinuity(fixture.Journal, new JournalAuthority(), history.Client);

        Assert.Equal(1, (await RequireAsync(current, fixture, service)).Ledger.Epoch);
        await AssertUnavailableAsync(() => RequireAsync(restored, fixture, service));

        history.Current = fixture.History;
        await AssertUnavailableAsync(() => RequireAsync(current, fixture, service));
    }

    [Theory]
    [InlineData("foreign-environment")]
    [InlineData("foreign-organization")]
    [InlineData("foreign-project")]
    [InlineData("foreign-security-generation")]
    [InlineData("foreign-journal-generation")]
    [InlineData("foreign-origin")]
    [InlineData("different-head")]
    [InlineData("different-policy")]
    [InlineData("unknown-environment")]
    [InlineData("pending-environment")]
    [InlineData("pending-journal")]
    [InlineData("restored-journal")]
    [InlineData("missing-root")]
    public async Task RejectsHistoryWithoutTheExactEnrolledEnvironmentAndConfirmedJournal(string fault)
    {
        await using var database = await template.Template.CreateDatabaseAsync();
        var fixture = await SeedAsync(database, installJournal: fault != "missing-root");
        var enrolled = fixture.History;
        var checkpoint = enrolled.Checkpoint!;
        var reported = fault switch
        {
            "foreign-environment" => enrolled with { EnvironmentId = Guid.NewGuid() },
            "foreign-organization" => enrolled with { Checkpoint = checkpoint with { OrganizationId = Guid.NewGuid() } },
            "foreign-project" => enrolled with { Checkpoint = checkpoint with { ProjectId = Guid.NewGuid() } },
            "foreign-security-generation" => enrolled with { Checkpoint = checkpoint with { SecurityGeneration = Guid.NewGuid() } },
            "foreign-journal-generation" => enrolled with { Journal = enrolled.Journal with { Generation = Guid.NewGuid() } },
            "foreign-origin" => enrolled with { Checkpoint = checkpoint with { InitializationHash = new string('c', 64) } },
            "different-head" => enrolled with { Checkpoint = checkpoint with { HeadHash = new string('d', 64) } },
            "different-policy" => enrolled with { Checkpoint = checkpoint with { RequireEnvironmentCredentials = true } },
            "unknown-environment" => enrolled with { State = "Unknown", Checkpoint = null },
            "pending-environment" => enrolled with { State = "Pending", Checkpoint = null },
            _ => enrolled
        };
        var intent = new GovernanceWriteIntent(Guid.NewGuid(), GovernanceJournalProtocol.Genesis, "YQ");
        var journal = new JournalAuthority
        {
            Current = fault switch
            {
                "pending-journal" => new(intent.Previous, intent, null),
                "restored-journal" => new(GovernanceJournalProtocol.NextPosition(fixture.Journal, intent), null, null),
                _ => new(intent.Previous, null, null)
            }
        };
        using var history = new HistoryAuthority(reported);
        var service = new PostgresGovernanceRecoveryContinuity(fixture.Journal, journal, history.Client);

        await AssertUnavailableAsync(() => RequireAsync(database, fixture, service));
    }

    [Fact]
    public async Task AuthenticatedEnvironmentHistoryCannotBeReplayedOnTheNextCheck()
    {
        await using var database = await template.Template.CreateDatabaseAsync();
        var fixture = await SeedAsync(database);
        using var history = new HistoryAuthority(fixture.History);
        var service = new PostgresGovernanceRecoveryContinuity(fixture.Journal, new JournalAuthority(), history.Client);
        Assert.Equal(GenesisHead, (await RequireAsync(database, fixture, service)).Ledger.HeadHash);

        history.ReplayFirstResponse = true;
        await AssertUnavailableAsync(() => RequireAsync(database, fixture, service));
        Assert.Equal(2, history.Requests);
    }

    [Theory]
    [InlineData("matching")]
    [InlineData("missing-older-effect")]
    [InlineData("local-pending")]
    [InlineData("orphaned-local-effect")]
    public async Task EveryRotationEffectMustAgreeWithTheConfirmedHistory(string fault)
    {
        await using var database = await template.Template.CreateDatabaseAsync();
        var fixture = await SeedAsync(database);
        var scope = fixture.History.Checkpoint!;
        await using var db = database.Context();
        await using var tx = await db.Database.BeginTransactionAsync();
        await TakeFenceAsync(db);
        var app = new ApplicationRecord { OrganizationId = scope.OrganizationId, ProjectId = scope.ProjectId,
            Code = "CONTINUITY-APP", Name = "Isolated application" };
        db.Add(app);
        await db.SaveChangesAsync();
        var first = await AddRotationAsync(db, fixture, app, GovernanceJournalProtocol.Genesis, fault == "missing-older-effect");
        var second = await AddRotationAsync(db, fixture, app, first, omitConfirmation: fault == "local-pending",
            omitJournal: fault == "orphaned-local-effect");
        var position = fault == "orphaned-local-effect" ? first : second;
        using var history = new HistoryAuthority(fixture.History with { Position = position });
        var service = new PostgresGovernanceRecoveryContinuity(fixture.Journal, new JournalAuthority { Current = new(position, null, null) }, history.Client);

        if (fault == "matching") Assert.Equal(position, (await service.RequireAsync(db, fixture.EnvironmentId)).Position);
        else await AssertUnavailableAsync(() => service.RequireAsync(db, fixture.EnvironmentId));
    }

    private static async Task<Fixture> SeedAsync(PostgresDatabase database, bool legacy = false, bool installJournal = true)
    {
        await using var db = database.Context();
        var journal = new GovernanceJournalId(Guid.NewGuid(), Guid.NewGuid());
        if (installJournal) await IsolatedGovernanceJournalSchema.InstallAsync(db, journal);
        var org = new Organization { Code = "CONTINUITY-ORG", Name = "Isolated continuity" };
        var project = new Project { OrganizationId = org.Id, Code = "CONTINUITY-PRJ", Name = "Isolated continuity" };
        var environment = new EnvironmentRecord { ProjectId = project.Id, Code = "CONTINUITY", Name = "Isolated continuity" };
        var actor = new UserRecord { Username = "continuity-fixture", DisplayName = "Isolated continuity" };
        var origin = new EnvironmentSecurityInitialization { EnvironmentId = environment.Id, ProjectId = project.Id,
            OrganizationId = org.Id, State = legacy ? "Unknown" : "FreshNonStrict", FreshProofId = legacy ? null : Guid.NewGuid(), UpdatedAt = At };
        db.AddRange(org, project, environment, actor, origin);
        await db.SaveChangesAsync();
        if (legacy)
        {
            origin.State = "VerifiedLegacyNetwork";
            origin.EvidenceHash = new string('a', 64);
            origin.ConfirmedBy = actor.Id;
            origin.ConfirmedAt = At;
            origin.ConfirmationReason = "Isolated legacy evidence";
            origin.Revision++;
            await db.SaveChangesAsync();
        }
        var state = new EnvironmentSecurityState { EnvironmentId = environment.Id, ProjectId = project.Id,
            OrganizationId = org.Id, UpdatedAt = At };
        db.Add(state);
        await db.SaveChangesAsync();
        var proof = new GovernanceEnvironmentOrigin(environment.Id, project.Id, org.Id, origin.State,
            origin.EvidenceHash, origin.FreshProofId, origin.ConfirmedBy, origin.ConfirmedAt, origin.ConfirmationReason, origin.Revision);
        var checkpoint = new GovernanceEnvironmentHistoryCheckpoint(org.Id, project.Id, state.SecurityGeneration, 0, GenesisHead,
            GovernanceEnvironmentHistoryProtocol.InitializationHash(proof), false, "2.0", false);
        return new(journal, environment.Id, actor.Id,
            new(journal, GovernanceJournalProtocol.Genesis, environment.Id, "Confirmed", checkpoint));
    }

    private static async Task<GovernanceEnvironmentHistoryCheckpoint> AdvancePolicyAsync(WebApiDbContext db, Fixture fixture)
    {
        var state = await db.Set<EnvironmentSecurityState>().SingleAsync(item => item.EnvironmentId == fixture.EnvironmentId);
        var operation = new SecurityOperation { Kind = "EnvironmentPolicyCutover", OrganizationId = state.OrganizationId,
            CommittedAt = At, RequestedBy = fixture.ActorId, Reason = "Isolated ledger change outside the commit journal" };
        var next = new SecurityEvent { EnvironmentId = state.EnvironmentId, OrganizationId = state.OrganizationId,
            ProjectId = state.ProjectId, SecurityGeneration = state.SecurityGeneration, Epoch = 1, OperationId = operation.Id,
            EventType = "EnvironmentPolicyChanged", PolicyJson = "{\"requireEnvironmentCredentials\":false,\"minimumSnapshotSchema\":\"2.4\"}",
            PreviousHash = GenesisHead, OccurredAt = At };
        next.EventHash = SecurityEventHash.Compute(next);
        state.Epoch = next.Epoch;
        state.HeadHash = next.EventHash;
        state.MinimumSnapshotSchema = "2.4";
        state.Revision++;
        db.AddRange(operation, next);
        await db.SaveChangesAsync();
        return fixture.History.Checkpoint! with { Epoch = 1, HeadHash = next.EventHash, MinimumSnapshotSchema = "2.4" };
    }

    private static async Task<GovernanceJournalPosition> AddRotationAsync(WebApiDbContext db, Fixture fixture, ApplicationRecord app,
        GovernanceJournalPosition previous, bool omitAudit = false, bool omitConfirmation = false, bool omitJournal = false)
    {
        var validUntil = At.AddHours(2);
        ApplicationCredential CreateCredential() => new() { ApplicationId = app.Id, OrganizationId = app.OrganizationId,
            ProjectId = app.ProjectId, EnvironmentId = fixture.EnvironmentId, BindingMode = "EnvironmentBound", ValidFrom = At,
            ExpiresAt = validUntil, AccessKey = Guid.NewGuid().ToString("N"), SecretHash = "isolated-credential-fixture", SecretLast4 = "test" };
        var old = CreateCredential();
        var replacement = CreateCredential();
        db.AddRange(old, replacement);
        await db.SaveChangesAsync();
        var window = new FormalRotationPreviewRequest(fixture.EnvironmentId, At, validUntil, At.AddMinutes(1), At.AddMinutes(2), replacement.Id);
        var targets = new[] { new FormalRotationPreviewTarget(Guid.NewGuid(), Guid.NewGuid()), new(Guid.NewGuid(), Guid.NewGuid()) }
            .OrderBy(item => item.NodeId).ToArray();
        var plan = new FormalRotationScheduleIntent(old.Id, app.Id, fixture.ActorId, app.OrganizationId, app.ProjectId!.Value,
            window, 1, At, "owner_request", new string('a', 64), new string('b', 64), targets);
        var intent = FormalRotationJournalIntent.Create(plan, Guid.NewGuid(), previous);
        var next = GovernanceJournalProtocol.NextPosition(fixture.Journal, intent);
        db.Add(new FormalCredentialRotation { Id = intent.OperationId, CredentialId = old.Id, ReplacementCredentialId = replacement.Id,
            EnvironmentId = fixture.EnvironmentId, HardDeadline = window.HardDeadline, RecordedAt = At, IntentHash = FormalRotationJournalIntent.Hash(intent) });
        if (!omitAudit)
            db.Add(new AuditLog { UserId = fixture.ActorId, OrganizationId = app.OrganizationId, ProjectId = app.ProjectId,
                EnvironmentId = fixture.EnvironmentId, ResourceType = "credential", ResourceId = old.Id.ToString(),
                Action = "credential.formal-rotation.confirm", CreatedAt = At,
                AfterJson = JsonSerializer.Serialize(new { RotationId = intent.OperationId, plan.Window, plan.Reason, plan.PreviewHash, plan.Targets }) });
        await db.SaveChangesAsync();
        if (!omitJournal)
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO credential_governance.journal_entries
                    (sequence, is_confirmation, operation_id, previous_hash, position_hash, recovery_material)
                SELECT {next.Sequence}, false, {intent.OperationId}, {previous.Hash}, {next.Hash}, {intent.RecoveryMaterial}
                UNION ALL
                SELECT {next.Sequence}, true, {intent.OperationId}, {previous.Hash}, {next.Hash}, NULL
                 WHERE {(!omitConfirmation)}
                ORDER BY 2
                """);
        return next;
    }

    private static Task TakeFenceAsync(WebApiDbContext db) =>
        db.Database.ExecuteSqlRawAsync("SELECT pg_catalog.pg_advisory_xact_lock(8901202)");

    private static async Task<GovernanceContinuity> RequireAsync(PostgresDatabase database, Fixture fixture, IGovernanceRecoveryContinuity service)
    {
        await using var db = database.Context();
        await using var tx = await db.Database.BeginTransactionAsync();
        await TakeFenceAsync(db);
        return await service.RequireAsync(db, fixture.EnvironmentId);
    }

    private static async Task AssertUnavailableAsync(Func<Task> action) => Assert.Equal("credential_governance_recovery_continuity_unavailable",
        (await Assert.ThrowsAsync<InvalidDataException>(action)).Message);

    private sealed record Fixture(GovernanceJournalId Journal, Guid EnvironmentId, Guid ActorId, GovernanceEnvironmentHistory History);

    private sealed class JournalAuthority : IGovernanceJournalWitness
    {
        public GovernanceWitnessState Current { get; init; } = new(GovernanceJournalProtocol.Genesis, null, null);

        public Task<GovernanceWitnessState> ObserveAsync(GovernanceJournalId journal, Guid? operationId, CancellationToken ct) => Task.FromResult(Current);
        public Task<GovernanceWitnessState> PrepareAsync(GovernanceJournalId journal, GovernanceWriteIntent intent, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<GovernanceWitnessState> ConfirmAsync(GovernanceJournalId journal, GovernanceWriteIntent intent, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class HistoryAuthority : IDisposable
    {
        private readonly ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private byte[]? firstResponse;

        internal HistoryAuthority(GovernanceEnvironmentHistory history)
        {
            Current = history;
            var trust = new GovernanceWitnessTrust("continuity-history", "history-test-key", Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
            Client = new(new Uri("https://history.example.test/observe"), trust,
                new BoundedWitnessHttpClient(new Handler(SendAsync), TimeSpan.FromSeconds(2)));
        }

        internal GovernanceEnvironmentHistory Current { get; set; }
        internal GovernanceEnvironmentHistoryHttpWitness Client { get; }
        internal bool ReplayFirstResponse { get; set; }
        internal int Requests { get; private set; }

        private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken cancellationToken)
        {
            Requests++;
            var bytes = await message.Content!.ReadAsByteArrayAsync(cancellationToken);
            var request = JsonSerializer.Deserialize<GovernanceEnvironmentHistoryRequest>(bytes, CanonicalJson.Options)!;
            var payload = new GovernanceEnvironmentHistoryPayload(GovernanceEnvironmentHistoryProtocol.RequestHash(request),
                "continuity-history", "history-test-key", Current);
            var signed = key.SignData(SecuritySignedEncoding.Unsigned(GovernanceEnvironmentHistoryProtocol.ResponseDomain, payload),
                HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            var response = CanonicalJson.Serialize(new SignedGovernanceEnvironmentHistoryResponse(payload, Convert.ToBase64String(signed)));
            firstResponse ??= response;
            var content = new ByteArrayContent(ReplayFirstResponse ? firstResponse : response);
            content.Headers.ContentType = new("application/json");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }

        public void Dispose()
        {
            Client.Dispose();
            key.Dispose();
        }

        private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                send(request, cancellationToken);
        }
    }
}
