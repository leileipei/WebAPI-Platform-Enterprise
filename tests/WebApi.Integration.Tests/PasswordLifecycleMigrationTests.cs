using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using WebApi.Migrator;
using Xunit;

namespace WebApi.Integration.Tests;

public sealed class PasswordLifecycleMigrationTests
{
    private const string PreviousMigration = "20261012004548_ReleaseFrozenContracts";
    private static async Task<JsonElement> UserJsonAsync(PostgresDatabase database, Guid id)
    {
        await using var connection = await database.OpenAsync();
        await using var command = new Npgsql.NpgsqlCommand("SELECT to_jsonb(u)::text FROM users u WHERE id=@id", connection);
        command.Parameters.AddWithValue("id", id);
        return JsonDocument.Parse((string)(await command.ExecuteScalarAsync())!).RootElement.Clone();
    }

    [Fact]
    public async Task Existing_accounts_keep_hash_stamp_roles_and_scopes()
    {
        await using var database = new PostgresDatabase(); await database.InitializeAsync();
        await using var db = database.Context(); var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(PreviousMigration);
        var id = Guid.NewGuid(); var stamp = Guid.NewGuid().ToString("N"); var hash = "legacy-private-hash-preserved";
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO users (id,username,display_name,password_hash,status,auth_source,created_at,updated_at,security_stamp) VALUES ({id},'legacy','旧账号',{hash},'Active','local',NOW(),NOW(),{stamp})");
        var org = new Organization { Code="LEGACY", Name="原组织" }; var role = new Role {Code="legacy",Name="原角色"};
        db.AddRange(org,role); db.Add(new UserRole {UserId=id,RoleId=role.Id});
        db.Add(new UserProjectScope {UserId=id,OrganizationId=org.Id,AccessMode="read_write"}); await db.SaveChangesAsync();
        var oldHistory = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
        await migrator.MigrateAsync();
        var after = await UserJsonAsync(database,id);
        Assert.True(after.TryGetProperty("must_change_password",out var flag)); Assert.False(flag.GetBoolean());
        Assert.Equal(JsonValueKind.Null,after.GetProperty("password_changed_at").ValueKind);
        Assert.Equal(hash,after.GetProperty("password_hash").GetString()); Assert.Equal(stamp,after.GetProperty("security_stamp").GetString());
        Assert.Equal("Active",after.GetProperty("status").GetString());
        Assert.True(await db.Set<UserRole>().AnyAsync(x=>x.UserId==id&&x.RoleId==role.Id));
        Assert.True(await db.Set<UserProjectScope>().AnyAsync(x=>x.UserId==id&&x.OrganizationId==org.Id&&x.AccessMode=="read_write"));
        Assert.Equal(oldHistory,(await db.Database.GetAppliedMigrationsAsync()).Take(oldHistory.Length).ToArray());
    }

    [Fact]
    public async Task Fresh_admin_requires_change_and_repeated_bootstrap_preserves_account()
    {
        await using var database = new PostgresDatabase(); await database.InitializeAsync();
        await using var db = database.Context(); await db.Database.MigrateAsync();
        var secretFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(secretFile,"Initial-only-Private-Secret-123!");
            if(!OperatingSystem.IsWindows()) File.SetUnixFileMode(secretFile,UnixFileMode.UserRead|UnixFileMode.UserWrite);
            await BootstrapAccounts.RunAsync(db,"new-admin",secretFile);
            var user = await db.Set<UserRecord>().AsNoTracking().SingleAsync(x=>x.Username=="new-admin");
            var first = await UserJsonAsync(database,user.Id);
            Assert.True(first.TryGetProperty("must_change_password",out var flag)); Assert.True(flag.GetBoolean());
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE users SET must_change_password=false WHERE id={user.Id}");
            await File.WriteAllTextAsync(secretFile,"Replacement-bootstrap-must-not-reset-456!");
            await BootstrapAccounts.RunAsync(db,"new-admin",secretFile);
            var after = await UserJsonAsync(database,user.Id);
            Assert.False(after.GetProperty("must_change_password").GetBoolean());
            Assert.Equal(first.GetProperty("password_hash").GetString(),after.GetProperty("password_hash").GetString());
            Assert.Equal(first.GetProperty("security_stamp").GetString(),after.GetProperty("security_stamp").GetString());
        }
        finally {File.Delete(secretFile);}
    }

    [Fact]
    public async Task Created_local_user_requires_change_in_response_and_database()
    {
        await using var api = new ApiFixture(); await api.InitializeAsync(); await api.SeedScopeAsync(platformAdmin:true);
        using var login = await api.LoginAsync(); login.EnsureSuccessStatusCode();
        using var result = await api.WriteAsync(HttpMethod.Post,"/api/v1/users",new {username="new-local",displayName="新账号",password="New-private-user-123!"});
        Assert.Equal(HttpStatusCode.OK,result.StatusCode);
        var body = JsonDocument.Parse(await result.Content.ReadAsStringAsync()).RootElement;
        Assert.True(body.TryGetProperty("mustChangePassword",out var flag)); Assert.True(flag.GetBoolean());
        var after = await UserJsonAsync(api.Database,body.GetProperty("id").GetGuid());
        Assert.True(after.GetProperty("must_change_password").GetBoolean());
    }

    [Fact]
    public async Task Sso_remains_without_local_password()
    {
        await using var database=new PostgresDatabase(); await database.InitializeAsync();
        await using var db=database.Context(); await db.Database.MigrateAsync();
        var user=new UserRecord {Username="sso_"+Guid.NewGuid().ToString("N"),DisplayName="外部身份",AuthSource="sso",SecurityStamp=Guid.NewGuid().ToString("N")};
        db.Add(user); await db.SaveChangesAsync(); var after=await UserJsonAsync(database,user.Id);
        Assert.True(after.TryGetProperty("must_change_password",out var flag)); Assert.False(flag.GetBoolean());
        Assert.Equal(JsonValueKind.Null,after.GetProperty("password_hash").ValueKind);
    }

    [Fact]
    public async Task Isolated_down_up_preserves_existing_account()
    {
        await using var api=new ApiFixture(); await api.InitializeAsync();
        var before=await UserJsonAsync(api.Database,api.User.Id);
        await using var db=api.Context(); var migrator=db.GetService<IMigrator>(); await migrator.MigrateAsync(PreviousMigration);
        var old=await UserJsonAsync(api.Database,api.User.Id); Assert.False(old.TryGetProperty("must_change_password",out _));
        await migrator.MigrateAsync(); var after=await UserJsonAsync(api.Database,api.User.Id);
        Assert.True(after.TryGetProperty("must_change_password",out var flag)); Assert.False(flag.GetBoolean());
        Assert.Equal(before.GetProperty("password_hash").GetString(),after.GetProperty("password_hash").GetString());
    }
}
