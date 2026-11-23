using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Catalog;
using WebApi.Contracts.Governance;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;

namespace WebApi.Integration.Tests;

public sealed class GovernanceFreshEnvironmentTests
{
    [Fact]
    public async Task NewEnvironmentRecordsFreshOriginInItsCreationTransaction()
    {
        await using var api = new ApiFixture();
        await api.InitializeAsync();
        await api.SeedScopeAsync();
        using var login = await api.LoginAsync();
        login.EnsureSuccessStatusCode();
        using var created = await api.WriteAsync(HttpMethod.Post, $"/api/v1/projects/{api.Project.Id}/environments", new { code = "UAT", name = "UAT" });
        created.EnsureSuccessStatusCode();
        using var response = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var environmentId = response.RootElement.GetProperty("id").GetGuid();
        Assert.Equal("Active", response.RootElement.GetProperty("status").GetString());

        await using var db = api.Context();
        Assert.Equal("Active", (await db.Set<EnvironmentRecord>().SingleAsync(x => x.Id == environmentId)).Status);
        var origin = await db.Set<EnvironmentSecurityInitialization>().SingleAsync(x => x.EnvironmentId == environmentId);
        Assert.Equal(api.Project.Id, origin.ProjectId);
        Assert.Equal(api.Organization.Id, origin.OrganizationId);
        Assert.Equal("FreshNonStrict", origin.State);
        Assert.NotNull(origin.FreshProofId);
        Assert.NotEqual(Guid.Empty, origin.FreshProofId);
        Assert.Null(origin.EvidenceHash);
        Assert.Null(origin.ConfirmedBy);
        var audit = await db.Set<AuditLog>().SingleAsync(x => x.Action == "environment.create");
        Assert.Contains("FreshNonStrict", audit.AfterJson);
        Assert.Contains(origin.FreshProofId!.Value.ToString(), audit.AfterJson);
        // Directly seeded old data is not retroactively given a fresh origin.
        Assert.False(await db.Set<EnvironmentSecurityInitialization>().AnyAsync(x => x.EnvironmentId == api.Environment.Id && x.State == "FreshNonStrict"));
    }

    [Fact]
    public async Task DisabledEnvironmentBlocksBusinessWritesFromCreationUntilReactivated()
    {
        await using var api = new ApiFixture();
        await api.InitializeAsync();
        await api.SeedCatalogAsync();
        using var login = await api.LoginAsync();
        login.EnsureSuccessStatusCode();

        using var created = await api.WriteAsync(HttpMethod.Post, $"/api/v1/projects/{api.Project.Id}/environments",
            new { code = "UAT", name = "UAT", status = "Disabled" });
        created.EnsureSuccessStatusCode();
        var environment = (await created.Content.ReadFromJsonAsync<EnvironmentDto>())!;
        var path = $"/api/v1/environments/{environment.Id}";
        Assert.Equal("Disabled", environment.Status);
        Assert.Equal(1, environment.Revision);
        Assert.Equal("Disabled", (await api.Client.GetFromJsonAsync<EnvironmentDetailDto>(path))!.Status);

        var cluster = new SaveClusterRequest("UAT backend");
        using var blocked = await api.WriteAsync(HttpMethod.Post, $"{path}/clusters", cluster);
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Equal("inactive_scope", (await blocked.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        await using (var db = api.Context())
        {
            Assert.Equal("Disabled", (await db.Set<EnvironmentRecord>().SingleAsync(x => x.Id == environment.Id)).Status);
            var audit = await db.Set<AuditLog>().SingleAsync(x => x.Action == "environment.create");
            using var changes = JsonDocument.Parse(audit.AfterJson!);
            var entry = changes.RootElement.EnumerateArray().Single(x => x.GetProperty("Type").GetString() == nameof(EnvironmentRecord));
            Assert.Equal("Disabled", entry.GetProperty("Fields").GetProperty("Status").GetString());
            Assert.False(await db.Set<UpstreamCluster>().AnyAsync(x => x.EnvironmentId == environment.Id));
        }

        using var enabled = await api.WriteAsync(HttpMethod.Put, path,
            new UpdateEnvironmentRequest(environment.Code, environment.Name, "Active", false, 0, null),
            created.Headers.ETag!.ToString());
        enabled.EnsureSuccessStatusCode();
        var current = (await api.Client.GetFromJsonAsync<EnvironmentDetailDto>(path))!;
        Assert.Equal("Active", current.Status);
        Assert.Equal(2, current.Revision);
        using var accepted = await api.WriteAsync(HttpMethod.Post, $"{path}/clusters", cluster);
        accepted.EnsureSuccessStatusCode();
        await using var verify = api.Context();
        Assert.Equal(cluster.Name, (await verify.Set<UpstreamCluster>().SingleAsync(x => x.EnvironmentId == environment.Id)).Name);
    }

    [Theory]
    [InlineData("Paused", "invalid_fields")]
    [InlineData(null, "invalid_request")]
    public async Task InvalidCreationStatusIsRejectedLikeAnUpdate(string? status, string code)
    {
        await using var api = new ApiFixture();
        await api.InitializeAsync();
        await api.SeedScopeAsync();
        using var login = await api.LoginAsync();
        login.EnsureSuccessStatusCode();

        var body = new { code = "INVALID", name = "Invalid", status, isProduction = false, sortOrder = 0, releasePolicyId = (Guid?)null };
        using var update = await api.WriteAsync(HttpMethod.Put, $"/api/v1/environments/{api.Environment.Id}", body, "\"1\"");
        using var create = await api.WriteAsync(HttpMethod.Post, $"/api/v1/projects/{api.Project.Id}/environments", body);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, update.StatusCode);
        Assert.Equal(code, (await update.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, create.StatusCode);
        Assert.Equal(code, (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        await using var db = api.Context();
        Assert.False(await db.Set<EnvironmentRecord>().AnyAsync(x => x.Code == body.code));
        Assert.False(await db.Set<AuditLog>().AnyAsync(x => x.Action == "environment.create" || x.Action == "environment.update"));
    }

    [Fact]
    public async Task CreationAuditFailureRollsBackBothEnvironmentAndOrigin()
    {
        await using var api = new ApiFixture();
        await api.InitializeAsync();
        await api.SeedScopeAsync();
        using var login = await api.LoginAsync();
        login.EnsureSuccessStatusCode();
        int origins;
        await using (var db = api.Context())
        {
            origins = await db.Set<EnvironmentSecurityInitialization>().CountAsync();
            await db.Database.ExecuteSqlRawAsync("""
                CREATE FUNCTION isolated_test_reject_environment_audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
                  IF NEW.action='environment.create' THEN RAISE EXCEPTION 'isolated_audit_failure'; END IF;
                  RETURN NEW;
                END $$;
                CREATE TRIGGER isolated_test_reject_environment_audit BEFORE INSERT ON audit_logs
                  FOR EACH ROW EXECUTE FUNCTION isolated_test_reject_environment_audit();
                """);
        }
        using var result = await api.WriteAsync(HttpMethod.Post, $"/api/v1/projects/{api.Project.Id}/environments", new { code = "UAT", name = "UAT" });
        Assert.Equal(HttpStatusCode.InternalServerError, result.StatusCode);
        await using var verify = api.Context();
        Assert.False(await verify.Set<EnvironmentRecord>().AnyAsync(x => x.ProjectId == api.Project.Id && x.Code == "UAT"));
        Assert.Equal(origins, await verify.Set<EnvironmentSecurityInitialization>().CountAsync());
        Assert.Equal(0, await verify.Set<AuditLog>().CountAsync(x => x.Action == "environment.create"));
    }
}
