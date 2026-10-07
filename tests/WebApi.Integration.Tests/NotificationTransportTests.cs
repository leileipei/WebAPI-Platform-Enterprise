using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using WebApi.Contracts.Common;
using WebApi.Contracts.Notifications;
using WebApi.Domain.Notifications;
using WebApi.Infrastructure.Notifications;
using WebApi.Integration.Tests.Support;
using Xunit;
namespace WebApi.Integration.Tests;
public sealed class NotificationTransportTests
{
    private sealed class Environment:IHostEnvironment
    {public string EnvironmentName {get;set;}="Development";public string ApplicationName {get;set;}="transport-tests";public string ContentRootPath {get;set;}="/tmp";public IFileProvider ContentRootFileProvider {get;set;}=new NullFileProvider();}
    private sealed class Dns(params IPAddress[] addresses):INotificationDnsResolver
    {
        public int Calls {get;private set;}
        public Task<IPAddress[]> ResolveAsync(string host,CancellationToken ct){ct.ThrowIfCancellationRequested();Calls++;return Task.FromResult(Calls==1?addresses:[IPAddress.Parse("169.254.169.254")]);}
    }
    private sealed class Clock:TimeProvider
    {public DateTimeOffset Now=DateTimeOffset.UtcNow;public override DateTimeOffset GetUtcNow()=>Now;}
    private static NotificationDeploymentSettings Settings(NotificationTransportFixture fixture,string smtpHost="smtp.fixture.test",Uri? hook=null,bool trust=true,string environment="Development",bool enabled=true,string? caFile=null)
    {
        var values=new Dictionary<string,string?>{{"Notifications:AllowedSmtpEndpoints:0",smtpHost+":"+fixture.Smtp.Port},{"Notifications:AllowedWebhookUrls:0",(hook??fixture.WebhookUrl).AbsoluteUri},{"Notifications:AllowedRecipientDomains:0","example.test"},{"Notifications:AllowedPrivateCidrs:0","127.0.0.0/8"},{"Notifications:FixtureEnabled",enabled.ToString()}};
        if(trust)values["Notifications:FixtureCaFile"]=caFile??fixture.RootCertificatePath;
        return NotificationDeploymentSettings.Read(new ConfigurationBuilder().AddInMemoryCollection(values).Build(),new Environment{EnvironmentName=environment});
    }
    private static byte[] Body()=>NotificationPayload.Test(new Uri("https://console.fixture.test/"),DateTimeOffset.UtcNow);
    private static async Task<NotificationSendEnvelope> Smtp(NotificationTransportFixture fixture,string recipient="User@example.test",string security="StartTlsRequired",string host="smtp.fixture.test",Guid? id=null)
    =>new(id??Guid.NewGuid(),Body(),host,fixture.Smtp.Port,await fixture.SecretAsync(NotificationChannel.Email),security,"sender@example.test",recipient);
    private static async Task<NotificationSendEnvelope> Hook(NotificationTransportFixture fixture,Guid? id=null,byte[]? body=null,Uri? url=null)
    {var endpoint=url??fixture.WebhookUrl;return new(id??Guid.NewGuid(),body??Body(),endpoint.IdnHost,endpoint.Port,await fixture.SecretAsync(NotificationChannel.Webhook),url:endpoint);}
    [Fact] public async Task SmtpUsesValidatedSocketRequiredTlsAndOneRecipient()
    {
        await using var fixture=new NotificationTransportFixture();await fixture.InitializeAsync();var settings=Settings(fixture);var dns=new Dns(IPAddress.Loopback);var transport=new SmtpNotificationTransport(settings,new NotificationAddressPolicy(settings,dns));var envelope=await Smtp(fixture);
        var result=await transport.SendAsync(envelope);Assert.Equal(DeliveryOutcome.Accepted,result.Outcome);Assert.Equal(1,dns.Calls);var receipt=Assert.Single(fixture.Smtp.Receipts,x=>x.DeliveryId==envelope.DeliveryId);Assert.True(receipt.Tls);Assert.True(receipt.Authenticated);Assert.Equal(1,receipt.RecipientCount);Assert.Equal(0,receipt.CcCount);Assert.Equal(0,receipt.BccCount);Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(envelope.Body)),receipt.BodyHash);
    }
    [Fact] public async Task SmtpDataAcceptedThenDisconnectedIsUnknown()
    {
        await using var fixture=new NotificationTransportFixture();await fixture.InitializeAsync();fixture.Smtp.SetMode("AcceptThenDisconnect");var settings=Settings(fixture);var transport=new SmtpNotificationTransport(settings,new NotificationAddressPolicy(settings,new Dns(IPAddress.Loopback)));var envelope=await Smtp(fixture);
        var result=await transport.SendAsync(envelope);Assert.Equal(DeliveryOutcome.OutcomeUnknown,result.Outcome);var receipt=Assert.Single(fixture.Smtp.Receipts,x=>x.DeliveryId==envelope.DeliveryId);Assert.True(receipt.Tls&&receipt.Authenticated&&receipt.Accepted);Assert.Null(receipt.ReplyCode);
    }
    [Theory][InlineData("Temporary",DeliveryOutcome.TransientFailure,451)][InlineData("Permanent",DeliveryOutcome.PermanentFailure,550)][InlineData("AcceptThenQuitDisconnect",DeliveryOutcome.Accepted,250)]
    public async Task SmtpExplicitReplyAndQuitResults(string mode,DeliveryOutcome outcome,int status)
    {
        await using var fixture=new NotificationTransportFixture();await fixture.InitializeAsync();fixture.Smtp.SetMode(mode);var settings=Settings(fixture);var transport=new SmtpNotificationTransport(settings,new NotificationAddressPolicy(settings,new Dns(IPAddress.Loopback)));var envelope=await Smtp(fixture);var result=await transport.SendAsync(envelope);Assert.Equal(outcome,result.Outcome);Assert.Equal(status,result.ProtocolStatus);var receipt=Assert.Single(fixture.Smtp.Receipts,x=>x.DeliveryId==envelope.DeliveryId);Assert.True(receipt.Tls&&receipt.Authenticated);Assert.Equal(status,receipt.ReplyCode);
    }
    [Fact] public async Task TwentyRecipientsAreTwentyDistinctEnvelopes()
    {
        await using var fixture=new NotificationTransportFixture();await fixture.InitializeAsync();var settings=Settings(fixture);var transport=new SmtpNotificationTransport(settings,new NotificationAddressPolicy(settings,new NotificationDnsFixed()));
        for(var i=0;i<20;i++)Assert.Equal(DeliveryOutcome.Accepted,(await transport.SendAsync(await Smtp(fixture,"Recipient"+i+"@example.test"))).Outcome);
        Assert.Equal(20,fixture.Smtp.Receipts.Count);Assert.Equal(20,fixture.Smtp.Receipts.Select(x=>x.DeliveryId).Distinct().Count());Assert.All(fixture.Smtp.Receipts,x=>{Assert.True(x.Tls&&x.Authenticated);Assert.Equal(1,x.RecipientCount);Assert.Equal(0,x.CcCount+x.BccCount);});
    }
    private sealed class NotificationDnsFixed:INotificationDnsResolver
    {public Task<IPAddress[]> ResolveAsync(string host,CancellationToken ct)=>Task.FromResult(new[]{IPAddress.Loopback});}
    [Fact] public async Task WebhookSignatureReplayAndExactBody()
    {
        await using var fixture=new NotificationTransportFixture();await fixture.InitializeAsync();var settings=Settings(fixture);var clock=new Clock();var transport=new WebhookNotificationTransport(settings,new NotificationAddressPolicy(settings,new NotificationDnsFixed()),clock);var envelope=await Hook(fixture);
        Assert.Equal(DeliveryOutcome.Accepted,(await transport.SendAsync(envelope)).Outcome);clock.Now=clock.Now.AddSeconds(2);Assert.Equal(DeliveryOutcome.Accepted,(await transport.SendAsync(envelope)).Outcome);
        Assert.Equal(2,fixture.Webhook.Receipts.Count);Assert.Equal(1,fixture.Webhook.BusinessAcceptCount);Assert.True(fixture.Webhook.Receipts[1].Duplicate);Assert.NotEqual(fixture.Webhook.Receipts[0].Timestamp,fixture.Webhook.Receipts[1].Timestamp);Assert.All(fixture.Webhook.Receipts,x=>{Assert.True(x.Tls&&x.SignatureValid);Assert.Equal(envelope.DeliveryId,x.DeliveryId);Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(envelope.Body)),x.BodyHash);});
    }
    [Theory][InlineData("Temporary",DeliveryOutcome.TransientFailure,500)][InlineData("Permanent",DeliveryOutcome.PermanentFailure,400)][InlineData("RateLimited",DeliveryOutcome.TransientFailure,429)][InlineData("Redirect",DeliveryOutcome.PermanentFailure,307)][InlineData("Disconnect",DeliveryOutcome.OutcomeUnknown,null)]
    public async Task WebhookProtocolResultsAreSafeAndDoNotFollowRedirect(string mode,DeliveryOutcome outcome,int? status)
    {
        await using var fixture=new NotificationTransportFixture();await fixture.InitializeAsync();fixture.Webhook.SetMode(mode,"7200");var settings=Settings(fixture);var dns=new Dns(IPAddress.Loopback);var transport=new WebhookNotificationTransport(settings,new NotificationAddressPolicy(settings,dns),TimeProvider.System);var envelope=await Hook(fixture);var result=await transport.SendAsync(envelope);Assert.Equal(outcome,result.Outcome);Assert.Equal(status,result.ProtocolStatus);Assert.Equal(1,dns.Calls);var receipt=Assert.Single(fixture.Webhook.Receipts);Assert.True(receipt.Tls&&receipt.SignatureValid);if(mode=="RateLimited")Assert.Equal(TimeSpan.FromSeconds(7200),result.RetryAfter);
    }
    [Fact] public async Task TimeoutAndBodyReadAreBounded()
    {
        await using var fixture=new NotificationTransportFixture();await fixture.InitializeAsync();fixture.Webhook.SetMode("Large");var settings=Settings(fixture);var transport=new WebhookNotificationTransport(settings,new NotificationAddressPolicy(settings,new NotificationDnsFixed()),TimeProvider.System);var result=await transport.SendAsync(await Hook(fixture));Assert.Equal(DeliveryOutcome.Accepted,result.Outcome);Assert.InRange(transport.LastResponseBytes,0,4096);Assert.True(Assert.Single(fixture.Webhook.Receipts).SignatureValid);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task ActualSendHasTenSecondTotalBudget(bool email)
    {
        await using var fixture=new NotificationTransportFixture();await fixture.InitializeAsync();fixture.Webhook.SetMode("Delay");fixture.Smtp.SetMode("Delay");var settings=Settings(fixture);var address=new NotificationAddressPolicy(settings,new NotificationDnsFixed());INotificationTransport transport=email?new SmtpNotificationTransport(settings,address):new WebhookNotificationTransport(settings,address,TimeProvider.System);var envelope=email?await Smtp(fixture):await Hook(fixture);var watch=Stopwatch.StartNew();var result=await transport.SendAsync(envelope);watch.Stop();Assert.Equal(DeliveryOutcome.OutcomeUnknown,result.Outcome);Assert.InRange(watch.Elapsed.TotalSeconds,9,12);
        if(!email)Assert.True(Assert.Single(fixture.Webhook.Receipts).Tls&&fixture.Webhook.Receipts[0].SignatureValid);
    }
    [Fact] public async Task SmtpTlsOnConnectAndNoStartTlsFallback()
    {
        await using(var fixture=new NotificationTransportFixture())
        {await fixture.InitializeAsync(tlsOnConnect:true);var settings=Settings(fixture);var transport=new SmtpNotificationTransport(settings,new NotificationAddressPolicy(settings,new NotificationDnsFixed()));Assert.Equal(DeliveryOutcome.Accepted,(await transport.SendAsync(await Smtp(fixture,security:"TlsOnConnect"))).Outcome);Assert.True(Assert.Single(fixture.Smtp.Receipts).Tls);}
        await using(var fixture=new NotificationTransportFixture())
        {await fixture.InitializeAsync(advertiseStartTls:false);var settings=Settings(fixture);var transport=new SmtpNotificationTransport(settings,new NotificationAddressPolicy(settings,new NotificationDnsFixed()));Assert.Equal(DeliveryOutcome.PermanentFailure,(await transport.SendAsync(await Smtp(fixture))).Outcome);Assert.DoesNotContain(fixture.Smtp.Receipts,x=>x.Authenticated||x.Accepted);}
    }
    [Fact] public async Task HostnameMismatchNotAllowedByFixtureCa()
    {
        await using var fixture=new NotificationTransportFixture();await fixture.InitializeAsync();var settings=Settings(fixture,"wrong.fixture.test",new Uri("https://wrong.fixture.test:"+fixture.WebhookUrl.Port+"/notify"));var smtp=new SmtpNotificationTransport(settings,new NotificationAddressPolicy(settings,new NotificationDnsFixed()));Assert.Equal(DeliveryOutcome.PermanentFailure,(await smtp.SendAsync(await Smtp(fixture,host:"wrong.fixture.test"))).Outcome);
        var hook=new WebhookNotificationTransport(settings,new NotificationAddressPolicy(settings,new NotificationDnsFixed()),TimeProvider.System);Assert.Equal(DeliveryOutcome.PermanentFailure,(await hook.SendAsync(await Hook(fixture,url:new Uri("https://wrong.fixture.test:"+fixture.WebhookUrl.Port+"/notify")))).Outcome);Assert.Empty(fixture.Webhook.Receipts);
    }
    [Fact] public async Task DnsRebindingMixedAnswersAndRedirectRejected()
    {
        await using var fixture=new NotificationTransportFixture();await fixture.InitializeAsync();var settings=Settings(fixture);var dns=new Dns(IPAddress.Loopback,IPAddress.Parse("169.254.169.254"));var addresses=new NotificationAddressPolicy(settings,dns);await Assert.ThrowsAsync<ApiException>(()=>addresses.ResolveAsync(NotificationChannel.Email,"smtp.fixture.test",fixture.Smtp.Port,null,CancellationToken.None));Assert.Equal(1,dns.Calls);Assert.Empty(fixture.Smtp.Receipts);
        fixture.Webhook.SetMode("Redirect");var onlyOnce=new Dns(IPAddress.Loopback);var hook=new WebhookNotificationTransport(settings,new NotificationAddressPolicy(settings,onlyOnce),TimeProvider.System);Assert.Equal(DeliveryOutcome.PermanentFailure,(await hook.SendAsync(await Hook(fixture))).Outcome);Assert.Equal(1,onlyOnce.Calls);Assert.Single(fixture.Webhook.Receipts);
    }
    [Fact] public async Task FixtureTrustRequiresExplicitDevelopmentConfiguration()
    {
        await using var fixture=new NotificationTransportFixture();await fixture.InitializeAsync();Assert.Throws<InvalidOperationException>(()=>Settings(fixture,environment:"Production"));Assert.Throws<InvalidOperationException>(()=>Settings(fixture,enabled:false));var settings=Settings(fixture,trust:false);var hook=new WebhookNotificationTransport(settings,new NotificationAddressPolicy(settings,new NotificationDnsFixed()),TimeProvider.System);Assert.Equal(DeliveryOutcome.PermanentFailure,(await hook.SendAsync(await Hook(fixture))).Outcome);Assert.Empty(fixture.Webhook.Receipts);
    }
    [Theory]
    [InlineData("0.0.0.0")][InlineData("169.254.169.254")][InlineData("224.0.0.1")][InlineData("255.255.255.255")][InlineData("::")][InlineData("fe80::1")][InlineData("ff02::1")][InlineData("::ffff:169.254.169.254")][InlineData("100.100.100.200")][InlineData("fd00:ec2::254")]
    public async Task NeverAllowedAddressesRemainRejectedEvenWithBroadPrivateCidrs(string value)
    {
        var settings=new NotificationDeploymentSettings(smtpEndpoints:["smtp.fixture.test:25"],privateCidrs:["0.0.0.0/0","::/0"]);var policy=new NotificationAddressPolicy(settings,new Dns(IPAddress.Parse(value)));await Assert.ThrowsAsync<ApiException>(()=>policy.ResolveAsync(NotificationChannel.Email,"smtp.fixture.test",25,null,CancellationToken.None));
    }
    [Theory][InlineData("127.0.0.1")][InlineData("10.1.2.3")][InlineData("172.31.1.2")][InlineData("192.168.1.2")][InlineData("100.64.1.2")][InlineData("::1")][InlineData("fd12::1")][InlineData("::ffff:127.0.0.1")]
    public async Task PrivateDestinationsRequireExplicitCidr(string value)
    {var settings=new NotificationDeploymentSettings(smtpEndpoints:["smtp.fixture.test:25"]);var policy=new NotificationAddressPolicy(settings,new Dns(IPAddress.Parse(value)));await Assert.ThrowsAsync<ApiException>(()=>policy.ResolveAsync(NotificationChannel.Email,"smtp.fixture.test",25,null,CancellationToken.None));}
    [Fact] public async Task UnknownTlsModeAndWrongAuthenticationArePermanentWithoutSending()
    {
        await using var fixture=new NotificationTransportFixture();await fixture.InitializeAsync();var settings=Settings(fixture);var dns=new Dns(IPAddress.Loopback);var transport=new SmtpNotificationTransport(settings,new NotificationAddressPolicy(settings,dns));Assert.Equal(DeliveryOutcome.PermanentFailure,(await transport.SendAsync(await Smtp(fixture,security:"Auto"))).Outcome);Assert.Equal(0,dns.Calls);Assert.Empty(fixture.Smtp.Receipts);
        var good=await Smtp(fixture);var bad=new NotificationSendEnvelope(good.DeliveryId,good.Body,good.Host,good.Port,new NotificationSecret("wrong-user","wrong-password"),good.Security,good.From,good.Target);Assert.Equal(DeliveryOutcome.PermanentFailure,(await transport.SendAsync(bad)).Outcome);Assert.DoesNotContain(fixture.Smtp.Receipts,x=>x.Authenticated||x.Accepted);
    }
    [Theory][InlineData("999999999999999999999999999")][InlineData("1200")][InlineData("not-a-date")]
    public async Task RetryAfterOverflowIsNotIgnoredOrShortened(string value)
    {
        await using var fixture=new NotificationTransportFixture();await fixture.InitializeAsync();fixture.Webhook.SetMode("RateLimited",value);var settings=Settings(fixture);var transport=new WebhookNotificationTransport(settings,new NotificationAddressPolicy(settings,new NotificationDnsFixed()),TimeProvider.System);var result=await transport.SendAsync(await Hook(fixture));Assert.Equal(DeliveryOutcome.TransientFailure,result.Outcome);Assert.True(Assert.Single(fixture.Webhook.Receipts).SignatureValid);
        if(value=="not-a-date")Assert.Null(result.RetryAfter);else {Assert.True(result.RetryAfter>=TimeSpan.FromSeconds(1200));var decision=NotificationDecisionMachine.NextAttemptAt(NotificationRetryPolicy.Default,1,DateTimeOffset.UtcNow,DateTimeOffset.UtcNow.AddDays(1),result.RetryAfter,0);if(value.Length>20){Assert.Equal(DeliveryStatus.Failed,decision.Status);Assert.Equal("RetryAfterExceedsBudget",decision.Reason);}else Assert.Equal(DeliveryStatus.RetryScheduled,decision.Status);}
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task ExplicitFixtureCaStillRejectsUntrustedOrExpiredCertificate(bool expired)
    {
        await using var fixture=new NotificationTransportFixture();await fixture.InitializeAsync(expiredCertificate:expired);await using var other=new NotificationTransportFixture();await other.InitializeAsync();var settings=Settings(fixture,caFile:expired?fixture.RootCertificatePath:other.RootCertificatePath);var address=new NotificationAddressPolicy(settings,new NotificationDnsFixed());
        Assert.Equal(DeliveryOutcome.PermanentFailure,(await new SmtpNotificationTransport(settings,address).SendAsync(await Smtp(fixture))).Outcome);Assert.Equal(DeliveryOutcome.PermanentFailure,(await new WebhookNotificationTransport(settings,address,TimeProvider.System).SendAsync(await Hook(fixture))).Outcome);Assert.DoesNotContain(fixture.Smtp.Receipts,x=>x.Authenticated||x.Accepted);Assert.Empty(fixture.Webhook.Receipts);
    }
    [Fact] public async Task CallerCancellationTerminatesActualHttpsAttempt()
    {
        await using var fixture=new NotificationTransportFixture();await fixture.InitializeAsync();fixture.Webhook.SetMode("Delay");var settings=Settings(fixture);var transport=new WebhookNotificationTransport(settings,new NotificationAddressPolicy(settings,new NotificationDnsFixed()),TimeProvider.System);using var cancel=new CancellationTokenSource();var send=transport.SendAsync(await Hook(fixture),cancel.Token);var deadline=DateTimeOffset.UtcNow.AddSeconds(3);while(fixture.Webhook.Receipts.Count==0&&DateTimeOffset.UtcNow<deadline)await Task.Delay(10);Assert.True(Assert.Single(fixture.Webhook.Receipts).SignatureValid);var watch=Stopwatch.StartNew();cancel.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>send);Assert.InRange(watch.Elapsed.TotalSeconds,0,2);
    }
}
