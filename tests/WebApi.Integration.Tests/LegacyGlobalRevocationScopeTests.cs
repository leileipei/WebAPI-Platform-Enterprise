using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using WebApi.Contracts.Applications;
using WebApi.Contracts.Common;
using WebApi.Contracts.Releases;
using WebApi.Contracts.Runtime;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.CredentialSecurity;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;

namespace WebApi.Integration.Tests;

public sealed class LegacyGlobalRevocationScopeTests
{
    [Fact]
    public async Task DiscoversActiveAndHistoricalUseInTheWholeOrganization()
    {
        await using var fixture = await Fixture.StartAsync();
        var affected = await fixture.ReadAsync();
        Assert.Equal(fixture.Uses.Select(e => e.Id).Order(), affected);
        Assert.Equal(fixture.Uses.Append(fixture.Unused).Select(e => e.Id).Order(),
            fixture.History.Observed!.Environments.Select(e => e.EnvironmentId));
        Assert.Equal(["candidate", "snapshot"], fixture.History.Observed.Artifacts.Select(a => a.Kind).Order());
        await fixture.AssertNoWritesAsync();
    }

    [Theory]
    [InlineData("unverified-history")]
    [InlineData("missing-snapshot")]
    [InlineData("missing-referenced-version")]
    [InlineData("archived-target")]
    [InlineData("inactive-affected-project")]
    [InlineData("ambiguous-frozen-candidate")]
    [InlineData("caller-specified-environment")]
    public async Task IncompleteRangeCannotPassTheWritePrecheck(string failure)
    {
        await using var fixture = await Fixture.StartAsync();
        Guid? expectedEnvironment = null;
        await using (var db = fixture.Database.Context())
        {
            switch (failure)
            {
                case "unverified-history":
                    fixture.History.Accept = false;
                    // An ordinary working revocation does not establish any witnessed history.
                    var key = await db.Set<ApplicationCredential>().SingleAsync();
                    key.Status = "Revoked";
                    key.RevokedAt = Fixture.Time;
                    await db.SaveChangesAsync();
                    break;
                case "missing-snapshot":
                    await db.Set<GatewayConfigSnapshot>().ExecuteDeleteAsync();
                    break;
                case "missing-referenced-version":
                    await db.Set<EnvironmentRecord>().Where(x => x.Id == fixture.Unused.Id)
                        .ExecuteUpdateAsync(u => u.SetProperty(e => e.DesiredConfigVersion, (long?)2));
                    break;
                case "archived-target":
                    await db.Set<EnvironmentRecord>().Where(x => x.Id == fixture.Uses[0].Id)
                        .ExecuteUpdateAsync(u => u.SetProperty(e => e.Status, "Disabled"));
                    break;
                case "inactive-affected-project":
                    await db.Set<Project>().Where(x => x.Id == fixture.Uses[0].ProjectId)
                        .ExecuteUpdateAsync(u => u.SetProperty(p => p.Status, "Disabled"));
                    break;
                case "ambiguous-frozen-candidate":
                    var malformedCandidate = "{}"u8.ToArray();
                    await db.Set<ReleaseRecord>().ExecuteUpdateAsync(u => u.SetProperty(x => x.CandidateBytes, malformedCandidate));
                    break;
                case "caller-specified-environment":
                    expectedEnvironment = fixture.Uses[0].Id;
                    break;
            }
        }

        var error = await Assert.ThrowsAsync<ApiException>(() => fixture.ReadAsync(expectedEnvironment));
        Assert.Equal(409, error.Status);
        Assert.Equal("credential_revocation_scope_unknown", error.Code);
        Assert.All(fixture.Uses, env => Assert.DoesNotContain(env.Id.ToString(), error.Message));
        await fixture.AssertNoWritesAsync();
    }

    [Fact]
    public async Task LocalHistoryGrowthNeedsARefreshedIndependentInventory()
    {
        await using var fixture = await Fixture.StartAsync();
        await fixture.ReadAsync();
        fixture.History.PinObservedInventory();

        await using (var db = fixture.Database.Context())
        {
            var version = new GatewayConfigVersion { EnvironmentId = fixture.Unused.Id, CreatedBy = fixture.Actor.UserId };
            var snapshot = new RuntimeSnapshot("2.3", fixture.Unused.Id, version.VersionNo, Fixture.Time, [], [], [], []);
            var bytes = CanonicalJson.Serialize(snapshot);
            version.SnapshotHash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            db.AddRange(version, new GatewayConfigSnapshot { ConfigVersionId = version.Id, PayloadBytes = bytes,
                SizeBytes = bytes.Length });
            await db.SaveChangesAsync();
        }

        var error = await Assert.ThrowsAsync<ApiException>(() => fixture.ReadAsync());
        Assert.Equal("credential_revocation_scope_unknown", error.Code);
        Assert.Equal(3, fixture.History.Observed!.Artifacts.Count);
        await fixture.AssertNoWritesAsync();
    }

