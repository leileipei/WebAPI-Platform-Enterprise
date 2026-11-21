using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Security.Cryptography;
using WebApi.Contracts.Runtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Contracts.Applications;
using WebApi.Contracts.Catalog;
using WebApi.Contracts.Common;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;

namespace WebApi.Integration.Tests;

public sealed class ApplicationPermissionMatrixTests
{
    private static string Root(ApiFixture f) => $"/api/v1/applications/{f.Application.Id}/permission-matrix";

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        using (response)
        {
            Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode} {await response.Content.ReadAsStringAsync()}");
            Assert.True(response.Headers.CacheControl?.NoStore);
            return await response.Content.ReadFromJsonAsync<JsonElement>();
        }
    }

    [Fact]
    public async Task MatrixPagesUseStableApiOrderAndRequireEnvironmentRead()
    {
        await using var f = new ApiFixture();
        await f.InitializeAsync();
        await f.SeedConsumerAsync(seedFreshEnvironment: true);
        using var login = await f.LoginAsync();
        login.EnsureSuccessStatusCode();
        await using var db = f.Context();
        var extra = new Api { OrganizationId = f.Organization.Id, ProjectId = f.Project.Id, Code = "AAA", Name = "另一 API", LifecycleStatus = "Draft", OwnerUserId = f.User.Id };
        db.Add(extra);
        await db.SaveChangesAsync();
        var url = $"{Root(f)}?projectId={f.Project.Id}&pageSize=1";
        var first = await JsonAsync(await f.Client.GetAsync(url));
        Assert.Equal(2, first.GetProperty("total").GetInt32());
        Assert.Equal(extra.Id, first.GetProperty("items")[0].GetProperty("id").GetGuid());
        Assert.True(first.GetProperty("environments")[0].GetProperty("canManage").GetBoolean());
        var second = await JsonAsync(await f.Client.GetAsync($"{url}&page=2"));
        Assert.Equal(f.Api.Id, second.GetProperty("items")[0].GetProperty("id").GetGuid());
        var read = await db.Set<Permission>().SingleAsync(p => p.Code == "environment.read");
        await db.Set<RolePermission>().Where(p => p.PermissionId == read.Id).ExecuteDeleteAsync();
        var restricted = await JsonAsync(await f.Client.GetAsync(url));
        Assert.Empty(restricted.GetProperty("environments").EnumerateArray());
        Assert.Empty(restricted.GetProperty("items")[0].GetProperty("grants").EnumerateArray());
        var apiRead = await db.Set<Permission>().SingleAsync(p => p.Code == "api.read");
        await db.Set<RolePermission>().Where(p => p.PermissionId == apiRead.Id).ExecuteDeleteAsync();
        using var denied = await f.Client.GetAsync(url);
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
    }

    [Fact]
    public async Task ProductionBatchRequiresConfirmationReplaysReceiptAndRevokesParentAndMethods()
    {
        await using var f = new ApiFixture();
        await f.InitializeAsync();
        await f.SeedReleaseAsync();
        using var login = await f.LoginAsync();
        login.EnsureSuccessStatusCode();
        using var created = await f.WriteAsync(HttpMethod.Post, $"/api/v1/environments/{f.Environment.Id}/routes", f.RouteBody("/orders"));
        created.EnsureSuccessStatusCode();
        var route = (await created.Content.ReadFromJsonAsync<RouteDto>())!;
        var choices = await f.Client.GetFromJsonAsync<RouteMethodOptionsDto>($"/api/v1/applications/{f.Application.Id}/route-method-options?environmentId={f.Environment.Id}&apiId={f.Api.Id}");
        var choice = Assert.Single(choices!.Routes);
        var changes = new[] { new { apiId = f.Api.Id, environmentId = f.Environment.Id, action = "Save", mode = "RouteMethod",
            validFrom = DateTimeOffset.UtcNow.AddDays(-2), expiresAt = DateTimeOffset.UtcNow.AddDays(-1),
            routeMethods = new[] { new { routeId = route.Id, authorizationRevision = choice.AuthorizationRevision, methods = new[] {"GET"} } } } };
        var root = Root(f);
        var preview = await JsonAsync(await f.WriteAsync(HttpMethod.Post, root + "/preview", new { projectId = f.Project.Id, changes }));
        Assert.True(preview.GetProperty("requiresProductionConfirmation").GetBoolean());
        var impact = preview.GetProperty("items")[0];
        Assert.Equal("Expired", impact.GetProperty("after").GetProperty("state").GetString());
        Assert.Equal("NotVisible", impact.GetProperty("running").GetProperty("status").GetString());
        var previewToken = preview.GetProperty("previewToken").GetString();
        var key = Guid.NewGuid().ToString("N");
        using var denied = await ApiFixture.CommandAsync(f.Client, root + "/commit", new { projectId = f.Project.Id, changes, previewToken, confirmProduction = false }, key);
        Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);
        var commit = new { projectId = f.Project.Id, changes, previewToken, confirmProduction = true };
        var receipt = await JsonAsync(await ApiFixture.CommandAsync(f.Client, root + "/commit", commit, key));
        Assert.Equal(1, receipt.GetProperty("appliedCount").GetInt32());
        var replay = await JsonAsync(await ApiFixture.CommandAsync(f.Client, root + "/commit", commit, key));
        Assert.Equal("WorkingConfiguration", replay.GetProperty("activation").GetString());
        await using var db = f.Context();
        var grant = await db.Set<ApplicationApiPermission>().AsNoTracking().SingleAsync();
        Assert.Single(await db.Set<ApplicationRouteMethodGrant>().ToArrayAsync());
        Assert.Single(await db.Set<AuditLog>().Where(x => x.Action == "app.permission.route-method.save").ToArrayAsync());
        // Window edits preserve the approved route set. A later route edit invalidates this preview.
        var window = new[] {new {apiId=f.Api.Id, environmentId=f.Environment.Id, action="SetWindow",
            permissionId=grant.Id, revision=grant.Revision, validFrom=DateTimeOffset.UtcNow.AddHours(-1)}};
        var windowPreview = await JsonAsync(await f.WriteAsync(HttpMethod.Post, root + "/preview", new { projectId = f.Project.Id, changes = window }));
        var after = windowPreview.GetProperty("items")[0].GetProperty("after");
        Assert.Equal("Active", after.GetProperty("state").GetString());
        Assert.Equal("/orders", after.GetProperty("methods")[0].GetProperty("path").GetString());
        Assert.True(after.GetProperty("methods")[0].GetProperty("matchesRoute").GetBoolean());
        using var updated = await f.WriteAsync(HttpMethod.Put, $"/api/v1/routes/{route.Id}", f.RouteBody("/orders-v2", id: route.Id), RevisionTag.Format(route.Revision));
        updated.EnsureSuccessStatusCode();
        using var stale = await ApiFixture.CommandAsync(f.Client, root + "/commit", new
        {
            projectId = f.Project.Id,
            changes = window,
            previewToken = windowPreview.GetProperty("previewToken").GetString(),
            confirmProduction = true
        });
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        var inspect = new[] { new { apiId = f.Api.Id, environmentId = f.Environment.Id, action = "Revoke", permissionId = grant.Id, revision = grant.Revision } };
        var changedPreview = await JsonAsync(await f.WriteAsync(HttpMethod.Post, root + "/preview", new { projectId = f.Project.Id, changes = inspect }));
        var oldRoute = changedPreview.GetProperty("items")[0].GetProperty("before").GetProperty("methods")[0];
        Assert.False(oldRoute.GetProperty("matchesRoute").GetBoolean());
        Assert.Equal(JsonValueKind.Null, oldRoute.GetProperty("path").ValueKind);
        var updatedOptions = await f.Client.GetFromJsonAsync<RouteMethodOptionsDto>(
            $"/api/v1/applications/{f.Application.Id}/route-method-options?environmentId={f.Environment.Id}&apiId={f.Api.Id}");
        var updatedChoice = Assert.Single(updatedOptions!.Routes);
        var regrant = new[] { new { apiId = f.Api.Id, environmentId = f.Environment.Id, action = "Save", mode = "RouteMethod",
            permissionId = grant.Id, revision = grant.Revision, validFrom = grant.ValidFrom,
            routeMethods = new[] { new { routeId = route.Id, authorizationRevision = updatedChoice.AuthorizationRevision, methods = new[] { "GET" } } } } };
        var regrantPreview = await JsonAsync(await f.WriteAsync(HttpMethod.Post, root + "/preview", new { projectId = f.Project.Id, changes = regrant }));
        var reviewed = regrantPreview.GetProperty("items")[0];
        Assert.False(reviewed.GetProperty("before").GetProperty("methods")[0].GetProperty("matchesRoute").GetBoolean());
        Assert.True(reviewed.GetProperty("after").GetProperty("methods")[0].GetProperty("matchesRoute").GetBoolean());
        Assert.Equal("/orders-v2", reviewed.GetProperty("after").GetProperty("methods")[0].GetProperty("path").GetString());
        // A prior success cannot be replayed after route-read has been revoked.
        var roleId = await db.Set<UserRole>().Where(x => x.UserId == f.User.Id).Select(x => x.RoleId).SingleAsync();
        var routeRead = await db.Set<Permission>().SingleAsync(p => p.Code == "route.read");
        await db.Set<RolePermission>().Where(p => p.PermissionId == routeRead.Id && p.RoleId == roleId).ExecuteDeleteAsync();
        using var deniedReplay = await ApiFixture.CommandAsync(f.Client, root + "/commit", commit, key);
        Assert.Equal(HttpStatusCode.Forbidden, deniedReplay.StatusCode);
        var revoke = new[] { new { apiId = f.Api.Id, environmentId = f.Environment.Id, action = "Revoke", permissionId = grant.Id, revision = grant.Revision } };
        var revokePreview = await JsonAsync(await f.WriteAsync(HttpMethod.Post, root + "/preview", new { projectId = f.Project.Id, changes = revoke }));
        Assert.Equal(JsonValueKind.Null, revokePreview.GetProperty("items")[0].GetProperty("before").GetProperty("methods").ValueKind);
        await JsonAsync(await ApiFixture.CommandAsync(f.Client, root + "/commit", new
        {
            projectId = f.Project.Id,
            changes = revoke,
            previewToken = revokePreview.GetProperty("previewToken").GetString(),
            confirmProduction = true
        }));
        Assert.Empty(await db.Set<ApplicationApiPermission>().AsNoTracking().ToArrayAsync());
        Assert.Empty(await db.Set<ApplicationRouteMethodGrant>().AsNoTracking().ToArrayAsync());
        Assert.Equal(f.Application.AuthorizationRevision + 2, (await db.Set<ApplicationRecord>().AsNoTracking().SingleAsync()).AuthorizationRevision);
        Assert.Single(await db.Set<AuditLog>().Where(x => x.Action == "app.permission.delete").ToArrayAsync());
    }

    [Fact]
    public async Task ChangedWindowRequestExpiredTokenOrReadonlyScopeCannotCommit()
    {
        var clock = new MatrixClock(DateTimeOffset.UtcNow);
        await using var f = new ApiFixture();
        await f.InitializeAsync(b => b.Services.AddSingleton<TimeProvider>(clock));
        await f.SeedConsumerAsync(seedFreshEnvironment: true);
        using var login = await f.LoginAsync();
        login.EnsureSuccessStatusCode();
        var root = Root(f);
        var from = clock.GetUtcNow().AddDays(2);
        object[] Save(DateTimeOffset start) => [new { apiId = f.Api.Id, environmentId = f.Environment.Id, action = "Save", mode = "LegacyApi", validFrom = start }];
        var original = Save(from);
        var preview = await JsonAsync(await f.WriteAsync(HttpMethod.Post, root + "/preview", new { projectId = f.Project.Id, changes = original }));
        Assert.Equal("Pending", preview.GetProperty("items")[0].GetProperty("after").GetProperty("state").GetString());
        var token = preview.GetProperty("previewToken").GetString();
        async Task<HttpStatusCode> Commit(object[] changes)
        {
            using var response = await ApiFixture.CommandAsync(f.Client, root + "/commit", new { projectId = f.Project.Id, changes, previewToken = token });
            return response.StatusCode;
        }
        Assert.Equal(HttpStatusCode.Conflict, await Commit(Save(from.AddDays(1))));
        var other = await f.NewReviewerAsync("ProjectAdmin");
        using var wrongActor = await ApiFixture.CommandAsync(other.Client, root + "/commit",
            new { projectId = f.Project.Id, changes = original, previewToken = token });
        Assert.Equal(HttpStatusCode.Conflict, wrongActor.StatusCode);
        clock.Now = clock.Now.AddMinutes(5);
        Assert.Equal(HttpStatusCode.Conflict, await Commit(original));
        await using var db = f.Context();
        await db.Set<UserProjectScope>().Where(s => s.UserId == f.User.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.AccessMode, "read_only"));
        Assert.Equal(HttpStatusCode.Forbidden, await Commit(original));
        Assert.Empty(await db.Set<ApplicationApiPermission>().AsNoTracking().ToArrayAsync());
    }

    [Fact]
    public async Task TwoCellCommitRollsBackFirstGrantAuditAndReceiptAfterDatabaseFailure()
    {
        await using var f = new ApiFixture();
        await f.InitializeAsync();
        await f.SeedConsumerAsync(seedFreshEnvironment: true);
        using var login = await f.LoginAsync();
        login.EnsureSuccessStatusCode();
        await using var db = f.Context();
        var extra = new Api { OrganizationId = f.Organization.Id, ProjectId = f.Project.Id, OwnerUserId = f.User.Id, Code = "RETURNS", Name = "退货", LifecycleStatus = "Draft" };
        db.Add(extra);
        await db.SaveChangesAsync();
        var ids = new[] { f.Api.Id, extra.Id }.Order().ToArray();
        var changes = ids.Select((id, index) => new { apiId = id, environmentId = f.Environment.Id, action = "Save", mode = "LegacyApi", validFrom = new DateTimeOffset(2021, 1, 1 + index, 0, 0, 0, TimeSpan.Zero) }).ToArray();
        var root = Root(f);
        var preview = await JsonAsync(await f.WriteAsync(HttpMethod.Post, root + "/preview", new { projectId = f.Project.Id, changes }));
        // The fixture owns its database. Reject only the second insert, after the first audited save.
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE application_api_permissions ADD CONSTRAINT matrix_test_fault CHECK (valid_from <> TIMESTAMPTZ '2021-01-02 00:00:00Z')");
        var key = Guid.NewGuid().ToString("N");
        var commit = new { projectId = f.Project.Id, changes, previewToken = preview.GetProperty("previewToken").GetString() };
        using var rejected = await ApiFixture.CommandAsync(f.Client, root + "/commit", commit, key);
        Assert.False(rejected.IsSuccessStatusCode);
        Assert.Empty(await db.Set<ApplicationApiPermission>().ToListAsync());
        Assert.Empty(await db.Set<AuditLog>().Where(x => x.Action == "app.permission.save").ToListAsync());
        Assert.Empty(await db.Set<IdempotencyRecord>().Where(x => x.Key == key).ToListAsync());
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE application_api_permissions DROP CONSTRAINT matrix_test_fault");
        var receipt = await JsonAsync(await ApiFixture.CommandAsync(f.Client, root + "/commit", commit, key));
        Assert.Equal(2, receipt.GetProperty("appliedCount").GetInt32());
    }

    [Fact]
    public async Task SharedApplicationSupportsAnotherProjectButNeverMixesCellsAcrossProjects()
    {
        await using var f = new ApiFixture();
        await f.InitializeAsync();
        await f.SeedConsumerAsync(seedFreshEnvironment: true);
        using var login = await f.LoginAsync();
        login.EnsureSuccessStatusCode();
        await using var db = f.Context();
        var other = new Project { OrganizationId = f.Organization.Id, Code = "OTHER", Name = "另一个项目" };
        var env = new EnvironmentRecord { ProjectId = other.Id, Code = "DEV", Name = "开发" };
        var api = new Api { ProjectId = other.Id, OrganizationId = f.Organization.Id, Code = "SHARED", Name = "共享 API", OwnerUserId = f.User.Id, LifecycleStatus = "Draft" };
        db.AddRange(other, env, api);
        await db.SaveChangesAsync();
        var url = $"{Root(f)}?projectId={other.Id}";
        using var foreign = await f.Client.GetAsync(url);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        await db.Set<ApplicationRecord>().Where(a => a.Id == f.Application.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.ProjectId, (Guid?)null));
        var page = await JsonAsync(await f.Client.GetAsync(url));
        Assert.Equal(env.Id, page.GetProperty("environments")[0].GetProperty("id").GetGuid());
        Assert.Equal(api.Id, page.GetProperty("items")[0].GetProperty("id").GetGuid());
        var start = DateTimeOffset.UtcNow.AddDays(-1);
        var changes = new[]{new {apiId=api.Id,environmentId=env.Id,action="Save",mode="LegacyApi",validFrom=start},
            new {apiId=f.Api.Id,environmentId=f.Environment.Id,action="Save",mode="LegacyApi",validFrom=start}};
        using var mixed = await f.WriteAsync(HttpMethod.Post, Root(f) + "/preview", new { projectId = other.Id, changes });
        Assert.Equal(HttpStatusCode.NotFound, mixed.StatusCode);
        Assert.Empty(await db.Set<ApplicationApiPermission>().ToListAsync());
        var read = await db.Set<Permission>().SingleAsync(p => p.Code == "app.read");
        await db.Set<RolePermission>().Where(p => p.PermissionId == read.Id).ExecuteDeleteAsync();
        using var hidden = await f.Client.GetAsync(url);
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunningImpactRequiresUnambiguousPublishedGrantAndCurrentNodeAcknowledgements(bool duplicateLegacyGrant)
    {
        await using var f = new ApiFixture();
        await f.InitializeAsync();
        await f.SeedConsumerAsync(seedFreshEnvironment: true);
        using var login = await f.LoginAsync();
        login.EnsureSuccessStatusCode();
        var now = DateTimeOffset.UtcNow;
        var historicalStart = now.AddDays(-8);
        var cluster = new RuntimeCluster(f.Cluster.Id, f.Environment.Id, "RoundRobin", true, "/health", 30,
            [new(f.Destination.Id, "http://unit-fixture:8080/", 1)]);
        var route = new RuntimeRoute(Guid.NewGuid(), f.Api.Id, f.Version.Id, cluster.Id, "/orders", ["GET"], 1, 30000, true);
        RuntimePermission[] grants = duplicateLegacyGrant ? [new(f.Api.Id, historicalStart, null), new(f.Api.Id, now, null)] : [new(f.Api.Id, historicalStart, null)];
        var published = new RuntimeApplication(f.Application.Id, "Active",
            [new(Guid.NewGuid(), "fixture-only", new string('b', 64), "Active", historicalStart, now.AddDays(3))],
            grants);
        var bytes = CanonicalJson.Serialize(new RuntimeSnapshot("2.0", f.Environment.Id, 3, now, [route], [cluster], [], [published]));
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        await using var db = f.Context();
        var version = new GatewayConfigVersion { EnvironmentId = f.Environment.Id, VersionNo = 3, CreatedBy = f.User.Id, SnapshotHash = hash };
        var release = new ReleaseRecord
        {
            EnvironmentId = f.Environment.Id,
            ReleaseNo = "matrix",
            ReleaseType = "publish",
            Status = "Succeeded",
            RequestedBy = f.User.Id,
            ToConfigVersion = 3,
            DeploymentSequence = 4
        };
        db.AddRange(version, new GatewayConfigSnapshot { ConfigVersionId = version.Id, PayloadBytes = bytes, SizeBytes = bytes.Length }, release);
        var environment = await db.Set<EnvironmentRecord>().SingleAsync();
        environment.DesiredConfigVersion = 3;
        environment.DeploymentSequence = 4;
        for (var i = 0; i < 2; i++)
        {
            var node = new GatewayNode
            {
                EnvironmentId = f.Environment.Id,
                NodeName = "matrix-" + i,
                InstanceId = Guid.NewGuid().ToString(),
                CurrentConfigVersion = 3,
                CurrentDeploymentSequence = 4,
                LastHeartbeatAt = now,
                Enabled = true
            };
            db.AddRange(node, new ReleaseTarget { ReleaseId = release.Id, NodeId = node.Id, InstanceId = node.InstanceId },
                new GatewayAck { ReleaseId = release.Id, NodeId = node.Id, InstanceId = node.InstanceId, ConfigVersion = 3, DeploymentSequence = 4, PayloadHash = hash, Success = true });
        }
        var role = await db.Set<UserRole>().Where(r => r.UserId == f.User.Id).Select(r => r.RoleId).SingleAsync();
        var read = new Permission { Code = "gateway.config.read", Name = "读取配置", Module = "gateway" };
        db.AddRange(read, new RolePermission { RoleId = role, PermissionId = read.Id });
        await db.SaveChangesAsync();
        var root = Root(f);
        var changes = new[] { new { apiId = f.Api.Id, environmentId = f.Environment.Id, action = "Save", mode = "LegacyApi", validFrom = now } };
        var body = new { projectId = f.Project.Id, changes };
        var preview = await JsonAsync(await f.WriteAsync(HttpMethod.Post, root + "/preview", body));
        var item = preview.GetProperty("items")[0];
        Assert.Equal(JsonValueKind.Null, item.GetProperty("before").ValueKind);
        var running = item.GetProperty("running");
        if (duplicateLegacyGrant)
        {
            Assert.Equal("Unconfirmed", running.GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.Null, running.GetProperty("grant").ValueKind);
            return;
        }
        Assert.Equal("Confirmed", running.GetProperty("status").GetString());
        Assert.Equal(3, running.GetProperty("configVersion").GetInt32());
        Assert.Equal(4, running.GetProperty("deploymentSequence").GetInt32());
        Assert.Equal(historicalStart, running.GetProperty("grant").GetProperty("validFrom").GetDateTimeOffset());
        Assert.DoesNotContain("fixture-only", item.GetRawText());
        Assert.DoesNotContain("credential", item.GetRawText(), StringComparison.OrdinalIgnoreCase);
        var target = await db.Set<GatewayNode>().FirstAsync();
        target.LastHeartbeatAt = now.AddHours(-1);
        await db.SaveChangesAsync();
        using var stale = await ApiFixture.CommandAsync(f.Client, root + "/commit", new
        {
            projectId = f.Project.Id,
            changes,
            previewToken = preview.GetProperty("previewToken").GetString()
        });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var fresh = await JsonAsync(await f.WriteAsync(HttpMethod.Post, root + "/preview", body));
        Assert.Equal("Unconfirmed", fresh.GetProperty("items")[0].GetProperty("running").GetProperty("status").GetString());
    }

    [Fact]
    public async Task TwoEnvironmentsCommitOnceThenEditWindowsWithBothCurrentRevisions()
    {
        await using var f = new ApiFixture();
        await f.InitializeAsync();
        await f.SeedConsumerAsync(seedFreshEnvironment: true);
        using var login = await f.LoginAsync();
        login.EnsureSuccessStatusCode();
        await using var db = f.Context();
        var production = new EnvironmentRecord { ProjectId = f.Project.Id, Code = "PROD", Name = "生产", IsProduction = true };
        db.Add(production);
        await db.SaveChangesAsync();

        var root = Root(f);
        var from = DateTimeOffset.UtcNow.AddDays(-3);
        var envIds = new[] { f.Environment.Id, production.Id };
        var creates = envIds.Select(environmentId => new { apiId = f.Api.Id, environmentId, action = "Save", mode = "LegacyApi", validFrom = from }).ToArray();
        async Task<string> PreviewToken(object changes)
        {
            var preview = await JsonAsync(await f.WriteAsync(HttpMethod.Post, root + "/preview", new { projectId = f.Project.Id, changes }));
            Assert.True(preview.GetProperty("requiresProductionConfirmation").GetBoolean());
            Assert.Equal(2, preview.GetProperty("items").GetArrayLength());
            return preview.GetProperty("previewToken").GetString()!;
        }
        var previewToken = await PreviewToken(creates);
        var key = Guid.NewGuid().ToString("N");
        async Task<JsonElement> Submit(object changes) => await JsonAsync(await ApiFixture.CommandAsync(f.Client, root + "/commit",
            new { projectId = f.Project.Id, changes, previewToken, confirmProduction = true }, key));
        var receipts = await Task.WhenAll(Submit(creates), Submit(creates.Reverse().ToArray()));
        Assert.All(receipts, receipt => Assert.Equal(2, receipt.GetProperty("appliedCount").GetInt32()));
        var parents = await db.Set<ApplicationApiPermission>().AsNoTracking().ToArrayAsync();
        Assert.Equal(2, parents.Length);
        var audits = await db.Set<AuditLog>().Where(a => a.Action == "app.permission.save").ToArrayAsync();
        Assert.Equal(envIds.Order(), audits.Select(a => a.EnvironmentId!.Value).Order());
        Assert.All(audits, a => Assert.Equal(nameof(ApplicationApiPermission), a.ResourceType));

        var next = DateTimeOffset.UtcNow.AddDays(2);
        var edits = parents.Select(p => new
        {
            apiId = f.Api.Id,
            environmentId = p.EnvironmentId,
            action = "SetWindow",
            permissionId = p.Id,
            revision = p.Revision,
            validFrom = next
        }).ToArray();
        previewToken = await PreviewToken(edits);
        using var conflict = await ApiFixture.CommandAsync(f.Client, root + "/commit",
            new { projectId = f.Project.Id, changes = edits, previewToken, confirmProduction = true }, key);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        key = Guid.NewGuid().ToString("N");
        await Submit(edits);
        var page = await JsonAsync(await f.Client.GetAsync($"{root}?projectId={f.Project.Id}"));
        Assert.All(page.GetProperty("items")[0].GetProperty("grants").EnumerateArray(),
            grant => Assert.Equal("Pending", grant.GetProperty("state").GetString()));
        using var stale = await f.WriteAsync(HttpMethod.Post, root + "/preview", new { projectId = f.Project.Id, changes = edits });
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
    }

    [Theory]
    [InlineData("environment.read")]
    [InlineData("api.read")]
    [InlineData("route.read")]
    [InlineData("gateway.config.read")]
    [InlineData("app.read")]
    public async Task ApplicationDetailAndLegacyPermissionsRespectMatrixVisibility(string removedPermission)
    {
        await using var f = new ApiFixture();
        await f.InitializeAsync();
        await f.SeedConsumerAsync(seedFreshEnvironment: true);
        using var login = await f.LoginAsync();
        login.EnsureSuccessStatusCode();
        var app = $"/api/v1/applications/{f.Application.Id}";
        using var created = await f.WriteAsync(HttpMethod.Post, $"/api/v1/environments/{f.Environment.Id}/routes", f.RouteBody("/visibility"));
        created.EnsureSuccessStatusCode();
        var choices = await f.Client.GetFromJsonAsync<RouteMethodOptionsDto>($"{app}/route-method-options?environmentId={f.Environment.Id}&apiId={f.Api.Id}");
        var choice = Assert.Single(choices!.Routes);
        using var saved = await f.WriteAsync(HttpMethod.Post, app + "/permissions", new PermissionSaveRequest(f.Api.Id, f.Environment.Id,
            DateTimeOffset.UtcNow.AddDays(-1), Mode: "RouteMethod", RouteMethods: [new(choice.RouteId, choice.AuthorizationRevision, ["GET"])]));
        saved.EnsureSuccessStatusCode();
        await using var db = f.Context();
        var role = await db.Set<UserRole>().Where(r => r.UserId == f.User.Id).Select(r => r.RoleId).SingleAsync();
        var configRead = new Permission { Code = "gateway.config.read", Name = "读取配置", Module = "gateway" };
        db.AddRange(configRead, new RolePermission { RoleId = role, PermissionId = configRead.Id },
            new GatewayNode
            {
                EnvironmentId = f.Environment.Id,
                NodeName = "visible",
                InstanceId = Guid.NewGuid().ToString(),
                CurrentConfigVersion = 17,
                LastHeartbeatAt = DateTimeOffset.UtcNow,
                Enabled = true
            });
        await db.SaveChangesAsync();
        var initial = await f.Client.GetFromJsonAsync<ApplicationDetailDto>(app);
        Assert.Equal(17, initial!.RunningConfigVersions[f.Environment.Id]);
        Assert.Equal(choice.RouteId, Assert.Single(Assert.Single(initial.Permissions).RouteMethods!).RouteId);

        var removed = await db.Set<Permission>().SingleAsync(p => p.Code == removedPermission);
        await db.Set<RolePermission>().Where(p => p.RoleId == role && p.PermissionId == removed.Id).ExecuteDeleteAsync();
        using var detailResponse = await f.Client.GetAsync(app);
        using var permissionsResponse = await f.Client.GetAsync(app + "/permissions");
        using var matrixResponse = await f.Client.GetAsync($"{Root(f)}?projectId={f.Project.Id}");
        using var optionsResponse = await f.Client.GetAsync($"{app}/route-method-options?environmentId={f.Environment.Id}&apiId={f.Api.Id}");
        if (removedPermission == "gateway.config.read")
        {
            optionsResponse.EnsureSuccessStatusCode();
            var routeOptions = (await optionsResponse.Content.ReadFromJsonAsync<RouteMethodOptionsDto>())!;
            Assert.Equal(choice.RouteId, Assert.Single(routeOptions.Routes).RouteId);
        }
        else
        {
            Assert.Equal(removedPermission == "route.read" ? HttpStatusCode.Forbidden : HttpStatusCode.NotFound, optionsResponse.StatusCode);
        }
        if (removedPermission == "app.read")
        {
            Assert.Equal(HttpStatusCode.NotFound, detailResponse.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, permissionsResponse.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, matrixResponse.StatusCode);
            return;
        }
        detailResponse.EnsureSuccessStatusCode();
        permissionsResponse.EnsureSuccessStatusCode();
        var detail = (await detailResponse.Content.ReadFromJsonAsync<ApplicationDetailDto>())!;
        var legacy = (await permissionsResponse.Content.ReadFromJsonAsync<ApplicationPermissionDto[]>())!;
        if (removedPermission is "api.read" or "environment.read")
        {
            Assert.Empty(detail.Permissions);
            Assert.Empty(legacy);
            Assert.Empty(detail.RunningConfigVersions);
            if (removedPermission == "api.read") Assert.Equal(HttpStatusCode.NotFound, matrixResponse.StatusCode);
            else
            {
                matrixResponse.EnsureSuccessStatusCode();
                var matrix = await matrixResponse.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Empty(matrix.GetProperty("environments").EnumerateArray());
                Assert.Empty(matrix.GetProperty("items")[0].GetProperty("grants").EnumerateArray());
            }
        }
        else
        {
            matrixResponse.EnsureSuccessStatusCode();
            var matrix = await matrixResponse.Content.ReadFromJsonAsync<JsonElement>();
            var matrixGrant = matrix.GetProperty("items")[0].GetProperty("grants")[0];
            var detailGrant = Assert.Single(detail.Permissions);
            var legacyGrant = Assert.Single(legacy);
            Assert.Equal("RouteMethod", detailGrant.Mode);
            Assert.Equal("RouteMethod", legacyGrant.Mode);
            if (removedPermission == "route.read")
            {
                Assert.Null(detailGrant.RouteMethods);
                Assert.Null(legacyGrant.RouteMethods);
                Assert.Equal(JsonValueKind.Null, matrixGrant.GetProperty("routeMethods").ValueKind);
            }
            else
            {
                Assert.Empty(detail.RunningConfigVersions);
                Assert.Single(detailGrant.RouteMethods!);
                Assert.Single(legacyGrant.RouteMethods!);
            }
        }
    }

    [Fact]
    public async Task LegacyMovePreviewsBothSidesThenPreservesPermissionIdentityAndAudit()
    {
        await using var f = new ApiFixture();
        await f.InitializeAsync();
        await f.SeedConsumerAsync(seedFreshEnvironment: true);
        using var login = await f.LoginAsync();
        login.EnsureSuccessStatusCode();
        await using var db = f.Context();
        var target = new EnvironmentRecord { ProjectId = f.Project.Id, Code = "PROD", Name = "生产", IsProduction = true };
        db.Add(target);
        await db.SaveChangesAsync();
        var from = DateTimeOffset.UtcNow.AddDays(-1);
        using var create = await f.WriteAsync(HttpMethod.Post, $"/api/v1/applications/{f.Application.Id}/permissions",
            new PermissionSaveRequest(f.Api.Id, f.Environment.Id, from));
        create.EnsureSuccessStatusCode();
        var parent = (await create.Content.ReadFromJsonAsync<ApplicationPermissionDto>())!;
        var changes = new[]{new {apiId=f.Api.Id,environmentId=f.Environment.Id,permissionId=parent.Id,revision=parent.Revision,
            action="MoveLegacy",destinationApiId=f.Api.Id,destinationEnvironmentId=target.Id,validFrom=from}};
        var root = Root(f);
        var preview = await JsonAsync(await f.WriteAsync(HttpMethod.Post, root + "/preview", new { projectId = f.Project.Id, changes }));
        Assert.True(preview.GetProperty("requiresProductionConfirmation").GetBoolean());
        var items = preview.GetProperty("items").EnumerateArray().ToDictionary(x => x.GetProperty("environmentId").GetGuid());
        Assert.Equal(2, items.Count);
        Assert.Equal("LegacyApi", items[f.Environment.Id].GetProperty("before").GetProperty("mode").GetString());
        Assert.Equal(JsonValueKind.Null, items[f.Environment.Id].GetProperty("after").ValueKind);
        Assert.Equal(JsonValueKind.Null, items[target.Id].GetProperty("before").ValueKind);
        Assert.Equal("LegacyApi", items[target.Id].GetProperty("after").GetProperty("mode").GetString());
        var previewToken = preview.GetProperty("previewToken").GetString();
        object[] splitActions = [new {apiId=f.Api.Id,environmentId=f.Environment.Id,action="Revoke",permissionId=parent.Id,revision=parent.Revision},
            new {apiId=f.Api.Id,environmentId=target.Id,action="Save",mode="LegacyApi",validFrom=from}];
        using var changedCommand = await ApiFixture.CommandAsync(f.Client, root + "/commit", new
        {
            projectId = f.Project.Id,
            changes = splitActions,
            previewToken,
            confirmProduction = true
        });
        Assert.Equal(HttpStatusCode.Conflict, changedCommand.StatusCode);
        using var denied = await ApiFixture.CommandAsync(f.Client, root + "/commit", new { projectId = f.Project.Id, changes, previewToken, confirmProduction = false });
        Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);
        var receipt = await JsonAsync(await ApiFixture.CommandAsync(f.Client, root + "/commit", new { projectId = f.Project.Id, changes, previewToken, confirmProduction = true }));
        Assert.Equal(1, receipt.GetProperty("appliedCount").GetInt32());
        var result = await db.Set<ApplicationApiPermission>().SingleAsync();
        Assert.Equal(parent.Id, result.Id);
        Assert.Equal(target.Id, result.EnvironmentId);
        Assert.Equal(parent.Revision + 1, result.Revision);
        var audit = await db.Set<AuditLog>().SingleAsync(a => a.ResourceType == nameof(ApplicationApiPermission) && a.ResourceId == parent.Id.ToString() && a.EnvironmentId == target.Id);
        Assert.Contains(f.Environment.Id.ToString(), audit.BeforeJson);
        Assert.Contains(target.Id.ToString(), audit.AfterJson);
    }

    private sealed class MatrixClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
