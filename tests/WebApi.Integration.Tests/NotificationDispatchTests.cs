using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Contracts.Alerts;
using WebApi.Contracts.Notifications;
using WebApi.Infrastructure.Notifications;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using WebApi.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WebApi.Worker.Workers;
using Xunit;
namespace WebApi.Integration.Tests;
public sealed class NotificationDispatchTests
{
    private static async Task<DeliveryLease?> Begin(NotificationScenario scenario,string owner)
    {using var services=scenario.Api.Services();return await services.ServiceProvider.GetRequiredService<NotificationDeliveryStore>().TryBeginAsync(owner);}
    private static async Task<bool> Complete(NotificationScenario scenario,DeliveryLease lease,TransportResult result)
    {using var services=scenario.Api.Services();return await services.ServiceProvider.GetRequiredService<NotificationDeliveryStore>().CompleteAsync(lease,result);}
    // Reusing the pre-disable profile would release these old tasks after re-enable.
    [Theory][InlineData("Queued")][InlineData("RetryScheduled")][InlineData("Paused")]
    public async Task ChannelReenableCannotReviveOldGeneration(string status)
    {
        await using var scenario=new NotificationScenario();await scenario.InitializeAsync();await scenario.AlertAsync();Guid? original;DateTimeOffset deadline;
        await using(var db=scenario.Api.Context()){var row=await db.Set<NotificationDelivery>().SingleAsync();row.Status=status;if(status=="RetryScheduled"){row.AttemptCount=1;row.NextAttemptAt=DateTimeOffset.UtcNow.AddMinutes(10);}original=row.ProfileId;deadline=row.ExpiresAt;await db.SaveChangesAsync();}
        object Values(bool enabled)=>new{smtpHost="smtp.fixture.test",smtpPort=scenario.Transport.Smtp.Port,fromEmail="sender@example.test",smtpSecretRef=new{operation="Keep"},webhookUrl=scenario.Transport.WebhookUrl.AbsoluteUri,webhookSecretRef=new{operation="Keep"},smtpEnabled=enabled,webhookEnabled=true,smtpSecurity="StartTlsRequired"};
        using(var disabled=await SystemSettingsFixture.SaveAsync(scenario.Api,"notification",Values(false)))disabled.EnsureSuccessStatusCode();
        using(var enabled=await SystemSettingsFixture.SaveAsync(scenario.Api,"notification",Values(true)))enabled.EnsureSuccessStatusCode();
        await using(var db=scenario.Api.Context()){Assert.NotEqual(original,(await db.Set<NotificationChannelState>().SingleAsync(x=>x.Channel=="Email")).ProfileId);var row=await db.Set<NotificationDelivery>().SingleAsync();Assert.Equal(original,row.ProfileId);Assert.Equal(deadline,row.ExpiresAt);if(status=="RetryScheduled")Assert.Equal(1,row.AttemptCount);using var services=scenario.Api.Services();var readiness=await services.ServiceProvider.GetRequiredService<NotificationDeliveryStore>().ReadinessAsync(row,default);Assert.Equal("Suppressed",readiness.Status);Assert.Equal("ProfileChanged",readiness.Reason);}
        Assert.Empty(scenario.Transport.Smtp.Receipts);
    }
    [Fact] public async Task MaintenanceRecoveryFencesInterruptedAttemptWithoutAutomaticResendOrNewBudget()
    {
        await using var scenario=new NotificationScenario();await scenario.InitializeAsync();await scenario.AlertAsync();var lease=Assert.IsType<DeliveryLease>(await Begin(scenario,"stopped-worker"));await scenario.AlertAsync();DateTimeOffset deadline;int max;
        await using(var db=scenario.Api.Context()){var row=await db.Set<NotificationDelivery>().SingleAsync(x=>x.Id==lease.Id);deadline=row.ExpiresAt;max=row.MaxAttempts;}
        var root=new DirectoryInfo(AppContext.BaseDirectory);while(root is not null&&!File.Exists(Path.Combine(root.FullName,"Directory.Build.props")))root=root.Parent;Assert.NotNull(root);var file=Path.Combine(root.FullName,"scripts","notification-approval","drain.sql");Assert.True(File.Exists(file),"Maintenance must have a non-sending recovery operation");
        await using(var db=scenario.Api.Context()){var sql=await File.ReadAllTextAsync(file);await Assert.ThrowsAnyAsync<Exception>(()=>db.Database.ExecuteSqlRawAsync(sql));sql="BEGIN; SET LOCAL webapi.notification_workers_stopped='confirmed';\n"+sql+"\nCOMMIT;";await db.Database.ExecuteSqlRawAsync(sql);await db.Database.ExecuteSqlRawAsync(sql);var row=await db.Set<NotificationDelivery>().SingleAsync(x=>x.Id==lease.Id);Assert.Equal("Failed",row.Status);Assert.Equal("MaintenanceInterrupted",row.Reason);Assert.Equal(1,row.AttemptCount);Assert.Equal(max,row.MaxAttempts);Assert.Equal(deadline,row.ExpiresAt);Assert.True(row.LeaseToken>lease.Token);Assert.Null(row.LeaseOwner);Assert.Null(row.LeaseUntil);var attempt=await db.Set<NotificationDeliveryAttempt>().SingleAsync(x=>x.DeliveryId==row.Id);Assert.Equal("OutcomeUnknown",attempt.Outcome);Assert.Equal("MaintenanceInterrupted",attempt.Code);Assert.NotNull(attempt.CompletedAt);Assert.Single(await db.Set<AuditLog>().Where(x=>x.Action=="notification.delivery").ToArrayAsync(),x=>x.AfterJson!.Contains("MaintenanceInterrupted"));Assert.Equal(1,await db.Set<NotificationDelivery>().CountAsync(x=>x.Status=="Queued"));}
        Assert.False(await Complete(scenario,lease,new(DeliveryOutcome.Accepted,"LateReceipt",250)));Assert.Empty(scenario.Transport.Smtp.Receipts);
    }
    [Theory][InlineData(false,"Recovery")][InlineData(true,"Manual")]
    public async Task ActualSmtpAndSignedWebhookCarryFrozenClosureClassification(bool manual,string kind)
    {
        await using var scenario=new NotificationScenario();await scenario.InitializeAsync();var id=await scenario.AlertAsync(["Email","Webhook"]);
        async Task Dispatch(){using var services=scenario.Api.Services();Assert.True(await services.ServiceProvider.GetRequiredService<NotificationDispatcher>().DispatchNextAsync("closure-wire"));}
        await Dispatch();await Dispatch();
        if(manual){using var response=await scenario.Api.WriteAsync(System.Net.Http.HttpMethod.Post,$"/api/v1/observability/alerts/{id}/resolve",new AlertAction("Resolve","private free-text excluded",null),"\"1\"");response.EnsureSuccessStatusCode();}
        else{using var services=scenario.Api.Services();var db=services.ServiceProvider.GetRequiredService<WebApi.Infrastructure.Persistence.WebApiDbContext>();await using var tx=await db.Database.BeginTransactionAsync();await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(8901202)");var alert=await db.Set<AlertEvent>().SingleAsync(x=>x.Id==id);await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM alert_rules WHERE id={alert.RuleId} FOR UPDATE");alert.Status="Resolved";alert.ResolvedAt=DateTimeOffset.UtcNow;var transition=new AlertEventTransition{EventId=id,FromStatus="Open",ToStatus="Resolved",Reason="ConditionRecovered"};db.Add(transition);await services.ServiceProvider.GetRequiredService<NotificationPlanner>().OnTransitionAsync(alert,transition);await db.SaveChangesAsync();await tx.CommitAsync();}
        await Dispatch();await Dispatch();await using var read=scenario.Api.Context();var resolved=await read.Set<NotificationDelivery>().Where(x=>x.EventId==id&&x.TriggeredDeliveryId!=null).ToArrayAsync();Assert.Equal(2,resolved.Length);
        foreach(var row in resolved){Assert.Equal("Accepted",row.Status);using var body=System.Text.Json.JsonDocument.Parse(row.Payload);Assert.Equal(kind,body.RootElement.GetProperty("closureKind").GetString());Assert.DoesNotContain("private free-text excluded",System.Text.Encoding.UTF8.GetString(row.Payload));var hash=Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(row.Payload));if(row.Channel=="Email"){var receipt=Assert.Single(scenario.Transport.Smtp.Receipts,x=>x.DeliveryId==row.Id);Assert.True(receipt.Accepted);Assert.Equal(hash,receipt.BodyHash);}else{var receipt=Assert.Single(scenario.Transport.Webhook.Receipts,x=>x.DeliveryId==row.Id);Assert.True(receipt.RemoteAccepted&&receipt.SignatureValid);Assert.Equal(hash,receipt.BodyHash);}}
    }
    [Fact] public async Task TwoWorkersCannotOwnSameLiveAttempt()
    {
        await using var scenario=new NotificationScenario();await scenario.InitializeAsync();await scenario.AlertAsync();var leases=await Task.WhenAll(Begin(scenario,"worker-a"),Begin(scenario,"worker-b"));var lease=Assert.Single(leases,x=>x is not null)!;await using var db=scenario.Api.Context();var row=await db.Set<NotificationDelivery>().SingleAsync();Assert.Equal("Sending",row.Status);Assert.Equal(lease.Token,row.LeaseToken);Assert.Equal(1,row.AttemptCount);Assert.Equal(1,await db.Set<NotificationDeliveryAttempt>().CountAsync());var attempt=await db.Set<NotificationDeliveryAttempt>().SingleAsync();Assert.InRange((row.LeaseUntil!.Value-attempt.StartedAt).TotalSeconds,59.99,60.01);
    }
    [Fact] public async Task ExpiredLeaseCannotOverwriteNewReceipt()
    {
        await using var scenario=new NotificationScenario();await scenario.InitializeAsync();await scenario.AlertAsync();var first=Assert.IsType<DeliveryLease>(await Begin(scenario,"old"));await using(var db=scenario.Api.Context()){var row=await db.Set<NotificationDelivery>().SingleAsync();row.LeaseUntil=DateTimeOffset.UtcNow.AddSeconds(-1);await db.SaveChangesAsync();}Assert.Null(await Begin(scenario,"recovery"));
        await using(var db=scenario.Api.Context()){var row=await db.Set<NotificationDelivery>().SingleAsync();Assert.Equal("OutcomeUnknown",(await db.Set<NotificationDeliveryAttempt>().SingleAsync()).Outcome);Assert.Equal("RetryScheduled",row.Status);row.NextAttemptAt=DateTimeOffset.UtcNow.AddSeconds(-1);await db.SaveChangesAsync();}
        var second=Assert.IsType<DeliveryLease>(await Begin(scenario,"new"));Assert.True(second.Token>first.Token);Assert.Equal(2,second.AttemptNo);Assert.True(await Complete(scenario,second,new(DeliveryOutcome.Accepted,"SmtpAccepted",250)));Assert.False(await Complete(scenario,first,new(DeliveryOutcome.PermanentFailure,"SmtpPermanentFailure",550)));await using var read=scenario.Api.Context();Assert.Equal("Accepted",(await read.Set<NotificationDelivery>().SingleAsync()).Status);Assert.Equal(2,await read.Set<NotificationDeliveryAttempt>().CountAsync());
    }
    [Fact] public async Task CrashAfterRemoteAcceptRecordsUnknownAndRecovers()
    {
        await using var scenario=new NotificationScenario();await scenario.InitializeAsync();var e=await scenario.AlertAsync();var lease=Assert.IsType<DeliveryLease>(await Begin(scenario,"crashed-worker"));
        using(var services=scenario.Api.Services()){var store=services.ServiceProvider.GetRequiredService<NotificationDeliveryStore>();var envelope=Assert.IsType<NotificationSendEnvelope>(await store.PrepareAsync(lease));var settings=services.ServiceProvider.GetRequiredService<NotificationDeploymentSettings>();var addresses=new NotificationAddressPolicy(settings,services.ServiceProvider.GetRequiredService<INotificationDnsResolver>());Assert.Equal(DeliveryOutcome.Accepted,(await new SmtpNotificationTransport(settings,addresses).SendAsync(envelope)).Outcome);Assert.True(Assert.Single(scenario.Transport.Smtp.Receipts).Authenticated);}
        // No receipt writeback: recreate the service after the remote acceptance.
        using var resolve=await scenario.Api.WriteAsync(HttpMethod.Post,$"/api/v1/observability/alerts/{e}/resolve",new AlertAction("Resolve","After remote acceptance",null),"\"1\"");resolve.EnsureSuccessStatusCode();await using(var db=scenario.Api.Context()){var row=await db.Set<NotificationDelivery>().SingleAsync();row.LeaseUntil=DateTimeOffset.UtcNow.AddSeconds(-1);await db.SaveChangesAsync();}Assert.Null(await Begin(scenario,"restart"));
        using(var services=scenario.Api.Services()){await services.ServiceProvider.GetRequiredService<NotificationPlanner>().CoordinateNextResolvedAsync();await services.ServiceProvider.GetRequiredService<NotificationPlanner>().CoordinateResolvedAsync(e);}
        await using var read=scenario.Api.Context();Assert.Equal("OutcomeUnknown",(await read.Set<NotificationDeliveryAttempt>().SingleAsync()).Outcome);var paired=Assert.Single(await read.Set<NotificationDelivery>().Where(x=>x.TriggeredDeliveryId!=null).ToArrayAsync());Assert.Equal(lease.Id,paired.TriggeredDeliveryId);Assert.Equal("Queued",paired.Status);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task SilenceBeforeDispatchBlocksAndAfterDispatchStopsOnlyFutureAttempts(bool began)
    {
        await using var scenario=new NotificationScenario();await scenario.InitializeAsync();var e=await scenario.AlertAsync();var lease=began?Assert.IsType<DeliveryLease>(await Begin(scenario,"before-silence")):null;using var silence=await scenario.Api.WriteAsync(HttpMethod.Post,$"/api/v1/observability/alerts/{e}/silence",new AlertAction("Silence","Pause",null,900),"\"1\"");silence.EnsureSuccessStatusCode();if(lease is not null)Assert.True(await Complete(scenario,lease,new(DeliveryOutcome.TransientFailure,"SmtpTemporaryFailure",451)));Assert.Null(await Begin(scenario,"after-silence"));await using var read=scenario.Api.Context();var row=await read.Set<NotificationDelivery>().SingleAsync();Assert.Equal("Paused",row.Status);Assert.Equal(began?1:0,row.AttemptCount);Assert.Empty(scenario.Transport.Smtp.Receipts);
    }
    [Fact] public async Task AttemptsStopAtOriginalMaximumWithoutResettingExpiry()
    {
        await using var scenario=new NotificationScenario();await scenario.InitializeAsync();await scenario.AlertAsync();DateTimeOffset deadline;await using(var db=scenario.Api.Context())deadline=(await db.Set<NotificationDelivery>().SingleAsync()).ExpiresAt;
        for(var i=1;i<=5;i++){var lease=Assert.IsType<DeliveryLease>(await Begin(scenario,"retry-"+i));Assert.Equal(i,lease.AttemptNo);Assert.True(await Complete(scenario,lease,new(DeliveryOutcome.TransientFailure,"SmtpTemporaryFailure",451)));await using var db=scenario.Api.Context();var row=await db.Set<NotificationDelivery>().SingleAsync();Assert.Equal(deadline,row.ExpiresAt);if(i<5){row.NextAttemptAt=DateTimeOffset.UtcNow.AddSeconds(-1);await db.SaveChangesAsync();}}
        Assert.Null(await Begin(scenario,"sixth"));await using var read=scenario.Api.Context();var final=await read.Set<NotificationDelivery>().SingleAsync();Assert.Equal("Failed",final.Status);Assert.Equal("AttemptsExhausted",final.Reason);Assert.Equal(5,await read.Set<NotificationDeliveryAttempt>().CountAsync());
    }
    [Fact] public async Task MissingProtectionKeysPausesRatherThanDeclaringSecretReplacement()
    {
        await using var scenario=new NotificationScenario();await scenario.InitializeAsync();await scenario.AlertAsync();var emptyKeys=Path.Combine(Path.GetDirectoryName(scenario.Transport.RootCertificatePath)!,"empty-worker-keys");Directory.CreateDirectory(emptyKeys);
        using var worker=WorkerApp.Build(["--environment","Development"],builder=>{builder.Configuration["ConnectionStrings:WebApi"]=scenario.Api.Database.ConnectionString;scenario.Transport.ConfigureNotifications(builder.Configuration);builder.Configuration["DataProtection:KeysDirectory"]=emptyKeys;builder.Configuration["DataProtection:ApplicationName"]="NotificationScenario";builder.Logging.ClearProviders();});
        using(var scope=worker.Services.CreateScope())Assert.Null(await scope.ServiceProvider.GetRequiredService<NotificationDeliveryStore>().TryBeginAsync("missing-key-worker"));
        await using var read=scenario.Api.Context();var row=await read.Set<NotificationDelivery>().SingleAsync();Assert.Equal("Paused",row.Status);Assert.Equal("ProtectionUnavailable",row.Reason);Assert.Equal(0,row.AttemptCount);Assert.Empty(Directory.GetFiles(emptyKeys,"*.xml"));Assert.Empty(scenario.Transport.Smtp.Receipts);
    }
    private sealed class BarrierTransport:INotificationTransport
    {
        private int started,active,maximum;
        public TaskCompletionSource FourStarted {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Maximum=>maximum;
        public async Task<TransportResult> SendAsync(NotificationSendEnvelope envelope,CancellationToken ct=default)
        {
            var count=Interlocked.Increment(ref active);int previous;do{previous=maximum;if(previous>=count)break;}while(Interlocked.CompareExchange(ref maximum,count,previous)!=previous);
            if(Interlocked.Increment(ref started)==4)FourStarted.TrySetResult();
            try{await Release.Task.WaitAsync(ct);return new(DeliveryOutcome.Accepted,"FixtureAccepted",202);}finally{Interlocked.Decrement(ref active);}
        }
    }
    [Fact] public async Task WorkerRunsFourImmediateSendsWithoutHoldingLifecycleLocksOrLeasingTheFifth()
    {
        var barrier=new BarrierTransport();await using var scenario=new NotificationScenario();await scenario.InitializeAsync(configure:builder=>builder.Services.AddSingleton<INotificationTransport>(barrier));var events=new List<Guid>();for(var i=0;i<5;i++)events.Add(await scenario.AlertAsync());
        using var scope=scenario.Api.Services();using var worker=new NotificationDeliveryWorker(scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>(),scope.ServiceProvider.GetRequiredService<NotificationDeploymentSettings>(),NullLogger<NotificationDeliveryWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await barrier.FourStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));await using(var db=scenario.Api.Context()){Assert.Equal(4,await db.Set<NotificationDelivery>().CountAsync(x=>x.Status=="Sending"));Assert.Equal(1,await db.Set<NotificationDelivery>().CountAsync(x=>x.Status=="Queued"));Assert.Equal(4,await db.Set<NotificationDeliveryAttempt>().CountAsync());}
            Guid sendingEvent;await using(var db=scenario.Api.Context())sendingEvent=(await db.Set<NotificationDelivery>().FirstAsync(x=>x.Status=="Sending")).EventId!.Value;
            using var silence=await scenario.Api.WriteAsync(HttpMethod.Post,$"/api/v1/observability/alerts/{sendingEvent}/silence",new AlertAction("Silence","During send",null,900),"\"1\"");silence.EnsureSuccessStatusCode();barrier.Release.TrySetResult();
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));while(true){await using var db=scenario.Api.Context();if(await db.Set<NotificationDelivery>().CountAsync(x=>x.Status=="Accepted",timeout.Token)==5)break;await Task.Delay(25,timeout.Token);}
            Assert.Equal(4,barrier.Maximum);await using var read=scenario.Api.Context();Assert.Equal(5,await read.Set<NotificationDeliveryAttempt>().CountAsync());
        }
        finally{barrier.Release.TrySetResult();await worker.StopAsync(CancellationToken.None);}
    }
    [Fact] public async Task PausedTasksExpireWithoutTakingAnotherAttempt()
    {
        await using var scenario=new NotificationScenario();await scenario.InitializeAsync();var e=await scenario.AlertAsync();using var silence=await scenario.Api.WriteAsync(HttpMethod.Post,$"/api/v1/observability/alerts/{e}/silence",new AlertAction("Silence","Pause",null,900),"\"1\"");silence.EnsureSuccessStatusCode();
        await using(var db=scenario.Api.Context()){var row=await db.Set<NotificationDelivery>().SingleAsync();row.ExpiresAt=DateTimeOffset.UtcNow.AddMinutes(-1);row.CreatedAt=row.ExpiresAt.AddMinutes(-row.ExpiresAfterMinutes);await db.SaveChangesAsync();}Assert.Null(await Begin(scenario,"expiry"));await using var read=scenario.Api.Context();var expired=await read.Set<NotificationDelivery>().SingleAsync();Assert.Equal("Expired",expired.Status);Assert.Equal("DeadlineExceeded",expired.Reason);Assert.Equal(0,expired.AttemptCount);
    }
    [Fact] public async Task ReplacingSecretFileCannotRetargetAnExistingMessage()
    {
        await using var scenario=new NotificationScenario();await scenario.InitializeAsync();await scenario.AlertAsync();var path=Path.Combine(Path.GetDirectoryName(scenario.Transport.RootCertificatePath)!,"smtp.json");await File.WriteAllTextAsync(path,System.Text.Json.JsonSerializer.Serialize(new{username="replacement",password="different-secret"}));Assert.Null(await Begin(scenario,"rotation"));await using var db=scenario.Api.Context();var row=await db.Set<NotificationDelivery>().SingleAsync();Assert.Equal("Suppressed",row.Status);Assert.Equal("SecretVersionChanged",row.Reason);Assert.Equal(0,row.AttemptCount);Assert.Empty(scenario.Transport.Smtp.Receipts);
    }

}
