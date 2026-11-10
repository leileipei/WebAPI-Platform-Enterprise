using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;

namespace WebApi.Integration.Tests;

public sealed class AuditPermissionMoveScopeTests
{
    [Theory]
    [InlineData("target", false, false)]
    [InlineData("both", true, false)]
    [InlineData("project", true, false)]
    [InlineData("organization", true, false)]
    [InlineData("platform", true, false)]
    [InlineData("project", false, true)]
    [InlineData("both", true, true)]
    [InlineData("both-projects", true, true)]
    [InlineData("organization", true, true)]
    public async Task MoveAuditRequiresAccessToEveryCapturedEnvironmentIncludingHistoricalRows(string access, bool visible, bool crossProject)
    {
        await using var api = new ApiFixture();
        await api.InitializeAsync();
        await api.SeedConsumerAsync();
        using var login = await api.LoginAsync();
        login.EnsureSuccessStatusCode();
        var target = new EnvironmentRecord { ProjectId = api.Project.Id, Code = "TARGET", Name = "Target" };
        var targetApiId = api.Api.Id;
        await using (var db = api.Context())
        {
            if (crossProject)
            {
                var project = new Project { OrganizationId = api.Organization.Id, Code = "TARGET", Name = "Target" };
                var targetApi = new Api { OrganizationId = api.Organization.Id, ProjectId = project.Id, Code = "TARGET", Name = "Target", OwnerUserId = api.User.Id };
                target.ProjectId = project.Id; targetApiId = targetApi.Id;
                (await db.Set<ApplicationRecord>().SingleAsync()).ProjectId = null;
                db.AddRange(project, targetApi);
            }
            db.Add(target); await db.SaveChangesAsync();
        }
        var from = DateTimeOffset.UtcNow.AddMinutes(-1);
        using (var create = await api.WriteAsync(HttpMethod.Post, $"/api/v1/applications/{api.Application.Id}/permissions",
            new { environmentId = api.Environment.Id, apiId = api.Api.Id, validFrom = from })) create.EnsureSuccessStatusCode();
        ApplicationPermissionAuditSnapshot before;
        await using (var db = api.Context()) { before = ApplicationPermissionAuditSnapshot.From(await db.Set<ApplicationApiPermission>().SingleAsync()); }
        using (var move = await api.WriteAsync(HttpMethod.Put, $"/api/v1/applications/{api.Application.Id}/permissions/{before.Id}",
            new { environmentId = target.Id, apiId = targetApiId, validFrom = from }, RevisionTag.Format(before.Revision))) move.EnsureSuccessStatusCode();

        long moveId, historicalId, ordinaryId, unresolvedId;
        string originalBefore, originalAfter;
        await using (var db = api.Context())
        {
            var audit = await db.Set<AuditLog>().Where(a => a.ResourceType == nameof(ApplicationApiPermission) && a.ResourceId == before.Id.ToString()).OrderByDescending(a => a.Id).FirstAsync();
            moveId = audit.Id; originalBefore = audit.BeforeJson!; originalAfter = audit.AfterJson!;
            // Historical reverse move: the restricted environment occurs in AfterJson, not BeforeJson.
            var history = new AuditLog { OrganizationId = api.Organization.Id, ProjectId = target.ProjectId, EnvironmentId = target.Id,
                ResourceType = nameof(ApplicationApiPermission), ResourceId = before.Id.ToString(), Action = "app.permission.save",
                BeforeJson = originalAfter, AfterJson = originalBefore };
            var ordinary = new AuditLog { OrganizationId = api.Organization.Id, ProjectId = target.ProjectId, EnvironmentId = target.Id,
                ResourceType = nameof(ApplicationApiPermission), ResourceId = before.Id.ToString(), Action = "app.permission.save",
                BeforeJson = originalAfter, AfterJson = originalAfter };
            // A multi-snapshot historical row must fail closed if even one scope cannot be resolved.
            var unresolved = new AuditLog { OrganizationId = api.Organization.Id, ProjectId = target.ProjectId, EnvironmentId = target.Id,
                ResourceType = nameof(ApplicationApiPermission), ResourceId = before.Id.ToString(), Action = "app.permission.save",
                BeforeJson = JsonSerializer.Serialize(new[] {
                    new { Type = nameof(ApplicationApiPermission), Fields = before with { EnvironmentId = target.Id, ApiId = targetApiId } },
                    new { Type = nameof(ApplicationApiPermission), Fields = before with { Id = Guid.NewGuid(), EnvironmentId = Guid.NewGuid() } }
                }), AfterJson = originalAfter };
            db.AddRange(history, ordinary, unresolved);
            var grant = await db.Set<UserProjectScope>().SingleAsync();
            grant.ProjectId = access is "organization" or "platform" ? null : target.ProjectId;
            grant.EnvironmentId = access is "target" or "both" ? target.Id : null;
            if (access is "both" or "both-projects") db.Add(new UserProjectScope { UserId = api.User.Id, OrganizationId = api.Organization.Id, ProjectId = api.Project.Id, EnvironmentId = access == "both" ? api.Environment.Id : null, AccessMode = "read_only" });
            if (access == "platform") { var role = await db.Set<Role>().SingleAsync(); role.Code = "PlatformAdmin"; role.IsSystem = true; role.OrganizationId = null; }
            await db.SaveChangesAsync(); historicalId = history.Id; ordinaryId = ordinary.Id; unresolvedId = unresolved.Id;
        }
        using var response = await api.Client.GetAsync($"/api/v1/audit-logs?resourceId={before.Id}&pageSize=100");
        response.EnsureSuccessStatusCode();
        var page = await response.Content.ReadFromJsonAsync<JsonElement>();
        var items = page.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(visible, items.Any(x => x.GetProperty("id").GetInt64() == moveId));
        Assert.Equal(visible, items.Any(x => x.GetProperty("id").GetInt64() == historicalId));
        Assert.Contains(items, x => x.GetProperty("id").GetInt64() == ordinaryId);
        Assert.Equal(access == "platform", items.Any(x => x.GetProperty("id").GetInt64() == unresolvedId));
        Assert.Equal(items.Length, page.GetProperty("total").GetInt32());
        if (!visible) Assert.DoesNotContain(api.Environment.Id.ToString(), await response.Content.ReadAsStringAsync());
        if ((!crossProject && access == "project") || access is "organization" or "platform")
        {
            using var appAudit = await api.Client.GetAsync($"/api/v1/applications/{api.Application.Id}/audit?pageSize=100");
            appAudit.EnsureSuccessStatusCode();
            var appPage = await appAudit.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Contains(appPage.GetProperty("items").EnumerateArray(), x => x.GetProperty("id").GetInt64() == moveId);
        }
        if (access == "both")
        {
            await using (var db = api.Context()) await db.Set<UserProjectScope>().Where(x => x.EnvironmentId == api.Environment.Id).ExecuteDeleteAsync();
            using var revoked = await api.Client.GetAsync($"/api/v1/audit-logs?resourceId={before.Id}");
            revoked.EnsureSuccessStatusCode();
            var revokedPage = await revoked.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(1, revokedPage.GetProperty("total").GetInt32());
            Assert.Equal(ordinaryId, Assert.Single(revokedPage.GetProperty("items").EnumerateArray()).GetProperty("id").GetInt64());
        }
        await using var check = api.Context();
        var preserved = await check.Set<AuditLog>().SingleAsync(x => x.Id == moveId);
        Assert.Equal(originalBefore, preserved.BeforeJson); Assert.Equal(originalAfter, preserved.AfterJson);
    }
}
