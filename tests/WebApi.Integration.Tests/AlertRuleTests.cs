using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using WebApi.Contracts.Alerts;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;
namespace WebApi.Integration.Tests;
public sealed class AlertRuleTests
{
    private static SaveAlertRuleRequest Definition(ApiFixture f,bool wide=false)=>new(f.Organization.Id,wide?null:f.Project.Id,wide?null:f.Environment.Id,"Errors","error_5xx_ratio","error_5xx_ratio > 0.05","Critical",true,300,"Environment",null,300,new(true,[]));
    private static async Task<ApiFixture> Fixture(MetricProtocolHandler? handler=null)
    {
        var f=new ApiFixture();await f.InitializeAsync(builder=>{if(handler is not null){builder.Configuration["Observability:Enabled"]="true";builder.Services.PostConfigure<HttpClientFactoryOptions>("observability",o=>o.HttpMessageHandlerBuilderActions.Add(b=>b.PrimaryHandler=handler));}});
        await f.SeedCatalogAsync();await ObservationTestSupport.GrantAsync(f,["alert.rule.manage"]);using var login=await f.LoginAsync();login.EnsureSuccessStatusCode();return f;
    }
    private static async Task<AlertRuleDto> Create(ApiFixture f,SaveAlertRuleRequest? d=null){using var r=await f.WriteAsync(HttpMethod.Post,"/api/v1/observability/alert-rules",d??Definition(f));r.EnsureSuccessStatusCode();return (await r.Content.ReadFromJsonAsync<AlertRuleDto>())!;}
    [Fact] public async Task EnvironmentGrantCannotCreateOrganizationRule()
    {
        await using var f=await Fixture();await using(var db=f.Context()){var grant=await db.Set<UserProjectScope>().SingleAsync(x=>x.UserId==f.User.Id);grant.ProjectId=f.Project.Id;grant.EnvironmentId=f.Environment.Id;await db.SaveChangesAsync();}
        using var denied=await f.WriteAsync(HttpMethod.Post,"/api/v1/observability/alert-rules",Definition(f,true));Assert.Equal(HttpStatusCode.Forbidden,denied.StatusCode);
        await Create(f);
    }
    [Fact] public async Task WideScopePreviewIncludesFutureEnvironmentPolicy()
    {
        await using var f=await Fixture();var definition=Definition(f,true);using var preview=await f.WriteAsync(HttpMethod.Post,"/api/v1/observability/alert-rules/preview",definition);preview.EnsureSuccessStatusCode();
        var first=await preview.Content.ReadFromJsonAsync<RuleScopePreviewDto>();Assert.Contains(f.Environment.Id,first!.EnvironmentIds);Assert.True(first.IncludesFutureActiveEnvironments);
        Guid added;await using(var db=f.Context()){var env=new EnvironmentRecord{ProjectId=f.Project.Id,Code="NEW",Name="Future"};added=env.Id;db.Add(env);await db.SaveChangesAsync();}
        using var next=await f.WriteAsync(HttpMethod.Post,"/api/v1/observability/alert-rules/preview",definition);next.EnsureSuccessStatusCode();Assert.Contains(added,(await next.Content.ReadFromJsonAsync<RuleScopePreviewDto>())!.EnvironmentIds);
    }
    [Fact] public async Task RenamingKeepsLogicRevision()
    {
        await using var f=await Fixture();var before=await Create(f);DateTimeOffset pending=DateTimeOffset.UtcNow.AddSeconds(-45);Guid stateId;
        await using(var db=f.Context()){var state=new AlertEvaluationState{RuleId=before.Id,LogicRevision=1,OrganizationId=f.Organization.Id,ProjectId=f.Project.Id,EnvironmentId=f.Environment.Id,ResourceType="Environment",ResourceKey="Environment",Phase="Pending",PendingSince=pending,SuppressedAt=pending,LastSuccessAt=pending};stateId=state.Id;db.Add(state);await db.SaveChangesAsync();}
        using var rename=await f.WriteAsync(HttpMethod.Put,$"/api/v1/observability/alert-rules/{before.Id}",before.Definition with{Name="Renamed",Notification=new(true,["Email"])},"\"1\"");rename.EnsureSuccessStatusCode();var after=(await rename.Content.ReadFromJsonAsync<AlertRuleDto>())!;Assert.Equal(before.LogicRevision,after.LogicRevision);Assert.Equal(2,after.Revision);
        await using var read=f.Context();var saved=await read.Set<AlertEvaluationState>().SingleAsync(x=>x.Id==stateId);Assert.Equal(pending.ToUnixTimeMilliseconds(),saved.PendingSince!.Value.ToUnixTimeMilliseconds());Assert.Equal("Pending",saved.Phase);Assert.NotNull(saved.SuppressedAt);
    }
    [Fact] public async Task LogicChangeResolvesOldOccurrence()
    {
        await using var f=await Fixture();var rule=await Create(f);Guid id;
        await using(var db=f.Context()){var e=new AlertEvent{RuleId=rule.Id,RuleRevision=1,LogicRevision=1,OrganizationId=f.Organization.Id,ProjectId=f.Project.Id,EnvironmentId=f.Environment.Id,ResourceType="Environment",ResourceKey="Environment",OccurrenceNo=1,Message="threshold exceeded",RuleSummary="safe"};id=e.Id;db.Add(e);db.Add(new AlertEvaluationState{RuleId=rule.Id,LogicRevision=1,OrganizationId=f.Organization.Id,ProjectId=f.Project.Id,EnvironmentId=f.Environment.Id,ResourceType="Environment",ResourceKey="Environment",Phase="Firing",LastEventId=e.Id});await db.SaveChangesAsync();}
        using var edit=await f.WriteAsync(HttpMethod.Put,$"/api/v1/observability/alert-rules/{rule.Id}",rule.Definition with{ForSeconds=600},"\"1\"");edit.EnsureSuccessStatusCode();Assert.Equal(2,(await edit.Content.ReadFromJsonAsync<AlertRuleDto>())!.LogicRevision);
        await using var read=f.Context();var closed=await read.Set<AlertEvent>().SingleAsync(x=>x.Id==id);Assert.Equal("Resolved",closed.Status);Assert.Equal("RuleChanged",closed.ResolveReason);Assert.Equal(1,await read.Set<AlertEventTransition>().CountAsync(x=>x.EventId==id));Assert.Equal("Warning",closed.Severity);
    }
    [Fact] public async Task RuleTestDoesNotPersistOrNotify()
    {
        var handler=new MetricProtocolHandler();await using var f=await Fixture(handler);handler.EnvironmentId=f.Environment.Id;handler.ApiId=f.Api.Id;handler.DestinationId=f.Destination.Id;handler.ApplicationId=f.Application.Id;handler.ClusterId=f.Cluster.Id;
        using var result=await f.WriteAsync(HttpMethod.Post,"/api/v1/observability/alert-rules/test",Definition(f));result.EnsureSuccessStatusCode();var test=(await result.Content.ReadFromJsonAsync<RuleTestDto>())!;Assert.Equal("Unknown",test.EvaluationState);Assert.Null(test.Condition);Assert.Equal(WebApi.Contracts.Observability.SourceState.NoData,test.Matches.Single().Values.Single().State);
        await using var db=f.Context();Assert.Equal(0,await db.Set<AlertRule>().CountAsync());Assert.Equal(0,await db.Set<AlertEvent>().CountAsync());Assert.Equal(0,await db.Set<OutboxMessage>().CountAsync());
    }
    [Fact] public async Task StaleEtagRetainsDatabaseRevision()
    {
        await using var f=await Fixture();var rule=await Create(f);using var conflict=await f.WriteAsync(HttpMethod.Put,$"/api/v1/observability/alert-rules/{rule.Id}",rule.Definition with{Name="Unsaved"},"\"0\"");Assert.Equal(HttpStatusCode.PreconditionFailed,conflict.StatusCode);
        await using var db=f.Context();var saved=await db.Set<AlertRule>().SingleAsync();Assert.Equal(1,saved.Revision);Assert.Equal("Errors",saved.Name);
    }
    private static async Task<HttpResponseMessage> Keyed(ApiFixture f,HttpMethod method,string path,object body,string key,string? tag=null)
    {
        using var request=new HttpRequestMessage(method,path){Content=JsonContent.Create(body)};request.Headers.Add("X-CSRF-Token",await f.CsrfAsync());request.Headers.Add("Idempotency-Key",key);if(tag is not null)request.Headers.Add("If-Match",tag);return await f.Client.SendAsync(request);
    }
    [Fact] public async Task ScopeEditRetryReplaysAndDifferentRequestConflicts()
    {
        await using var f=await Fixture();var rule=await Create(f);Guid next;
        await using(var db=f.Context()){var env=new EnvironmentRecord{ProjectId=f.Project.Id,Code="NEXT",Name="Next"};next=env.Id;db.Add(env);await db.SaveChangesAsync();}
        var input=rule.Definition with{EnvironmentId=next};var key=Guid.NewGuid().ToString("N");var path=$"/api/v1/observability/alert-rules/{rule.Id}";
        using var first=await Keyed(f,HttpMethod.Put,path,input,key,"\"1\"");first.EnsureSuccessStatusCode();
        using var retry=await Keyed(f,HttpMethod.Put,path,input,key,"\"1\"");Assert.Equal(HttpStatusCode.OK,retry.StatusCode);Assert.Equal(2,(await retry.Content.ReadFromJsonAsync<AlertRuleDto>())!.Revision);
        using var conflict=await Keyed(f,HttpMethod.Put,path,input with{Name="Different"},key,"\"1\"");Assert.Equal(HttpStatusCode.Conflict,conflict.StatusCode);
        await using var read=f.Context();Assert.Equal(1,await read.Set<AlertRule>().CountAsync());Assert.Equal(2,(await read.Set<AlertRule>().SingleAsync()).Revision);
    }
    [Fact] public async Task OrganizationReparentIsExplicitlyRejected()
    {
        await using var f=await Fixture();var rule=await Create(f);using var result=await f.WriteAsync(HttpMethod.Put,$"/api/v1/observability/alert-rules/{rule.Id}",rule.Definition with{OrganizationId=Guid.NewGuid(),ProjectId=null,EnvironmentId=null},"\"1\"");Assert.Equal(HttpStatusCode.UnprocessableEntity,result.StatusCode);
        await using var db=f.Context();Assert.Equal(f.Organization.Id,(await db.Set<AlertRule>().SingleAsync()).OrganizationId);
    }
    [Fact] public async Task WideRuleDoesNotHideUnknownEnvironment()
    {
        await using var f=await Fixture();var rule=await Create(f,Definition(f) with{EnvironmentId=null});var a=DateTimeOffset.UtcNow.AddSeconds(-10);var b=a.AddSeconds(-10);Guid envId;
        await using(var db=f.Context()){var env=new EnvironmentRecord{ProjectId=f.Project.Id,Code="SECOND",Name="Second"};envId=env.Id;db.Add(env);db.Add(new AlertEvaluationState{RuleId=rule.Id,LogicRevision=1,OrganizationId=f.Organization.Id,ProjectId=f.Project.Id,EnvironmentId=f.Environment.Id,ResourceType="Environment",ResourceKey="Environment",EvaluationState="Known",LastSuccessAt=a,LastEvaluatedSlot=DateTimeOffset.UtcNow});await db.SaveChangesAsync();}
        using(var missing=await f.Client.GetAsync($"/api/v1/observability/alert-rules/{rule.Id}")){missing.EnsureSuccessStatusCode();var dto=(await missing.Content.ReadFromJsonAsync<AlertRuleDto>())!;Assert.Equal("Unknown",dto.EvaluationState);Assert.Null(dto.LastSuccessAt);}
        await using(var db=f.Context()){db.Add(new AlertEvaluationState{RuleId=rule.Id,LogicRevision=1,OrganizationId=f.Organization.Id,ProjectId=f.Project.Id,EnvironmentId=envId,ResourceType="Environment",ResourceKey="Environment",EvaluationState="Known",LastSuccessAt=b,LastEvaluatedSlot=DateTimeOffset.UtcNow});await db.SaveChangesAsync();}
        using var known=await f.Client.GetAsync($"/api/v1/observability/alert-rules/{rule.Id}");known.EnsureSuccessStatusCode();var complete=(await known.Content.ReadFromJsonAsync<AlertRuleDto>())!;Assert.Equal("Known",complete.EvaluationState);Assert.Equal(b.ToUnixTimeMilliseconds(),complete.LastSuccessAt!.Value.ToUnixTimeMilliseconds());
    }
    [Theory] [InlineData("https://secret.invalid")] [InlineData("SMS")]
    public async Task NotificationUrlAndUnsupportedChannelRejected(string channel)
    {
        await using var f=await Fixture();using var r=await f.WriteAsync(HttpMethod.Post,"/api/v1/observability/alert-rules",Definition(f) with{Notification=new(true,[channel])});Assert.Equal(HttpStatusCode.UnprocessableEntity,r.StatusCode);
    }
    [Fact] public async Task ApiHealthDoesNotBorrowWholeEnvironmentHealth()
    {
        await using var f=await Fixture();var input=Definition(f) with{TargetType="Api",TargetId=f.Api.Id,Metric="unhealthy_destinations",Expression="unhealthy_destinations > 0"};using var r=await f.WriteAsync(HttpMethod.Post,"/api/v1/observability/alert-rules",input);Assert.Equal(HttpStatusCode.UnprocessableEntity,r.StatusCode);
    }
    [Fact] public async Task RemovedTargetRuleCanBeInspectedAndRepaired()
    {
        await using var f=await Fixture();var rule=await Create(f,Definition(f) with{TargetType="Destination",TargetId=f.Destination.Id});
        await using(var db=f.Context()){db.Remove(await db.Set<UpstreamDestination>().SingleAsync(x=>x.Id==f.Destination.Id));await db.SaveChangesAsync();}
        using var detail=await f.Client.GetAsync($"/api/v1/observability/alert-rules/{rule.Id}");Assert.Equal(HttpStatusCode.OK,detail.StatusCode);
        using var repair=await f.WriteAsync(HttpMethod.Put,$"/api/v1/observability/alert-rules/{rule.Id}",rule.Definition with{TargetType="Environment",TargetId=null},"\"1\"");Assert.Equal(HttpStatusCode.OK,repair.StatusCode);Assert.Equal(2,(await repair.Content.ReadFromJsonAsync<AlertRuleDto>())!.LogicRevision);
    }
    [Fact] public async Task LiveTestWorksWithoutMetricsPermissionOrIdempotencyKey()
    {
        var handler=new MetricProtocolHandler();await using var f=await Fixture(handler);handler.EnvironmentId=f.Environment.Id;handler.ApiId=f.Api.Id;handler.DestinationId=f.Destination.Id;handler.ApplicationId=f.Application.Id;handler.ClusterId=f.Cluster.Id;
        int audits;await using(var db=f.Context()){f.Application.OrganizationId=f.Organization.Id;f.Application.ProjectId=f.Project.Id;db.Add(f.Application);db.AddRange(new GatewayNode{EnvironmentId=f.Environment.Id,NodeName="node-0"},new GatewayNode{EnvironmentId=f.Environment.Id,NodeName="node-1"});await db.SaveChangesAsync();audits=await db.Set<AuditLog>().CountAsync();Assert.False(await db.Set<Permission>().AnyAsync(x=>x.Code=="metrics.read"));}
        using var request=new HttpRequestMessage(HttpMethod.Post,"/api/v1/observability/alert-rules/test"){Content=JsonContent.Create(Definition(f) with{Metric="request_rps",Expression="request_rps > 0"})};request.Headers.Add("X-CSRF-Token",await f.CsrfAsync());using var result=await f.Client.SendAsync(request);result.EnsureSuccessStatusCode();var value=(await result.Content.ReadFromJsonAsync<RuleTestDto>())!;Assert.Equal("Known",value.EvaluationState);Assert.True(value.Condition);Assert.InRange(value.Matches.Single().Values.Single().Value!.Value,0.66,0.67);
        await using var read=f.Context();Assert.Equal(0,await read.Set<AlertRule>().CountAsync());Assert.Equal(0,await read.Set<AlertEvent>().CountAsync());Assert.Equal(audits,await read.Set<AuditLog>().CountAsync());Assert.Equal(0,await read.Set<OutboxMessage>().CountAsync());
    }
    [Fact] public async Task RuleValidationAndNormalizedWideNameAreEnforced()
    {
        await using var f=await Fixture();var valid=Definition(f);
        foreach(var input in new[]{valid with{Name=""},valid with{Name=new string('x',129)},valid with{ForSeconds=-1},valid with{ForSeconds=86401},valid with{WindowSeconds=59},valid with{WindowSeconds=3601},valid with{Severity="Danger"},valid with{Expression="sum(rate(secret[5m])) > 0"},valid with{Notification=new(false,[])},valid with{TargetId=f.Api.Id},valid with{TargetType="Destination",TargetId=f.Destination.Id,EnvironmentId=null}})
        {using var invalid=await f.WriteAsync(HttpMethod.Post,"/api/v1/observability/alert-rules",input);Assert.Equal(HttpStatusCode.UnprocessableEntity,invalid.StatusCode);}
        await Create(f,Definition(f,true));using var duplicate=await f.WriteAsync(HttpMethod.Post,"/api/v1/observability/alert-rules",Definition(f,true) with{Name=" Ｅｒｒｏｒｓ "});Assert.Equal(HttpStatusCode.Conflict,duplicate.StatusCode);
    }
    [Fact] public async Task RevocationDeniesEditsButKeepsPersistedRule()
    {
        await using var f=await Fixture();var rule=await Create(f);await using(var db=f.Context()){db.RemoveRange(await db.Set<UserProjectScope>().ToArrayAsync());await db.SaveChangesAsync();}
        using var denied=await f.WriteAsync(HttpMethod.Put,$"/api/v1/observability/alert-rules/{rule.Id}",rule.Definition with{Name="Denied"},"\"1\"");Assert.Equal(HttpStatusCode.Forbidden,denied.StatusCode);
        await using var read=f.Context();Assert.Equal(1,await read.Set<AlertRule>().CountAsync());Assert.Equal(1,(await read.Set<AlertRule>().SingleAsync()).Revision);
    }
    [Fact] public async Task SourceFailureLiveTestReturnsUnknownWithoutSideEffects()
    {
        var handler=new MetricProtocolHandler{Status=503};await using var f=await Fixture(handler);using var response=await f.WriteAsync(HttpMethod.Post,"/api/v1/observability/alert-rules/test",Definition(f));Assert.Equal(HttpStatusCode.OK,response.StatusCode);var result=(await response.Content.ReadFromJsonAsync<RuleTestDto>())!;Assert.Equal("Unknown",result.EvaluationState);Assert.Null(result.Condition);Assert.Equal(WebApi.Contracts.Observability.SourceState.Unavailable,result.Matches.Single().Values.Single().State);
        await using var db=f.Context();Assert.Equal(0,await db.Set<AlertRule>().CountAsync());Assert.Equal(0,await db.Set<AlertEvent>().CountAsync());Assert.Equal(0,await db.Set<OutboxMessage>().CountAsync());
    }
    [Fact] public async Task StoppedWorkerCannotLeaveRuleEvaluationFalselyKnown()
    {
        await using var f=await Fixture();var rule=await Create(f);
        await using(var db=f.Context()){db.Add(new AlertEvaluationState{RuleId=rule.Id,LogicRevision=1,OrganizationId=f.Organization.Id,ProjectId=f.Project.Id,EnvironmentId=f.Environment.Id,ResourceType="Environment",ResourceKey="Environment",EvaluationState="Known",LastSuccessAt=DateTimeOffset.UtcNow.AddMinutes(-2),LastEvaluatedSlot=DateTimeOffset.UtcNow.AddMinutes(-1)});await db.SaveChangesAsync();}
        using var get=await f.Client.GetAsync($"/api/v1/observability/alert-rules/{rule.Id}");get.EnsureSuccessStatusCode();var value=(await get.Content.ReadFromJsonAsync<AlertRuleDto>())!;Assert.Equal("Unknown",value.EvaluationState);Assert.True(value.LastSuccessAt<DateTimeOffset.UtcNow.AddMinutes(-1));
    }
}
