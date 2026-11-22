using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Catalog;
using WebApi.Contracts.Common;
using WebApi.Contracts.Releases;
using WebApi.Infrastructure.Catalog;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;

namespace WebApi.Integration.Tests;

public sealed class ApiPendingReleaseTests(MigratedDatabaseTemplateFixture template) : IClassFixture<MigratedDatabaseTemplateFixture>
{
    private async Task<ApiFixture> OpenAsync()
    {
        var f = new ApiFixture(template.Template);
        await f.InitializeAsync();
        await f.SeedReleaseAsync();
        using var login = await f.LoginAsync();
        login.EnsureSuccessStatusCode();
        return f;
    }

    private static Task<ApiDetailDto?> DetailAsync(ApiFixture f, Guid? environment = null) =>
        f.Client.GetFromJsonAsync<ApiDetailDto>($"/api/v1/apis/{f.Api.Id}" +
            (environment is null ? "" : $"?environmentId={environment}"));

    private static ReleaseRecord Draft(ApiFixture f, Guid environment, Guid version, int order) => new()
    {
        EnvironmentId = environment, ReleaseNo = $"REL-{order}", ReleaseType = "publish", Status = "Draft",
        RequestedBy = f.User.Id, CreatedAt = DateTimeOffset.UnixEpoch.AddMinutes(order),
        CandidateBytes = CanonicalJson.Serialize(new CreateReleaseRequest(0,[version],[]))
    };

    [Fact]
    public async Task EachEnvironmentReturnsItsLatestMatchingReleaseAndLegacyClientsCanReadTheLatest()
    {
        await using var f = await OpenAsync();
        await using var db = f.Context();
        var other = new EnvironmentRecord {ProjectId=f.Project.Id, Code="QA", Name="联调"};
        var older = Draft(f,f.Environment.Id,f.Version.Id,1);
        var first = Draft(f,f.Environment.Id,f.Version.Id,2);
        var second = Draft(f,other.Id,f.Version.Id,3);
        db.AddRange(other,older,first,second);
        await db.SaveChangesAsync();
        Assert.Equal(first.Id,(await DetailAsync(f,f.Environment.Id))!.PendingReleaseId);
        Assert.Equal(second.Id,(await DetailAsync(f,other.Id))!.PendingReleaseId);
        Assert.Equal(second.Id,(await DetailAsync(f))!.PendingReleaseId);
        using var release = await f.Client.GetAsync($"/api/v1/releases/{first.Id}");
        release.EnsureSuccessStatusCode();

        second.Status = "Cancelled";
        await db.SaveChangesAsync();
        Assert.Null((await DetailAsync(f,other.Id))!.PendingReleaseId);
        Assert.Null((await DetailAsync(f,Guid.NewGuid()))!.PendingReleaseId);
        Assert.Equal(first.Id,(await DetailAsync(f))!.PendingReleaseId);
    }

    [Theory]
    [InlineData("publish","WaitingApproval")]
    [InlineData("rollback","Draft")]
    [InlineData("retry","Building")]
    public async Task FrozenVersionIdentifiesTheApiEvenAfterItsWorkingVersionIsGone(string type,string state)
    {
        await using var f = await OpenAsync();
        await using var db = f.Context();
        var version = CatalogService.Dto(f.Version) with {Id=Guid.NewGuid()};
        var candidate = new FrozenReleaseCandidate(f.Environment.Id,f.Organization.Id,f.Project.Id,0,
            [version.Id],[new(CatalogService.Dto(f.Api),version,[],[])],[],[],[],[],[],[]);
        var frozen = Draft(f,f.Environment.Id,version.Id,1);
        frozen.ReleaseType = type;
        frozen.Status = state;
        frozen.ApprovalPolicy = state == "Draft" ? null : "[]";
        frozen.CandidateBytes = CanonicalJson.Serialize(candidate);
        var otherApi = new Api {OrganizationId=f.Organization.Id,ProjectId=f.Project.Id,Code="OTHER",Name="其他",OwnerUserId=f.User.Id};
        var unrelatedVersion = new ApiVersion {ApiId=otherApi.Id,Version="1.0",ChangeType="compatible",CreatedBy=f.User.Id};
        var unrelated = Draft(f,f.Environment.Id,unrelatedVersion.Id,2);
        var unrelatedFrozen = Draft(f,f.Environment.Id,unrelatedVersion.Id,3);
        unrelatedFrozen.Status = "Ready";
        unrelatedFrozen.ApprovalPolicy = "[]";
        unrelatedFrozen.CandidateBytes = CanonicalJson.Serialize(candidate with {
            VersionIds = [unrelatedVersion.Id],
            Versions = [new(CatalogService.Dto(otherApi),CatalogService.Dto(unrelatedVersion),[],[])],
            Applications = [new(new(f.Application.Id,f.Organization.Id,null,"SHARED","共享","Owner","Active",1),[],
                [new(Guid.NewGuid(),f.Application.Id,f.Api.Id,f.Environment.Id,DateTimeOffset.UnixEpoch,null,1)])]
        });
        db.AddRange(frozen,otherApi,unrelatedVersion,unrelated,unrelatedFrozen);

        int index = 4;
        foreach (var terminal in new[] {"Succeeded","Failed","Cancelled","Rejected","RolledBack"})
        {
            var ended = Draft(f,f.Environment.Id,f.Version.Id,index++);
            ended.Status = terminal;
            db.Add(ended);
        }

        await db.SaveChangesAsync();
        Assert.Equal(frozen.Id,(await DetailAsync(f,f.Environment.Id))!.PendingReleaseId);
        Assert.Equal(frozen.Id,(await DetailAsync(f))!.PendingReleaseId);
    }