    [Fact]
    public async Task InvisibleScopeDoesNotDiscloseAnyDiscoveredEnvironments()
    {
        await using var fixture = await Fixture.StartAsync();
        await using (var db = fixture.Database.Context())
            await db.Set<UserProjectScope>().ExecuteUpdateAsync(u => u.SetProperty(x => x.ProjectId, fixture.Uses[0].ProjectId));

        var error = await Assert.ThrowsAsync<ApiException>(() => fixture.ReadAsync());
        Assert.Equal(403, error.Status);
        Assert.Equal("scope_denied", error.Code);
        Assert.Null(fixture.History.Observed);
        await fixture.AssertNoWritesAsync();
    }

    [Fact]
    public async Task ResolverRequiresTheWriterFenceAndDoesNotEnableTheActualMutation()
    {
        await using var fixture = await Fixture.StartAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ReadAsync(useWriterFence: false));
        Assert.Null(fixture.History.Observed);

        await using var db = fixture.Database.Context();
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SELECT pg_catalog.pg_advisory_xact_lock(8901202)");
        var mutation = new CredentialRevocationMutation(fixture.Actor, fixture.History);
        var denied = await Assert.ThrowsAsync<ApiException>(
            () => mutation.RequireScopeAsync(db, fixture.Credential.Id, null, default));
        Assert.Equal("credential_revocation_scope_unknown", denied.Code);
        Assert.NotNull(fixture.History.Observed);
        await tx.RollbackAsync();
        await fixture.AssertNoWritesAsync();
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal static readonly DateTimeOffset Time = new(2026, 11, 18, 0, 0, 0, TimeSpan.Zero);
        internal PostgresDatabase Database { get; } = new(ContractDatabaseGroup.GovernanceSecurityAck);
        internal ActorContext Actor { get; } = new(Guid.NewGuid(), "legacy-global-scope");
        internal ApplicationCredential Credential { get; } = new();
        internal EnvironmentRecord[] Uses { get; } = new EnvironmentRecord[4];
        internal EnvironmentRecord Unused { get; private set; } = null!;
        internal HistoryProof History { get; } = new();

        internal static async Task<Fixture> StartAsync()
        {
            var fixture = new Fixture();
            try { await fixture.SeedAsync(); return fixture; }
            catch { await fixture.DisposeAsync(); throw; }
        }

