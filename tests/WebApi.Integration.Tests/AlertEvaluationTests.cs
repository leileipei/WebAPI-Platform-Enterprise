using Microsoft.Extensions.Logging;
using WebApi.Worker;
using StackExchange.Redis;
using System.Globalization;
using System.Net.Http.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using WebApi.Contracts.Alerts;
using WebApi.Contracts.Catalog;
using WebApi.Contracts.Gateway;
using WebApi.Contracts.Observability;
using WebApi.Infrastructure.Alerts;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;
namespace WebApi.Integration.Tests;
public sealed class AlertEvaluationTests
{
    private static void ConfigureMetrics(IConfiguration configuration, IServiceCollection services,
        AlertClockedMetricHandler handler, int? queryDelaySeconds)
    {
        configuration["Observability:Enabled"] = "true";
        configuration["Alerts:IntervalSeconds"] = "1";
        configuration["Alerts:QueryDelaySeconds"] = queryDelaySeconds?.ToString(CultureInfo.InvariantCulture);
        services.PostConfigure<HttpClientFactoryOptions>("observability",
            options => options.HttpMessageHandlerBuilderActions.Add(client => client.PrimaryHandler = handler));
    }
    private static void ConfigureEvaluator(Microsoft.AspNetCore.Builder.WebApplicationBuilder builder, AlertClockedMetricHandler handler, int? queryDelaySeconds=0)
    {
        ConfigureMetrics(builder.Configuration, builder.Services, handler, queryDelaySeconds);
        builder.Services.AddScoped<AlertEvaluationLeaseStore>();
        builder.Services.AddScoped<AlertEvaluationService>();
    }
    private static async Task<(ApiFixture Fixture,AlertClockedMetricHandler Handler,Guid Rule)> Setup(string target="Environment",int duration=0,int? queryDelaySeconds=0)
    {
        var handler=new AlertClockedMetricHandler();var fixture=new ApiFixture();
        await fixture.InitializeAsync(builder=>ConfigureEvaluator(builder,handler,queryDelaySeconds));
        await fixture.SeedCatalogAsync();await ObservationTestSupport.GrantAsync(fixture,["alert.rule.manage"]);
        await using(var db=fixture.Context()){fixture.Application.OrganizationId=fixture.Organization.Id;fixture.Application.ProjectId=fixture.Project.Id;db.Add(fixture.Application);db.AddRange(new GatewayNode{EnvironmentId=fixture.Environment.Id,NodeName="node-0"},new GatewayNode{EnvironmentId=fixture.Environment.Id,NodeName="node-1"});await db.SaveChangesAsync();}
        handler.Inner.EnvironmentId=fixture.Environment.Id;handler.Inner.ApiId=fixture.Api.Id;handler.Inner.ApplicationId=fixture.Application.Id;handler.Inner.DestinationId=fixture.Destination.Id;handler.Inner.ClusterId=fixture.Cluster.Id;
        using var login=await fixture.LoginAsync();login.EnsureSuccessStatusCode();var request=new SaveAlertRuleRequest(fixture.Organization.Id,fixture.Project.Id,fixture.Environment.Id,"Rps","request_rps","request_rps > 1","Warning",true,duration,target,target=="Destination"?fixture.Destination.Id:null,60,new(true,[]));
        using var saved=await fixture.WriteAsync(HttpMethod.Post,"/api/v1/observability/alert-rules",request);saved.EnsureSuccessStatusCode();return(fixture,handler,(await saved.Content.ReadFromJsonAsync<AlertRuleDto>())!.Id);
    }
    private static async Task<DateTimeOffset> Slot(ApiFixture f,CancellationToken ct=default){using var scope=f.Services();return await scope.ServiceProvider.GetRequiredService<AlertEvaluationLeaseStore>().CurrentSlotAsync(ct);}
    private static async Task<IReadOnlyList<EvaluationLease>> Claim(ApiFixture f,DateTimeOffset slot,string owner,CancellationToken ct=default){using var scope=f.Services();return await scope.ServiceProvider.GetRequiredService<AlertEvaluationLeaseStore>().ClaimAsync(slot,owner,ct);}
    private static async Task<bool> Evaluate(ApiFixture f,EvaluationLease lease){using var scope=f.Services();return await scope.ServiceProvider.GetRequiredService<AlertEvaluationService>().EvaluateAsync(lease,default);}
    private static async Task<EvaluationLease> Next(ApiFixture f,DateTimeOffset? initialSlot=null){await Task.Delay(1100);var leases=await ClaimCurrent(f,"next",initialSlot);return Assert.Single(leases);}
    [Fact] public async Task NextLeaseHelperReacquiresAnExpiredSlot()
    {
        var setup=await Setup();await using var f=setup.Fixture;var stale=(await Slot(f)).AddSeconds(-2);Assert.NotNull(await Next(f,stale));
    }
    private static async Task<IReadOnlyList<EvaluationLease>[]> ClaimSameSlot(ApiFixture f,DateTimeOffset initialSlot)
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(3));var slot=initialSlot;
        while(true){var leases=await Task.WhenAll(Claim(f,slot,"worker-a",deadline.Token),Claim(f,slot,"worker-b",deadline.Token));if(leases.Any(x=>x.Count>0))return leases;await Task.Delay(10,deadline.Token);slot=await Slot(f,deadline.Token);}
    }
    private static async Task<IReadOnlyList<EvaluationLease>> ClaimCurrent(ApiFixture f,string owner,DateTimeOffset? initialSlot=null)
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(3));var slot=initialSlot??await Slot(f,deadline.Token);
        while(true){var leases=await Claim(f,slot,owner,deadline.Token);if(leases.Count>0)return leases;await Task.Delay(10,deadline.Token);slot=await Slot(f,deadline.Token);}
    }
    [Theory]
    [InlineData(5, 5, 200, true)]
    [InlineData(null, 30, 6, false)]
    public async Task RuleTestAndWorkerUseTheConfiguredQueryDelay(int? configuredDelay, int expectedDelay,
        int expectedSamples, bool expectedCondition)
    {
        var setup = await Setup(queryDelaySeconds: configuredDelay);
        await using var f = setup.Fixture;
        using var get = await f.Client.GetAsync($"/api/v1/observability/alert-rules/{setup.Rule}");
        get.EnsureSuccessStatusCode();
        var definition = (await get.Content.ReadFromJsonAsync<AlertRuleDto>())!.Definition;
        using var worker = WorkerApp.Build(["--environment", "Development"], builder =>
        {
            builder.Configuration["ConnectionStrings:WebApi"] = f.Database.ConnectionString;
            ConfigureMetrics(builder.Configuration, builder.Services, setup.Handler, configuredDelay);
            builder.Logging.ClearProviders();
        });

        var lease = Assert.Single(await ClaimCurrent(f, "query-delay"));
        setup.Handler.TrafficIncreaseAt = lease.Slot.AddSeconds(-15);
        using var test = await f.WriteAsync(HttpMethod.Post, "/api/v1/observability/alert-rules/test", definition);
        test.EnsureSuccessStatusCode();
        var result = (await test.Content.ReadFromJsonAsync<RuleTestDto>())!;
        var tested = Assert.Single(Assert.Single(result.Matches).Values);
        var previewEnd = setup.Handler.Inner.End;
        var afterPreview = await Slot(f);

        using var scope = worker.Services.CreateScope();
        var evaluator = scope.ServiceProvider.GetRequiredService<AlertEvaluationService>();
        Assert.True(await evaluator.EvaluateAsync(lease, default));
        var workerEnd = setup.Handler.Inner.End;
        await using var db = f.Context();
        var state = await db.Set<AlertEvaluationState>().SingleAsync();
        Assert.Equal("Known", state.EvaluationState);
        Assert.Equal(expectedCondition, state.LastCondition);
        Assert.Equal(expectedCondition, await db.Set<AlertEvent>().AnyAsync(e => e.Status == "Open"));
        Assert.Equal(lease.Slot.AddSeconds(-expectedDelay), workerEnd);
        Assert.Equal("Known", result.EvaluationState);
        Assert.Equal(expectedCondition, result.Condition);
        Assert.Equal(expectedSamples, tested.SampleCount);
        Assert.InRange(previewEnd, lease.Slot.AddSeconds(-expectedDelay), afterPreview.AddSeconds(-expectedDelay));
        Assert.Equal(TimeSpan.Zero, previewEnd - DateTimeOffset.FromUnixTimeSeconds(previewEnd.ToUnixTimeSeconds()));
    }
    [Fact] public async Task ConcurrentLeaseHelperReacquiresAnExpiredSlot()
    {
        var setup=await Setup();await using var f=setup.Fixture;var stale=(await Slot(f)).AddSeconds(-2);Assert.Single((await ClaimSameSlot(f,stale)).SelectMany(x=>x));
    }
    [Theory][InlineData("Ready")][InlineData("NotReady")][InlineData("Degraded")]
    public async Task RegisteredNodeStatusCanEvaluateActualRule(string status)
    {
        var setup=await Setup();await using var f=setup.Fixture;
        await using(var db=f.Context()){foreach(var node in await db.Set<GatewayNode>().ToArrayAsync())node.Status=status;await db.SaveChangesAsync();}
        Assert.True(await Evaluate(f,Assert.Single(await ClaimCurrent(f,"operational"))));
        await using var read=f.Context();Assert.Equal("Open",(await read.Set<AlertEvent>().SingleAsync()).Status);
    }
    [Fact]
    public async Task SharedApplicationTrafficTriggersEnvironmentAlertOnlyWhileInScope()
    {
        var setup = await Setup();
        await using var f = setup.Fixture;
        var shared = await SharedObservationFixture.AddApplicationAsync(f);
        setup.Handler.Inner.ApplicationId = shared.Id;
        Assert.True(await Evaluate(f, Assert.Single(await ClaimCurrent(f, "shared"))));
        await using (var db = f.Context())
        {
            Assert.Equal("Open", (await db.Set<AlertEvent>().SingleAsync()).Status);
            db.Remove(await db.Set<ApplicationApiPermission>().SingleAsync());
            await db.SaveChangesAsync();
        }
        Assert.True(await Evaluate(f, await Next(f)));
        await using var read = f.Context();
        Assert.Equal("Resolved", (await read.Set<AlertEvent>().SingleAsync()).Status);
    }

    [Fact]
    public async Task SharedApplicationOrganizationAlertCountsOnlyTheTwoGrantedEnvironments()
    {
        var setup = await Setup();
        await using var f = setup.Fixture;
        var shared = await SharedObservationFixture.AddApplicationAsync(f);
        var (_, api, allowed, denied) = await SharedObservationFixture.AddSecondProjectAsync(f, shared.Id);
        using var existing = await f.Client.GetAsync($"/api/v1/observability/alert-rules/{setup.Rule}");
        var rule = (await existing.Content.ReadFromJsonAsync<AlertRuleDto>())!;
        using var saved = await f.WriteAsync(HttpMethod.Put, $"/api/v1/observability/alert-rules/{setup.Rule}",
            rule.Definition with { ProjectId = null, EnvironmentId = null }, "\"1\"");
        saved.EnsureSuccessStatusCode();

        var source = setup.Handler.Inner;
        source.ApplicationId = shared.Id;
        source.NoDestination = true;
        var leases = await ClaimCurrent(f, "organization-rule");
        Assert.Equal(3, leases.Count);
        foreach (var lease in leases)
        {
            source.EnvironmentId = lease.EnvironmentId;
            source.ApiId = lease.EnvironmentId == f.Environment.Id ? f.Api.Id : api.Id;
            source.DefaultNodeName = lease.EnvironmentId == f.Environment.Id ? "node-0" :
                lease.EnvironmentId == allowed.Id ? "allowed-node" : "denied-node";
            Assert.True(await Evaluate(f, lease));
        }
        await using var read = f.Context();
        var events = await read.Set<AlertEvent>().Where(e => e.Status == "Open").Select(e => e.EnvironmentId).ToArrayAsync();
        Assert.Equal(new[] { f.Environment.Id, allowed.Id }.Order(), events.Order());
        var isolated = await read.Set<AlertEvaluationState>().SingleAsync(s => s.EnvironmentId == denied.Id);
        Assert.Equal("Known", isolated.EvaluationState);
        Assert.False(isolated.LastCondition);
    }

    [Fact] public async Task TwoWorkersSameSlotCreateOneEvent()
    {
        var setup=await Setup();await using var f=setup.Fixture;var slot=await Slot(f);var claimed=await ClaimSameSlot(f,slot);var lease=Assert.Single(claimed.SelectMany(x=>x));
        var commits=await Task.WhenAll(Evaluate(f,lease),Evaluate(f,lease));Assert.Single(commits,x=>x);await using var db=f.Context();var e=Assert.Single(await db.Set<AlertEvent>().ToArrayAsync());Assert.Equal("Open",e.Status);Assert.Equal(1,await db.Set<AlertEventTransition>().CountAsync());var audit=await db.Set<AuditLog>().SingleAsync(x=>x.Action=="alert.triggered");Assert.Null(audit.UserId);Assert.Equal(e.Id.ToString(),audit.ResourceId);Assert.Equal(f.Environment.Id,audit.EnvironmentId);Assert.Contains("worker",audit.AfterJson!);
    }
    [Fact] public async Task LeaseExpiredResultCannotCommitAfterDisable()
    {
        var setup=await Setup();await using var f=setup.Fixture;var lease=Assert.Single(await ClaimCurrent(f,"expired"));setup.Handler.PauseFirst=true;var running=Evaluate(f,lease);await setup.Handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            await using(var db=f.Context())await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE alert_evaluation_states SET lease_until=clock_timestamp()-interval '1 second' WHERE rule_id={setup.Rule}");
            using var disable=await f.WriteAsync(HttpMethod.Post,$"/api/v1/observability/alert-rules/{setup.Rule}/disable",null,"\"1\"").WaitAsync(TimeSpan.FromSeconds(3));disable.EnsureSuccessStatusCode();
        }
        finally{setup.Handler.Resume.TrySetResult(true);}
        Assert.False(await running);await using var read=f.Context();Assert.Equal(0,await read.Set<AlertEvent>().CountAsync());
    }
    [Fact] public async Task ReclaimedTokenRejectsOldResultAndOnlyNewWorkerCommits()
    {
        var setup=await Setup();await using var f=setup.Fixture;var slot=await Slot(f);var old=Assert.Single(await ClaimCurrent(f,"old",slot));await using(var db=f.Context())await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE alert_evaluation_states SET lease_until=clock_timestamp()-interval '1 second' WHERE rule_id={setup.Rule}");
        var replacement=Assert.Single(await ClaimCurrent(f,"new"));Assert.True(replacement.Token>old.Token);Assert.False(await Evaluate(f,old));Assert.True(await Evaluate(f,replacement));await using var read=f.Context();Assert.Equal(1,await read.Set<AlertEvent>().CountAsync());
    }
    [Fact] public async Task RestartAndSourceFailureDoNotDuplicateOrRecoverFiringEvent()
    {
        var setup=await Setup();await using var f=setup.Fixture;Assert.True(await Evaluate(f,Assert.Single(await ClaimCurrent(f,"before-restart"))));setup.Handler.Inner.Status=503;Assert.True(await Evaluate(f,await Next(f)));
        await using(var db=f.Context()){Assert.Equal("Open",(await db.Set<AlertEvent>().SingleAsync()).Status);Assert.Equal("Unknown",(await db.Set<AlertEvaluationState>().SingleAsync()).EvaluationState);}
        setup.Handler.Inner.Status=200;Assert.True(await Evaluate(f,await Next(f)));await using var read=f.Context();Assert.Equal(1,await read.Set<AlertEvent>().CountAsync());Assert.Equal(1,await read.Set<AuditLog>().CountAsync(x=>x.Action=="alert.triggered"));
    }
    [Fact] public async Task UnknownResetsPendingAndCpuClockRollbackCannotClaimPastSlot()
    {
        var setup=await Setup(duration:300);await using var f=setup.Fixture;var slot=await Slot(f);Assert.True(await Evaluate(f,Assert.Single(await ClaimCurrent(f,"pending",slot))));
        setup.Handler.Inner.Status=503;Assert.True(await Evaluate(f,await Next(f)));await using var read=f.Context();var state=await read.Set<AlertEvaluationState>().SingleAsync();Assert.Equal("Inactive",state.Phase);Assert.Null(state.PendingSince);Assert.Empty(await Claim(f,slot.AddSeconds(-1),"cpu-backward"));Assert.Equal(0,await read.Set<AlertEvent>().CountAsync());
    }
    [Fact] public async Task ResourceRetirementClosesActualEventWithoutBorrowingSourceAbsence()
    {
        var setup=await Setup("Destination");await using var f=setup.Fixture;Assert.True(await Evaluate(f,Assert.Single(await ClaimCurrent(f,"active"))));
        await using(var db=f.Context()){db.Remove(await db.Set<UpstreamDestination>().SingleAsync(x=>x.Id==f.Destination.Id));await db.SaveChangesAsync();}
        var sourceCount=setup.Handler.Inner.Requests.Count;Assert.True(await Evaluate(f,await Next(f)));Assert.Equal(sourceCount,setup.Handler.Inner.Requests.Count);await using var read=f.Context();var e=await read.Set<AlertEvent>().SingleAsync();Assert.Equal("Resolved",e.Status);Assert.Equal("ResourceRetired",e.ResolveReason);Assert.Equal(2,await read.Set<AlertEventTransition>().CountAsync());
    }
    [Fact] public async Task ScopePauseKeepsFiringAndRecoveryResolvesAfterReactivation()
    {
        var setup=await Setup();await using var f=setup.Fixture;Assert.True(await Evaluate(f,Assert.Single(await ClaimCurrent(f,"active"))));
        await using(var db=f.Context()){var env=await db.Set<EnvironmentRecord>().SingleAsync();env.Status="Inactive";await db.SaveChangesAsync();}
        Assert.True(await Evaluate(f,await Next(f)));await using(var db=f.Context()){var alert=await db.Set<AlertEvent>().SingleAsync();Assert.Equal("Open",alert.Status);Assert.Equal("ScopeInactive",alert.EvaluationState);Assert.Equal("Firing",(await db.Set<AlertEvaluationState>().SingleAsync()).Phase);var env=await db.Set<EnvironmentRecord>().SingleAsync();env.Status="Active";await db.SaveChangesAsync();}
        setup.Handler.Inner.ResetWindow=true;Assert.True(await Evaluate(f,await Next(f)));await using var read=f.Context();var recovered=await read.Set<AlertEvent>().SingleAsync();Assert.Equal("Resolved",recovered.Status);Assert.Equal("Recovered",recovered.ResolveReason);
    }
    [Fact] public async Task ScopePauseDoesNotEraseManualSuppression()
    {
        var setup=await Setup();await using var f=setup.Fixture;Assert.True(await Evaluate(f,Assert.Single(await ClaimCurrent(f,"active"))));
        await using(var db=f.Context()){var state=await db.Set<AlertEvaluationState>().SingleAsync();var alert=await db.Set<AlertEvent>().SingleAsync();alert.Status="Resolved";alert.ResolveReason="Manual";alert.ResolvedAt=DateTimeOffset.UtcNow;state.Phase="SuppressedUntilRecovery";state.SuppressedAt=alert.ResolvedAt;var env=await db.Set<EnvironmentRecord>().SingleAsync();env.Status="Inactive";await db.SaveChangesAsync();}
        Assert.True(await Evaluate(f,await Next(f)));await using var read=f.Context();Assert.Equal("SuppressedUntilRecovery",(await read.Set<AlertEvaluationState>().SingleAsync()).Phase);Assert.NotNull((await read.Set<AlertEvaluationState>().SingleAsync()).SuppressedAt);
    }
    [Fact] public async Task WideRuleSeedsNewEnvironmentBeforeAnyOccurrenceExists()
    {
        var setup=await Setup(duration:300);await using var f=setup.Fixture;using var get=await f.Client.GetAsync($"/api/v1/observability/alert-rules/{setup.Rule}");var rule=(await get.Content.ReadFromJsonAsync<AlertRuleDto>())!;using var wide=await f.WriteAsync(HttpMethod.Put,$"/api/v1/observability/alert-rules/{setup.Rule}",rule.Definition with{EnvironmentId=null},"\"1\"");wide.EnsureSuccessStatusCode();
        Assert.True(await Evaluate(f,Assert.Single(await ClaimCurrent(f,"first"))));Guid added;
        await using(var db=f.Context()){var env=new EnvironmentRecord{ProjectId=f.Project.Id,Code="FUTURE",Name="Future"};added=env.Id;db.Add(env);await db.SaveChangesAsync();}
        await Task.Delay(1100);var leases=await ClaimCurrent(f,"future");Assert.Contains(leases,x=>x.EnvironmentId==added);foreach(var lease in leases)Assert.True(await Evaluate(f,lease));
        await using var read=f.Context();Assert.Equal(2,await read.Set<AlertEvaluationState>().CountAsync(x=>x.LogicRevision==2));Assert.Equal(0,await read.Set<AlertEvent>().CountAsync());using var detail=await f.Client.GetAsync($"/api/v1/observability/alert-rules/{setup.Rule}");var current=(await detail.Content.ReadFromJsonAsync<AlertRuleDto>())!;Assert.Equal("Unknown",current.EvaluationState);Assert.Null(current.LastSuccessAt);
    }

    [Fact] public async Task ZeroRpsIsKnownAndRecoversHighThenTriggersLowTrafficRule()
    {
        var setup=await Setup();await using var f=setup.Fixture;
        Assert.True(await Evaluate(f,Assert.Single(await ClaimCurrent(f,"high"))));
        using var get=await f.Client.GetAsync($"/api/v1/observability/alert-rules/{setup.Rule}");var rule=(await get.Content.ReadFromJsonAsync<AlertRuleDto>())!;
        setup.Handler.HealthOnly=true;
        using var test=await f.WriteAsync(HttpMethod.Post,"/api/v1/observability/alert-rules/test",rule.Definition);test.EnsureSuccessStatusCode();var result=(await test.Content.ReadFromJsonAsync<RuleTestDto>())!;Assert.Equal("Known",result.EvaluationState);Assert.False(result.Condition);Assert.Equal(0,result.Matches.Single().Values.Single().Value);
        Assert.True(await Evaluate(f,await Next(f)));
        await using(var db=f.Context()){var old=await db.Set<AlertEvent>().SingleAsync();Assert.Equal("Resolved",old.Status);Assert.Equal("Recovered",old.ResolveReason);}
        var low=rule.Definition with{Expression="request_rps < 1"};using var update=await f.WriteAsync(HttpMethod.Put,$"/api/v1/observability/alert-rules/{setup.Rule}",low,"\"1\"");update.EnsureSuccessStatusCode();
        Assert.True(await Evaluate(f,await Next(f)));await using(var db=f.Context()){Assert.Equal("Open",(await db.Set<AlertEvent>().SingleAsync(x=>x.LogicRevision==2)).Status);}
        foreach(var metric in new[]{"error_5xx_ratio","latency_p95_ms"}){using var unknown=await f.WriteAsync(HttpMethod.Post,"/api/v1/observability/alert-rules/test",low with{Metric=metric,Expression=metric+" < 1"});unknown.EnsureSuccessStatusCode();Assert.Equal("Unknown",(await unknown.Content.ReadFromJsonAsync<RuleTestDto>())!.EvaluationState);}
    }
    [Fact] public async Task HealthGaugeWithoutBusinessRequestsCanTriggerAndReadonlyTestIsKnown()
    {
        var setup=await Setup();await using var f=setup.Fixture;using var read=await f.Client.GetAsync($"/api/v1/observability/alert-rules/{setup.Rule}");var rule=(await read.Content.ReadFromJsonAsync<AlertRuleDto>())!;var definition=rule.Definition with{Metric="unhealthy_destinations",Expression="unhealthy_destinations > 0"};using var change=await f.WriteAsync(HttpMethod.Put,$"/api/v1/observability/alert-rules/{setup.Rule}",definition,"\"1\"");change.EnsureSuccessStatusCode();setup.Handler.HealthOnly=true;
        using var test=await f.WriteAsync(HttpMethod.Post,"/api/v1/observability/alert-rules/test",definition);test.EnsureSuccessStatusCode();var result=(await test.Content.ReadFromJsonAsync<RuleTestDto>())!;Assert.Equal("Known",result.EvaluationState);Assert.True(result.Condition);Assert.Equal(1,result.Matches.Single().Values.Single().Value);
        Assert.True(await Evaluate(f,Assert.Single(await ClaimCurrent(f,"health"))));await using var db=f.Context();Assert.Equal("Open",(await db.Set<AlertEvent>().SingleAsync()).Status);
    }

    [Fact]
    public async Task DisabledDestinationDraftKeepsTheUnhealthyEventOpen()
    {
        var handler = new AlertClockedMetricHandler();
        await using var deployment = new DeploymentScenario();
        await deployment.InitializeAsync(builder => ConfigureEvaluator(builder, handler));
        var f = deployment.Api;
        await ObservationTestSupport.GrantAsync(f, ["alert.rule.manage"]);
        using var sibling = await f.WriteAsync(HttpMethod.Post, $"/api/v1/clusters/{f.Cluster.Id}/destinations",
            new SaveDestinationRequest("secondary", "http://test-backend:8080/secondary"));
        sibling.EnsureSuccessStatusCode();
        await deployment.PublishAsync();
        var rule = new SaveAlertRuleRequest(f.Organization.Id, f.Project.Id, f.Environment.Id, "Destination health",
            "unhealthy_destinations", "unhealthy_destinations > 0", "Warning", true, 0, "Destination", f.Destination.Id, 60, new(true, []));
        using var saved = await f.WriteAsync(HttpMethod.Post, "/api/v1/observability/alert-rules", rule);
        saved.EnsureSuccessStatusCode();
        var source = handler.Inner;
        source.EnvironmentId = f.Environment.Id;
        source.DestinationId = f.Destination.Id;
        source.ClusterId = f.Cluster.Id;
        source.HealthSamples = [("node-0", f.Destination.Id, 0), ("node-1", f.Destination.Id, 1)];
        Assert.True(await Evaluate(f, Assert.Single(await ClaimCurrent(f, "firing"))));
        using var draft = await f.WriteAsync(HttpMethod.Put, $"/api/v1/destinations/{f.Destination.Id}",
            new SaveDestinationRequest(f.Destination.Name, f.Destination.Address, Enabled: false), "\"1\"");
        draft.EnsureSuccessStatusCode();

        async Task CheckEvent(string status, string evaluation, int transitions)
        {
            Assert.True(await Evaluate(f, await Next(f)));
            await using var db = f.Context();
            var alert = await db.Set<AlertEvent>().SingleAsync();
            Assert.Equal(status, alert.Status);
            Assert.Equal(evaluation, alert.EvaluationState);
            Assert.Equal(transitions, await db.Set<AlertEventTransition>().CountAsync());
        }
        await CheckEvent("Open", "Known", 1);
        using var preview = await f.WriteAsync(HttpMethod.Post, "/api/v1/observability/alert-rules/test", rule);
        preview.EnsureSuccessStatusCode();
        Assert.True((await preview.Content.ReadFromJsonAsync<RuleTestDto>())!.Condition);
        source.HealthSamples = [("node-1", f.Destination.Id, 1)];
        await CheckEvent("Open", "Unknown", 1);
        source.HealthSamples = [("node-0", f.Destination.Id, 0), ("node-1", f.Destination.Id, 0)];
        var release = await deployment.PublishAsync(acknowledge: false);
        var envelope = (await deployment.DesiredAsync(release)).Envelope;
        await deployment.AckAsync(0, envelope);
        await CheckEvent("Open", "Known", 1);
        await deployment.AckAsync(1, envelope);
        await CheckEvent("Resolved", "Known", 2);
    }

    [Fact]
    public async Task TrafficRulesIncludeADeletedDestinationUntilItsRequestsLeaveTheWindow()
    {
        var handler = new AlertClockedMetricHandler();
        await using var deployment = new DeploymentScenario();
        await deployment.InitializeAsync(builder => ConfigureEvaluator(builder, handler));
        var f = deployment.Api;
        await ObservationTestSupport.GrantAsync(f, ["alert.rule.manage", "metrics.read"]);
        using var added = await f.WriteAsync(HttpMethod.Post, $"/api/v1/clusters/{f.Cluster.Id}/destinations",
            new SaveDestinationRequest("replica", "http://test-backend:8080/replica"));
        added.EnsureSuccessStatusCode();
        var target = (await added.Content.ReadFromJsonAsync<DestinationDto>())!;
        var source = handler.Inner;
        source.EnvironmentId = f.Environment.Id;
        source.ApiId = f.Api.Id;
        source.ApplicationId = f.Application.Id;
        source.ClusterId = f.Cluster.Id;
        source.DestinationId = target.Id;
        source.ForeignEnvironmentId = Guid.NewGuid();
        source.ExtraApiTraffic = [(Guid.NewGuid(), 1000, null)];
        await deployment.PublishAsync();

        var rule = new SaveAlertRuleRequest(f.Organization.Id, f.Project.Id, f.Environment.Id, "Traffic",
            "request_rps", "request_rps > 0.01", "Warning", true, 0, "Environment", null, 3600, new(true, []));
        using var saved = await f.WriteAsync(HttpMethod.Post, "/api/v1/observability/alert-rules", rule);
        saved.EnsureSuccessStatusCode();
        Assert.True(await Evaluate(f, await Next(f)));
        await using var db = f.Context();
        var open = await db.Set<AlertEvent>().AsNoTracking().SingleAsync();
        Assert.Equal("Open", open.Status);

        using var deleted = await f.WriteAsync(HttpMethod.Delete, $"/api/v1/destinations/{target.Id}", etag: added.Headers.ETag!.ToString());
        deleted.EnsureSuccessStatusCode();
        await deployment.PublishAsync();
        var retiredAt = DateTimeOffset.UtcNow;
        Assert.True(await Evaluate(f, await Next(f)));
        var current = await db.Set<AlertEvent>().AsNoTracking().SingleAsync();
        Assert.Equal(open.Id, current.Id);
        Assert.Equal("Open", current.Status);
        Assert.True(current.LastCondition);
        Assert.Equal(1, await db.Set<AlertEventTransition>().CountAsync());

        foreach (var definition in new[] { rule, rule with { TargetType = "Api", TargetId = f.Api.Id } })
        {
            using var test = await f.WriteAsync(HttpMethod.Post, "/api/v1/observability/alert-rules/test", definition);
            test.EnsureSuccessStatusCode();
            var result = (await test.Content.ReadFromJsonAsync<RuleTestDto>())!;
            var tested = Assert.Single(Assert.Single(result.Matches).Values);
            var end = source.End;
            var start = end.AddSeconds(-definition.WindowSeconds);
            Assert.InRange(retiredAt, start, end);
            var apiFilter = definition.TargetId is Guid api ? $"&apiId={api}" : "";
            using var metrics = await f.Client.GetAsync($"/api/v1/observability/metrics?organizationId={f.Organization.Id}&projectId={f.Project.Id}" +
                $"&environmentId={f.Environment.Id}&start={Uri.EscapeDataString(start.ToString("O"))}&end={Uri.EscapeDataString(end.ToString("O"))}{apiFilter}");
            metrics.EnsureSuccessStatusCode();
            var observed = (await metrics.Content.ReadFromJsonAsync<ObservationEnvelope<MetricsDto>>())!;
            var actual = Assert.Single(observed.Data!.Kpis, kpi => kpi.Metric == rule.Metric);
            Assert.True(observed.Coverage.Complete);
            Assert.Equal(200, actual.SampleCount);
            Assert.Equal(200d / rule.WindowSeconds, actual.Value);
            Assert.Equal(actual.Value, current.LastValue);
            Assert.Equal(actual, tested);
            Assert.Equal("Known", result.EvaluationState);
            Assert.True(result.Condition);
        }

        source.ResetWindow = true;
        Assert.True(await Evaluate(f, await Next(f)));
        var recovered = await db.Set<AlertEvent>().AsNoTracking().SingleAsync();
        Assert.Equal(open.Id, recovered.Id);
        Assert.Equal("Recovered", recovered.ResolveReason);
        Assert.Equal(2, await db.Set<AlertEventTransition>().CountAsync());
    }

    [Fact]
    public async Task DeletedDestinationKeepsBothEventsUntilEveryNodeProvesRemoval()
    {
        var handler = new AlertClockedMetricHandler();
        await using var deployment = new DeploymentScenario();
        await deployment.InitializeAsync(builder => ConfigureEvaluator(builder, handler));
        var f = deployment.Api;
        await ObservationTestSupport.GrantAsync(f, ["alert.rule.manage"]);
        using var added = await f.WriteAsync(HttpMethod.Post, $"/api/v1/clusters/{f.Cluster.Id}/destinations",
            new SaveDestinationRequest("replica", "http://test-backend:8080/replica"));
        added.EnsureSuccessStatusCode();
        var target = (await added.Content.ReadFromJsonAsync<DestinationDto>())!;
        await deployment.PublishAsync();
        var scopeRule = new SaveAlertRuleRequest(f.Organization.Id, f.Project.Id, f.Environment.Id, "Environment health",
            "unhealthy_destinations", "unhealthy_destinations > 0", "Warning", true, 0, "Environment", null, 60, new(true, []));
        var destinationRule = scopeRule with { Name = "Destination health", TargetType = "Destination", TargetId = target.Id };
        foreach (var definition in new[] { scopeRule, destinationRule })
        {
            using var saved = await f.WriteAsync(HttpMethod.Post, "/api/v1/observability/alert-rules", definition);
            saved.EnsureSuccessStatusCode();
        }
        var source = handler.Inner;
        source.EnvironmentId = f.Environment.Id;
        source.ApiId = f.Api.Id;
        source.ApplicationId = f.Application.Id;
        source.DestinationId = target.Id;
        source.ClusterId = f.Cluster.Id;
        source.HealthSamples = [("node-0", target.Id, 0), ("node-1", target.Id, 0),
            ("node-0", f.Destination.Id, 1), ("node-1", f.Destination.Id, 1)];

        async Task EvaluateBoth()
        {
            // The worker evaluates the previous whole-second boundary. Advance past the last ACK.
            await Task.Delay(1100);
            var leases = await ClaimCurrent(f, "health");
            Assert.Equal(2, leases.Count);
            foreach (var lease in leases) Assert.True(await Evaluate(f, lease));
        }
        async Task CheckOpen(string evaluation)
        {
            await EvaluateBoth();
            await using var db = f.Context();
            var events = await db.Set<AlertEvent>().ToArrayAsync();
            Assert.Equal(2, events.Length);
            Assert.All(events, e => { Assert.Equal("Open", e.Status); Assert.Equal(evaluation, e.EvaluationState); });
            Assert.Equal(2, await db.Set<AlertEventTransition>().CountAsync());
        }
        await CheckOpen("Known");
        await using var history = f.Context();
        var original = await history.Set<AlertEventTransition>().AsNoTracking().OrderBy(t => t.Id)
            .Select(t => new { t.Id, t.EventId, t.OccurredAt, t.Reason }).ToArrayAsync();
        using var deleted = await f.WriteAsync(HttpMethod.Delete, $"/api/v1/destinations/{target.Id}", etag: added.Headers.ETag!.ToString());
        deleted.EnsureSuccessStatusCode();
        await CheckOpen("Known");
        using var preview = await f.WriteAsync(HttpMethod.Post, "/api/v1/observability/alert-rules/preview", destinationRule);
        preview.EnsureSuccessStatusCode();
        var next = await deployment.PublishAsync(acknowledge: false);
        var envelope = (await deployment.DesiredAsync(next)).Envelope;
        await CheckOpen("Known");
        await deployment.AckAsync(0, envelope);
        await CheckOpen("Known");
        using var heartbeat = await deployment.NodeAsync(1, $"/internal/v1/nodes/{deployment.NodeIds[1]}/heartbeat",
            new NodeHeartbeat(deployment.Instances[1], envelope.ConfigVersion, envelope.DeploymentSequence, "Ready"));
        heartbeat.EnsureSuccessStatusCode();
        source.HealthSamples = [("node-0", target.Id, 0), ("node-1", target.Id, 1),
            ("node-0", f.Destination.Id, 1), ("node-1", f.Destination.Id, 1)];
        await CheckOpen("Unknown");
        await deployment.AckAsync(1, envelope);
        await EvaluateBoth();
        await EvaluateBoth();
        var resolved = await history.Set<AlertEvent>().AsNoTracking().ToDictionaryAsync(e => e.ResourceType);
        Assert.Equal(2, resolved.Count);
        Assert.All(resolved.Values, e => { Assert.Equal("Resolved", e.Status); Assert.Equal(1, e.OccurrenceNo); });
        Assert.Equal("Recovered", resolved["Environment"].ResolveReason);
        Assert.Equal("ResourceRetired", resolved["Destination"].ResolveReason);
        var transitions = await history.Set<AlertEventTransition>().AsNoTracking().OrderBy(t => t.Id)
            .Select(t => new { t.Id, t.EventId, t.OccurredAt, t.Reason }).ToArrayAsync();
        Assert.Equal(4, transitions.Length);
        Assert.Equal(original, transitions.Where(t => t.Reason == "Triggered").ToArray());
        Assert.All(transitions.Where(t => t.Reason != "Triggered"), t =>
            Assert.Contains(resolved.Values, e => e.Id == t.EventId && e.ResolveReason == t.Reason));
    }

    [Fact] public async Task SourceFailureDoesNotStopStandalonePublishWorker()
    {
        await using var scenario=new DeploymentScenario();await scenario.InitializeAsync();await ObservationTestSupport.GrantAsync(scenario.Api,["alert.rule.manage"]);
        using var create=await scenario.Api.WriteAsync(HttpMethod.Post,"/api/v1/observability/alert-rules",new SaveAlertRuleRequest(scenario.Api.Organization.Id,scenario.Api.Project.Id,scenario.Api.Environment.Id,"Fault isolation","request_rps","request_rps > 1","Warning",true,0,"Environment",null,60,new(true,[])));create.EnsureSuccessStatusCode();
        var release=await scenario.ReadyAsync();using var publish=await ApiFixture.CommandAsync(scenario.Api.Client,$"/api/v1/releases/{release}/publish");publish.EnsureSuccessStatusCode();
        var handler=new AlertClockedMetricHandler();handler.Inner.Status=503;var prefix="alert-worker-test:"+Guid.NewGuid().ToString("N");
        using var worker=WorkerApp.Build(["--environment","Development"],builder=>{builder.Configuration["ConnectionStrings:WebApi"]=scenario.Api.Database.ConnectionString;builder.Configuration["Redis:Prefix"]=prefix;builder.Configuration["Observability:Enabled"]="true";builder.Configuration["Alerts:IntervalSeconds"]="1";builder.Configuration["Alerts:QueryDelaySeconds"]="0";builder.Services.PostConfigure<HttpClientFactoryOptions>("observability",o=>o.HttpMessageHandlerBuilderActions.Add(b=>b.PrimaryHandler=handler));builder.Logging.ClearProviders();});
        await worker.StartAsync();var progressed=false;
        try
        {
            var until=DateTimeOffset.UtcNow.AddSeconds(10);while(DateTimeOffset.UtcNow<until){await using var db=scenario.Api.Context();if(await db.Set<ReleaseRecord>().AnyAsync(x=>x.Id==release&&x.Status=="Publishing")&&await db.Set<AlertEvaluationState>().AnyAsync(x=>x.LastEvaluatedSlot!=null&&x.EvaluationState=="Unknown")){progressed=true;break;}await Task.Delay(100);}
        }
        finally
        {
            await worker.StopAsync();using var redis=await ConnectionMultiplexer.ConnectAsync("redis:6379,abortConnect=false");var key=prefix+":{"+scenario.Api.Environment.Id+"}:desired";await redis.GetDatabase().KeyDeleteAsync(key);Assert.False(await redis.GetDatabase().KeyExistsAsync(key));
        }
        Assert.True(progressed,"Alert source failure must leave queued publish processing alive.");
    }

    [Fact] public async Task RenameDuringQueryReleasesObsoleteLeaseWithoutResettingPending()
    {
        var setup=await Setup(duration:300);await using var f=setup.Fixture;Assert.True(await Evaluate(f,Assert.Single(await ClaimCurrent(f,"before"))));DateTimeOffset? pending;
        await using(var db=f.Context())pending=(await db.Set<AlertEvaluationState>().SingleAsync()).PendingSince;
        var next=await NextLeaseOnly(f);setup.Handler.PauseFirst=true;var running=Evaluate(f,next);await setup.Handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try{using var get=await f.Client.GetAsync($"/api/v1/observability/alert-rules/{setup.Rule}");var rule=(await get.Content.ReadFromJsonAsync<AlertRuleDto>())!;using var rename=await f.WriteAsync(HttpMethod.Put,$"/api/v1/observability/alert-rules/{setup.Rule}",rule.Definition with{Name="Renamed while querying"},"\"1\"").WaitAsync(TimeSpan.FromSeconds(3));rename.EnsureSuccessStatusCode();}
        finally{setup.Handler.Resume.TrySetResult(true);}
        Assert.False(await running);await using(var db=f.Context()){var state=await db.Set<AlertEvaluationState>().SingleAsync();Assert.Equal(pending,state.PendingSince);Assert.Null(state.LeaseUntil);Assert.Equal("Pending",state.Phase);}
        var current=Assert.Single(await ClaimCurrent(f,"renamed"));Assert.Equal(2,current.RuleRevision);Assert.Equal(1,current.LogicRevision);Assert.True(await Evaluate(f,current));
    }
    private static async Task<EvaluationLease> NextLeaseOnly(ApiFixture f){await Task.Delay(1100);return Assert.Single(await ClaimCurrent(f,"next"));}

}
public sealed class AlertClockedMetricHandler:HttpMessageHandler
{
    public MetricProtocolHandler Inner {get;}=new();
    public DateTimeOffset? TrafficIncreaseAt { get; set; }
    public bool HealthOnly {get;set;}
    public bool PauseFirst {get;set;}
    public TaskCompletionSource<bool> Entered {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<bool> Resume {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int paused;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
    {
        if(PauseFirst&&Interlocked.Exchange(ref paused,1)==0)return WaitAsync(request,ct);
        return Respond(request,ct);
    }
    private async Task<HttpResponseMessage> WaitAsync(HttpRequestMessage request,CancellationToken ct){Entered.TrySetResult(true);await Resume.Task.WaitAsync(ct);return await Respond(request,ct);}
    private Task<HttpResponseMessage> Respond(HttpRequestMessage request,CancellationToken ct)
    {
        var query=QueryHelpers.ParseQuery(request.RequestUri!.Query);var text=query.ContainsKey("time")?query["time"].ToString():query["end"].ToString();Inner.End=DateTimeOffset.UnixEpoch.AddSeconds(double.Parse(text,CultureInfo.InvariantCulture));
        if (TrafficIncreaseAt is { } increaseAt) Inner.ResetWindow = Inner.End < increaseAt;
        if(HealthOnly&&query["query"].ToString().Contains("webapi_gateway_requests_total",StringComparison.Ordinal))return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK){Content=JsonContent.Create(new{status="success",data=new{resultType=request.RequestUri.AbsolutePath.EndsWith("query_range",StringComparison.Ordinal)?"matrix":"vector",result=Array.Empty<object>()}})});
        if(HealthOnly&&query["query"].ToString().Contains("webapi_destination_health",StringComparison.Ordinal))return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK){Content=JsonContent.Create(new{status="success",data=new{resultType="vector",result=new[]{new{metric=new{webapi_environment_id=Inner.EnvironmentId.ToString(),webapi_destination_id=Inner.DestinationId.ToString(),service_instance_id="node-0"},value=new object[]{Inner.End.ToUnixTimeSeconds(),"0"}}}}})});
        return new HttpMessageInvoker(Inner,false).SendAsync(request,ct);
    }
}