    [Theory]
    [InlineData("broken-json")]
    [InlineData("invalid-utf8")]
    [InlineData("empty")]
    [InlineData("null")]
    public async Task UnreadableHistoricalCandidateDoesNotBlockTheCatalogOrAnOlderMatchingRelease(string encoding)
    {
        await using var f = await OpenAsync();
        await using var db = f.Context();
        var valid = Draft(f,f.Environment.Id,f.Version.Id,1);
        var broken = Draft(f,f.Environment.Id,f.Version.Id,2);
        broken.CandidateBytes = encoding switch
        {
            "broken-json" => "{broken"u8.ToArray(),
            "invalid-utf8" => [0xff],
            "empty" => [],
            _ => null
        };
        db.AddRange(valid,broken);
        // Unrelated drafts fill multiple read batches at the same timestamp.
        for (var i = 3; i < 36; i++)
        {
            var unrelated = Draft(f,f.Environment.Id,Guid.NewGuid(),i);
            unrelated.CreatedAt = broken.CreatedAt;
            db.Add(unrelated);
        }
        await db.SaveChangesAsync();
        Assert.Equal(valid.Id,(await DetailAsync(f,f.Environment.Id))!.PendingReleaseId);
    }

    [Theory]
    [InlineData("api.read")]
    [InlineData("api.version.read")]
    public async Task VersionsEndpointKeepsBothReadPermissions(string revoked)
    {
        await using var f = await OpenAsync();
        var path = $"/api/v1/apis/{f.Api.Id}/versions";
        Assert.Equal(f.Version.Id,Assert.Single((await f.Client.GetFromJsonAsync<VersionDto[]>(path))!).Id);
        await using var db = f.Context();
        var permission = await db.Set<Permission>().Where(p=>p.Code==revoked).Select(p=>p.Id).SingleAsync();
        await db.Set<RolePermission>().Where(r=>r.PermissionId==permission).ExecuteDeleteAsync();
        using var denied = await f.Client.GetAsync(path);
        Assert.Equal(HttpStatusCode.NotFound,denied.StatusCode);
    }

    [Fact]
    public async Task CatalogReadAloneCannotDiscloseReleaseIdsAndInactiveEnvironmentsAreExcluded()
    {
        await using var f = await OpenAsync();
        await using var db = f.Context();
        var disabled = new EnvironmentRecord {ProjectId=f.Project.Id,Code="OLD",Name="停用",Status="Disabled"};
        var active = Draft(f,f.Environment.Id,f.Version.Id,1);
        var hidden = Draft(f,disabled.Id,f.Version.Id,2);
        db.AddRange(disabled,active,hidden);
        await db.SaveChangesAsync();
        Assert.Null((await DetailAsync(f,disabled.Id))!.PendingReleaseId);
        Assert.Equal(active.Id,(await DetailAsync(f))!.PendingReleaseId);

        var read = await db.Set<Permission>().Where(p=>p.Code=="release.read").Select(p=>p.Id).SingleAsync();
        await db.Set<RolePermission>().Where(r=>r.PermissionId==read).ExecuteDeleteAsync();
        Assert.Null((await DetailAsync(f,f.Environment.Id))!.PendingReleaseId);
        Assert.Null((await DetailAsync(f))!.PendingReleaseId);
        using var denied = await f.Client.GetAsync($"/api/v1/releases/{active.Id}");
        Assert.Equal(HttpStatusCode.NotFound,denied.StatusCode);
    }
}
