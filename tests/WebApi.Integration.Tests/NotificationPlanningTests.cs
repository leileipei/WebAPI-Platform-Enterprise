using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Contracts.Alerts;
using WebApi.Contracts.Common;
using WebApi.Infrastructure.Alerts;
using WebApi.Infrastructure.Notifications;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Domain.Notifications;
using WebApi.Integration.Tests.Support;
using Xunit;
namespace WebApi.Integration.Tests;
public sealed class NotificationPlanningTests
{
    private static NotificationIntent Policy()=>new(true,["Email"],true,["First@example.test","Second@example.test"]);
    private static async Task<(ApiFixture Fixture,Guid Rule)> Setup(bool forcePolicy=true,bool channelEnabled=true)
    {
        var f=new ApiFixture();await f.InitializeAsync(builder=>builder.Services.AddSingleton(new NotificationDeploymentSettings(allowedDomains:["example.test"],smtpEndpoints:["smtp.example.test:587"],webhookUrls:["https://hook.example.test/notify"],consoleBaseUrl:new Uri("https://console.fixture.test/"))));await f.SeedCatalogAsync();await ObservationTestSupport.GrantAsync(f,["alert.read","alert.operate","alert.rule.manage"]);using var login=await f.LoginAsync();login.EnsureSuccessStatusCode();using var response=await f.WriteAsync(HttpMethod.Post,"/api/v1/observability/alert-rules",new SaveAlertRuleRequest(f.Organization.Id,f.Project.Id,f.Environment.Id,"Planning","request_rps","request_rps > 1","Warning",true,0,"Environment",null,60,Policy()));response.EnsureSuccessStatusCode();var rule=(await response.Content.ReadFromJsonAsync<AlertRuleDto>())!;
        await using(var db=f.Context())
        {
            if(forcePolicy)(await db.Set<AlertRule>().SingleAsync(x=>x.Id==rule.Id)).Notification=Encoding.UTF8.GetString(CanonicalJson.Serialize(Policy()));
            var profile=new NotificationChannelProfile{Channel="Email",ConfigurationHash=new string('a',64),PrivateConfiguration=Encoding.UTF8.GetString(CanonicalJson.Serialize(new NotificationProfileConfiguration("Email","smtp.example.test",587,"sender@example.test","StartTlsRequired",null,"vault://fixture/smtp"))),ProtectedSecretFingerprint="fixture-pin-unused-by-planner",CreatedBy=f.User.Id,SettingsRevision=1};db.Add(profile);var state=await db.Set<NotificationChannelState>().SingleAsync(x=>x.Channel=="Email");state.ProfileId=profile.Id;state.Enabled=channelEnabled;await db.SaveChangesAsync();
        }
        return(f,rule.Id);
    }
    private static AlertEvent Event(ApiFixture f,Guid rule)=>new(){RuleId=rule,RuleRevision=1,LogicRevision=1,OrganizationId=f.Organization.Id,ProjectId=f.Project.Id,EnvironmentId=f.Environment.Id,ResourceType="Environment",ResourceKey="Environment",OccurrenceNo=1,Message="Threshold",RuleSummary="request_rps > 1",EvaluationState="Known",LastCondition=true};
    private static AlertEventTransition Transition(AlertEvent e,string to="Open",string? from=null,string reason="Triggered",Guid? actor=null)=>new(){EventId=e.Id,FromStatus=from,ToStatus=to,Reason=reason,ActorId=actor};
    private static async Task<AlertEvent> Trigger(ApiFixture f,Guid rule)
    {
        using var services=f.Services();var db=services.ServiceProvider.GetRequiredService<WebApiDbContext>();await using var tx=await db.Database.BeginTransactionAsync();await Lock(db,rule);var e=Event(f,rule);var transition=Transition(e);db.AddRange(e,transition);await services.ServiceProvider.GetRequiredService<NotificationPlanner>().OnTransitionAsync(e,transition);await db.SaveChangesAsync();await tx.CommitAsync();return e;
    }
    private static async Task Lock(WebApiDbContext db,Guid rule)
    {await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(8901202)");await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM alert_rules WHERE id={rule} FOR UPDATE");}
    [Theory][InlineData(false,"Recovery")][InlineData(true,"Manual")]
    public async Task FrozenResolvedMessageDistinguishesManualClosureFromRecovery(bool manual,string closureKind)
    {
        var setup=await Setup();await using var f=setup.Fixture;var e=await Trigger(f,setup.Rule);await FinishParents(f,e.Id);
        using(var services=f.Services()){var db=services.ServiceProvider.GetRequiredService<WebApiDbContext>();await using var tx=await db.Database.BeginTransactionAsync();await Lock(db,setup.Rule);var alert=await db.Set<AlertEvent>().SingleAsync(x=>x.Id==e.Id);alert.Status="Resolved";var transition=Transition(alert,"Resolved","Open",manual?"private investigation reason":"ConditionRecovered",manual?f.User.Id:null);db.Add(transition);await services.ServiceProvider.GetRequiredService<NotificationPlanner>().OnTransitionAsync(alert,transition);await db.SaveChangesAsync();await tx.CommitAsync();}
        await using var read=f.Context();var rows=await read.Set<NotificationDelivery>().Where(x=>x.TriggeredDeliveryId!=null).ToArrayAsync();Assert.Equal(2,rows.Length);foreach(var row in rows){using var body=JsonDocument.Parse(row.Payload);Assert.Equal("Resolved",body.RootElement.GetProperty("transition").GetString());Assert.True(body.RootElement.TryGetProperty("closureKind",out var kind));Assert.Equal(closureKind,kind.GetString());Assert.DoesNotContain("private investigation reason",Encoding.UTF8.GetString(row.Payload));}
    }
    [Fact] public async Task RuleSavePreservesExternalPolicyAndValidatesDeploymentRecipients()
    {
        var setup=await Setup(forcePolicy:false);await using var f=setup.Fixture;using var detail=await f.Client.GetAsync("/api/v1/observability/alert-rules/"+setup.Rule);var rule=(await detail.Content.ReadFromJsonAsync<AlertRuleDto>())!;Assert.True(rule.Definition.Notification.ExternalEnabled);Assert.Equal(2,rule.Definition.Notification.EmailRecipients!.Count);
        using var forbidden=await f.WriteAsync(HttpMethod.Put,"/api/v1/observability/alert-rules/"+setup.Rule,rule.Definition with{Notification=Policy() with{EmailRecipients=["outside@unauthorized.test"]}},"\"1\"");Assert.Equal(HttpStatusCode.UnprocessableEntity,forbidden.StatusCode);
    }
    [Fact] public async Task EventAndTasksCommitOrRollbackTogether()
    {
        var setup=await Setup();await using var f=setup.Fixture;
        using(var services=f.Services())
        {var db=services.ServiceProvider.GetRequiredService<WebApiDbContext>();await using var tx=await db.Database.BeginTransactionAsync();await Lock(db,setup.Rule);var e=Event(f,setup.Rule);var transition=Transition(e);db.AddRange(e,transition);await services.ServiceProvider.GetRequiredService<NotificationPlanner>().OnTransitionAsync(e,transition);await db.SaveChangesAsync();Assert.Equal(2,await db.Set<NotificationDelivery>().CountAsync());await tx.RollbackAsync();}
        await using(var read=f.Context()){Assert.Empty(await read.Set<NotificationDelivery>().ToArrayAsync());Assert.Empty(await read.Set<AlertEvent>().ToArrayAsync());}
        var committed=await Trigger(f,setup.Rule);await using var final=f.Context();Assert.Equal(2,await final.Set<NotificationDelivery>().CountAsync(x=>x.EventId==committed.Id));
    }
    [Fact] public async Task RepeatedTransitionCreatesOneTaskPerTarget()
    {
        var setup=await Setup();await using var f=setup.Fixture;using var services=f.Services();var db=services.ServiceProvider.GetRequiredService<WebApiDbContext>();await using var tx=await db.Database.BeginTransactionAsync();await Lock(db,setup.Rule);var e=Event(f,setup.Rule);var transition=Transition(e);db.AddRange(e,transition);var planner=services.ServiceProvider.GetRequiredService<NotificationPlanner>();await planner.OnTransitionAsync(e,transition);await planner.OnTransitionAsync(e,transition);await db.SaveChangesAsync();await tx.CommitAsync();var deliveries=await db.Set<NotificationDelivery>().ToArrayAsync();Assert.Equal(2,deliveries.Length);Assert.Equal(2,deliveries.Select(x=>x.TargetHash).Distinct().Count());Assert.All(deliveries,x=>Assert.Equal("Queued",x.Status));Assert.Contains("First@example.test",e.FrozenNotification);
    }
    [Fact] public async Task DisabledChannelCreatesSuppressionWithoutActivatingIt()
    {var setup=await Setup(channelEnabled:false);await using var f=setup.Fixture;await Trigger(f,setup.Rule);await using var db=f.Context();var rows=await db.Set<NotificationDelivery>().ToArrayAsync();Assert.Equal(2,rows.Length);Assert.All(rows,x=>{Assert.Equal("Suppressed",x.Status);Assert.Equal("ChannelDisabled",x.Reason);});Assert.False((await db.Set<NotificationChannelState>().SingleAsync(x=>x.Channel=="Email")).Enabled);}
    [Fact] public async Task NotificationEditDoesNotResetLogicOrRetargetOldOccurrence()
    {
        var setup=await Setup();await using var f=setup.Fixture;var e=await Trigger(f,setup.Rule);string frozen;
        await using(var db=f.Context()){frozen=(await db.Set<AlertEvent>().SingleAsync()).FrozenNotification;Assert.True(JsonSerializer.Deserialize<NotificationFrozenSnapshot>(frozen,CanonicalJson.Options)!.Policy.ExternalEnabled);var planned=await db.Set<NotificationDelivery>().ToArrayAsync();Assert.Equal(2,planned.Length);Assert.Contains(planned,x=>x.Target=="First@example.test");db.Add(new AlertEvaluationState{RuleId=setup.Rule,LogicRevision=1,OrganizationId=f.Organization.Id,ProjectId=f.Project.Id,EnvironmentId=f.Environment.Id,ResourceType="Environment",ResourceKey="Environment",Phase="Firing",LastEventId=e.Id,NextOccurrenceNo=2});await db.SaveChangesAsync();}
        using var detail=await f.Client.GetAsync("/api/v1/observability/alert-rules/"+setup.Rule);var rule=(await detail.Content.ReadFromJsonAsync<AlertRuleDto>())!;using var edit=await f.WriteAsync(HttpMethod.Put,"/api/v1/observability/alert-rules/"+setup.Rule,rule.Definition with{Notification=Policy() with{EmailRecipients=["New@example.test"]}},"\"1\"");edit.EnsureSuccessStatusCode();Assert.Equal(1,(await edit.Content.ReadFromJsonAsync<AlertRuleDto>())!.LogicRevision);
        await using var read=f.Context();Assert.Equal(frozen,(await read.Set<AlertEvent>().SingleAsync()).FrozenNotification);Assert.Equal("Firing",(await read.Set<AlertEvaluationState>().SingleAsync()).Phase);Assert.DoesNotContain(await read.Set<NotificationDelivery>().ToArrayAsync(),x=>x.Target=="New@example.test");
    }
    private static async Task FinishParents(ApiFixture f,Guid e,string status="Accepted",string outcome="Accepted")
    {await using var db=f.Context();foreach(var delivery in await db.Set<NotificationDelivery>().Where(x=>x.EventId==e&&x.TriggeredDeliveryId==null).ToArrayAsync()){delivery.Status=status;delivery.AttemptCount=1;delivery.LeaseToken=1;delivery.CompletedAt=DateTimeOffset.UtcNow;db.Add(new NotificationDeliveryAttempt{DeliveryId=delivery.Id,AttemptNo=1,LeaseToken=1,StartedAt=delivery.CreatedAt,CompletedAt=delivery.CompletedAt,Outcome=outcome,Code=outcome});}await db.SaveChangesAsync();}
    [Theory][InlineData("Open",false)][InlineData("Silenced",true)]
    public async Task SilencedResolvedAndManualClosureAreDifferent(string from,bool silenced)
    {
        var setup=await Setup();await using var f=setup.Fixture;var e=await Trigger(f,setup.Rule);await FinishParents(f,e.Id);
        using(var services=f.Services())
        {var db=services.ServiceProvider.GetRequiredService<WebApiDbContext>();await using var tx=await db.Database.BeginTransactionAsync();await Lock(db,setup.Rule);var alert=await db.Set<AlertEvent>().SingleAsync(x=>x.Id==e.Id);alert.Status="Resolved";alert.ResolvedBy=f.User.Id;alert.ResolvedAt=DateTimeOffset.UtcNow;var transition=Transition(alert,"Resolved",from,"Manual investigation closed",f.User.Id);db.Add(transition);await services.ServiceProvider.GetRequiredService<NotificationPlanner>().OnTransitionAsync(alert,transition);await db.SaveChangesAsync();await tx.CommitAsync();}
        await using var read=f.Context();var resolved=await read.Set<NotificationDelivery>().Where(x=>x.TriggeredDeliveryId!=null).ToArrayAsync();Assert.Equal(2,resolved.Length);Assert.All(resolved,x=>{Assert.Equal(silenced?"Suppressed":"Queued",x.Status);Assert.Equal(silenced?"SuppressedSilenced":"ManualClosure",x.Reason);Assert.DoesNotContain("Healthy",Encoding.UTF8.GetString(x.Payload));});
    }
    [Theory][InlineData("Accepted","Accepted")][InlineData("RetryScheduled","OutcomeUnknown")]
    public async Task ResolveDuringSendingSurvivesRestart(string finished,string outcome)
    {
        var setup=await Setup();await using var f=setup.Fixture;var e=await Trigger(f,setup.Rule);Guid parent;
        await using(var db=f.Context()){var rows=await db.Set<NotificationDelivery>().ToArrayAsync();Assert.Equal(2,rows.Length);var p=rows[0];parent=p.Id;p.Status="Sending";p.AttemptCount=1;p.LeaseToken=1;p.LeaseOwner="fixture-worker";p.LeaseUntil=DateTimeOffset.UtcNow.AddSeconds(60);db.Add(new NotificationDeliveryAttempt{DeliveryId=p.Id,AttemptNo=1,LeaseToken=1,StartedAt=DateTimeOffset.UtcNow});await db.SaveChangesAsync();}
        using(var services=f.Services())
        {var db=services.ServiceProvider.GetRequiredService<WebApiDbContext>();await using var tx=await db.Database.BeginTransactionAsync();await Lock(db,setup.Rule);var alert=await db.Set<AlertEvent>().SingleAsync(x=>x.Id==e.Id);alert.Status="Resolved";alert.ResolvedAt=DateTimeOffset.UtcNow;var transition=Transition(alert,"Resolved","Open","ConditionRecovered");db.Add(transition);await services.ServiceProvider.GetRequiredService<NotificationPlanner>().OnTransitionAsync(alert,transition);await db.SaveChangesAsync();await tx.CommitAsync();Assert.Empty(await db.Set<NotificationDelivery>().Where(x=>x.TriggeredDeliveryId!=null).ToArrayAsync());}
        DateTimeOffset completed;await using(var db=f.Context()){var p=await db.Set<NotificationDelivery>().SingleAsync(x=>x.Id==parent);p.Status=finished;p.LeaseOwner=null;p.LeaseUntil=null;p.CompletedAt=completed=DateTimeOffset.UtcNow;var attempt=await db.Set<NotificationDeliveryAttempt>().SingleAsync();attempt.CompletedAt=completed;attempt.Outcome=outcome;attempt.Code=outcome;await db.SaveChangesAsync();}
        using(var restarted=f.Services()){var planner=restarted.ServiceProvider.GetRequiredService<NotificationPlanner>();Assert.True(await planner.CoordinateNextResolvedAsync());await planner.CoordinateResolvedAsync(e.Id);}
        await using var read=f.Context();var paired=Assert.Single(await read.Set<NotificationDelivery>().Where(x=>x.TriggeredDeliveryId!=null).ToArrayAsync());Assert.Equal(parent,paired.TriggeredDeliveryId);Assert.True(paired.CreatedAt>=completed);Assert.Equal("Queued",paired.Status);Assert.Equal("Suppressed",(await read.Set<NotificationDelivery>().SingleAsync(x=>x.EventId==e.Id&&x.Id!=parent&&x.TriggeredDeliveryId==null)).Status);
    }
    [Fact] public async Task ActualSilenceAndUnsilenceUpdateExistingTasksWithoutDuplication()
    {
        var setup=await Setup();await using var f=setup.Fixture;var e=await Trigger(f,setup.Rule);using var silence=await f.WriteAsync(HttpMethod.Post,$"/api/v1/observability/alerts/{e.Id}/silence",new AlertAction("Silence","Maintenance",null,900),"\"1\"");silence.EnsureSuccessStatusCode();await using(var db=f.Context()){var rows=await db.Set<NotificationDelivery>().ToArrayAsync();Assert.Equal(2,rows.Length);Assert.All(rows,x=>Assert.Equal("Paused",x.Status));}
        using var unsilence=await f.WriteAsync(HttpMethod.Post,$"/api/v1/observability/alerts/{e.Id}/unsilence",new AlertAction("Unsilence",null,null),"\"2\"");unsilence.EnsureSuccessStatusCode();await using var read=f.Context();var restored=await read.Set<NotificationDelivery>().ToArrayAsync();Assert.Equal(2,restored.Length);Assert.All(restored,x=>Assert.Equal("Queued",x.Status));
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task RuleDisableOrScopeChangeCancelsUnstartedTasksWithoutRecoveryMessages(bool moveScope)
    {
        var setup=await Setup();await using var f=setup.Fixture;var e=await Trigger(f,setup.Rule);using var detail=await f.Client.GetAsync("/api/v1/observability/alert-rules/"+setup.Rule);var definition=(await detail.Content.ReadFromJsonAsync<AlertRuleDto>())!.Definition;
        if(moveScope){await using var target=f.Context();var next=new EnvironmentRecord{ProjectId=f.Project.Id,Code="MOVE",Name="New scope"};target.Add(next);await target.SaveChangesAsync();definition=definition with{EnvironmentId=next.Id};}else definition=definition with{Enabled=false};
        using var edit=await f.WriteAsync(HttpMethod.Put,"/api/v1/observability/alert-rules/"+setup.Rule,definition,"\"1\"");edit.EnsureSuccessStatusCode();var expected=moveScope?"RuleChanged":"RuleDisabled";await using var db=f.Context();Assert.Equal(expected,(await db.Set<AlertEvent>().SingleAsync(x=>x.Id==e.Id)).ResolveReason);var rows=await db.Set<NotificationDelivery>().ToArrayAsync();Assert.Equal(2,rows.Length);Assert.All(rows,x=>{Assert.Equal("Suppressed",x.Status);Assert.Equal(expected,x.Reason);Assert.Null(x.TriggeredDeliveryId);});
    }
    [Fact] public async Task OldOccurrenceIsNeverBackfilled()
    {
        var setup=await Setup();await using var f=setup.Fixture;Guid id;await using(var db=f.Context()){var e=Event(f,setup.Rule);id=e.Id;e.Status="Resolved";e.ResolvedAt=DateTimeOffset.UtcNow;db.AddRange(e,Transition(e,"Resolved","Open","ConditionRecovered"));await db.SaveChangesAsync();}
        using var services=f.Services();await services.ServiceProvider.GetRequiredService<NotificationPlanner>().CoordinateResolvedAsync(id);await using var read=f.Context();Assert.Empty(await read.Set<NotificationDelivery>().ToArrayAsync());
    }
    [Theory][InlineData("NotifyOff")][InlineData("RuleChanged")]
    public async Task TerminalUnpairedOccurrencesDoNotStarveRecoverableEvents(string kind)
    {
        var setup=await Setup();await using var f=setup.Fixture;var current=await Trigger(f,setup.Rule);await FinishParents(f,current.Id);
        await using(var db=f.Context())
        {
            var profile=(await db.Set<NotificationChannelState>().SingleAsync(x=>x.Channel=="Email")).ProfileId;var now=DateTimeOffset.UtcNow;
            for(var i=0;i<51;i++)
            {
                var e=Event(f,setup.Rule);e.OccurrenceNo=i+2;e.Status="Resolved";e.ResolvedAt=now.AddHours(-1).AddMilliseconds(i);e.FrozenNotification=Encoding.UTF8.GetString(CanonicalJson.Serialize(new NotificationFrozenSnapshot(Policy() with{NotifyRecovery=kind!="NotifyOff"},profile,null,kind=="RuleChanged"?"Governance":"Recovery")));
                var trigger=Transition(e);var resolved=Transition(e,"Resolved","Open",kind=="NotifyOff"?"ConditionRecovered":"RuleChanged");db.AddRange(e,trigger,resolved);
                var parent=new NotificationDelivery{EventId=e.Id,TransitionId=trigger.Id,OrganizationId=e.OrganizationId,ProjectId=e.ProjectId,EnvironmentId=e.EnvironmentId,Channel="Email",Target="First@example.test",TargetHash=Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("First@example.test"))),ProfileId=profile,Status="Accepted",AttemptCount=1,LeaseToken=1,CreatedAt=e.ResolvedAt.Value,CompletedAt=e.ResolvedAt.Value,ExpiresAt=e.ResolvedAt.Value.AddDays(1),Payload=NotificationPayload.Alert(e.Id,e.OccurrenceNo,e.EnvironmentId,e.Severity,"request_rps","request_rps > 1","Triggered",e.StartedAt,new Uri("https://console.fixture.test/"))};db.Add(parent);db.Add(new NotificationDeliveryAttempt{DeliveryId=parent.Id,AttemptNo=1,LeaseToken=1,StartedAt=parent.CreatedAt,CompletedAt=parent.CompletedAt,Outcome="Accepted",Code="SmtpAccepted",ProtocolStatus=250});
            }
            var alert=await db.Set<AlertEvent>().SingleAsync(x=>x.Id==current.Id);alert.Status="Resolved";alert.ResolvedAt=now;db.Add(Transition(alert,"Resolved","Open","ConditionRecovered"));await db.SaveChangesAsync();
        }
        using(var restarted=f.Services())Assert.True(await restarted.ServiceProvider.GetRequiredService<NotificationPlanner>().CoordinateNextResolvedAsync());
        await using var read=f.Context();Assert.Equal(2,await read.Set<NotificationDelivery>().CountAsync(x=>x.TriggeredDeliveryId!=null&&x.EventId==current.Id));Assert.Equal(2,await read.Set<NotificationDelivery>().CountAsync(x=>x.TriggeredDeliveryId!=null));
    }
    [Fact] public async Task ActualSilenceExpiryRestoresOriginalTasks()
    {
        var setup=await Setup();await using var f=setup.Fixture;var e=await Trigger(f,setup.Rule);using var silence=await f.WriteAsync(HttpMethod.Post,$"/api/v1/observability/alerts/{e.Id}/silence",new AlertAction("Silence","Maintenance",null,900),"\"1\"");silence.EnsureSuccessStatusCode();Guid[] ids;await using(var db=f.Context()){ids=await db.Set<NotificationDelivery>().Select(x=>x.Id).OrderBy(x=>x).ToArrayAsync();(await db.Set<AlertEvent>().SingleAsync()).SilencedUntil=DateTimeOffset.UtcNow.AddMinutes(-1);await db.SaveChangesAsync();}
        using(var services=f.Services())Assert.Equal(1,await services.ServiceProvider.GetRequiredService<AlertSilenceExpiryService>().ExpireAsync(default));await using var read=f.Context();Assert.Equal(ids,await read.Set<NotificationDelivery>().Select(x=>x.Id).OrderBy(x=>x).ToArrayAsync());Assert.All(await read.Set<NotificationDelivery>().ToArrayAsync(),x=>Assert.Equal("Queued",x.Status));
    }
    [Theory][InlineData("RuleChanged")][InlineData("RuleDisabled")][InlineData("ResourceRetired")][InlineData("Triggered")]
    public async Task ManualReasonTextCannotMasqueradeAsGovernanceClosure(string reason)
    {
        var setup=await Setup();await using var f=setup.Fixture;var e=await Trigger(f,setup.Rule);await FinishParents(f,e.Id);using var response=await f.WriteAsync(HttpMethod.Post,$"/api/v1/observability/alerts/{e.Id}/resolve",new AlertAction("Resolve",reason,null),"\"1\"");response.EnsureSuccessStatusCode();await using var db=f.Context();var rows=await db.Set<NotificationDelivery>().Where(x=>x.TriggeredDeliveryId!=null).ToArrayAsync();Assert.Equal(2,rows.Length);Assert.All(rows,x=>{Assert.Equal("Queued",x.Status);Assert.Equal("ManualClosure",x.Reason);});
    }
    [Fact] public async Task AckDuringSilenceLeavesNotificationTasksUnchanged()
    {
        var setup=await Setup();await using var f=setup.Fixture;var e=await Trigger(f,setup.Rule);using var silence=await f.WriteAsync(HttpMethod.Post,$"/api/v1/observability/alerts/{e.Id}/silence",new AlertAction("Silence","Maintenance",null,900),"\"1\"");silence.EnsureSuccessStatusCode();(Guid Id,long Revision,string Status)[] original;
        await using(var db=f.Context())original=(await db.Set<NotificationDelivery>().OrderBy(x=>x.Id).ToArrayAsync()).Select(x=>(x.Id,x.Revision,x.Status)).ToArray();
        using var ack=await f.WriteAsync(HttpMethod.Post,$"/api/v1/observability/alerts/{e.Id}/ack",new AlertAction("Ack",null,null),"\"2\"");ack.EnsureSuccessStatusCode();await using var read=f.Context();Assert.Equal(original,(await read.Set<NotificationDelivery>().OrderBy(x=>x.Id).ToArrayAsync()).Select(x=>(x.Id,x.Revision,x.Status)).ToArray());
    }
    [Fact] public async Task RuleTestWithExternalIntentCreatesNoDeliveryOrEvent()
    {
        var setup=await Setup(forcePolicy:false);await using var f=setup.Fixture;using var detail=await f.Client.GetAsync("/api/v1/observability/alert-rules/"+setup.Rule);var definition=(await detail.Content.ReadFromJsonAsync<AlertRuleDto>())!.Definition;Assert.True(definition.Notification.ExternalEnabled);using var result=await f.WriteAsync(HttpMethod.Post,"/api/v1/observability/alert-rules/test",definition);result.EnsureSuccessStatusCode();await using var db=f.Context();Assert.Empty(await db.Set<NotificationDelivery>().ToArrayAsync());Assert.Empty(await db.Set<AlertEvent>().ToArrayAsync());Assert.Empty(await db.Set<OutboxMessage>().ToArrayAsync());
    }
    [Fact] public async Task WebhookPlansOneIndependentTargetAndRecoveryKeepsFrozenChannels()
    {
        var setup=await Setup();await using var f=setup.Fixture;await using(var db=f.Context())
        {
            var rule=await db.Set<AlertRule>().SingleAsync(x=>x.Id==setup.Rule);rule.Notification=Encoding.UTF8.GetString(CanonicalJson.Serialize(Policy() with{RequestedChannels=["Email","Webhook"]}));var profile=new NotificationChannelProfile{Channel="Webhook",ConfigurationHash=new string('b',64),PrivateConfiguration=Encoding.UTF8.GetString(CanonicalJson.Serialize(new NotificationProfileConfiguration("Webhook",null,null,null,null,"https://hook.example.test/notify","vault://fixture/webhook"))),ProtectedSecretFingerprint="fixture-pin-unused-by-planner",CreatedBy=f.User.Id,SettingsRevision=1};db.Add(profile);var state=await db.Set<NotificationChannelState>().SingleAsync(x=>x.Channel=="Webhook");state.Enabled=true;state.ProfileId=profile.Id;await db.SaveChangesAsync();
        }
        var e=await Trigger(f,setup.Rule);await using(var db=f.Context()){var rows=await db.Set<NotificationDelivery>().ToArrayAsync();Assert.Equal(3,rows.Length);Assert.All(rows,x=>Assert.Equal("Queued",x.Status));Assert.Equal("https://hook.example.test/notify",Assert.Single(rows,x=>x.Channel=="Webhook").Target);}
        await FinishParents(f,e.Id);using var detail=await f.Client.GetAsync("/api/v1/observability/alert-rules/"+setup.Rule);var definition=(await detail.Content.ReadFromJsonAsync<AlertRuleDto>())!.Definition;using var edit=await f.WriteAsync(HttpMethod.Put,"/api/v1/observability/alert-rules/"+setup.Rule,definition with{Notification=Policy() with{EmailRecipients=["New@example.test"]}},"\"1\"");edit.EnsureSuccessStatusCode();using var resolve=await f.WriteAsync(HttpMethod.Post,$"/api/v1/observability/alerts/{e.Id}/resolve",new AlertAction("Resolve","Manual closure",null),"\"1\"");resolve.EnsureSuccessStatusCode();await using var read=f.Context();var paired=await read.Set<NotificationDelivery>().Where(x=>x.TriggeredDeliveryId!=null).ToArrayAsync();Assert.Equal(3,paired.Length);Assert.Equal("https://hook.example.test/notify",Assert.Single(paired,x=>x.Channel=="Webhook").Target);Assert.DoesNotContain(paired,x=>x.Target=="New@example.test");
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task PlanningUsesActualActiveScopeAndNotCreatorGrants(bool inactiveEnvironment)
    {
        var setup=await Setup();await using var f=setup.Fixture;await using(var db=f.Context()){db.RemoveRange(await db.Set<UserProjectScope>().Where(x=>x.UserId==f.User.Id).ToArrayAsync());if(inactiveEnvironment)(await db.Set<EnvironmentRecord>().SingleAsync(x=>x.Id==f.Environment.Id)).Status="Archived";await db.SaveChangesAsync();}
        var e=await Trigger(f,setup.Rule);await using var read=f.Context();var rows=await read.Set<NotificationDelivery>().ToArrayAsync();Assert.Equal(2,rows.Length);Assert.All(rows,x=>{Assert.Equal(inactiveEnvironment?"Suppressed":"Queued",x.Status);Assert.Equal(inactiveEnvironment?"ScopeInactive":null,x.Reason);Assert.Equal(e.EnvironmentId,x.EnvironmentId);Assert.Equal(e.ProjectId,x.ProjectId);Assert.Equal(e.OrganizationId,x.OrganizationId);});
    }
}
