using System.Net;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;

namespace WebApi.Integration.Tests;

public sealed class GovernanceLegacyApplicationCompatibilityTests
{
    [Fact]
    public async Task ExistingCredentialEndpointRetainsItsOrganizationAndLegacyGlobalBinding()
    {
        await using var api = new ApiFixture();
        await api.InitializeAsync();
        await api.SeedConsumerAsync();
        using var login = await api.LoginAsync();
        login.EnsureSuccessStatusCode();

        using var result = await api.WriteAsync(HttpMethod.Post, $"/api/v1/applications/{api.Application.Id}/credentials",
            new { validFrom = DateTimeOffset.UtcNow.AddMinutes(-1), expiresAt = DateTimeOffset.UtcNow.AddDays(1) });
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        await using var db = api.Context();
        var credential = await db.Set<ApplicationCredential>().SingleAsync();
        Assert.Equal(api.Organization.Id, credential.OrganizationId);
        Assert.Equal("LegacyGlobal", credential.BindingMode);
        Assert.Null(credential.EnvironmentId);
        Assert.Null(credential.ProjectId);
    }

    [Fact]
    public async Task ExistingPermissionEndpointPreservesLegacyApiScope()
    {
        await using var api = new ApiFixture();
        await api.InitializeAsync();
        await api.SeedConsumerAsync();
        using var login = await api.LoginAsync();
        login.EnsureSuccessStatusCode();

        using var result = await api.WriteAsync(HttpMethod.Post, $"/api/v1/applications/{api.Application.Id}/permissions",
            new { environmentId = api.Environment.Id, apiId = api.Api.Id, validFrom = DateTimeOffset.UtcNow.AddMinutes(-1), expiresAt = DateTimeOffset.UtcNow.AddDays(1) });
        Assert.True(result.IsSuccessStatusCode, $"Legacy permission endpoint returned {(int)result.StatusCode}.");
        await using var db = api.Context();
        var permission = await db.Set<ApplicationApiPermission>().SingleAsync();
        Assert.Equal("LegacyApi", permission.Mode);
        Assert.Equal(api.Environment.Id, permission.EnvironmentId);
        Assert.Equal(api.Api.Id, permission.ApiId);
        Assert.Empty(await db.Set<ApplicationRouteMethodGrant>().ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingPermissionEditorCanChangeApiOrEnvironmentInTheSameProject(bool changeEnvironment)
    {
        await using var api = new ApiFixture();
        await api.InitializeAsync();
        await api.SeedConsumerAsync();
        using var login = await api.LoginAsync();
        login.EnsureSuccessStatusCode();
        var secondApi = new Api { OrganizationId = api.Organization.Id, ProjectId = api.Project.Id, Code = "SECOND", Name = "Second", OwnerUserId = api.User.Id };
        var secondEnvironment = new EnvironmentRecord { ProjectId = api.Project.Id, Code = "UAT", Name = "UAT" };
        await using (var setup = api.Context()) { setup.AddRange(secondApi, secondEnvironment); await setup.SaveChangesAsync(); }
        var from = DateTimeOffset.UtcNow.AddMinutes(-1);
        var until = DateTimeOffset.UtcNow.AddDays(1);
        using (var created = await api.WriteAsync(HttpMethod.Post, $"/api/v1/applications/{api.Application.Id}/permissions",
            new { environmentId = api.Environment.Id, apiId = api.Api.Id, validFrom = from, expiresAt = until }))
            Assert.True(created.IsSuccessStatusCode, $"Create returned {(int)created.StatusCode}.");
        Guid id; long revision;
        await using (var check = api.Context()) { var original = await check.Set<ApplicationApiPermission>().SingleAsync(); id = original.Id; revision = original.Revision; }

        using var result = await api.WriteAsync(HttpMethod.Put, $"/api/v1/applications/{api.Application.Id}/permissions/{id}",
            new { environmentId = changeEnvironment ? secondEnvironment.Id : api.Environment.Id,
                  apiId = changeEnvironment ? api.Api.Id : secondApi.Id, validFrom = from, expiresAt = until }, RevisionTag.Format(revision));
        Assert.True(result.IsSuccessStatusCode, $"Edit returned {(int)result.StatusCode}.");
        await using var verify = api.Context();
        var permission = await verify.Set<ApplicationApiPermission>().SingleAsync();
        Assert.Equal(id, permission.Id);
        Assert.Equal("LegacyApi", permission.Mode);
        Assert.Equal(changeEnvironment ? secondEnvironment.Id : api.Environment.Id, permission.EnvironmentId);
        Assert.Equal(changeEnvironment ? api.Api.Id : secondApi.Id, permission.ApiId);
        Assert.True(permission.Revision > revision);
        var audit = await verify.Set<AuditLog>().Where(a => a.Action == "app.permission.save" && a.ResourceType == nameof(ApplicationApiPermission) && a.ResourceId == id.ToString()).OrderByDescending(a => a.Id).FirstAsync();
        Assert.Contains(api.Environment.Id.ToString(), audit.BeforeJson!);
        Assert.Contains(api.Api.Id.ToString(), audit.BeforeJson!);
        Assert.Contains(permission.EnvironmentId.ToString(), audit.AfterJson!);
        Assert.Contains(permission.ApiId.ToString(), audit.AfterJson!);
    }

    [Fact]
    public async Task LegacyEditorCannotModifyRouteMethodPermissionWithoutItsExplicitMethods()
    {
        await using var api = new ApiFixture();
        await api.InitializeAsync();
        await api.SeedConsumerAsync();
        using var login = await api.LoginAsync();
        login.EnsureSuccessStatusCode();
        var from = DateTimeOffset.UtcNow.AddMinutes(-1);
        var until = DateTimeOffset.UtcNow.AddDays(1);
        using (var created = await api.WriteAsync(HttpMethod.Post, $"/api/v1/applications/{api.Application.Id}/permissions",
            new { environmentId = api.Environment.Id, apiId = api.Api.Id, validFrom = from, expiresAt = until })) created.EnsureSuccessStatusCode();
        Guid id; long revision;
        await using (var setup = api.Context())
        {
            var permission = await setup.Set<ApplicationApiPermission>().SingleAsync();
            permission.Mode = "RouteMethod";
            await setup.SaveChangesAsync();
            await setup.Entry(permission).ReloadAsync();
            id = permission.Id; revision = permission.Revision;
        }
        using var result = await api.WriteAsync(HttpMethod.Put, $"/api/v1/applications/{api.Application.Id}/permissions/{id}",
            new { environmentId = api.Environment.Id, apiId = api.Api.Id, validFrom = from, expiresAt = until.AddDays(1) }, RevisionTag.Format(revision));
        Assert.Equal(HttpStatusCode.Conflict, result.StatusCode);
        await using var verify = api.Context();
        var unchanged = await verify.Set<ApplicationApiPermission>().SingleAsync();
        Assert.Equal("RouteMethod", unchanged.Mode);
        Assert.Equal(revision, unchanged.Revision);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyPermissionMoveIsAtomicWhenRevisionChangesOrAuditFails(bool failAudit)
    {
        await using var api = new ApiFixture();
        await api.InitializeAsync();
        await api.SeedConsumerAsync();
        using var login = await api.LoginAsync();
        login.EnsureSuccessStatusCode();
        var target = new Api { OrganizationId = api.Organization.Id, ProjectId = api.Project.Id, Code = "MOVE", Name = "Move", OwnerUserId = api.User.Id };
        await using (var setup = api.Context()) { setup.Add(target); await setup.SaveChangesAsync(); }
        var from = DateTimeOffset.UtcNow.AddMinutes(-1);
        var until = DateTimeOffset.UtcNow.AddDays(1);
        using (var created = await api.WriteAsync(HttpMethod.Post, $"/api/v1/applications/{api.Application.Id}/permissions",
            new { environmentId = api.Environment.Id, apiId = api.Api.Id, validFrom = from, expiresAt = until })) created.EnsureSuccessStatusCode();
        Guid id; long suppliedRevision; long storedRevision; long applicationRevision; int auditCount;
        await using (var setup = api.Context())
        {
            var permission = await setup.Set<ApplicationApiPermission>().SingleAsync();
            id = permission.Id; suppliedRevision = permission.Revision;
            if (!failAudit)
            {
                await setup.Set<ApplicationApiPermission>().Where(x => x.Id == id).ExecuteUpdateAsync(x => x.SetProperty(y => y.Revision, y => y.Revision + 1));
                await setup.Entry(permission).ReloadAsync();
            }
            else
            {
                await setup.Database.ExecuteSqlRawAsync("""
                    CREATE FUNCTION isolated_test_reject_permission_audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
                      IF NEW.action='app.permission.save' THEN RAISE EXCEPTION 'isolated_audit_failure'; END IF;
                      RETURN NEW;
                    END $$;
                    CREATE TRIGGER isolated_test_reject_permission_audit BEFORE INSERT ON audit_logs
                      FOR EACH ROW EXECUTE FUNCTION isolated_test_reject_permission_audit();
                    """);
            }
            storedRevision = permission.Revision;
            applicationRevision = (await setup.Set<ApplicationRecord>().SingleAsync()).Revision;
            auditCount = await setup.Set<AuditLog>().CountAsync();
        }
        using var result = await api.WriteAsync(HttpMethod.Put, $"/api/v1/applications/{api.Application.Id}/permissions/{id}",
            new { environmentId = api.Environment.Id, apiId = target.Id, validFrom = from, expiresAt = until }, RevisionTag.Format(suppliedRevision));
        Assert.Equal(failAudit ? HttpStatusCode.InternalServerError : HttpStatusCode.PreconditionFailed, result.StatusCode);
        await using var verify = api.Context();
        var unchanged = await verify.Set<ApplicationApiPermission>().SingleAsync();
        Assert.Equal(api.Api.Id, unchanged.ApiId);
        Assert.Equal(storedRevision, unchanged.Revision);
        Assert.Equal(applicationRevision, (await verify.Set<ApplicationRecord>().SingleAsync()).Revision);
        Assert.Equal(auditCount, await verify.Set<AuditLog>().CountAsync());
    }

    [Fact]
    public async Task LegacyEditorCannotMovePermissionOutOfAStrictEnvironment()
    {
        await using var api = new ApiFixture();
        await api.InitializeAsync();
        await api.SeedConsumerAsync();
        using var login = await api.LoginAsync();
        login.EnsureSuccessStatusCode();
        var target = new EnvironmentRecord { ProjectId = api.Project.Id, Code = "UAT", Name = "UAT" };
        await using (var setup = api.Context()) { setup.Add(target); await setup.SaveChangesAsync(); }
        var from = DateTimeOffset.UtcNow.AddMinutes(-1);
        var until = DateTimeOffset.UtcNow.AddDays(1);
        using (var created = await api.WriteAsync(HttpMethod.Post, $"/api/v1/applications/{api.Application.Id}/permissions",
            new { environmentId = api.Environment.Id, apiId = api.Api.Id, validFrom = from, expiresAt = until })) created.EnsureSuccessStatusCode();
        Guid id; long revision;
        await using (var setup = api.Context())
        {
            var permission = await setup.Set<ApplicationApiPermission>().SingleAsync();
            id = permission.Id; revision = permission.Revision;
            await setup.Database.OpenConnectionAsync();
            await setup.Database.ExecuteSqlRawAsync("SET session_replication_role=replica");
            try
            {
                setup.Add(new EnvironmentSecurityState
                {
                    EnvironmentId = api.Environment.Id, ProjectId = api.Project.Id, OrganizationId = api.Organization.Id,
                    MinimumSnapshotSchema = "2.4", UpdatedAt = DateTimeOffset.UtcNow
                });
                await setup.SaveChangesAsync();
            }
            finally { await setup.Database.ExecuteSqlRawAsync("SET session_replication_role=origin"); }
        }
        using var result = await api.WriteAsync(HttpMethod.Put, $"/api/v1/applications/{api.Application.Id}/permissions/{id}",
            new { environmentId = target.Id, apiId = api.Api.Id, validFrom = from, expiresAt = until }, RevisionTag.Format(revision));
        Assert.Equal(HttpStatusCode.Conflict, result.StatusCode);
        await using var verify = api.Context();
        var unchanged = await verify.Set<ApplicationApiPermission>().SingleAsync();
        Assert.Equal(api.Environment.Id, unchanged.EnvironmentId);
        Assert.Equal(revision, unchanged.Revision);
    }

    [Theory]
    [InlineData("2.3")]
    [InlineData("2.4")]
    public async Task ExistingPermissionEndpointCannotCreateLegacyApiAfterStrictCutover(string minimum)
    {
        await using var api = new ApiFixture();
        await api.InitializeAsync();
        await api.SeedConsumerAsync();
        using var login = await api.LoginAsync();
        login.EnsureSuccessStatusCode();

        // A deliberately untrusted ledger marker in this temporary database is
        // used only to verify denial; it is not an attestation or valid ledger.
        await using (var setup = api.Context())
        {
            await setup.Database.OpenConnectionAsync();
            await setup.Database.ExecuteSqlRawAsync("SET session_replication_role=replica");
            try
            {
                setup.Add(new EnvironmentSecurityState
                {
                    EnvironmentId = api.Environment.Id, ProjectId = api.Project.Id, OrganizationId = api.Organization.Id,
                    MinimumSnapshotSchema = minimum, UpdatedAt = DateTimeOffset.UtcNow
                });
                await setup.SaveChangesAsync();
            }
            finally { await setup.Database.ExecuteSqlRawAsync("SET session_replication_role=origin"); }
        }
        using var result = await api.WriteAsync(HttpMethod.Post, $"/api/v1/applications/{api.Application.Id}/permissions",
            new { environmentId = api.Environment.Id, apiId = api.Api.Id, validFrom = DateTimeOffset.UtcNow.AddMinutes(-1), expiresAt = DateTimeOffset.UtcNow.AddDays(1) });
        Assert.Equal(HttpStatusCode.Conflict, result.StatusCode);
        await using var check = api.Context();
        Assert.Empty(await check.Set<ApplicationApiPermission>().ToArrayAsync());
    }
}
