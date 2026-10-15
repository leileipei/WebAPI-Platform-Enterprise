using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Gateway;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using WebApi.PerformanceHost;
using Xunit;
namespace WebApi.Integration.Tests;
public sealed class CredentialUsageStorageTests
{
    private static readonly string[] Tables=["credential_usage_batches","credential_usage_collector_instances","credential_usage_collectors","credential_usage_coverage","credential_usage_hourly"];
    private static async Task RequireTablesAsync(PostgresDatabase database)
    {
        await using var c=await database.OpenAsync();await using var cmd=new NpgsqlCommand("SELECT tablename FROM pg_tables WHERE schemaname='public' AND tablename LIKE 'credential_usage_%' ORDER BY tablename",c);
        await using var rows=await cmd.ExecuteReaderAsync();var names=new List<string>();while(await rows.ReadAsync())names.Add(rows.GetString(0));Assert.Equal(Tables,names);
    }
    [Fact]
    public async Task Migration_adds_only_five_usage_tables_and_preserves_all_existing_bytes()
    {
        await using var api=new ApiFixture();await api.InitializeAsync();await api.SeedConsumerAsync();await using var db=api.Context();
        var migrator=db.GetService<IMigrator>();await migrator.MigrateAsync("20261015010000_AccountPasswordLifecycle");
        var before=await MigrationProbe.InspectAsync(db);await migrator.MigrateAsync();await RequireTablesAsync(api.Database);var after=await MigrationProbe.InspectAsync(db);
        Assert.Equal(Tables,after.Tables.Where(t=>!before.Tables.Any(b=>b.Name==t.Name)).Select(t=>t.Name).Order(StringComparer.Ordinal));
        Assert.Equal(before.Tables.Select(t=>(t.Name,t.Columns.Count,t.Rows,t.DataHash)),after.Tables.Where(t=>before.Tables.Any(b=>b.Name==t.Name)).Select(t=>(t.Name,t.Columns.Count,t.Rows,t.DataHash)));
        Assert.All(before.Indexes,x=>Assert.Contains(after.Indexes,y=>y.Name==x.Name&&y.Table==x.Table&&y.DefinitionHash==x.DefinitionHash&&y.Valid&&y.Ready));
        Assert.False(db.Database.HasPendingModelChanges());
    }
    [Theory]
    [InlineData("credential_usage_collectors","node_id,collector_id")]
    [InlineData("credential_usage_collector_instances","node_id,collector_id,instance_id")]
    [InlineData("credential_usage_batches","node_id,collector_id,sequence")]
    [InlineData("credential_usage_hourly","environment_id,credential_id,hour_start")]
    [InlineData("credential_usage_coverage","environment_id,node_id,hour_start")]
    public async Task Primary_keys_match_protocol_identities(string table,string expected)
    {
        await using var api=new ApiFixture();await api.InitializeAsync();await RequireTablesAsync(api.Database);await using var c=await api.Database.OpenAsync();
        await using var cmd=new NpgsqlCommand("SELECT string_agg(a.attname,',' ORDER BY k.ordinality) FROM pg_constraint p JOIN pg_class t ON t.oid=p.conrelid CROSS JOIN LATERAL unnest(p.conkey) WITH ORDINALITY k(attnum,ordinality) JOIN pg_attribute a ON a.attrelid=t.oid AND a.attnum=k.attnum WHERE p.contype='p' AND t.relname=@table",c);cmd.Parameters.AddWithValue("table",table);
        Assert.Equal(expected,(string)(await cmd.ExecuteScalarAsync())!);
    }
    [Fact]
    public async Task Cleanup_is_bounded_and_keeps_collector_cursor_and_last_used()
    {
        await using var api=new ApiFixture();await api.InitializeAsync();await api.SeedConsumerAsync();await RequireTablesAsync(api.Database);
        var now=DateTimeOffset.Parse("2026-10-15T12:00:00Z");var node=new GatewayNode{EnvironmentId=api.Environment.Id,NodeName="usage-cleanup",InstanceId=Guid.NewGuid().ToString(),Enabled=true};var credential=new ApplicationCredential{ApplicationId=api.Application.Id,AccessKey="cleanup",SecretHash="synthetic",SecretLast4="0000",ValidFrom=now.AddDays(-40),ExpiresAt=now.AddDays(10),LastUsedAt=now.AddDays(-35)};var collector=Guid.NewGuid();
        await using(var db=api.Context()){db.AddRange(node,credential);await db.SaveChangesAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO credential_usage_collectors(node_id,collector_id,environment_id,protocol,accepted_sequence,created_at) VALUES ({node.Id},{collector},{api.Environment.Id},'1.0',17,{now.AddDays(-40)})");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO credential_usage_hourly(environment_id,credential_id,hour_start,application_id,count,last_used_at) SELECT {api.Environment.Id},{credential.Id},{now.AddDays(-90)}+g*INTERVAL '1 hour',{api.Application.Id},1,{now.AddDays(-90)}+g*INTERVAL '1 hour'+INTERVAL '30 minutes' FROM generate_series(0,1000) g");
        }
        using var services=api.Services();var assembly=typeof(NodeRegistry).Assembly;var type=assembly.GetType("WebApi.Infrastructure.CredentialUsage.CredentialUsageCleanupService");Assert.NotNull(type);
        var service=ActivatorUtilities.CreateInstance(services.ServiceProvider,type);var method=type.GetMethod("CleanupAsync");Assert.NotNull(method);
        var task=(Task<int>)method.Invoke(service,[now,CancellationToken.None])!;var deleted=await task;Assert.InRange(deleted,1,1000);
        await using var after=api.Context();Assert.Equal(now.AddDays(-35),(await after.Set<ApplicationCredential>().AsNoTracking().SingleAsync(x=>x.Id==credential.Id)).LastUsedAt);
        await using var c=await api.Database.OpenAsync();await using var count=new NpgsqlCommand("SELECT accepted_sequence FROM credential_usage_collectors WHERE node_id=@node AND collector_id=@collector",c);count.Parameters.AddWithValue("node",node.Id);count.Parameters.AddWithValue("collector",collector);Assert.Equal(17L,(long)(await count.ExecuteScalarAsync())!);
    }
    [Fact]
    public async Task Invalid_hour_or_zero_count_cannot_be_stored()
    {
        await using var api=new ApiFixture();await api.InitializeAsync();await RequireTablesAsync(api.Database);await using var c=await api.Database.OpenAsync();
        foreach(var (hour,count) in new[]{("2026-10-15T12:30:00Z",1L),("2026-10-15T12:00:00Z",0L)})
        {
            await using var cmd=new NpgsqlCommand("INSERT INTO credential_usage_hourly(environment_id,credential_id,hour_start,application_id,count,last_used_at) VALUES (@e,@c,@h,@a,@count,@h)",c);cmd.Parameters.AddWithValue("e",Guid.NewGuid());cmd.Parameters.AddWithValue("c",Guid.NewGuid());cmd.Parameters.AddWithValue("h",DateTimeOffset.Parse(hour));cmd.Parameters.AddWithValue("a",Guid.NewGuid());cmd.Parameters.AddWithValue("count",count);
            Assert.Equal(PostgresErrorCodes.CheckViolation,(await Assert.ThrowsAsync<PostgresException>(()=>cmd.ExecuteNonQueryAsync())).SqlState);
        }
    }
    [Fact]
    public async Task Node_history_records_real_disable_transitions_only()
    {
        await using var api=new ApiFixture();await api.InitializeAsync();await api.SeedScopeAsync(platformAdmin:true);
        var node=new GatewayNode{EnvironmentId=api.Environment.Id,NodeName="usage-history",InstanceId=Guid.NewGuid().ToString(),Enabled=true};
        await using(var db=api.Context())
        {
            var role=await db.Set<UserRole>().Where(x=>x.UserId==api.User.Id).Select(x=>x.RoleId).SingleAsync();var permission=new Permission{Code="gateway.operate",Module="gateway",Name="网关操作"};db.Add(permission);db.Add(new RolePermission{RoleId=role,PermissionId=permission.Id});db.Add(node);await db.SaveChangesAsync();
        }
        using var services=api.Services();var reader=services.ServiceProvider.GetRequiredService<GatewayReadService>();var actor=new ActorContext(api.User.Id,"usage-node-history");
        var tagMethod=typeof(GatewayReadService).GetMethod("Tag",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic);Assert.NotNull(tagMethod);
        var tag=(string)tagMethod.Invoke(null,[node])!;await reader.SetEnabledAsync(node.Id,false,tag,actor);
        await using var after=api.Context();var events=await after.Set<GatewayNodeEvent>().Where(x=>x.GatewayNodeId==node.Id&&x.EventType=="usage.node.disabled").ToArrayAsync();Assert.Single(events);
        var disabled=await after.Set<GatewayNode>().SingleAsync(x=>x.Id==node.Id);var nextTag=(string)tagMethod.Invoke(null,[disabled])!;await reader.SetEnabledAsync(node.Id,false,nextTag,actor);
        Assert.Equal(1,await after.Set<GatewayNodeEvent>().CountAsync(x=>x.GatewayNodeId==node.Id&&x.EventType=="usage.node.disabled"));
    }
}
