using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Releases;
using WebApi.Contracts.Runtime;
using WebApi.Contracts.Security;
using WebApi.Domain.CredentialSecurity;
using WebApi.Infrastructure.CredentialSecurity;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;

namespace WebApi.Integration.Tests;

public sealed class CredentialArchivedIngressTests(MigratedDatabaseTemplateFixture template) : IClassFixture<MigratedDatabaseTemplateFixture>
{
    // The database enforces issue timestamps no later than its real clock.
    private static readonly DateTimeOffset Now = new(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ADisabledEnvironmentWithExpiredLeasesRequiresAnExactIndependentSeal()
    {
        await using var database = await template.Template.CreateDatabaseAsync();
        var (environment, organizationId, generation) = await SeedAsync(database);
        var witness = new Witness();

        await AssertBlockedAsync(() => VerifyAsync(database, environment.Id, organizationId, null));
        await AssertBlockedAsync(() => VerifyAsync(database, environment.Id, organizationId, witness));
        var scope = Assert.IsType<CredentialArchivedIngressScope>(witness.Requested);
        Assert.Equal(environment.Id, scope.EnvironmentId);
        Assert.Equal(environment.ProjectId, scope.ProjectId);
        Assert.Equal(organizationId, scope.OrganizationId);
        Assert.Equal(generation, scope.SecurityGeneration);
        Assert.Equal(SecurityEventHash.Genesis, scope.HeadHash);
        Assert.Equal(environment.Revision, scope.Revision);
        Assert.Equal(environment.AuthorizationRevision, scope.AuthorizationRevision);
        Assert.Equal(environment.DesiredConfigVersion, scope.DesiredConfigVersion);
        Assert.Equal(environment.DeploymentSequence, scope.DeploymentSequence);
        Assert.True(SecuritySignedEncoding.HexHash(scope.HistoricalInventoryHash));
        Assert.True(SecuritySignedEncoding.HexHash(scope.NodeLeaseInventoryHash));

        witness.Seal = Seal(scope);
        Assert.Equal(witness.Seal, await VerifyAsync(database, environment.Id, organizationId, witness));
    }

    [Theory]
    [InlineData("active-environment")]
    [InlineData("enabled-node")]
    [InlineData("live-lease")]
    [InlineData("lease-within-skew")]
    [InlineData("incomplete-issuance")]
    [InlineData("uninitialized")]
    [InlineData("missing-snapshot")]
    [InlineData("wrong-organization")]
    [InlineData("shared-lock")]
    public async Task LocalFactsMustBeCompleteAndQuiescentBeforeConsultingTheWitness(string fault)
    {
        await using var database = await template.Template.CreateDatabaseAsync();
        var (environment, organizationId, _) = await SeedAsync(database, fault);
        var witness = new Witness();

        await AssertBlockedAsync(() => VerifyAsync(database, environment.Id,
            fault == "wrong-organization" ? Guid.NewGuid() : organizationId, witness, sharedLock: fault == "shared-lock"));
        Assert.Null(witness.Requested);
    }

    [Theory]
    [InlineData("other-environment")]
    [InlineData("other-ledger-head")]
    [InlineData("other-history")]
    [InlineData("expired")]
    [InlineData("future")]
    [InlineData("non-utc")]
    [InlineData("empty-proof")]
    public async Task RejectsAnIncorrectOrStaleProviderSeal(string fault)
    {
        await using var database = await template.Template.CreateDatabaseAsync();
        var (environment, organizationId, _) = await SeedAsync(database);
        var witness = new Witness();
        await AssertBlockedAsync(() => VerifyAsync(database, environment.Id, organizationId, witness));
        var scope = Assert.IsType<CredentialArchivedIngressScope>(witness.Requested);
        var seal = Seal(scope);
        witness.Seal = fault switch
        {
            "other-environment" => seal with { Scope = scope with { EnvironmentId = Guid.NewGuid() } },
            "other-ledger-head" => seal with { Scope = scope with { HeadHash = new string('e', 64) } },
            "other-history" => seal with { Scope = scope with { HistoricalInventoryHash = new string('e', 64) } },
            "expired" => seal with { ValidUntil = Now },
            "future" => seal with { VerifiedAt = Now.AddSeconds(1) },
            "non-utc" => seal with { VerifiedAt = seal.VerifiedAt.ToOffset(TimeSpan.FromHours(1)) },
            "empty-proof" => seal with { ProofHash = string.Empty },
            _ => throw new InvalidOperationException("Unknown fixture fault.")
        };

        await AssertBlockedAsync(() => VerifyAsync(database, environment.Id, organizationId, witness));
    }

    [Fact]
    public async Task ANewDisabledNodeAndAHistoricalArtifactChangeInvalidateAnEarlierSeal()
    {
        await using var database = await template.Template.CreateDatabaseAsync();
        var (environment, organizationId, _) = await SeedAsync(database);
        var witness = new Witness();
        await AssertBlockedAsync(() => VerifyAsync(database, environment.Id, organizationId, witness));
        var original = Assert.IsType<CredentialArchivedIngressScope>(witness.Requested);
        witness.Seal = Seal(original);
        await using (var db = database.Context())
        {
            db.Add(NewNode(environment.Id));
            await db.SaveChangesAsync();
        }

        await AssertBlockedAsync(() => VerifyAsync(database, environment.Id, organizationId, witness));
        var withNode = Assert.IsType<CredentialArchivedIngressScope>(witness.Requested);
        Assert.NotEqual(original.NodeLeaseInventoryHash, withNode.NodeLeaseInventoryHash);
        Assert.Equal(original.HistoricalInventoryHash, withNode.HistoricalInventoryHash);
        witness.Seal = Seal(withNode);
        await using (var db = database.Context())
        {
            var userId = await db.Set<UserRecord>().Where(user => user.Username == "archive-operator").Select(user => user.Id).SingleAsync();
            AddPublishedVersion(db, environment, organizationId, userId, 2);
            await db.SaveChangesAsync();
        }

        await AssertBlockedAsync(() => VerifyAsync(database, environment.Id, organizationId, witness));
        var withHistory = Assert.IsType<CredentialArchivedIngressScope>(witness.Requested);
        Assert.NotEqual(withNode.HistoricalInventoryHash, withHistory.HistoricalInventoryHash);
        Assert.Equal(withNode.NodeLeaseInventoryHash, withHistory.NodeLeaseInventoryHash);
    }

    [Fact]
    public async Task AnEarlierSealCannotIgnoreARegisteredInstanceWithoutALease()
    {
        await using var database = await template.Template.CreateDatabaseAsync();
        var (environment, organizationId, _) = await SeedAsync(database);
        var witness = new Witness();
        await AssertBlockedAsync(() => VerifyAsync(database, environment.Id, organizationId, witness));
        var original = Assert.IsType<CredentialArchivedIngressScope>(witness.Requested);
        witness.Seal = Seal(original);
        Assert.Equal(witness.Seal, await VerifyAsync(database, environment.Id, organizationId, witness));

        await using (var db = database.Context())
        {
            var nodeId = await db.Set<GatewayNode>().Where(node => node.EnvironmentId == environment.Id).Select(node => node.Id).SingleAsync();
            db.Add(new GatewayNodeEvent { GatewayNodeId = nodeId, EventType = "registered", CreatedAt = Now.AddHours(-1),
                Detail = JsonSerializer.Serialize(new { instanceId = Guid.NewGuid() }) });
            await db.SaveChangesAsync();
        }

        await AssertBlockedAsync(() => VerifyAsync(database, environment.Id, organizationId, witness));
        var updated = Assert.IsType<CredentialArchivedIngressScope>(witness.Requested);
        Assert.NotEqual(original.NodeLeaseInventoryHash, updated.NodeLeaseInventoryHash);
        Assert.Equal(original with { NodeLeaseInventoryHash = updated.NodeLeaseInventoryHash }, updated);
    }

    private static CredentialArchivedIngressSeal Seal(CredentialArchivedIngressScope scope) =>
        new(scope, new string('b', 64), Now.AddSeconds(-1), Now.AddSeconds(20));

    private static async Task AssertBlockedAsync(Func<Task> action)
    {
        var denied = await Assert.ThrowsAsync<ApiException>(action);
        Assert.Equal(409, denied.Status);
        Assert.Equal("credential_revocation_archive_unverified", denied.Code);
    }

    private static async Task<CredentialArchivedIngressSeal> VerifyAsync(PostgresDatabase database, Guid environmentId,
        Guid organizationId, ICredentialArchivedIngressWitness? witness, bool sharedLock = false)
    {
        await using var db = database.Context();
        await using var transaction = await db.Database.BeginTransactionAsync();
        if (sharedLock) await db.Database.ExecuteSqlRawAsync("SELECT pg_catalog.pg_advisory_xact_lock_shared(8901202)");
        else await db.Database.ExecuteSqlRawAsync("SELECT pg_catalog.pg_advisory_xact_lock(8901202)");
        var environment = await db.Set<EnvironmentRecord>().SingleAsync(row => row.Id == environmentId);
        return await CredentialArchivedIngress.RequireAsync(db, environment, organizationId, witness, Now, default);
    }

    private static async Task<(EnvironmentRecord Environment, Guid OrganizationId, Guid Generation)> SeedAsync(
        PostgresDatabase database, string? fault = null)
    {
        await using var db = database.Context();
        var organization = new Organization { Code = "ARCHIVE-ORG", Name = "Isolated archive" };
        var project = new Project { OrganizationId = organization.Id, Code = "ARCHIVE-PROJ", Name = "Isolated archive" };
        var user = new UserRecord { Username = "archive-operator", DisplayName = "Isolated" };
        var environment = new EnvironmentRecord { ProjectId = project.Id, Code = "ARCHIVE", Name = "Isolated archive",
            Status = fault == "active-environment" ? "Active" : "Disabled", DesiredConfigVersion = 1, DeploymentSequence = 1,
            GatewayPublicUrl = "https://archived-gateway.example.test", Revision = 3, AuthorizationRevision = 2 };
        var authority = new EnvironmentSecurityState
        { OrganizationId = organization.Id, ProjectId = project.Id, EnvironmentId = environment.Id, UpdatedAt = Now };
        db.AddRange(organization, project, user, environment);
        AddPublishedVersion(db, environment, organization.Id, user.Id, 1, fault == "missing-snapshot");
        var node = NewNode(environment.Id);
        node.Enabled = fault == "enabled-node";
        db.Add(node);
        if (fault == "uninitialized")
        {
            await db.SaveChangesAsync();
            return (environment, organization.Id, Guid.Empty);
        }

        db.AddRange(authority, new EnvironmentSecurityInitialization { EnvironmentId = environment.Id, ProjectId = project.Id,
            OrganizationId = organization.Id, State = "FreshNonStrict", FreshProofId = Guid.NewGuid() });
        var expiry = fault switch
        {
            "live-lease" => Now.AddSeconds(5),
            "lease-within-skew" => Now.AddSeconds(-1),
            _ => Now.AddSeconds(-10)
        };
        var lease = new SecurityNodeLeasePayload(Guid.NewGuid(), SecuritySignedEncoding.Protocol, environment.Id, authority.SecurityGeneration,
            0, new string('c', 64), node.Id, Guid.Parse(node.InstanceId), SecuritySignedEncoding.Audience, Now.AddSeconds(-15), expiry, "archive-test");
        db.Add(new GatewaySecurityLease { LeaseId = lease.LeaseId, NodeId = node.Id, InstanceId = lease.InstanceId,
            EnvironmentId = environment.Id, ProjectId = project.Id, OrganizationId = organization.Id, SecurityGeneration = authority.SecurityGeneration,
            CheckpointHash = lease.CheckpointHash, IssuedAt = lease.IssuedAt, FreshUntil = expiry, LastIssuedLeaseUntil = expiry, SigningKeyId = lease.KeyId });
        if (fault != "incomplete-issuance") db.Add(SecurityLeaseIssuanceEvidence.Create(lease, organization.Id, project.Id));
        await db.SaveChangesAsync();
        return (environment, organization.Id, authority.SecurityGeneration);
    }

    private static GatewayNode NewNode(Guid environmentId) => new()
    {
        EnvironmentId = environmentId, NodeName = $"archive-{Guid.NewGuid():N}", InstanceId = Guid.NewGuid().ToString(),
        CurrentConfigVersion = 1, CurrentDeploymentSequence = 1, Enabled = false, Status = "Ready", IdentityHash = new string('a', 64)
    };

    private static void AddPublishedVersion(WebApiDbContext db, EnvironmentRecord environment, Guid organizationId, Guid userId,
        long versionNo, bool omitSnapshot = false)
    {
        var bytes = CanonicalJson.Serialize(new RuntimeSnapshot("2.3", environment.Id, versionNo, Now.AddHours(-1), [], [], [], []));
        var version = new GatewayConfigVersion { EnvironmentId = environment.Id, VersionNo = versionNo, CreatedBy = userId,
            SnapshotHash = Convert.ToHexStringLower(SHA256.HashData(bytes)) };
        var candidate = new FrozenReleaseCandidate(environment.Id, organizationId, environment.ProjectId, 0, [], [], [], [], [], [], [], []);
        db.AddRange(version, new ReleaseRecord { EnvironmentId = environment.Id, ReleaseNo = "ARCHIVE-" + versionNo,
            RequestedBy = userId, Status = "Succeeded", ReleaseType = "publish", ToConfigVersion = versionNo,
            DeploymentSequence = versionNo == environment.DesiredConfigVersion ? environment.DeploymentSequence : null,
            CandidateBytes = CanonicalJson.Serialize(candidate) });
        if (!omitSnapshot) db.Add(new GatewayConfigSnapshot { ConfigVersionId = version.Id, PayloadBytes = bytes, SizeBytes = bytes.Length });
    }

    private sealed class Witness : ICredentialArchivedIngressWitness
    {
        public CredentialArchivedIngressScope? Requested { get; private set; }
        public CredentialArchivedIngressSeal? Seal { get; set; }

        public CredentialArchivedIngressSeal? GetVerifiedSeal(CredentialArchivedIngressScope scope)
        {
            Requested = scope;
            return Seal;
        }
    }
}
