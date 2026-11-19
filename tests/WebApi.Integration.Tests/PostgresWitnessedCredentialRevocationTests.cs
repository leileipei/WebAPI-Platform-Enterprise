using Microsoft.EntityFrameworkCore;
using Npgsql;
using System.Security.Cryptography;
using WebApi.Contracts.Applications;
using WebApi.Contracts.Common;
using WebApi.Contracts.Releases;
using WebApi.Contracts.Runtime;
using WebApi.Contracts.Security;
using WebApi.Domain.CredentialSecurity;
using WebApi.Domain.Runtime;
using WebApi.Gateway.Security;
using WebApi.Gateway.Storage;
using WebApi.Infrastructure.CredentialSecurity;
using WebApi.Infrastructure.Gateway;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Releases;
using WebApi.Infrastructure.Security;
using WebApi.Integration.Tests.Support;
using Xunit;

namespace WebApi.Integration.Tests;

public sealed class PostgresWitnessedCredentialRevocationTests
{
    [Theory]
    [InlineData("prepare-reply-lost")]
    [InlineData("commit-before-confirm")]
    [InlineData("confirmed-reply-lost")]
    public async Task RestartsReconcileTheSameDurableRevocation(string fault)
    {
        await using var fixture = await Fixture.StartAsync();
        fixture.Witness.Fault = fault;
        await Assert.ThrowsAsync<IOException>(() => fixture.RevokeAsync());
        var committed = fault != "prepare-reply-lost";
        await fixture.AssertFactsAsync(committed ? 1 : 0);

        fixture.Witness.Fault = null;
        await fixture.Service().RecoverAsync(fixture.Actor);
        var retry = await fixture.RevokeAsync();
        await fixture.AssertFactsAsync(1);
        Assert.True(retry.Replayed);
        Assert.Equal("Unconfirmed", retry.Status);
        Assert.Equal(2, retry.Targets.Count);
        Assert.Equal(1, fixture.Witness.Preparations);
        Assert.Equal(1, fixture.Witness.Confirmations);
        Assert.Equal(fixture.Witness.Operation, retry.OperationId);
    }

    [Fact]
    public async Task ConcurrentRetriesCreateOneLedgerEventAndChangedRequestsConflict()
    {
        await using var fixture = await Fixture.StartAsync();
        var replies = await Task.WhenAll(fixture.RevokeAsync(), fixture.RevokeAsync());
        Assert.Equal(replies[0].OperationId, replies[1].OperationId);
        Assert.Single(replies, reply => !reply.Replayed);
        var changed = await Assert.ThrowsAsync<ApiException>(() => fixture.RevokeAsync("changed reason"));
        Assert.Equal(409, changed.Status);
        await fixture.AssertFactsAsync(1);
        Assert.Equal(1, fixture.Witness.Preparations);
    }

