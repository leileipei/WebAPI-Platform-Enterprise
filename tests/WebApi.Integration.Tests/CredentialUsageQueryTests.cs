using WebApi.PerformanceHost;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.CredentialUsage;
using WebApi.Contracts.Security;
using System.Globalization;
using System.Data;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Contracts.Applications;
using WebApi.Contracts.CredentialUsage;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;
namespace WebApi.Integration.Tests;

public sealed class CredentialUsageQueryTests
{
    private sealed class Fixture:IAsyncDisposable
    {
        public readonly ApiFixture Api=new();public readonly DateTimeOffset To=Floor(DateTimeOffset.UtcNow);public DateTimeOffset From=>To.AddHours(-2);
        public readonly RequestSqlProbe Probe=new();public ApplicationCredential Credential=null!;public GatewayNode Node=null!;
        public async Task InitializeAsync(bool cohort=true,bool complete=true,int credentials=1,int retention=30,int batchHashHours=25)
        {
            await Api.InitializeAsync(b=>{b.Configuration["CredentialUsage:RetentionDays"]=retention.ToString(CultureInfo.InvariantCulture);b.Configuration["CredentialUsage:BatchHashHours"]=batchHashHours.ToString(CultureInfo.InvariantCulture);b.Services.AddDbContext<WebApiDbContext>(o=>o.AddInterceptors(Probe));});await Api.SeedConsumerAsync();using var login=await Api.LoginAsync();login.EnsureSuccessStatusCode();
            await using var db=Api.Context();
            for(var i=0;i<credentials;i++){var credential=new ApplicationCredential{ApplicationId=Api.Application.Id,AccessKey="query-key-"+i.ToString("D4"),SecretHash="never-returned",SecretLast4="1234",ValidFrom=From.AddDays(-50),ExpiresAt=To.AddDays(1),LastUsedAt=To.AddMinutes(10)};if(i==0)Credential=credential;db.Add(credential);}
            if(cohort)
            {
                Node=new(){EnvironmentId=Api.Environment.Id,NodeName="historical-query",InstanceId=Guid.NewGuid().ToString(),Enabled=true};db.Add(Node);db.Add(new GatewayNodeEvent{GatewayNodeId=Node.Id,EventType="registered",CreatedAt=From.AddHours(-1)});
                var collector=Guid.NewGuid();db.Add(new CredentialUsageCollector{NodeId=Node.Id,CollectorId=collector,EnvironmentId=Api.Environment.Id,CreatedAt=From.AddHours(-1),AcceptedSequence=2,AcceptedThrough=To});
                db.Add(new CredentialUsageCollectorInstance{NodeId=Node.Id,CollectorId=collector,InstanceId=Guid.Parse(Node.InstanceId),RegisteredAt=From.AddHours(-1)});
                if(complete)for(var h=From;h<To;h=h.AddHours(1))db.Add(Coverage(h,h,h.AddHours(1)));
            }
            await db.SaveChangesAsync();
        }
        public CredentialUsageCoverage Coverage(DateTimeOffset hour,DateTimeOffset from,DateTimeOffset to)=>new(){EnvironmentId=Api.Environment.Id,NodeId=Node.Id,HourStart=hour,Intervals=JsonSerializer.Serialize(new[]{new UsageInterval(from,to)},UsageProtocolJson.Options),DurableThrough=to,AcceptedThrough=to};
        public async Task BucketAsync(long count,DateTimeOffset? hour=null,Guid? environment=null)
        {await using var db=Api.Context();var h=hour??From;db.Add(new CredentialUsageHourly{ApplicationId=Api.Application.Id,CredentialId=Credential.Id,EnvironmentId=environment??Api.Environment.Id,HourStart=h,Count=count,LastUsedAt=h.AddMinutes(10)});await db.SaveChangesAsync();}
        public string Path(DateTimeOffset? from=null,DateTimeOffset? to=null,Guid? environment=null,int page=1,int size=50)=>$"/api/v1/applications/{Api.Application.Id}/credential-usage?from={Uri.EscapeDataString((from??From).ToString("O",CultureInfo.InvariantCulture))}&to={Uri.EscapeDataString((to??To).ToString("O",CultureInfo.InvariantCulture))}&page={page}&pageSize={size}"+(environment is Guid e?$"&environmentId={e}":"");
        public async Task<JsonElement> ReadAsync(string? path=null){using var response=await Api.Client.GetAsync(path??Path());response.EnsureSuccessStatusCode();return await response.Content.ReadFromJsonAsync<JsonElement>();}
        public ValueTask DisposeAsync()=>Api.DisposeAsync();
    }
    private static DateTimeOffset Floor(DateTimeOffset time)=>new(time.UtcDateTime.Year,time.UtcDateTime.Month,time.UtcDateTime.Day,time.UtcDateTime.Hour,0,0,TimeSpan.Zero);
    private static JsonElement Row(JsonElement page)=>page.GetProperty("page").GetProperty("items")[0];
    [Fact]
    public async Task Source_proof_also_survives_replay_horizon_when_longer_than_statistics_retention()
    {
        await using var f=new Fixture();await f.InitializeAsync(retention:1,batchHashHours:49);
        await using(var db=f.Api.Context())
        {
            var old=await db.Set<CredentialUsageCollectorInstance>().SingleAsync();old.RegisteredAt=f.To.AddHours(-50);old.EndedAt=f.To.AddHours(-26);
            var node=await db.Set<GatewayNode>().SingleAsync();node.InstanceId=Guid.NewGuid().ToString();
            db.Add(new CredentialUsageCollectorInstance{NodeId=node.Id,CollectorId=old.CollectorId,InstanceId=Guid.Parse(node.InstanceId),RegisteredAt=f.To});await db.SaveChangesAsync();
        }
        using var services=f.Api.Services();var cleanup=services.ServiceProvider.GetRequiredService<CredentialUsageCleanupService>();await cleanup.CleanupAsync(f.To);
        await using(var retained=f.Api.Context())Assert.Equal(2,await retained.Set<CredentialUsageCollectorInstance>().CountAsync());
        await cleanup.CleanupAsync(f.To.AddHours(24));
        await using var after=f.Api.Context();Assert.Single(await after.Set<CredentialUsageCollectorInstance>().ToArrayAsync());Assert.Equal(2L,(await after.Set<CredentialUsageCollector>().SingleAsync()).AcceptedSequence);
    }
    [Theory][InlineData(IsolationLevel.RepeatableRead)][InlineData(IsolationLevel.Serializable)]
    public async Task Query_reuses_caller_snapshot_without_committing_its_writes(IsolationLevel isolation)
    {
        await using var f=new Fixture();await f.InitializeAsync();using var services=f.Api.Services();var db=services.ServiceProvider.GetRequiredService<WebApiDbContext>();
        await using var transaction=await db.Database.BeginTransactionAsync(isolation);
        db.Add(new CredentialUsageHourly{ApplicationId=f.Api.Application.Id,CredentialId=f.Credential.Id,EnvironmentId=f.Api.Environment.Id,HourStart=f.From,Count=6,LastUsedAt=f.From.AddMinutes(10)});await db.SaveChangesAsync();
        var page=await services.ServiceProvider.GetRequiredService<CredentialUsageQuery>().ReadAsync(f.Api.Application.Id,null,new(f.From,f.To),1,50,new ActorContext(f.Api.User.Id,"caller-snapshot"));
        Assert.Equal("6",page.Page.Items[0].Count);Assert.Equal("Available",page.Page.Items[0].Coverage.State);Assert.Same(transaction,db.Database.CurrentTransaction);
        await transaction.RollbackAsync();await using var after=f.Api.Context();Assert.Empty(await after.Set<CredentialUsageHourly>().ToArrayAsync());
    }
    [Fact]
    public async Task Query_refuses_weak_caller_isolation_without_ending_the_caller_transaction()
    {
        await using var f=new Fixture();await f.InitializeAsync();using var services=f.Api.Services();var db=services.ServiceProvider.GetRequiredService<WebApiDbContext>();
        await using var transaction=await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>services.ServiceProvider.GetRequiredService<CredentialUsageQuery>().ReadAsync(f.Api.Application.Id,null,new(f.From,f.To),1,50,new ActorContext(f.Api.User.Id,"weak-snapshot")));
        Assert.Same(transaction,db.Database.CurrentTransaction);await transaction.RollbackAsync();
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task Pre_window_retirement_anchor_is_preserved_with_optional_reenable_inside_window(bool reenable)
    {
        await using var f=new Fixture();await f.InitializeAsync();
        await using(var db=f.Api.Context())
        {
            await db.Set<GatewayNodeEvent>().Where(x=>x.GatewayNodeId==f.Node.Id&&x.EventType=="registered").ExecuteUpdateAsync(s=>s.SetProperty(x=>x.CreatedAt,f.From.AddDays(-10)));
            db.Add(new GatewayNodeEvent{GatewayNodeId=f.Node.Id,EventType="usage.node.disabled",CreatedAt=f.From.AddDays(-2)});
            if(reenable)db.Add(new GatewayNodeEvent{GatewayNodeId=f.Node.Id,EventType="usage.node.enabled",CreatedAt=f.From.AddHours(1)});
            await db.Set<CredentialUsageCoverage>().Where(x=>!reenable||x.HourStart==f.From).ExecuteDeleteAsync();await db.SaveChangesAsync();
        }
        var row=Row(await f.ReadAsync());Assert.Equal("0",row.GetProperty("count").GetString());Assert.Equal("NoData",row.GetProperty("coverage").GetProperty("state").GetString());
    }
    [Fact]
    public async Task Retained_complete_history_survives_restart_and_receipt_cleanup()
    {
        await using var f=new Fixture();await f.InitializeAsync();await f.BucketAsync(3);
        await using(var db=f.Api.Context())
        {
            var old=await db.Set<CredentialUsageCollectorInstance>().SingleAsync();old.EndedAt=f.To;
            var node=await db.Set<GatewayNode>().SingleAsync();node.InstanceId=Guid.NewGuid().ToString();
            db.Add(new CredentialUsageCollectorInstance{NodeId=node.Id,CollectorId=old.CollectorId,InstanceId=Guid.Parse(node.InstanceId),RegisteredAt=f.To});
            await db.SaveChangesAsync();
        }
        Assert.Equal("Available",Row(await f.ReadAsync()).GetProperty("coverage").GetProperty("state").GetString());
        using var services=f.Api.Services();var cleanup=services.ServiceProvider.GetRequiredService<CredentialUsageCleanupService>();
        // Advance only the cleanup clock, beyond receipt retention but inside the 30-day statistics horizon.
        await cleanup.CleanupAsync(f.To.AddHours(26));
        var retained=Row(await f.ReadAsync());Assert.Equal("3",retained.GetProperty("count").GetString());Assert.Equal("Available",retained.GetProperty("coverage").GetProperty("state").GetString());
        await cleanup.CleanupAsync(f.To.AddDays(31));
        await using var after=f.Api.Context();Assert.Single(await after.Set<CredentialUsageCollectorInstance>().ToArrayAsync());
        Assert.Equal(Guid.Parse((await after.Set<GatewayNode>().SingleAsync()).InstanceId),(await after.Set<CredentialUsageCollectorInstance>().SingleAsync()).InstanceId);
    }
    [Fact]
    public async Task Coverage_reads_only_window_events_anchors_and_overlapping_instances()
    {
        await using var f=new Fixture();await f.InitializeAsync();await f.BucketAsync(4);
        async Task<(CredentialUsagePage Page,long Rows)> Measure()
        {
            var id=Guid.NewGuid().ToString("N");using var probe=f.Probe.Begin(id);using var services=f.Api.Services();
            var page=await services.ServiceProvider.GetRequiredService<CredentialUsageQuery>().ReadAsync(f.Api.Application.Id,null,new(f.From,f.To),1,50,new ActorContext(f.Api.User.Id,"bounded-history"));
            return (page,f.Probe.Read(id).Rows);
        }
        var baseline=await Measure();
        await using(var db=f.Api.Context())
        {
            var collector=await db.Set<CredentialUsageCollector>().SingleAsync();
            for(var i=0;i<600;i++)
            {
                var at=f.From.AddDays(-100).AddHours(i);
                db.Add(new GatewayNodeEvent{GatewayNodeId=f.Node.Id,EventType=i%3==0?"registered":i%3==1?"usage.node.disabled":"usage.node.enabled",CreatedAt=at});
                db.Add(new CredentialUsageCollectorInstance{NodeId=f.Node.Id,CollectorId=collector.CollectorId,InstanceId=Guid.NewGuid(),RegisteredAt=at,EndedAt=at.AddMinutes(30)});
            }
            await db.SaveChangesAsync();
        }
        var measured=await Measure();Assert.Equal("4",measured.Page.Page.Items[0].Count);Assert.Equal("Available",measured.Page.Coverage.State);
        Assert.InRange(measured.Rows,1,baseline.Rows+2);
    }
    [Fact]
    public async Task Idle_complete_is_zero_unknown_is_null_partial_is_lower_bound()
    {
        await using var complete=new Fixture();await complete.InitializeAsync();var idle=Row(await complete.ReadAsync());Assert.Equal("0",idle.GetProperty("count").GetString());Assert.Equal("NoData",idle.GetProperty("coverage").GetProperty("state").GetString());
        await using var unknown=new Fixture();await unknown.InitializeAsync(cohort:false);var missing=Row(await unknown.ReadAsync());Assert.Equal(JsonValueKind.Null,missing.GetProperty("count").ValueKind);Assert.Equal("Unknown",missing.GetProperty("coverage").GetProperty("state").GetString());
        await using var partial=new Fixture();await partial.InitializeAsync(complete:false);await partial.BucketAsync(7);var lower=Row(await partial.ReadAsync());Assert.Equal("7",lower.GetProperty("count").GetString());Assert.Equal("Partial",lower.GetProperty("coverage").GetProperty("state").GetString());Assert.Single(lower.GetProperty("coverage").GetProperty("missingNodes").EnumerateArray());
    }
    [Theory][InlineData(9007199254740993L,"9007199254740993")][InlineData(long.MaxValue,"18446744073709551614")]
    public async Task Numeric_sum_preserves_more_than_js_and_int64(long count,string expected)
    {
        await using var f=new Fixture();await f.InitializeAsync();await f.BucketAsync(count);if(count==long.MaxValue)await f.BucketAsync(count,f.From.AddHours(1));var row=Row(await f.ReadAsync());Assert.Equal(expected,row.GetProperty("count").GetString());Assert.Equal("Available",row.GetProperty("coverage").GetProperty("state").GetString());
    }
    [Fact]
    public async Task Unseen_environment_last_used_is_never_leaked_in_any_existing_response()
    {
        await using var f=new Fixture();await f.InitializeAsync();await f.BucketAsync(2);
        var hidden=new EnvironmentRecord{ProjectId=f.Api.Project.Id,Code="HIDDEN",Name="停用环境",Status="Disabled"};await using(var db=f.Api.Context()){db.Add(hidden);await db.SaveChangesAsync();}await f.BucketAsync(99,f.To,hidden.Id);
        var row=Row(await f.ReadAsync());Assert.Equal("2",row.GetProperty("count").GetString());Assert.Equal(f.From.AddMinutes(10),row.GetProperty("visibleLastUsedAt").GetDateTimeOffset());
        foreach(var path in new[]{$"/api/v1/applications/{f.Api.Application.Id}",$"/api/v1/applications/{f.Api.Application.Id}/credentials",$"/api/v1/credentials/{f.Credential.Id}"})
        {using var response=await f.Api.Client.GetAsync(path);response.EnsureSuccessStatusCode();var json=await response.Content.ReadFromJsonAsync<JsonElement>();var credential=json.ValueKind==JsonValueKind.Array?json[0]:json.TryGetProperty("credentials",out var items)?items[0]:json;Assert.Equal(f.From.AddMinutes(10),credential.GetProperty("lastUsedAt").GetDateTimeOffset());}
        using var update=await f.Api.WriteAsync(HttpMethod.Put,$"/api/v1/credentials/{f.Credential.Id}",new CredentialUpdateRequest(f.Credential.ValidFrom,f.Credential.ExpiresAt,"Active"),"\"1\"");update.EnsureSuccessStatusCode();Assert.Equal(f.From.AddMinutes(10),(await update.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("lastUsedAt").GetDateTimeOffset());
        foreach(var id in new[]{hidden.Id,Guid.NewGuid()}){using var forbidden=await f.Api.Client.GetAsync(f.Path(environment:id));Assert.Equal(HttpStatusCode.Forbidden,forbidden.StatusCode);}
    }
    [Theory][InlineData("half-hour")][InlineData("reverse")][InlineData("31days")][InlineData("page0")][InlineData("size101")][InlineData("future")]
    public async Task Invalid_windows_and_paging_are_rejected(string sample)
    {
        await using var f=new Fixture();await f.InitializeAsync();var path=sample switch{"half-hour"=>f.Path(from:f.From.AddMinutes(30)),"reverse"=>f.Path(from:f.To),"31days"=>f.Path(from:f.To.AddDays(-31)),"page0"=>f.Path(page:0),"size101"=>f.Path(size:101),_=>f.Path(to:f.To.AddHours(2))};using var response=await f.Api.Client.GetAsync(path);Assert.Equal(HttpStatusCode.UnprocessableEntity,response.StatusCode);
    }
    [Fact]
    public async Task Equivalent_offset_windows_are_normalized_and_current_hour_is_partial()
    {
        await using var f=new Fixture();await f.InitializeAsync();var normal=await f.ReadAsync();var offset=await f.ReadAsync(f.Path(f.From.ToOffset(TimeSpan.FromHours(8)),f.To.ToOffset(TimeSpan.FromHours(-4))));Assert.Equal(normal.GetProperty("window").GetRawText(),offset.GetProperty("window").GetRawText());
        var ongoing=await f.ReadAsync(f.Path(to:f.To.AddHours(1)));Assert.Equal("Partial",ongoing.GetProperty("coverage").GetProperty("state").GetString());Assert.Equal(JsonValueKind.Null,ongoing.GetProperty("coverage").GetProperty("completeThrough").ValueKind);
    }
    [Fact]
    public async Task Historical_retired_node_is_required_until_its_disable_event()
    {
        await using var f=new Fixture();await f.InitializeAsync();await using(var db=f.Api.Context()){var node=await db.Set<GatewayNode>().SingleAsync();node.Enabled=false;db.Add(new GatewayNodeEvent{GatewayNodeId=node.Id,EventType="usage.node.disabled",CreatedAt=f.From.AddHours(1)});await db.Set<CredentialUsageCoverage>().Where(x=>x.HourStart==f.From).ExecuteDeleteAsync();await db.SaveChangesAsync();}
        var result=await f.ReadAsync();Assert.Equal("Partial",result.GetProperty("coverage").GetProperty("state").GetString());Assert.Contains(result.GetProperty("coverage").GetProperty("missingNodes").EnumerateArray(),x=>x.GetGuid()==f.Node.Id);
    }
    [Fact]
    public async Task Gap_wins_over_full_interval_and_never_becomes_fake_zero()
    {
        await using var f=new Fixture();await f.InitializeAsync();await using(var db=f.Api.Context()){var row=await db.Set<CredentialUsageCoverage>().FirstAsync();row.Gaps=JsonSerializer.Serialize(new[]{new UsageGap(f.From.AddMinutes(1),f.From.AddMinutes(2),"CrashTail")},UsageProtocolJson.Options);await db.SaveChangesAsync();}
        var result=await f.ReadAsync();Assert.Equal("Partial",result.GetProperty("coverage").GetProperty("state").GetString());Assert.NotEmpty(result.GetProperty("coverage").GetProperty("gaps").EnumerateArray());
    }
    [Fact]
    public async Task Paging_is_stable_and_missing_global_provenance_remains_null()
    {
        await using var f=new Fixture();await f.InitializeAsync(credentials:105);var first=await f.ReadAsync(f.Path(size:100));var second=await f.ReadAsync(f.Path(page:2,size:100));Assert.Equal(105,first.GetProperty("page").GetProperty("total").GetInt32());Assert.Equal(100,first.GetProperty("page").GetProperty("items").GetArrayLength());Assert.Equal(5,second.GetProperty("page").GetProperty("items").GetArrayLength());Assert.All(first.GetProperty("page").GetProperty("items").EnumerateArray(),x=>Assert.Equal(JsonValueKind.Null,x.GetProperty("visibleLastUsedAt").ValueKind));
    }
    [Theory][InlineData(50)][InlineData(100)]
    public async Task Page_query_count_and_fact_rows_are_bounded_without_per_credential_queries(int size)
    {
        await using var f=new Fixture();await f.InitializeAsync(credentials:205);await using(var db=f.Api.Context())
        {var credentials=await db.Set<ApplicationCredential>().ToArrayAsync();foreach(var key in credentials)db.Add(new CredentialUsageHourly{ApplicationId=f.Api.Application.Id,CredentialId=key.Id,EnvironmentId=f.Api.Environment.Id,HourStart=f.From,Count=1,LastUsedAt=f.From.AddMinutes(10)});await db.SaveChangesAsync();}
        var id=Guid.NewGuid().ToString("N");using var probe=f.Probe.Begin(id);using var services=f.Api.Services();
        var result=await services.ServiceProvider.GetRequiredService<CredentialUsageQuery>().ReadAsync(f.Api.Application.Id,null,new(f.From,f.To),1,size,new ActorContext(f.Api.User.Id,"usage-sql"),default);var stats=f.Probe.Read(id);
        Assert.Equal(size,result.Page.Items.Count);Assert.InRange(stats.Total,1,22);Assert.InRange(stats.Rows,1,size*3+30);
    }
    [Fact]
    public async Task Actual_shorter_retention_rejects_older_window_and_never_fills_global_last_used()
    {
        await using var f=new Fixture();await f.InitializeAsync(retention:1);await f.BucketAsync(5,f.To.AddDays(-2));using var expired=await f.Api.Client.GetAsync(f.Path(from:f.To.AddDays(-2)));Assert.Equal(HttpStatusCode.UnprocessableEntity,expired.StatusCode);
        var row=Row(await f.ReadAsync());Assert.Equal(JsonValueKind.Null,row.GetProperty("visibleLastUsedAt").ValueKind);
    }
    [Fact]
    public async Task Visible_last_use_outside_selected_window_is_still_proven_by_retained_facts()
    {
        await using var f=new Fixture();await f.InitializeAsync();await f.BucketAsync(5,f.From.AddHours(-1));var row=Row(await f.ReadAsync());Assert.Equal("0",row.GetProperty("count").GetString());Assert.Equal(JsonValueKind.Null,row.GetProperty("lastObservedAt").ValueKind);Assert.Equal(f.From.AddMinutes(-50),row.GetProperty("visibleLastUsedAt").GetDateTimeOffset());
    }
    [Fact]
    public async Task Disabled_node_does_not_require_coverage_after_historical_retirement()
    {
        await using var f=new Fixture();await f.InitializeAsync();await using(var db=f.Api.Context()){var node=await db.Set<GatewayNode>().SingleAsync();node.Enabled=false;db.Add(new GatewayNodeEvent{GatewayNodeId=node.Id,EventType="usage.node.disabled",CreatedAt=f.From.AddHours(1)});await db.Set<CredentialUsageCoverage>().Where(x=>x.HourStart==f.From.AddHours(1)).ExecuteDeleteAsync();await db.SaveChangesAsync();}
        var row=Row(await f.ReadAsync());Assert.Equal("NoData",row.GetProperty("coverage").GetProperty("state").GetString());Assert.Equal("0",row.GetProperty("count").GetString());
    }
    [Fact]
    public async Task Old_protocol_or_ended_instances_cannot_prove_idle_coverage()
    {
        await using var f=new Fixture();await f.InitializeAsync();await using(var db=f.Api.Context()){await db.Set<CredentialUsageCollectorInstance>().ExecuteDeleteAsync();}
        var row=Row(await f.ReadAsync());Assert.Equal("Partial",row.GetProperty("coverage").GetProperty("state").GetString());Assert.NotEmpty(row.GetProperty("coverage").GetProperty("missingNodes").EnumerateArray());
    }

    [Fact]
    public async Task Credential_manager_without_app_read_keeps_safe_write_but_receives_no_last_used()
    {
        await using var f=new Fixture();await f.InitializeAsync();await f.BucketAsync(2);await using(var db=f.Api.Context()){var permission=await db.Set<Permission>().Where(x=>x.Code=="app.read").Select(x=>x.Id).SingleAsync();await db.Set<RolePermission>().Where(x=>x.PermissionId==permission).ExecuteDeleteAsync();}
        using var response=await f.Api.WriteAsync(HttpMethod.Put,$"/api/v1/credentials/{f.Credential.Id}",new CredentialUpdateRequest(f.Credential.ValidFrom,f.Credential.ExpiresAt.AddHours(1),"Active"),"\"1\"");response.EnsureSuccessStatusCode();Assert.Equal(JsonValueKind.Null,(await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("lastUsedAt").ValueKind);
        using var denied=await f.Api.Client.GetAsync(f.Path());Assert.Equal(HttpStatusCode.Forbidden,denied.StatusCode);
    }

    [Fact]
    public async Task Collection_started_inside_window_returns_known_lower_bound_and_explicit_initial_gap()
    {
        await using var f=new Fixture();await f.InitializeAsync();await f.BucketAsync(2,f.From.AddHours(1));var start=f.From.AddMinutes(30);
        await using(var db=f.Api.Context()){await db.Set<GatewayNodeEvent>().Where(x=>x.EventType=="registered").ExecuteUpdateAsync(s=>s.SetProperty(x=>x.CreatedAt,start));await db.Set<CredentialUsageCollectorInstance>().ExecuteUpdateAsync(s=>s.SetProperty(x=>x.RegisteredAt,start));}
        var row=Row(await f.ReadAsync());Assert.Equal("Partial",row.GetProperty("coverage").GetProperty("state").GetString());Assert.Equal("2",row.GetProperty("count").GetString());Assert.Contains(row.GetProperty("coverage").GetProperty("gaps").EnumerateArray(),x=>x.GetProperty("reason").GetString()=="CoverageStartedLate");
    }

}