        private async Task SeedAsync()
        {
            await Database.InitializeAsync();
            await using var db = Database.Context();
            await db.Database.MigrateAsync();
            var org = new Organization { Code = "GLOBAL", Name = "Isolated organization" };
            var app = new ApplicationRecord { Code = "APP", Name = "Isolated application", OrganizationId = org.Id };
            var firstProject = new Project { Code = "P1", Name = "First project", OrganizationId = org.Id };
            var secondProject = new Project { Code = "P2", Name = "Second project", OrganizationId = org.Id };
            Uses[0] = Environment(firstProject.Id, "GRANT");
            Uses[1] = Environment(firstProject.Id, "MAPPING");
            Uses[2] = Environment(secondProject.Id, "FROZEN");
            Uses[3] = Environment(secondProject.Id, "SNAPSHOT");
            Unused = Environment(secondProject.Id, "UNUSED");
            Credential.ApplicationId = app.Id;
            Credential.OrganizationId = org.Id;
            Credential.AccessKey = "fixture-identifier";
            Credential.SecretHash = new string('a', 64);
            Credential.ValidFrom = Time;
            Credential.ExpiresAt = Time.AddDays(1);
            var role = new Role { Code = "GLOBAL-FIXTURE", Name = "Global test role", OrganizationId = org.Id };
            var permission = await db.Set<Permission>().SingleOrDefaultAsync(x => x.Code == "credential.revoke");
            if (permission is null)
            {
                permission = new() { Code = "credential.revoke", Module = "credential", Name = "Revoke test permission" };
                db.Add(permission);
            }

            var api = new Api { Code = "TEST", Name = "Fixture API", OrganizationId = org.Id, ProjectId = firstProject.Id,
                LifecycleStatus = "Active", OwnerUserId = Actor.UserId };
            var version = new ApiVersion { ApiId = api.Id, Version = "1.0", CreatedBy = Actor.UserId, ChangeType = "initial" };
            var cluster = new UpstreamCluster { ProjectId = firstProject.Id, EnvironmentId = Uses[1].Id, Name = "Fixture upstream",
                LoadBalancingPolicy = "RoundRobin", HealthCheckPath = "/", HealthCheckIntervalSec = 30 };
            var route = new ApiRoute { ApiId = api.Id, ApiVersionId = version.Id, EnvironmentId = Uses[1].Id,
                RouteName = "Fixture JWT", Path = "/fixture", NormalizedPath = "/fixture", TimeoutMs = 1000,
                AuthenticationMode = "JWT", Methods = ["GET"], ClusterId = cluster.Id };
            var policy = new Policy { OrganizationId = org.Id, ProjectId = firstProject.Id, Name = "JWT mapping",
                Type = "authentication", Config = JwtApplicationBindingTests.Jwt(app.Id) };
            db.AddRange(org, firstProject, secondProject, app, Credential, role, api, version, cluster, route, policy, Unused,
                new UserRecord { Id = Actor.UserId, Username = "global-revocation-operator" },
                new UserRole { UserId = Actor.UserId, RoleId = role.Id },
                new RolePermission { RoleId = role.Id, PermissionId = permission.Id },
                new UserProjectScope { UserId = Actor.UserId, OrganizationId = org.Id, AccessMode = "read_write" },
                new ApplicationApiPermission { ApplicationId = app.Id, ApiId = api.Id, EnvironmentId = Uses[0].Id,
                    CreatedBy = Actor.UserId, Mode = "LegacyApi" },
                new RoutePolicyBinding { RouteId = route.Id, PolicyId = policy.Id });
            db.AddRange(Uses);

            var frozenCredential = new FrozenCredential(Credential.Id, Credential.AccessKey, Credential.SecretHash, "last",
                "Active", Time, Time.AddDays(1), 1);
            var frozen = new FrozenReleaseCandidate(Uses[2].Id, org.Id, secondProject.Id, 0, [], [], [], [], [], [],
                [new(new(app.Id, org.Id, null, app.Code, app.Name, app.Owner, app.Status, app.Revision), [frozenCredential], [])], []);
            var runtime = new RuntimeSnapshot("2.3", Uses[3].Id, 1, Time, [], [], [],
                [new(app.Id, "Active", [new(Credential.Id, Credential.AccessKey, Credential.SecretHash, "Active", Time, Time.AddDays(1))], [])]);
            var bytes = CanonicalJson.Serialize(runtime);
            var configuration = new GatewayConfigVersion { EnvironmentId = Uses[3].Id, CreatedBy = Actor.UserId,
                SnapshotHash = Convert.ToHexStringLower(SHA256.HashData(bytes)) };
            db.AddRange(new ReleaseRecord { EnvironmentId = Uses[2].Id, ReleaseNo = "FROZEN", ReleaseType = "publish",
                    Status = "WaitingApproval", RequestedBy = Actor.UserId, CandidateBytes = CanonicalJson.Serialize(frozen) },
                configuration, new GatewayConfigSnapshot { ConfigVersionId = configuration.Id, PayloadBytes = bytes, SizeBytes = bytes.Length });
            await db.SaveChangesAsync();
        }

        private static EnvironmentRecord Environment(Guid project, string code) => new() { ProjectId = project, Code = code, Name = code };

        internal async Task<IReadOnlyList<Guid>> ReadAsync(Guid? expectedEnvironment = null, bool useWriterFence = true)
        {
            await using var db = Database.Context();
            await using var tx = await db.Database.BeginTransactionAsync();
            if (useWriterFence) await db.Database.ExecuteSqlRawAsync("SELECT pg_catalog.pg_advisory_xact_lock(8901202)");
            var key = await db.Set<ApplicationCredential>().SingleAsync();
            var app = await db.Set<ApplicationRecord>().SingleAsync();
            return await LegacyGlobalRevocationScope.ReadAsync(db, key, app, expectedEnvironment, Actor, History, default);
        }

        internal async Task AssertNoWritesAsync()
        {
            await using var db = Database.Context();
            Assert.Empty(await db.Set<SecurityOperation>().ToArrayAsync());
            Assert.Empty(await db.Set<SecurityEvent>().ToArrayAsync());
            Assert.Empty(await db.Set<CredentialRevocation>().ToArrayAsync());
        }

        public ValueTask DisposeAsync() => Database.DisposeAsync();
    }

    private sealed class HistoryProof : ICredentialRevocationCoverage, ICredentialRevocationHistory
    {
        private byte[]? pinned;
        internal bool Accept { get; set; } = true;
        internal CredentialRevocationHistoryScope? Observed { get; private set; }
        internal void PinObservedInventory() => pinned = CanonicalJson.Serialize(Observed);

        public bool IsVerified(CredentialRevocationHistoryScope scope)
        {
            Observed = scope;
            return Accept && (pinned is null || CanonicalJson.Serialize(scope).AsSpan().SequenceEqual(pinned));
        }
        public bool IsVerified(CredentialRevocationCoverageScope scope) => false;
    }
}
