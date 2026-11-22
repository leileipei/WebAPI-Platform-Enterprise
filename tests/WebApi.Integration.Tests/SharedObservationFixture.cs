using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;

namespace WebApi.Integration.Tests;

internal static class SharedObservationFixture
{
    internal static async Task<ApplicationRecord> AddApplicationAsync(ApiFixture fixture)
    {
        var shared = new ApplicationRecord { OrganizationId = fixture.Organization.Id, Code = "SHARED", Name = "共享消费方" };
        await using var db = fixture.Context();
        db.Add(shared);
        db.Add(Grant(fixture, shared.Id, fixture.Environment.Id, fixture.Api.Id));
        await db.SaveChangesAsync();
        return shared;
    }

    internal static async Task<(Project Project, Api Api, EnvironmentRecord Allowed, EnvironmentRecord Denied)>
        AddSecondProjectAsync(ApiFixture fixture, Guid appId)
    {
        var project = new Project { OrganizationId = fixture.Organization.Id, Code = "SECOND", Name = "第二项目" };
        var api = new Api { OrganizationId = fixture.Organization.Id, ProjectId = project.Id, Code = "SECOND_API",
            Name = "订单", OwnerUserId = fixture.User.Id };
        var allowed = new EnvironmentRecord { ProjectId = project.Id, Code = "ALLOWED", Name = "授权环境" };
        var denied = new EnvironmentRecord { ProjectId = project.Id, Code = "DENIED", Name = "隔离环境" };
        var expired = Grant(fixture, appId, denied.Id, api.Id);
        expired.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        await using var db = fixture.Context();
        db.AddRange(project, api, allowed, denied, expired, Grant(fixture, appId, allowed.Id, api.Id),
            new GatewayNode { EnvironmentId = allowed.Id, NodeName = "allowed-node" },
            new GatewayNode { EnvironmentId = denied.Id, NodeName = "denied-node" });
        await db.SaveChangesAsync();
        return (project, api, allowed, denied);
    }

    internal static ApplicationApiPermission Grant(ApiFixture fixture, Guid application, Guid environment, Guid api) =>
        new() { ApplicationId = application, EnvironmentId = environment, ApiId = api,
            Mode = "LegacyApi", CreatedBy = fixture.User.Id, ValidFrom = DateTimeOffset.UtcNow.AddHours(-1) };
}
