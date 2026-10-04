using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using WebApi.Integration.Tests.Support;
using Xunit;
namespace WebApi.Integration.Tests;
public sealed class AlertPersistenceTests
{
    private const string RuleSql="""
      INSERT INTO alert_rules(id,organization_id,project_id,environment_id,name,normalized_name,metric,expression,severity,enabled,for_seconds,target_type,target_id,window_seconds,notification,revision,logic_revision,created_by,updated_by,created_at,updated_at)
      VALUES(@rule,@org,@project,@env,'Errors','ERRORS','error_5xx_ratio','error_5xx_ratio > 0.05','Critical',true,300,'Environment',null,300,'{"inConsole":true,"requestedChannels":[]}',1,1,@actor,@actor,now(),now());
      """;
    private const string EventSql="""
      INSERT INTO alert_events(id,rule_id,rule_revision,logic_revision,organization_id,project_id,environment_id,resource_key,resource_type,resource_id,occurrence_no,status,severity,message,rule_summary,started_at,condition_started_at,evaluation_state,revision)
      VALUES(@id,@rule,1,1,@org,@project,@env,'Environment','Environment',null,@occurrence,'Open','Critical','threshold exceeded','safe rule',now(),now(),'Known',1);
      """;
    private static NpgsqlCommand Command(string sql,NpgsqlConnection c,ApiFixture f,Guid rule,Guid? id=null,int occurrence=1)
    {
        var cmd=new NpgsqlCommand(sql,c);cmd.Parameters.AddWithValue("rule",rule);cmd.Parameters.AddWithValue("org",f.Organization.Id);cmd.Parameters.AddWithValue("project",f.Project.Id);cmd.Parameters.AddWithValue("env",f.Environment.Id);cmd.Parameters.AddWithValue("actor",f.User.Id);cmd.Parameters.AddWithValue("id",id??Guid.NewGuid());cmd.Parameters.AddWithValue("occurrence",occurrence);return cmd;
    }
    [Fact] public async Task OnlyOneActiveOccurrenceForNullableResourceId()
    {
        await using var f=new ApiFixture();await f.InitializeAsync();await f.SeedScopeAsync();var rule=Guid.NewGuid();
        await using var c=await f.Database.OpenAsync();await using(var seed=Command(RuleSql,c,f,rule))await seed.ExecuteNonQueryAsync();
        await using var tx=await c.BeginTransactionAsync();await using(var first=Command(EventSql,c,f,rule))await first.ExecuteNonQueryAsync();
        await using var other=await f.Database.OpenAsync();await using var tx2=await other.BeginTransactionAsync();await using var second=Command(EventSql,other,f,rule,occurrence:2);
        var competing=second.ExecuteNonQueryAsync();await Task.Delay(100);Assert.False(competing.IsCompleted);await tx.CommitAsync();
        var error=await Assert.ThrowsAsync<PostgresException>(async()=>await competing);Assert.Equal(PostgresErrorCodes.UniqueViolation,error.SqlState);await tx2.RollbackAsync();
        await using var count=new NpgsqlCommand("SELECT count(*)::int FROM alert_events WHERE status <> 'Resolved'",c);Assert.Equal(1,await count.ExecuteScalarAsync());
        await using(var resolve=new NpgsqlCommand("UPDATE alert_events SET status='Resolved',resolved_at=now(),resolve_reason='Recovered'",c))await resolve.ExecuteNonQueryAsync();
        await using(var reopened=Command(EventSql,c,f,rule,occurrence:2))Assert.Equal(1,await reopened.ExecuteNonQueryAsync());
    }
    [Fact] public async Task WrongScopeForeignKeyRejected()
    {
        await using var f=new ApiFixture();await f.InitializeAsync();await f.SeedScopeAsync();await using var c=await f.Database.OpenAsync();var other=Guid.NewGuid();
        await using(var org=new NpgsqlCommand("INSERT INTO organizations(id,code,name,status,created_at,updated_at) VALUES(@id,'OTHER','Other','Active',now(),now())",c)){org.Parameters.AddWithValue("id",other);await org.ExecuteNonQueryAsync();}
        await using var cmd=Command(RuleSql,c,f,Guid.NewGuid());cmd.Parameters["org"].Value=other;
        var error=await Assert.ThrowsAsync<PostgresException>(()=>cmd.ExecuteNonQueryAsync());Assert.Equal(PostgresErrorCodes.ForeignKeyViolation,error.SqlState);
    }
    [Fact] public async Task InvalidScopeTargetAndNormalizedDuplicateRejected()
    {
        await using var f=new ApiFixture();await f.InitializeAsync();await f.SeedScopeAsync();await using var c=await f.Database.OpenAsync();
        await using(var seed=Command(RuleSql,c,f,Guid.NewGuid()))await seed.ExecuteNonQueryAsync();
        await using(var duplicate=Command(RuleSql,c,f,Guid.NewGuid()))Assert.Equal(PostgresErrorCodes.UniqueViolation,(await Assert.ThrowsAsync<PostgresException>(()=>duplicate.ExecuteNonQueryAsync())).SqlState);
        foreach(var mutation in new[]{"'Environment',null","'Api',null","'Destination',null"})
        {
            var sql=RuleSql.Replace("'Environment',null",mutation).Replace("'Errors','ERRORS'","'Other','OTHER'");
            if(mutation=="'Environment',null")sql=sql.Replace("@project,@env","null,@env");
            await using var bad=Command(sql,c,f,Guid.NewGuid());Assert.Equal(PostgresErrorCodes.CheckViolation,(await Assert.ThrowsAsync<PostgresException>(()=>bad.ExecuteNonQueryAsync())).SqlState);
        }
    }
    [Fact] public async Task MigratingExistingCorePreservesBytesAndDoesNotSeedRules()
    {
        await using var database=new PostgresDatabase();await database.InitializeAsync();await using var db=database.Context();
        var migrator=db.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
        await migrator.MigrateAsync("20261003213457_RecoveryReference");
        var user=new WebApi.Infrastructure.Persistence.Entities.UserRecord{Username="legacy-owner",DisplayName="Legacy"};
        var org=new WebApi.Infrastructure.Persistence.Entities.Organization{Code="KEEP",Name="Keep"};
        var project=new WebApi.Infrastructure.Persistence.Entities.Project{OrganizationId=org.Id,Code="KEEP",Name="Keep"};
        var env=new WebApi.Infrastructure.Persistence.Entities.EnvironmentRecord{ProjectId=project.Id,Code="KEEP",Name="Keep"};
        var config=new WebApi.Infrastructure.Persistence.Entities.GatewayConfigVersion{EnvironmentId=env.Id,CreatedBy=user.Id};
        byte[] bytes=System.Text.Encoding.UTF8.GetBytes("{ \"name\": \"订单\", \"value\": 1 }\n");
        db.AddRange(user,org,project,env,config,new WebApi.Infrastructure.Persistence.Entities.GatewayConfigSnapshot{ConfigVersionId=config.Id,Payload="{}",PayloadBytes=bytes,SizeBytes=bytes.Length});
        await db.SaveChangesAsync();await migrator.MigrateAsync();db.ChangeTracker.Clear();
        Assert.Equal(bytes,(await db.Set<WebApi.Infrastructure.Persistence.Entities.GatewayConfigSnapshot>().SingleAsync()).PayloadBytes);
        Assert.Equal("Keep",(await db.Set<WebApi.Infrastructure.Persistence.Entities.Organization>().SingleAsync()).Name);
        await using var c=await database.OpenAsync();await using var count=new NpgsqlCommand("SELECT count(*)::int FROM alert_rules",c);Assert.Equal(0,await count.ExecuteScalarAsync());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }
}
