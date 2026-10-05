using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;
namespace WebApi.Integration.Tests;
public sealed class SsoPersistenceTests
{
    [Fact] public async Task MigrationPreservesLocalUsers()
    {
        await using var database=new PostgresDatabase();await database.InitializeAsync();
        var id=Guid.NewGuid();const string hash="unchanged-existing-password-hash";
        await using(var db=database.Context())
        {
            await db.GetService<IMigrator>().MigrateAsync("20261005090000_SystemSettings");
            db.Add(new UserRecord{Id=id,Username="existing-admin",DisplayName="Existing",PasswordHash=hash,SecurityStamp="existing-stamp"});await db.SaveChangesAsync();
            await db.Database.MigrateAsync();
            var user=await db.Set<UserRecord>().SingleAsync(x=>x.Id==id);Assert.Equal(hash,user.PasswordHash);Assert.Equal("local",user.AuthSource);
            var exists=await db.Database.SqlQueryRaw<bool>("SELECT to_regclass('public.sso_providers') IS NOT NULL AS \"Value\"").SingleAsync();Assert.True(exists,"SSO table must be created by migration.");
            Assert.Equal(0,await db.Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM sso_providers").SingleAsync());
            Assert.False(db.Database.HasPendingModelChanges());
        }
    }
    [Fact] public async Task DefaultConstraintsAreScoped()
    {
        await using var database=new PostgresDatabase();await database.InitializeAsync();await using var db=database.Context();await db.Database.MigrateAsync();
        var exists=await db.Database.SqlQueryRaw<bool>("SELECT to_regclass('public.sso_providers') IS NOT NULL AS \"Value\"").SingleAsync();Assert.True(exists,"SSO constraints require migrated table.");
        var one=Guid.NewGuid();var two=Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO sso_providers(id,name,issuer,client_id,secret_ref,scopes,claim_mapping,enabled,is_default) VALUES ({one},'one','https://id.example/','console','file://sso/one','[\"openid\"]'::jsonb,'{{}}'::jsonb,true,true)");
        var duplicate=await Assert.ThrowsAsync<Npgsql.PostgresException>(()=>db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO sso_providers(id,name,issuer,client_id,secret_ref,scopes,claim_mapping,enabled,is_default) VALUES ({two},'two','https://id.example/','console','file://sso/two','[\"openid\"]'::jsonb,'{{}}'::jsonb,true,true)"));Assert.Equal("23505",duplicate.SqlState);
        var disabled=await Assert.ThrowsAsync<Npgsql.PostgresException>(()=>db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO sso_providers(id,name,issuer,client_id,secret_ref,scopes,claim_mapping,enabled,is_default) VALUES ({two},'two','https://id.example/','console','file://sso/two','[\"openid\"]'::jsonb,'{{}}'::jsonb,false,true)"));Assert.Equal("23514",disabled.SqlState);
        foreach(var code in new[]{"A","B"}) {var org=Guid.NewGuid();await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO organizations(id,code,name,status,created_at,updated_at,revision) VALUES ({org},{code},{code},'Active',now(),now(),1)");await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO sso_providers(id,organization_id,name,issuer,client_id,secret_ref,scopes,claim_mapping,enabled,is_default) VALUES ({Guid.NewGuid()},{org},'org','https://id.example/','console','file://sso/org','[\"openid\"]'::jsonb,'{{}}'::jsonb,true,true)");}
    }
}
