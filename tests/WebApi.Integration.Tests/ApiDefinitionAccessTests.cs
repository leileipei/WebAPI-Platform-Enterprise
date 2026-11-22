using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Catalog;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;

namespace WebApi.Integration.Tests;

public sealed class ApiDefinitionAccessTests(MigratedDatabaseTemplateFixture template) : IClassFixture<MigratedDatabaseTemplateFixture>
{
    [Theory]
    [InlineData(true, true, false, "read_write")]
    [InlineData(true, false, true, "read_write")]
    [InlineData(false, true, true, "read_write")]
    [InlineData(true, true, true, "read_only")]
    public async Task DetailReportsIndependentResourcePermissionsAndMatchesEachEndpoint(
        bool schemaRead, bool schemaWrite, bool versionWrite, string accessMode)
    {
        await using var f = new ApiFixture(template.Template);
        await f.InitializeAsync();
        await f.SeedCatalogAsync();
        await using (var db = f.Context())
        {
            var localRoleId = await db.Set<UserRole>().Where(r => r.UserId == f.User.Id).Select(r => r.RoleId).SingleAsync();
            var foreignOrganization = new Organization { Code = "OTHER", Name = "其他组织" };
            var otherRole = new Role { OrganizationId = foreignOrganization.Id, Code = "OtherCatalog", Name = "其他组织契约维护" };
            db.AddRange(foreignOrganization, otherRole, new UserRole { UserId = f.User.Id, RoleId = otherRole.Id });
            await db.Set<UserProjectScope>().Where(s => s.UserId == f.User.Id)
                .ExecuteUpdateAsync(update => update.SetProperty(s => s.AccessMode, accessMode));
            db.Add(new UserProjectScope { UserId = f.User.Id, OrganizationId = foreignOrganization.Id, AccessMode = "read_write" });
            foreach (var (code, grantedHere) in new[] { ("api.schema.read", schemaRead), ("api.schema.write", schemaWrite), ("api.version.write", versionWrite) })
            {
                var permission = await db.Set<Permission>().SingleAsync(p => p.Code == code);
                db.Add(new RolePermission { RoleId = otherRole.Id, PermissionId = permission.Id });
                if (!grantedHere)
                    await db.Set<RolePermission>().Where(rp => rp.RoleId == localRoleId && rp.PermissionId == permission.Id).ExecuteDeleteAsync();
            }
            await db.SaveChangesAsync();
        }
        using var login = await f.LoginAsync();
        login.EnsureSuccessStatusCode();
        var me = await f.Client.GetFromJsonAsync<JsonElement>("/api/v1/auth/me");
        foreach (var code in new[] { "api.schema.read", "api.schema.write", "api.version.write" })
            Assert.Contains(me.GetProperty("permissions").EnumerateArray(), value => value.GetString() == code);

        var api = $"/api/v1/apis/{f.Api.Id}";
        var version = $"/api/v1/versions/{f.Version.Id}";
        var writableSchemas = schemaWrite && accessMode == "read_write";
        var writableVersions = versionWrite && accessMode == "read_write";
        var etag = "\"1\"";
        foreach (var kind in new[] { "parameters", "schemas" })
        {
            using var read = await f.Client.GetAsync($"{version}/{kind}");
            Assert.Equal(schemaRead ? HttpStatusCode.OK : HttpStatusCode.NotFound, read.StatusCode);
            using var write = await f.WriteAsync(HttpMethod.Put, $"{version}/{kind}", Array.Empty<object>(), etag);
            if (writableSchemas) { write.EnsureSuccessStatusCode(); etag = write.Headers.ETag!.Tag; }
            else Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
        }
        using var create = await f.WriteAsync(HttpMethod.Post, $"{api}/versions", new CreateVersionRequest("2.0"));
        using var edit = await f.WriteAsync(HttpMethod.Put, version, new CreateVersionRequest("1.1"), etag);
        if (writableVersions) { create.EnsureSuccessStatusCode(); edit.EnsureSuccessStatusCode(); }
        else { Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode); Assert.Equal(HttpStatusCode.Forbidden, edit.StatusCode); }
        var detail = await f.Client.GetFromJsonAsync<JsonElement>(api);
        Assert.True(detail.TryGetProperty("canReadSchemas", out var canRead), "API detail did not report scoped schema read access.");
        Assert.Equal(schemaRead, canRead.GetBoolean());
        Assert.Equal(writableSchemas, detail.GetProperty("canWriteSchemas").GetBoolean());
        Assert.Equal(writableVersions, detail.GetProperty("canWriteVersions").GetBoolean());
    }
}
