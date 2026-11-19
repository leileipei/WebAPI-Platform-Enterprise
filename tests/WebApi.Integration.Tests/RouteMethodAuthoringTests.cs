using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Applications;
using WebApi.Contracts.Catalog;
using WebApi.Contracts.Common;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;

namespace WebApi.Integration.Tests;

public sealed class RouteMethodAuthoringTests
{
    [Fact]
    public async Task ExplicitAuthorizationUsesTheCurrentRouteRevisionAndKeepsLegacyDowngradeClosed()
    {
        await using var fixture = new ApiFixture();
        await fixture.InitializeAsync();
        await fixture.SeedConsumerAsync(seedFreshEnvironment: true);
        using var login = await fixture.LoginAsync();
        login.EnsureSuccessStatusCode();
        using var created = await fixture.WriteAsync(HttpMethod.Post, $"/api/v1/environments/{fixture.Environment.Id}/routes",
            fixture.RouteBody("/reviewed-route"));
        created.EnsureSuccessStatusCode();
        var route = (await created.Content.ReadFromJsonAsync<RouteDto>())!;

        var application = $"/api/v1/applications/{fixture.Application.Id}";
        var options = await fixture.Client.GetFromJsonAsync<RouteMethodOptionsDto>(
            $"{application}/route-method-options?environmentId={fixture.Environment.Id}&apiId={fixture.Api.Id}");
        var choice = Assert.Single(options!.Routes);
        Assert.Equal(route.Id, choice.RouteId);
        Assert.Equal("GET", Assert.Single(choice.Methods));
        var window = new PermissionSaveRequest(fixture.Api.Id, fixture.Environment.Id, DateTimeOffset.UtcNow.AddMinutes(-1));
        using var legacy = await fixture.WriteAsync(HttpMethod.Post, $"{application}/permissions", window);
        legacy.EnsureSuccessStatusCode();
        var parent = (await legacy.Content.ReadFromJsonAsync<ApplicationPermissionDto>())!;
        Assert.Null(parent.Mode);
        var path = $"{application}/permissions/{parent.Id}";
        var explicitGrant = window with { Mode = "RouteMethod",
            RouteMethods = [new(choice.RouteId, choice.AuthorizationRevision, ["GET"])] };
        foreach (var invalid in new[]
                 {
                     explicitGrant with { RouteMethods = [new(choice.RouteId, choice.AuthorizationRevision + 1, ["GET"])] },
                     explicitGrant with { RouteMethods = [new(Guid.NewGuid(), choice.AuthorizationRevision, ["GET"])] },
                     explicitGrant with { RouteMethods = [new(choice.RouteId, choice.AuthorizationRevision, ["GET", "GET"])] }
                 })
        {
            using var rejected = await fixture.WriteAsync(HttpMethod.Put, path, invalid, RevisionTag.Format(parent.Revision));
            Assert.Contains(rejected.StatusCode, new[] { HttpStatusCode.PreconditionFailed, HttpStatusCode.UnprocessableEntity });
        }
        using var converted = await fixture.WriteAsync(HttpMethod.Put, path, explicitGrant, RevisionTag.Format(parent.Revision));
        Assert.True(converted.IsSuccessStatusCode, await converted.Content.ReadAsStringAsync());
        var grant = (await converted.Content.ReadFromJsonAsync<ApplicationPermissionDto>())!;
        Assert.Equal("RouteMethod", grant.Mode);
        Assert.Equal(choice.AuthorizationRevision, Assert.Single(grant.RouteMethods!).AuthorizationRevision);
        using var downgrade = await fixture.WriteAsync(HttpMethod.Put, path, window, RevisionTag.Format(grant.Revision));
        Assert.Equal(HttpStatusCode.Conflict, downgrade.StatusCode);

        using var updated = await fixture.WriteAsync(HttpMethod.Put, $"/api/v1/routes/{route.Id}",
            fixture.RouteBody("/reviewed-route-v2", id: route.Id), RevisionTag.Format(route.Revision));
        updated.EnsureSuccessStatusCode();
        using var stale = await fixture.WriteAsync(HttpMethod.Put, path, explicitGrant, RevisionTag.Format(grant.Revision));
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        var read = await fixture.Client.GetFromJsonAsync<ApplicationPermissionDto[]>($"{application}/permissions");
        Assert.Equal(choice.AuthorizationRevision, Assert.Single(Assert.Single(read!).RouteMethods!).AuthorizationRevision);
        await using var db = fixture.Context();
        Assert.Single(await db.Set<ApplicationRouteMethodGrant>().ToArrayAsync());
        Assert.Single(await db.Set<AuditLog>().Where(x => x.Action == "app.permission.route-method.save").ToArrayAsync());
    }
}
