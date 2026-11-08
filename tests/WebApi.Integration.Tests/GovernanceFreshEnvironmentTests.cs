using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
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

        await using var db = api.Context();
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