    [Fact]
    public async Task GlobalRevocationCommitsAllEnvironmentsAsOneFactAndReplays()
    {
        await using var fixture = await Fixture.StartAsync(global: true);
        var replies = await Task.WhenAll(fixture.RevokeAsync(), fixture.RevokeAsync());
        Assert.Equal(replies[0].OperationId, replies[1].OperationId);
        Assert.Single(replies, x => !x.Replayed);
        Assert.Equal(fixture.EnvironmentIds.Order(), replies[0].Environments.Select(x => x.EnvironmentId));
        Assert.Equal(4, replies[0].Targets.Count);
        await fixture.AssertFactsAsync(1);
        Assert.Equal(1, fixture.Witness.Preparations);
        Assert.Equal(1, fixture.Witness.Confirmations);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GlobalRevocationIncludesASealedEnvironmentOnlyWithIndependentProof(bool proofAvailable)
    {
        await using var fixture = await Fixture.StartAsync(global: true, archived: true);
        fixture.Archive.Approved = proofAvailable;
        if (!proofAvailable)
        {
            var denied = await Assert.ThrowsAsync<ApiException>(() => fixture.RevokeAsync());
            Assert.Equal("credential_revocation_archive_unverified", denied.Code);
            Assert.Equal(0, fixture.Witness.Preparations);
            await fixture.AssertFactsAsync(0);
            return;
        }

        var receipt = await fixture.RevokeAsync();
        var sealedEnvironment = Assert.Single(receipt.Environments, environment => environment.EnvironmentId == fixture.EnvironmentIds[1]);
        Assert.Equal("SealedNoIngressAtCommit", sealedEnvironment.CoverageState);
        Assert.Equal(2, receipt.Targets.Count);
        Assert.All(receipt.Targets, target => Assert.Equal(fixture.EnvironmentId, target.EnvironmentId));
        var laterEnvironment = await fixture.AddInheritedEnvironmentAsync();
        fixture.Archive.Approved = false;
        var replay = await fixture.RevokeAsync();
        Assert.True(replay.Replayed);
        Assert.Equal(receipt.Environments, replay.Environments);
        await using var unrelated = fixture.Database.Context();
        await using var held = await unrelated.Database.BeginTransactionAsync();
        await unrelated.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM environments WHERE id={laterEnvironment} FOR UPDATE");
        await using var db = fixture.Database.Context();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var polled = await new SecurityOperationStatusReader(db).ReadRevocationAsync(receipt.OperationId, fixture.Actor, (_, _) => Task.CompletedTask,
            timeout.Token);
        await held.CommitAsync();
        Assert.Equal(receipt.Environments, polled.Environments);
        Assert.Equal(2, polled.Targets.Count);
        var archived = await new SecurityLedgerReader(db).ReadCompleteAsync(fixture.EnvironmentIds[1]);
        Assert.Equal("CredentialRevokedArchived", Assert.Single(archived.Events).EventType);
        Assert.Single(archived.Revocations);
    }

    [Theory]
    [InlineData("verified")]
    [InlineData("missing-horizon")]
    [InlineData("missing-manage")]
    [InlineData("changed-revision")]
    [InlineData("legacy-snapshot")]
    public async Task FormalRotationPreviewRechecksAuthorityAndDeadlineWithoutWriting(string scenario)
    {
        await using var fixture = await Fixture.StartAsync(withGovernanceSnapshot: scenario != "legacy-snapshot",
            includeCredentialInPublication: true);
        var horizon = new TestRotationHorizon { Approved = scenario != "missing-horizon" };
        await using (var db = fixture.Database.Context())
        {
            if (scenario != "missing-manage")
            {
                var role = await db.Set<Role>().SingleAsync(item => item.Code == "SECURITY-FIXTURE");
                var manage = new Permission { Code = "credential.manage", Name = "Manage", Module = "credential" };
                db.AddRange(manage, new RolePermission { RoleId = role.Id, PermissionId = manage.Id });
                await db.SaveChangesAsync();
            }
        }
        var service = new PostgresFormalRotationPreparation(fixture.Database.ConnectionString, fixture.Journal, fixture.Coverage, horizon);
        var now = DateTimeOffset.UtcNow;
        var request = new FormalRotationPreviewRequest(fixture.EnvironmentId, now, now.AddHours(2), now.AddSeconds(70), now.AddSeconds(120));
        var tag = scenario == "changed-revision" ? "\"2\"" : "\"1\"";
        if (scenario == "verified")
        {
            var preview = await service.PreviewAsync(fixture.CredentialId, request, tag, fixture.Actor);
            var retry = await service.PreviewAsync(fixture.CredentialId, request, tag, fixture.Actor);
            Assert.Equal(preview.PreviewHash, retry.PreviewHash);
            Assert.Equal(2, preview.Targets.Count);
            Assert.True(preview.LastWorkingCredentialAtDeadline);
            Assert.True(preview.LastPublishedCredentialAtDeadline);
            Assert.DoesNotContain("isolated-access-key", preview.MaskedAccessKey);
        }
        else
        {
            var error = await Assert.ThrowsAsync<ApiException>(() => service.PreviewAsync(fixture.CredentialId, request, tag, fixture.Actor));
            Assert.Equal(scenario switch { "missing-horizon" => 503, "missing-manage" => 403, "changed-revision" => 412, _ => 409 }, error.Status);
        }
        await fixture.AssertFactsAsync(0);
        Assert.Equal(0, fixture.Witness.Preparations);
    }

    [Fact]
    public async Task DesiredDeliveryRequiresAQualifiedJournalAndTheExactCurrentInstance()
    {
        await using var fixture = await Fixture.StartAsync(includeCredentialInPublication: true);
        await using var db = fixture.Database.Context();
        var config = await db.Set<GatewayConfigVersion>().SingleAsync();
        config.Status = "Published";
        await db.SaveChangesAsync();
        var node = await db.Set<GatewayNode>().AsNoTracking().OrderBy(item => item.Id).FirstAsync();
        var identity = new NodeIdentity(node.Id, node.EnvironmentId, node.IdentityHash);
        var identities = new NodeIdentityService(db, new([]));
        var instance = Guid.Parse(node.InstanceId);
        var required = new GovernanceDeliverySettings(true);
        var unavailable = new NodeRegistry(db, identities, governanceDelivery: required);

        var missing = await Assert.ThrowsAsync<ApiException>(() => unavailable.DesiredAsync(identity, instanceId: instance));
        Assert.Equal("credential_governance_delivery_unavailable", missing.Code);

        var verified = new IsolatedDeliveryContinuity(fixture.Journal);
        var registry = new NodeRegistry(db, identities, verified, required);
        var mismatch = await Assert.ThrowsAsync<ApiException>(() => registry.DesiredAsync(identity, instanceId: Guid.NewGuid()));
        Assert.Equal("node_instance_changed", mismatch.Code);
        Assert.Equal(0, verified.Calls);
        var desired = await registry.DesiredAsync(identity, instanceId: instance);
        Assert.NotNull(desired);
        Assert.Equal("2.4", SnapshotValidator.ParsePayload(desired.Payload, node.EnvironmentId).SchemaVersion);
        Assert.Equal(1, verified.Calls);

        verified.Available = false;
        var restored = await Assert.ThrowsAsync<ApiException>(() => registry.DesiredAsync(identity, instanceId: instance));
        Assert.Equal("credential_governance_delivery_unavailable", restored.Code);
    }

    [Fact]
    public async Task ExactConcurrentFormalConfirmationsCreateOneScheduleWithTheOldCredentialStillActive()
    {
        await using var fixture = await Fixture.StartAsync(includeCredentialInPublication: true);
        var request = await PrepareFormalRotationAsync(fixture);
        var service = fixture.RotationService();
        Task<FormalRotationReceipt> Confirm() => service.ConfirmAsync(fixture.CredentialId, request,
            "\"1\"", new("isolated-rotation"), fixture.Actor);

        var replies = await Task.WhenAll(Confirm(), Confirm());
        var created = Assert.Single(replies, reply => !reply.Replayed);
        var replay = Assert.Single(replies, reply => reply.Replayed);
        Assert.Equal(created, replay with { Replayed = false });
        Assert.Equal("Scheduled", created.Status);
        Assert.Null(created.RevocationOperationId);
        Assert.Equal(fixture.CredentialId, created.CredentialId);
        Assert.Equal(request.Window.ReplacementCredentialId, created.ReplacementCredentialId);
        Assert.Equal(fixture.EnvironmentId, created.EnvironmentId);
        Assert.Equal(request.Window.HardDeadline, created.HardDeadline);

        var changed = await Assert.ThrowsAsync<ApiException>(() => service.ConfirmAsync(fixture.CredentialId,
            request with { Reason = "policy_change" }, "\"1\"", new("isolated-rotation"), fixture.Actor));
        Assert.Equal(409, changed.Status);
        var secondSchedule = await Assert.ThrowsAsync<ApiException>(() => service.ConfirmAsync(fixture.CredentialId,
            request, "\"1\"", new("second-rotation"), fixture.Actor));
        Assert.Equal(409, secondSchedule.Status);
        Assert.Equal(0, await service.RevokeDueAsync());
        Assert.Equal(1, fixture.Witness.FormalPreparations);
        Assert.Equal(1, fixture.Witness.Preparations);
        Assert.Equal(1, fixture.Witness.Confirmations);
        await AssertFormalRotationAsync(fixture, request.Window, created.RotationId);
    }

    [Theory]
    [InlineData("missing-confirmation", 422)]
    [InlineData("rejected-confirmation", 422)]
    [InlineData("wrong-preview", 409)]
    [InlineData("changed-deadline", 409)]
    [InlineData("missing-permission", 403)]
    [InlineData("changed-credential", 412)]
    [InlineData("changed-replacement", 409)]
    public async Task FormalConfirmationRechecksTheExplicitDecisionAndTheReviewedFacts(string scenario, int expectedStatus)
    {
        await using var fixture = await Fixture.StartAsync(includeCredentialInPublication: true);
        var request = await PrepareFormalRotationAsync(fixture);
        var service = fixture.RotationService();
        await using (var db = fixture.Database.Context())
        {
            switch (scenario)
            {
                case "missing-confirmation": request = request with { ConfirmAutomaticRevocation = null }; break;
                case "rejected-confirmation": request = request with { ConfirmAutomaticRevocation = false }; break;
                case "wrong-preview": request = request with { PreviewHash = new string('0', 64) }; break;
                case "changed-deadline":
                    request = request with { Window = request.Window with { HardDeadline = request.Window.HardDeadline.AddSeconds(1) } };
                    break;
                case "missing-permission":
                    await db.Set<RolePermission>().Where(item => item.PermissionId == fixture.RevokePermission).ExecuteDeleteAsync();
                    break;
                case "changed-credential":
                    await db.Set<ApplicationCredential>().Where(item => item.Id == fixture.CredentialId)
                        .ExecuteUpdateAsync(set => set.SetProperty(item => item.Revision, item => item.Revision + 1));
                    break;
                case "changed-replacement":
                    await db.Set<ApplicationCredential>().Where(item => item.Id == request.Window.ReplacementCredentialId)
                        .ExecuteUpdateAsync(set => set.SetProperty(item => item.ExpiresAt, request.Window.NewExpiresAt.AddMinutes(5))
                            .SetProperty(item => item.Revision, item => item.Revision + 1));
                    break;
            }
        }

        var error = await Assert.ThrowsAsync<ApiException>(() => service.ConfirmAsync(fixture.CredentialId, request,
            "\"1\"", new("isolated-rotation"), fixture.Actor));
        Assert.Equal(expectedStatus, error.Status);
        Assert.Equal(0, fixture.Witness.Preparations);
        await AssertFormalRotationAsync(fixture, request.Window, null);
    }

    [Theory]
    [InlineData("prepare-reply-lost", false)]
    [InlineData("commit-before-confirm", true)]
    [InlineData("confirmed-reply-lost", true)]
    public async Task AnAmbiguousFormalConfirmationResumesTheSameSchedule(string fault, bool recorded)
    {
        await using var fixture = await Fixture.StartAsync(includeCredentialInPublication: true);
        var request = await PrepareFormalRotationAsync(fixture);
        Task<FormalRotationReceipt> Confirm() => fixture.RotationService().ConfirmAsync(fixture.CredentialId, request,
            "\"1\"", new("isolated-rotation"), fixture.Actor);

        fixture.Witness.Fault = fault;
        await Assert.ThrowsAsync<IOException>(Confirm);
        await using (var db = fixture.Database.Context())
        {
            Assert.Equal(recorded ? 1 : 0, await db.Set<FormalCredentialRotation>().CountAsync());
            Assert.Empty(await db.Set<SecurityEvent>().ToArrayAsync());
        }

        fixture.Witness.Fault = null;
        var receipt = await Confirm();
        Assert.Equal("Scheduled", receipt.Status);
        Assert.True(receipt.Replayed);
        Assert.Equal(1, fixture.Witness.FormalPreparations);
        Assert.Equal(1, fixture.Witness.Confirmations);
        await AssertFormalRotationAsync(fixture, request.Window, receipt.RotationId);
    }

    [Fact]
    public async Task ConfirmCannotOvertakeAnAllowLeaseBeyondTheWitnessCutoff()
    {
        await using var fixture = await Fixture.StartAsync(includeCredentialInPublication: true);
        var request = await PrepareFormalRotationAsync(fixture);
        var service = fixture.RotationService();
        Task<FormalRotationReceipt> Confirm() => service.ConfirmAsync(fixture.CredentialId, request,
            "\"1\"", new("isolated-rotation"), fixture.Actor);

        fixture.Witness.LatestFormalAllowExpiry = request.Window.HardDeadline.AddSeconds(-1);
        await Assert.ThrowsAsync<IOException>(Confirm);
        Assert.Equal(0, fixture.Witness.Preparations);
        Assert.Null(fixture.Witness.Pending);
        await AssertFormalRotationAsync(fixture, request.Window, null);

        fixture.Witness.LatestFormalAllowExpiry = request.Window.HardDeadline.AddSeconds(-2);
        var scheduled = await Confirm();
        Assert.False(scheduled.Replayed);
        Assert.Equal(1, fixture.Witness.FormalPreparations);
        await AssertFormalRotationAsync(fixture, request.Window, scheduled.RotationId);
    }

    [Fact]
    public async Task PassingTheOverlapDoesNotRevokeAndTheExactDeadlineRecordsOneRevocation()
    {
        await using var fixture = await Fixture.StartAsync(includeCredentialInPublication: true);
        var request = await PrepareFormalRotationAsync(fixture);
        var clock = request.Window.OverlapUntil.AddSeconds(1);
        var service = fixture.RotationService((_, _) => Task.FromResult(clock));
        var scheduled = await service.ConfirmAsync(fixture.CredentialId, request, "\"1\"", new("isolated-rotation"), fixture.Actor);
        fixture.Witness.FormalNow = clock;

        Assert.Equal(0, await service.RevokeDueAsync());
        await AssertFormalRotationAsync(fixture, request.Window, scheduled.RotationId);
        await using (var db = fixture.Database.Context())
            await db.Set<RolePermission>().Where(item => item.PermissionId == fixture.RevokePermission).ExecuteDeleteAsync();

        clock = request.Window.HardDeadline;
        fixture.Witness.FormalNow = clock;
        var results = await Task.WhenAll(service.RevokeDueAsync(), service.RevokeDueAsync());
        Assert.Equal(1, results.Sum());
        Assert.Equal(0, await service.RevokeDueAsync());
        Assert.Equal(1, fixture.Witness.FormalRevocationPreparations);
        await AssertFormalRotationAsync(fixture, request.Window, scheduled.RotationId, revoked: true);
    }

    [Fact]
    public async Task TheDatabaseClockCannotTriggerRevocationBeforeTheIndependentDeadline()
    {
        await using var fixture = await Fixture.StartAsync(includeCredentialInPublication: true);
        var request = await PrepareFormalRotationAsync(fixture);
        var service = fixture.RotationService((_, _) => Task.FromResult(request.Window.HardDeadline.AddSeconds(1)));
        var schedule = await service.ConfirmAsync(fixture.CredentialId, request,
            "\"1\"", new("isolated-rotation"), fixture.Actor);

        fixture.Witness.FormalNow = request.Window.HardDeadline.AddMilliseconds(-1);
        var failure = await Record.ExceptionAsync(() => service.RevokeDueAsync());
        if (failure is not null) Assert.IsAssignableFrom<IOException>(failure);
        Assert.Equal(1, fixture.Witness.FormalRevocationChecks);
        Assert.Equal(0, fixture.Witness.FormalRevocationPreparations);
        Assert.Null(fixture.Witness.Pending);
        await AssertFormalRotationAsync(fixture, request.Window, schedule.RotationId);

        fixture.Witness.FormalNow = request.Window.HardDeadline;
        Assert.Equal(1, await service.RevokeDueAsync());
        await AssertFormalRotationAsync(fixture, request.Window, schedule.RotationId, revoked: true);
    }

    [Theory]
    [InlineData("prepare-reply-lost", false)]
    [InlineData("commit-before-confirm", true)]
    [InlineData("confirmed-reply-lost", true)]
    public async Task AnAmbiguousHardDeadlineRevocationRecoversWithoutATombstoneDuplicate(string fault, bool recorded)
    {
        await using var fixture = await Fixture.StartAsync(includeCredentialInPublication: true);
        var request = await PrepareFormalRotationAsync(fixture);
        var service = fixture.RotationService((_, _) => Task.FromResult(request.Window.HardDeadline));
        Task<FormalRotationReceipt> Confirm() => service.ConfirmAsync(fixture.CredentialId, request,
            "\"1\"", new("isolated-rotation"), fixture.Actor);
        var schedule = await Confirm();

        fixture.Witness.FormalNow = request.Window.HardDeadline;
        fixture.Witness.Fault = fault;
        var failure = await Record.ExceptionAsync(() => service.RevokeDueAsync());
        if (failure is not null) Assert.IsAssignableFrom<IOException>(failure);
        Assert.Equal(1, fixture.Witness.FormalRevocationPreparations);
        await AssertFormalRotationAsync(fixture, request.Window, schedule.RotationId, revoked: recorded);

        fixture.Witness.Fault = null;
        await service.RevokeDueAsync();
        Assert.Null(fixture.Witness.Pending);
        Assert.Equal(2, fixture.Witness.Confirmations);
        Assert.Equal(1, fixture.Witness.FormalRevocationPreparations);
        await AssertFormalRotationAsync(fixture, request.Window, schedule.RotationId, revoked: true);

        var replay = await Confirm();
        Assert.True(replay.Replayed);
        Assert.Equal(schedule.RotationId, replay.RotationId);
        Assert.Equal("RevocationRecorded", replay.Status);
        Assert.Equal(fixture.Witness.Operation, replay.RevocationOperationId);
    }

    [Fact]
    public async Task AConfirmedFormalScheduleKeepsExplicitRevocationAvailable()
    {
        await using var fixture = await Fixture.StartAsync(includeCredentialInPublication: true);
        var request = await PrepareFormalRotationAsync(fixture);
        var service = fixture.RotationService();
        var schedule = await service.ConfirmAsync(fixture.CredentialId, request,
            "\"1\"", new("isolated-rotation"), fixture.Actor);

        var explicitRevocation = await fixture.RevokeAsync();
        Assert.Equal("Unconfirmed", explicitRevocation.Status);
        Assert.False(explicitRevocation.Replayed);
        Assert.Equal(2, fixture.Witness.Preparations);
        Assert.Equal(0, fixture.Witness.FormalRevocationPreparations);
        await AssertFormalRotationAsync(fixture, request.Window, schedule.RotationId, revoked: true);
    }

    private static async Task<FormalRotationConfirmRequest> PrepareFormalRotationAsync(Fixture fixture)
    {
        await using var db = fixture.Database.Context();
        var original = await db.Set<ApplicationCredential>().AsNoTracking().SingleAsync(item => item.Id == fixture.CredentialId);
        var role = await db.Set<Role>().SingleAsync(item => item.Code == "SECURITY-FIXTURE");
        var manage = new Permission { Code = "credential.manage", Name = "Manage", Module = "credential" };
        var now = await db.Database.SqlQueryRaw<DateTimeOffset>("SELECT pg_catalog.clock_timestamp() AS \"Value\"").SingleAsync();
        var replacement = new ApplicationCredential { ApplicationId = original.ApplicationId, OrganizationId = original.OrganizationId,
            ProjectId = original.ProjectId, EnvironmentId = fixture.EnvironmentId, BindingMode = "EnvironmentBound",
            AccessKey = "isolated-replacement-access", SecretHash = new string('b', 64), ValidFrom = now, ExpiresAt = now.AddHours(2) };
        db.AddRange(manage, new RolePermission { RoleId = role.Id, PermissionId = manage.Id }, replacement);
        await db.SaveChangesAsync();

        var window = new FormalRotationPreviewRequest(fixture.EnvironmentId, replacement.ValidFrom, replacement.ExpiresAt,
            now.AddSeconds(70), now.AddSeconds(120), replacement.Id);
        var preview = new PostgresFormalRotationPreparation(fixture.Database.ConnectionString, fixture.Journal, fixture.Coverage,
            new TestRotationHorizon { Approved = true });
        var reviewed = await preview.PreviewAsync(fixture.CredentialId, window, "\"1\"", fixture.Actor);
        return new(window, reviewed.PreviewHash, true, "credential_retired");
    }

    private static async Task AssertFormalRotationAsync(Fixture fixture, FormalRotationPreviewRequest window, Guid? rotationId,
        bool revoked = false)
    {
        await using var db = fixture.Database.Context();
        var schedules = await db.Set<FormalCredentialRotation>().AsNoTracking().ToArrayAsync();
        if (rotationId is Guid recorded)
        {
            var schedule = Assert.Single(schedules);
            Assert.Equal(recorded, schedule.Id);
            Assert.Equal(fixture.CredentialId, schedule.CredentialId);
            Assert.Equal(window.ReplacementCredentialId, schedule.ReplacementCredentialId);
            Assert.Equal(fixture.EnvironmentId, schedule.EnvironmentId);
            Assert.Equal(window.HardDeadline, schedule.HardDeadline);
        }
        else Assert.Empty(schedules);

        var old = await db.Set<ApplicationCredential>().AsNoTracking().SingleAsync(item => item.Id == fixture.CredentialId);
        var replacement = await db.Set<ApplicationCredential>().AsNoTracking().SingleAsync(item => item.Id == window.ReplacementCredentialId);
        Assert.Equal(revoked ? "Revoked" : "Active", old.Status);
        Assert.Equal("Active", replacement.Status);
        Assert.Equal(revoked ? 1 : 0, await db.Set<SecurityEvent>().CountAsync());
        Assert.Equal(revoked ? 1 : 0, await db.Set<CredentialRevocation>().CountAsync(item => item.CredentialId == old.Id));
        if (revoked)
        {
            var fact = await db.Set<SecurityEvent>().SingleAsync();
            var operation = await db.Set<SecurityOperation>().SingleAsync();
            var outbox = await db.Set<SecurityOutboxMessage>().SingleAsync();
            Assert.Equal(old.Id, fact.CredentialId);
            Assert.Equal(window.EnvironmentId, fact.EnvironmentId);
            Assert.Equal("CredentialRevoked", fact.EventType);
            Assert.Equal(fact.OperationId, operation.Id);
            Assert.Equal(fact.OperationId, outbox.OperationId);
            Assert.Equal(2, await db.Set<SecurityOperationTarget>().CountAsync(item => item.OperationId == fact.OperationId));
        }
    }

    [Fact]
    public async Task GlobalRevocationStillReplaysWhenTheIndependentInventoryMustBeRefreshed()
    {
        await using var fixture = await Fixture.StartAsync(global: true);
        var committed = await fixture.RevokeAsync();
        fixture.Coverage.HistoryApproved = false;

        var replay = await fixture.RevokeAsync();
        Assert.True(replay.Replayed);
        Assert.Equal(committed.OperationId, replay.OperationId);
        Assert.Equal(committed.Environments, replay.Environments);
        await fixture.AssertFactsAsync(1);
    }

    [Fact]
    public async Task GlobalRevocationCannotPrepareWhenCurrentProofCannotCoverTheWitnessRequest()
    {
        await using var fixture = await Fixture.StartAsync(global: true, leaseLifetime: TimeSpan.FromSeconds(4));
        var error = await Assert.ThrowsAsync<ApiException>(() => fixture.RevokeAsync());
        Assert.Equal("credential_revocation_coverage_unknown", error.Code);
        Assert.Equal(0, fixture.Witness.Preparations);
        await fixture.AssertFactsAsync(0);
    }

    [Theory]
    [InlineData("2.3", false)]
    [InlineData("2.4", true)]
    public async Task GlobalRevocationRequiresModernReplacementNodes(string schema, bool accepted)
    {
        await using var fixture = await Fixture.StartAsync(global: true);
        await fixture.RevokeAsync();
        await using var db = fixture.Database.Context();
        const string name = "isolated-replacement";
        const string secret = "fixture-replacement-credential-identity";
        var identity = new NodeIdentityService(db, new([new(fixture.EnvironmentId, name, NodeIdentityService.Hash(secret))]));
        var registry = new NodeRegistry(db, identity);
        var request = new WebApi.Contracts.Gateway.RegisterNodeRequest(fixture.EnvironmentId, name, Guid.NewGuid(),
            "fixture", [schema], ["security-ledger-v1"]);
        if (accepted)
        {
            var node = await registry.RegisterAsync(request, secret);
            Assert.Single(await db.Set<SecurityOperationTarget>().Where(x => x.NodeId == node.NodeId).ToArrayAsync());
        }
        else
        {
            var error = await Assert.ThrowsAsync<ApiException>(() => registry.RegisterAsync(request, secret));
            Assert.Equal("gateway_security_capability_required", error.Code);
            Assert.False(await db.Set<GatewayNode>().AnyAsync(x => x.NodeName == name));
        }
    }

    [Fact]
    public async Task GlobalRevocationDoesNotLetAnOperatorReenableALegacyNode()
    {
        await using var fixture = await Fixture.StartAsync(global: true);
        await fixture.RevokeAsync();
        await using var db = fixture.Database.Context();
        var legacyNode = new GatewayNode { EnvironmentId = fixture.EnvironmentId, InstanceId = Guid.NewGuid().ToString(),
            NodeName = "isolated-disabled-legacy", Enabled = false };
        var role = await db.Set<Role>().SingleAsync(x => x.Code == "SECURITY-FIXTURE");
        var permission = new Permission { Code = "gateway.operate", Module = "gateway", Name = "Operate" };
        db.AddRange(legacyNode, permission, new RolePermission { RoleId = role.Id, PermissionId = permission.Id });
        await db.SaveChangesAsync();
        var scope = new ScopeResolver(db);
        var service = new GatewayReadService(db, new AuthorizationService(db), scope, new HistoricalSnapshotService(db, scope),
            new AuditedCommandExecutor(db));

        var error = await Assert.ThrowsAsync<ApiException>(() => service.SetEnabledAsync(legacyNode.Id, true,
            $"\"{legacyNode.InstanceId}:False\"", fixture.Actor));
        Assert.Equal("gateway_security_capability_required", error.Code);
        Assert.False((await db.Set<GatewayNode>().AsNoTracking().SingleAsync(x => x.Id == legacyNode.Id)).Enabled);
    }

    [Fact]
    public async Task GlobalRevocationKeepsAReportedCapabilityDowngradeOutOfReady()
    {
        await using var fixture = await Fixture.StartAsync(global: true);
        await fixture.RevokeAsync();
        await using var db = fixture.Database.Context();
        var node = await db.Set<GatewayNode>().AsNoTracking().FirstAsync(x => x.EnvironmentId == fixture.EnvironmentId);
        var registry = new NodeRegistry(db, new NodeIdentityService(db, new([])));
        await registry.HeartbeatAsync(new(node.Id, node.EnvironmentId, node.IdentityHash),
            new(Guid.Parse(node.InstanceId), 1, 1, "Ready", ["2.3"], ["security-ledger-v1"]));

        var saved = await db.Set<GatewayNode>().AsNoTracking().SingleAsync(x => x.Id == node.Id);
        Assert.Equal("NotReady", saved.Status);
        Assert.Equal(["2.3"], SnapshotSchemaCapabilities.Read(saved));
    }

    [Fact]
    public async Task EnvironmentBoundRecoveryKeepsTheOriginalJournalEncoding()
    {
        await using var fixture = await Fixture.StartAsync();
        fixture.Witness.Fault = "prepare-reply-lost";
        await Assert.ThrowsAsync<IOException>(() => fixture.RevokeAsync());
        var original = Assert.IsType<GovernanceWriteIntent>(fixture.Witness.Pending);
        var plan = CredentialRevocationIntent.Read(original);
        var legacyBytes = CanonicalJson.Serialize(new
        {
            plan.CredentialId, plan.ApplicationId, plan.ActorId, plan.CredentialRevision,
            plan.ApplicationRevision, plan.ApplicationAuthorizationRevision, plan.EnvironmentAuthorizationRevision,
            plan.Reason, plan.RequestHash, plan.EventTime, plan.Coverage, plan.Challenges
        });
        Assert.Equal(System.Buffers.Text.Base64Url.EncodeToString(legacyBytes), original.RecoveryMaterial);

        fixture.Witness.Fault = null;
        await fixture.Service().RecoverAsync(fixture.Actor);
        await fixture.AssertFactsAsync(1);
    }

    [Theory]
    [InlineData("missing-current-receipt", "credential_revocation_coverage_unknown")]
    [InlineData("scoped-request", "credential_revocation_scope_unknown")]
    [InlineData("independent-coverage", "credential_revocation_coverage_unknown")]
    public async Task GlobalRevocationNeverPreparesWithoutTheWholeProvenScope(string failure, string code)
    {
        await using var fixture = await Fixture.StartAsync(global: true, withCurrentProof: failure != "missing-current-receipt");
        if (failure == "independent-coverage") fixture.Coverage.RejectedEnvironment = fixture.EnvironmentIds[1];
        var error = await Assert.ThrowsAsync<ApiException>(() => fixture.RevokeAsync(
            environment: failure == "scoped-request" ? fixture.EnvironmentId : null));
        Assert.Equal(409, error.Status);
        Assert.Equal(code, error.Code);
        Assert.Equal(0, fixture.Witness.Preparations);
        await fixture.AssertFactsAsync(0);
    }

    [Fact]
    public async Task GlobalRevocationRollsBackEveryEnvironmentWhenOneTombstoneFails()
    {
        await using var fixture = await Fixture.StartAsync(global: true);
        await using (var db = fixture.Database.Context())
            await db.Database.ExecuteSqlRawAsync("""
                CREATE FUNCTION credential_governance.fixture_reject_second_tombstone() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM credential_revocations WHERE operation_id = NEW.operation_id) THEN
                        RAISE EXCEPTION 'fixture_second_tombstone_failed' USING ERRCODE='23514';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER fixture_reject_second_tombstone BEFORE INSERT ON credential_revocations
                    FOR EACH ROW EXECUTE FUNCTION credential_governance.fixture_reject_second_tombstone();
                """);
        var failure = await Assert.ThrowsAsync<DbUpdateException>(() => fixture.RevokeAsync());
        Assert.Equal("fixture_second_tombstone_failed", Assert.IsType<PostgresException>(failure.InnerException).MessageText);
        Assert.NotNull(fixture.Witness.Pending);
        await fixture.AssertFactsAsync(0);
        await using (var db = fixture.Database.Context())
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fixture_reject_second_tombstone ON credential_revocations");

        await fixture.Service().RecoverAsync(fixture.Actor);
        await fixture.AssertFactsAsync(1);
    }

    [Fact]
    public async Task RevokedPermissionPreventsRecoveryFromApplyingPreparedIntent()
    {
        await using var fixture = await Fixture.StartAsync();
        fixture.Witness.Fault = "prepare-reply-lost";
        await Assert.ThrowsAsync<IOException>(() => fixture.RevokeAsync());
        fixture.Witness.Fault = null;
        await using (var change = fixture.Database.Context())
            await change.Set<RolePermission>().Where(x => x.PermissionId == fixture.RevokePermission).ExecuteDeleteAsync();

        var retry = await Assert.ThrowsAsync<ApiException>(() => fixture.RevokeAsync());
        Assert.Equal(403, retry.Status);
        var recovery = await Assert.ThrowsAsync<ApiException>(() => fixture.Service().RecoverAsync(fixture.Actor));
        Assert.Equal(403, recovery.Status);
        Assert.NotNull(fixture.Witness.Pending);
        await fixture.AssertFactsAsync(0);
    }

    [Fact]
    public async Task UnknownCoverageWrongScopeOrStaleRevisionNeverPrepares()
    {
        await using var fixture = await Fixture.StartAsync();
        fixture.Coverage.Approved = false;
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => fixture.RevokeAsync())).Status);
        fixture.Coverage.Approved = true;
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => fixture.RevokeAsync(environment: Guid.NewGuid()))).Status);
        Assert.Equal(412, (await Assert.ThrowsAsync<ApiException>(() => fixture.RevokeAsync(tag: "\"2\""))).Status);
        Assert.Equal(0, fixture.Witness.Preparations);
        await fixture.AssertFactsAsync(0);
    }

    [Theory]
    [InlineData("missing-receipt")]
    [InlineData("legacy-snapshot")]
    public async Task NodeClaimsAloneCannotPrepareRevocation(string missing)
    {
        await using var fixture = await Fixture.StartAsync(withCurrentProof: missing != "missing-receipt", withGovernanceSnapshot: missing != "legacy-snapshot");
        var failure = await Assert.ThrowsAsync<ApiException>(() => fixture.RevokeAsync());
        Assert.Equal("credential_revocation_coverage_unknown", failure.Code);
        Assert.Equal(0, fixture.Witness.Preparations);
        await fixture.AssertFactsAsync(0);
    }

    [Fact]
    public async Task JournalInsertFailureRollsBackTheActualBusinessChangesBeforeRecovery()
    {
        await using var fixture = await Fixture.StartAsync();
        await using (var db = fixture.Database.Context())
            await db.Database.ExecuteSqlRawAsync("""
                CREATE FUNCTION credential_governance.fixture_reject_append() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'fixture_journal_insert_failed' USING ERRCODE='23514'; END $$;
                CREATE TRIGGER fixture_reject_append BEFORE INSERT ON credential_governance.journal_entries
                    FOR EACH ROW EXECUTE FUNCTION credential_governance.fixture_reject_append();
                """);
        var failure = await Assert.ThrowsAsync<PostgresException>(() => fixture.RevokeAsync());
        Assert.Equal("fixture_journal_insert_failed", failure.MessageText);
        Assert.NotNull(fixture.Witness.Pending);
        await fixture.AssertFactsAsync(0);
        await using (var db = fixture.Database.Context())
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fixture_reject_append ON credential_governance.journal_entries");

        await fixture.Service().RecoverAsync(fixture.Actor);
        await fixture.AssertFactsAsync(1);
        Assert.Equal(1, fixture.Witness.Confirmations);
    }

    [Fact]
    public async Task WitnessBehindDurablePostgresIsNeverRebuiltFromLocalHistory()
    {
        await using var fixture = await Fixture.StartAsync();
        await fixture.RevokeAsync();
        var restoredWitness = new TestWitness();
        var service = new PostgresCredentialRevocationService(fixture.Database.ConnectionString, fixture.Journal, restoredWitness, fixture.Coverage);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.RecoverAsync(fixture.Actor));
        Assert.Equal(0, restoredWitness.Preparations);
        Assert.Equal(0, restoredWitness.Confirmations);
        await fixture.AssertFactsAsync(1);
    }

    [Fact]
    public async Task CommittedLedgerCanDriveTwoIsolatedGatewayStatesAndTheOldLeaseExpiresDuringLoss()
    {
        await using var fixture = await Fixture.StartAsync();
        await using var db = fixture.Database.Context();
        var reader = new SecurityLedgerReader(db);
        var previous = await reader.ReadCompleteAsync(fixture.EnvironmentId);
        var revoked = await fixture.RevokeAsync();
        var current = await reader.ReadCompleteAsync(fixture.EnvironmentId);
        Assert.Equal(revoked.OperationId, Assert.Single(current.Revocations).OperationId);

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        const string fixtureKeyId = "fixture";
        var signer = new SecurityCheckpointSigner(fixtureKeyId, key.ExportPkcs8PrivateKeyPem());
        var keys = new TrustedSigningKeys(new Dictionary<string, string> { [fixtureKeyId] = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) });
        var clock = new TestGatewayClock(revoked.CommittedAt.AddSeconds(-1));
        var directory = Path.Combine(Path.GetTempPath(), "witnessed-revoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var first = revoked.Targets[0];
        var second = revoked.Targets[1];
        try
        {
            await using var firstState = new SecurityStateStore(new(fixture.EnvironmentId, () => first.NodeId, first.InstanceGuid), new SecurityLkgStore(Path.Combine(directory, "first")), clock);
            await using var secondState = new SecurityStateStore(new(fixture.EnvironmentId, () => second.NodeId, second.InstanceGuid), new SecurityLkgStore(Path.Combine(directory, "second")), clock);
            await ApplyAsync(firstState, first, previous, lifetime: 5);
            await ApplyAsync(secondState, second, previous, lifetime: 5);
            Assert.Equal(SecurityAdmissionDecision.Allow, secondState.CanAdmit(fixture.CredentialId, clock.GetUtcNow()));

            clock.Advance(1);
            await ApplyAsync(firstState, first, current, lifetime: 20);
            Assert.Equal(SecurityAdmissionDecision.KnownRevoked, firstState.CanAdmit(fixture.CredentialId, clock.GetUtcNow()));
            Assert.Equal(SecurityAdmissionDecision.Allow, secondState.CanAdmit(fixture.CredentialId, clock.GetUtcNow()));

            clock.Advance(4);
            Assert.Equal(SecurityAdmissionDecision.Unavailable, secondState.CanAdmit(fixture.CredentialId, clock.GetUtcNow()));
            await ApplyAsync(secondState, second, current, lifetime: 20);
            Assert.Equal(SecurityAdmissionDecision.KnownRevoked, secondState.CanAdmit(fixture.CredentialId, clock.GetUtcNow()));
            Assert.Equal(SecurityAdmissionDecision.Allow, secondState.CanAdmit(Guid.NewGuid(), clock.GetUtcNow()));
        }
        finally { Directory.Delete(directory, recursive: true); }

        async Task ApplyAsync(SecurityStateStore state, RevocationTargetDto target, SecurityLedgerMaterial ledger, int lifetime)
        {
            // Keys and lease issuance exist solely inside this fixture. The
            // production issuer remains closed without independent activation.
            var now = clock.GetUtcNow();
            var until = now.AddSeconds(lifetime);
            var tombstones = ledger.Revocations.Select(r => new SecurityRevocationProof(r.CredentialId, r.OperationId,
                r.Epoch, r.RevokedAt, ledger.Events.Single(e => e.Epoch == r.Epoch).EventHash)).ToArray();
            var checkpoint = SecurityCheckpointVerifier.VerifyCheckpoint(new(signer.SignCheckpoint(new(SecuritySignedEncoding.Protocol,
                ledger.EnvironmentId, ledger.SecurityGeneration, ledger.Epoch, ledger.HeadHash, tombstones,
                ledger.RequireEnvironmentCredentials, ledger.MinimumSnapshotSchema, now, until, fixtureKeyId))), keys, ledger.EnvironmentId);
            var lease = SecurityCheckpointVerifier.VerifyNodeLease(new(signer.SignNodeLease(new(Guid.NewGuid(), SecuritySignedEncoding.Protocol,
                ledger.EnvironmentId, ledger.SecurityGeneration, ledger.Epoch, checkpoint.Hash, target.NodeId, target.InstanceGuid,
                SecuritySignedEncoding.Audience, now, until, fixtureKeyId))), keys, ledger.EnvironmentId, target.NodeId, target.InstanceGuid);
            Assert.True((await state.ApplyAsync(checkpoint, lease)).Applied);
        }
    }

    private sealed class TestGatewayClock(DateTimeOffset initial) : TimeProvider
    {
        private long ticks;
        public override DateTimeOffset GetUtcNow() => initial.AddTicks(ticks);
        public override long GetTimestamp() => ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public void Advance(int seconds) => ticks += TimeSpan.FromSeconds(seconds).Ticks;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public PostgresDatabase Database { get; } = new(ContractDatabaseGroup.GovernanceSecurityAck);
        public ActorContext Actor { get; } = new(Guid.NewGuid(), "isolated-revocation");
        public GovernanceJournalId Journal { get; } = new(Guid.NewGuid(), Guid.NewGuid());
        public TestWitness Witness { get; } = new();
        public TestCoverage Coverage { get; } = new();
        public TestArchive Archive { get; } = new();
        public Guid CredentialId { get; } = Guid.NewGuid();
        public Guid EnvironmentId { get; } = Guid.NewGuid();
        public Guid RevokePermission { get; } = Guid.NewGuid();
        public Guid[] EnvironmentIds { get; private set; } = [];

        public static async Task<Fixture> StartAsync(bool withCurrentProof = true, bool withGovernanceSnapshot = true, bool global = false,
            TimeSpan? leaseLifetime = null, bool archived = false, bool includeCredentialInPublication = false)
        {
            var fixture = new Fixture();
            try { await fixture.InitializeAsync(withCurrentProof, withGovernanceSnapshot, global, leaseLifetime ?? TimeSpan.FromSeconds(30), archived, includeCredentialInPublication); return fixture; }
            catch { await fixture.DisposeAsync(); throw; }
        }

        private async Task InitializeAsync(bool withCurrentProof, bool withGovernanceSnapshot, bool global, TimeSpan leaseLifetime, bool archived,
            bool includeCredentialInPublication)
        {
            await Database.InitializeAsync();
            await using var db = Database.Context();
            await db.Database.MigrateAsync();
            var org = new Organization { Code = "REVOKE", Name = "Isolated" };
            var project = new Project { OrganizationId = org.Id, Code = "REV-PROJECT", Name = "Isolated" };
            var environment = new EnvironmentRecord { Id = EnvironmentId, ProjectId = project.Id, Code = "TEST", Name = "Isolated", DeploymentSequence = 1, DesiredConfigVersion = 1 };
            var app = new ApplicationRecord { OrganizationId = org.Id, ProjectId = global ? null : project.Id, Code = "APP", Name = "Isolated" };
            var role = new Role { OrganizationId = org.Id, Code = "SECURITY-FIXTURE", Name = "Isolated" };
            var permitRecovery = new Permission { Code = "credential.governance.recover", Module = "credential", Name = "Recover" };
            db.AddRange(org, project, environment, app, role, permitRecovery,
                new Permission { Id = RevokePermission, Code = "credential.revoke", Module = "credential", Name = "Revoke" },
                new UserRecord { Id = Actor.UserId, Username = "isolated-operator", DisplayName = "Isolated" },
                new UserRole { UserId = Actor.UserId, RoleId = role.Id },
                new RolePermission { RoleId = role.Id, PermissionId = RevokePermission },
                new RolePermission { RoleId = role.Id, PermissionId = permitRecovery.Id },
                new UserProjectScope { UserId = Actor.UserId, OrganizationId = org.Id, ProjectId = global ? null : project.Id,
                    EnvironmentId = global ? null : EnvironmentId, AccessMode = "read_write" });
            var credential = new ApplicationCredential { Id = CredentialId, ApplicationId = app.Id, OrganizationId = org.Id,
                ProjectId = global ? null : project.Id, EnvironmentId = global ? null : EnvironmentId,
                BindingMode = global ? "LegacyGlobal" : "EnvironmentBound", AccessKey = "isolated-access-key",
                SecretHash = new string('a', 64), ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) };
            db.Add(credential);
            var environments = new List<EnvironmentRecord> { environment };
            if (global)
            {
                var second = new Project { OrganizationId = org.Id, Code = "REV-SECOND", Name = "Isolated second project" };
                var secondEnvironment = new EnvironmentRecord { ProjectId = second.Id, Code = "SECOND", Name = "Isolated second environment",
                    DeploymentSequence = 1, DesiredConfigVersion = 1, Status = archived ? "Disabled" : "Active" };
                db.AddRange(second, secondEnvironment);
                environments.Add(secondEnvironment);
            }
            EnvironmentIds = environments.Select(x => x.Id).ToArray();
            Coverage.EnvironmentIds = global ? EnvironmentIds : [];
            foreach (var target in environments)
                db.Add(new EnvironmentSecurityInitialization { EnvironmentId = target.Id, ProjectId = target.ProjectId, OrganizationId = org.Id,
                    State = "FreshNonStrict", FreshProofId = Guid.NewGuid() });
            await db.SaveChangesAsync();
            // Dedicated permission for automated recovery of already prepared work is platform-scoped.
            var recoveryRole = new Role { Code = "PlatformAdmin", Name = "Recovery", IsSystem = true };
            db.AddRange(recoveryRole, new UserRole { UserId = Actor.UserId, RoleId = recoveryRole.Id },
                new RolePermission { RoleId = recoveryRole.Id, PermissionId = permitRecovery.Id });
            foreach (var target in environments)
                AddDeployment(db, org.Id, target, app, credential, withGovernanceSnapshot,
                    target == environment && global || withCurrentProof, leaseLifetime, includeCredentialInPublication);
            await db.SaveChangesAsync();
            await IsolatedGovernanceJournalSchema.InstallAsync(db, Journal);
        }

        private void AddDeployment(WebApiDbContext db, Guid organizationId, EnvironmentRecord environment, ApplicationRecord app,
            ApplicationCredential credential, bool withGovernanceSnapshot, bool withCurrentProof, TimeSpan leaseLifetime, bool includeCredentialInPublication)
        {
            var environmentId = environment.Id;
            var projectId = environment.ProjectId;
            var state = new EnvironmentSecurityState { EnvironmentId = environmentId, ProjectId = projectId, OrganizationId = organizationId };
            db.Add(state);
            RuntimeApplication[] applications = includeCredentialInPublication && withGovernanceSnapshot
                ? [new(app.Id, "Active", [new(credential.Id, credential.AccessKey, credential.SecretHash, credential.Status,
                    credential.ValidFrom, credential.ExpiresAt, environmentId, "EnvironmentBound")], [], environmentId)] : [];
            var snapshot = withGovernanceSnapshot
                ? new RuntimeSnapshot("2.4", environmentId, 1, DateTimeOffset.UtcNow, [], [], [], applications, state.SecurityGeneration, 0, false, "2.0")
                : new RuntimeSnapshot("2.3", environmentId, 1, DateTimeOffset.UtcNow, [], [], [], []);
            var bytes = CanonicalJson.Serialize(snapshot);
            var snapshotHash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            var version = new GatewayConfigVersion { EnvironmentId = environmentId, VersionNo = 1, CreatedBy = Actor.UserId,
                SnapshotHash = snapshotHash };
            var release = new ReleaseRecord { EnvironmentId = environmentId, ReleaseNo = $"isolated-{environment.Code}", RequestedBy = Actor.UserId,
                Status = "Succeeded", ReleaseType = "publish", ToConfigVersion = 1, DeploymentSequence = 1 };
            if (credential.BindingMode == "LegacyGlobal")
            {
                var frozenKey = new FrozenCredential(credential.Id, credential.AccessKey, credential.SecretHash, "last", "Active",
                    credential.ValidFrom, credential.ExpiresAt, credential.Revision);
                var frozen = new FrozenReleaseCandidate(environmentId, organizationId, projectId, 0, [], [], [], [], [], [],
                    [new(new(app.Id, organizationId, app.ProjectId, app.Code, app.Name, app.Owner, app.Status, app.Revision), [frozenKey], [])], []);
                release.CandidateBytes = CanonicalJson.Serialize(frozen);
            }
            db.AddRange(version, release, new GatewayConfigSnapshot { ConfigVersionId = version.Id, PayloadBytes = bytes, SizeBytes = bytes.Length });
            if (environment.Status == "Disabled") return;
            for (var number = 1; number <= 2; number++)
            {
                var instance = Guid.NewGuid();
                var node = new GatewayNode { EnvironmentId = environmentId, NodeName = $"isolated-{environment.Code}-{number}", Status = "Ready", InstanceId = instance.ToString(),
                    IdentityHash = new string('a', 64), CurrentConfigVersion = 1, CurrentDeploymentSequence = 1, LastHeartbeatAt = DateTimeOffset.UtcNow,
                    Metadata = SecurityProtocolCapabilities.Merge(SnapshotSchemaCapabilities.Merge(null, instance, ["2.4"]), instance, ["security-ledger-v1"]) };
                var issuedAt = DateTimeOffset.UtcNow.AddMilliseconds(-100);
                var lease = new SecurityNodeLeasePayload(Guid.NewGuid(), SecuritySignedEncoding.Protocol, environmentId, state.SecurityGeneration, 0,
                    new string('b', 64), node.Id, instance, SecuritySignedEncoding.Audience, issuedAt, issuedAt + leaseLifetime, "fixture");
                db.AddRange(node, new ReleaseTarget { ReleaseId = release.Id, NodeId = node.Id, InstanceId = node.InstanceId },
                    new GatewayAck { ReleaseId = release.Id, NodeId = node.Id, InstanceId = node.InstanceId, ConfigVersion = 1,
                        DeploymentSequence = 1, PayloadHash = snapshotHash, Success = true },
                    new GatewaySecurityLease { LeaseId = lease.LeaseId, NodeId = node.Id, InstanceId = instance, EnvironmentId = environmentId,
                        ProjectId = projectId, OrganizationId = organizationId, SecurityGeneration = lease.SecurityGeneration, CheckpointHash = lease.CheckpointHash,
                        IssuedAt = lease.IssuedAt, FreshUntil = lease.FreshUntil, LastIssuedLeaseUntil = lease.FreshUntil, SigningKeyId = lease.KeyId },
                    SecurityLeaseIssuanceEvidence.Create(lease, organizationId, projectId));
                if (withCurrentProof || number == 1)
                    db.Add(new GatewaySecurityCurrentReceipt { LeaseId = lease.LeaseId, NodeId = node.Id, InstanceId = instance, EnvironmentId = environmentId,
                        ProjectId = projectId, OrganizationId = organizationId, SecurityGeneration = lease.SecurityGeneration, CheckpointHash = lease.CheckpointHash,
                        HeadHash = state.HeadHash, FreshUntil = lease.FreshUntil, ReceivedAt = lease.IssuedAt });
            }
        }

        public PostgresCredentialRevocationService Service() => new(Database.ConnectionString, Journal, Witness, Coverage, Archive);

        public PostgresFormalRotationService RotationService(Func<WebApiDbContext, CancellationToken, Task<DateTimeOffset>>? clock = null) =>
            new(Database.ConnectionString, Journal, Witness, Coverage, new TestRotationHorizon { Approved = true }, Archive)
            { DeadlineClock = clock ?? SecurityDatabaseClock.ObserveAsync };

        public async Task<Guid> AddInheritedEnvironmentAsync()
        {
            await using var db = Database.Context();
            await using var tx = await db.Database.BeginTransactionAsync();
            await db.Database.ExecuteSqlRawAsync("SELECT pg_catalog.pg_advisory_xact_lock(8901202)");
            var origin = await db.Set<EnvironmentRecord>().SingleAsync(item => item.Id == EnvironmentId);
            var project = await db.Set<Project>().SingleAsync(item => item.Id == origin.ProjectId);
            var target = new EnvironmentRecord { ProjectId = project.Id, Code = "INHERITED", Name = "Inherited" };
            db.AddRange(target, new EnvironmentSecurityInitialization { EnvironmentId = target.Id, OrganizationId = project.OrganizationId,
                ProjectId = project.Id, State = "FreshNonStrict", FreshProofId = Guid.NewGuid() });
            await db.SaveChangesAsync();
            await GlobalCredentialInheritance.InitializeNewAsync(db, target.Id, new(project.OrganizationId, project.Id), Actor, default);
            await tx.CommitAsync();
            return target.Id;
        }

        public Task<RevocationOperationDto> RevokeAsync(string reason = "Retire isolated credential", Guid? environment = null, string tag = "\"1\"") =>
            Service().RevokeAsync(CredentialId, new(reason, environment ?? (EnvironmentIds.Length == 1 ? EnvironmentId : null)),
                tag, new("isolated-request"), Actor);

        public async Task AssertFactsAsync(int count)
        {
            await using var db = Database.Context();
            foreach (var environment in EnvironmentIds)
            {
                var ledger = await new SecurityLedgerReader(db).ReadCompleteAsync(environment);
                Assert.Equal(count, ledger.Epoch);
                Assert.Equal(count, ledger.Revocations.Count);
            }
            Assert.Equal(count, await db.Set<SecurityOperation>().CountAsync());
            Assert.Equal(count * EnvironmentIds.Length, await db.Set<SecurityOutboxMessage>().CountAsync());
            Assert.Equal(count * EnvironmentIds.Length, await db.Set<AuditLog>().CountAsync(x => x.Action == "credential.revoke"));
            Assert.Equal(count * EnvironmentIds.Length * 2, await db.Set<SecurityOperationTarget>().CountAsync());
            Assert.Equal(count == 0 ? "Active" : "Revoked", (await db.Set<ApplicationCredential>().SingleAsync()).Status);
        }

        public ValueTask DisposeAsync() => Database.DisposeAsync();
    }

    private sealed class TestArchive : ICredentialArchivedIngressWitness
    {
        public bool Approved;

        public CredentialArchivedIngressSeal? GetVerifiedSeal(CredentialArchivedIngressScope scope) => Approved ?
            new(scope, new string('d', 64), DateTimeOffset.UtcNow.AddSeconds(-10), DateTimeOffset.UtcNow.AddMinutes(2)) : null;
    }

    private sealed class IsolatedDeliveryContinuity(GovernanceJournalId journal) : IGovernanceRecoveryContinuity
    {
        public bool Available { get; set; } = true;
        public int Calls { get; private set; }

        public async Task<GovernanceContinuity> RequireAsync(WebApiDbContext db, Guid environmentId, CancellationToken ct = default)
        {
            Calls++;
            Assert.True(await db.Database.SqlQueryRaw<bool>("""
                SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_locks WHERE pid=pg_backend_pid() AND locktype='advisory'
                    AND classid=0 AND objid=8901202 AND objsubid=1 AND mode='ExclusiveLock' AND granted) AS "Value"
                """).SingleAsync(ct));
            if (!Available) throw new InvalidDataException("isolated-history-unavailable");
            return new(journal, GovernanceJournalProtocol.Genesis, await new SecurityLedgerReader(db).ReadCompleteLockedAsync(environmentId, ct));
        }
    }

    private sealed class TestRotationHorizon : IFormalRotationLeaseHorizonWitness
    {
        public bool Approved;

        public FormalRotationLeaseHorizonEvidence? GetVerifiedHorizon(FormalRotationLeaseScope scope)
        {
            var now = DateTimeOffset.UtcNow;
            return Approved ? new(scope, new string('e', 64), true, false,
                now.AddSeconds(-1), now.AddSeconds(1), now.AddSeconds(20), now.AddSeconds(30)) : null;
        }
    }

    private sealed class TestCoverage : ICredentialRevocationCoverage, ICredentialRevocationHistory
    {
        public bool Approved = true;
        public bool HistoryApproved = true;
        public Guid? RejectedEnvironment;
        public Guid[] EnvironmentIds = [];
        public bool IsVerified(CredentialRevocationHistoryScope scope) =>
            HistoryApproved && EnvironmentIds.Length > 1 && scope.Environments.Select(x => x.EnvironmentId).SequenceEqual(EnvironmentIds.Order()) &&
            scope.Artifacts.Count == EnvironmentIds.Length * 2;
        public bool IsVerified(CredentialRevocationCoverageScope scope) => Approved && scope.Nodes.Count == 2 && scope.ReleaseId != Guid.Empty &&
            scope.EnvironmentId != RejectedEnvironment &&
            SecuritySignedEncoding.HexHash(scope.SnapshotHash) && scope.Nodes.All(x => x.LeaseId != Guid.Empty) &&
            scope.DeploymentSequence == 1 && scope.ConfigVersion == 1;
    }

    private sealed class TestWitness : IFormalRotationJournalWitness
    {
        private readonly object sync = new();
        private readonly Dictionary<Guid, GovernanceOperationConfirmation> confirmations = [];
        private readonly Dictionary<Guid, (Guid EnvironmentId, DateTimeOffset Deadline)> formalSchedules = [];
        private GovernanceJournalPosition position = GovernanceJournalProtocol.Genesis;
        public GovernanceWriteIntent? Pending { get; private set; }
        public Guid? Operation { get; private set; }
        public string? Fault { get; set; }
        public DateTimeOffset? FormalNow { get; set; }
        public DateTimeOffset? LatestFormalAllowExpiry { get; set; }
        public int Preparations { get; private set; }
        public int Confirmations { get; private set; }
        public int FormalPreparations { get; private set; }
        public int FormalRevocationChecks { get; private set; }
        public int FormalRevocationPreparations { get; private set; }

        public Task<GovernanceWitnessState> ObserveAsync(GovernanceJournalId journal, Guid? operationId, CancellationToken ct)
        {
            lock (sync) return Task.FromResult(Observe(operationId));
        }

        public Task<GovernanceWitnessState> PrepareAsync(GovernanceJournalId journal, GovernanceWriteIntent intent, CancellationToken ct)
        {
            lock (sync) return Task.FromResult(Prepare(intent));
        }

        public Task<GovernanceWitnessState> PrepareFormalRotationAsync(GovernanceJournalId journal, GovernanceWriteIntent intent,
            Guid environmentId, DateTimeOffset hardDeadline, CancellationToken ct)
        {
            lock (sync)
            {
                Assert.Equal(position, intent.Previous);
                Assert.Null(Pending);
                if (LatestFormalAllowExpiry > hardDeadline.AddSeconds(-2))
                    throw new IOException("Fixture Allow expiry exceeds the cutoff.");
                formalSchedules.Add(intent.OperationId, (environmentId, hardDeadline));
                FormalPreparations++;
                return Task.FromResult(Prepare(intent));
            }
        }

        public Task<GovernanceWitnessState> PrepareFormalRotationRevocationAsync(GovernanceJournalId journal, GovernanceWriteIntent intent,
            Guid scheduleId, Guid environmentId, DateTimeOffset hardDeadline, CancellationToken ct)
        {
            lock (sync)
            {
                Assert.Equal((environmentId, hardDeadline), formalSchedules[scheduleId]);
                Assert.Contains(scheduleId, confirmations.Keys);
                FormalRevocationChecks++;
                if ((FormalNow ?? DateTimeOffset.UtcNow) < hardDeadline)
                    throw new IOException("Fixture independent deadline has not arrived.");
                FormalRevocationPreparations++;
                return Task.FromResult(Prepare(intent));
            }
        }

        private GovernanceWitnessState Prepare(GovernanceWriteIntent intent)
        {
            Assert.Equal(position, intent.Previous);
            Assert.Null(Pending);
            Pending = intent;
            Preparations++;
            if (Fault == "prepare-reply-lost") throw new IOException("Fixture witness preparation response lost.");
            return Observe(intent.OperationId);
        }

        public Task<GovernanceWitnessState> ConfirmAsync(GovernanceJournalId journal, GovernanceWriteIntent intent, CancellationToken ct)
        {
            lock (sync)
            {
                Assert.Equal(Pending, intent);
                if (Fault == "commit-before-confirm") throw new IOException("Fixture stopped before witness confirmation.");
                position = GovernanceJournalProtocol.NextPosition(journal, intent);
                confirmations.Add(intent.OperationId, new(intent.OperationId, position));
                Operation = intent.OperationId;
                Pending = null;
                Confirmations++;
                if (Fault == "confirmed-reply-lost") throw new IOException("Fixture witness confirmation response lost.");
                return Task.FromResult(Observe(intent.OperationId));
            }
        }

        private GovernanceWitnessState Observe(Guid? operationId) =>
            new(position, Pending, operationId is Guid id ? confirmations.GetValueOrDefault(id) : null);
    }
}
