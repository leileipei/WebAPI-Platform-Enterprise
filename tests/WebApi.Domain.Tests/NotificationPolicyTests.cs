using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WebApi.Contracts.Alerts;
using WebApi.Contracts.Common;
using WebApi.Contracts.Notifications;
using WebApi.Domain.Notifications;
using Xunit;
namespace WebApi.Domain.Tests;

public sealed class NotificationPolicyTests
{
    private static readonly DateTimeOffset Now=new(2026,10,8,8,0,0,TimeSpan.Zero);
    private static NotificationIntent Intent(NotificationRetryPolicy? retry=null)=>new(true,["Email","Webhook"],true,["Ops@example.test"],true,retry);
    private static NotificationMessageV1 Message()=>new(1,"Alert",Guid.Parse("10000000-0000-4000-8000-000000000001"),1,Guid.Parse("20000000-0000-4000-8000-000000000002"),"Warning","request_rps > 10","Triggered",Now,"https://console.example.test/observability/alerts?eventId=10000000-0000-4000-8000-000000000001");
    [Fact] public void LegacyIntentDefaultsToExternalOff()
    {
        var value=NotificationPolicyValidator.Normalize(NotificationPolicyValidator.Parse("{\"inConsole\":true,\"requestedChannels\":[\"Email\"]}"));
        Assert.True(value.InConsole);Assert.False(value.ExternalEnabled);Assert.True(value.NotifyRecovery);Assert.Empty(value.EmailRecipients!);Assert.Equal(NotificationRetryPolicy.Default,value.RetryPolicy);
    }
    [Theory]
    [InlineData(0,30,900,1440)][InlineData(6,30,900,1440)][InlineData(5,0,900,1440)][InlineData(5,301,900,1440)]
    [InlineData(5,30,29,1440)][InlineData(5,30,3601,1440)][InlineData(5,30,900,4)][InlineData(5,30,900,1441)]
    public void RetryBudgetsRejectEdges(int attempts,int start,int max,int expiry)=>Assert.Throws<ApiException>(()=>NotificationPolicyValidator.Normalize(Intent(new(attempts,start,max,expiry))));
    [Theory][InlineData(1,1,1,5)][InlineData(5,300,3600,1440)]
    public void RetryBudgetBoundariesAreAccepted(int attempts,int start,int max,int expiry)
    {var policy=new NotificationRetryPolicy(attempts,start,max,expiry);Assert.Equal(policy,NotificationPolicyValidator.Normalize(Intent(policy)).RetryPolicy);}
    [Fact] public void TwentyRecipientsAreSeparateAndTwentyFirstRejected()
    {
        var recipients=Enumerable.Range(1,20).Select(i=>"ops"+i+"@example.test").ToArray();var value=NotificationPolicyValidator.Normalize(Intent() with {EmailRecipients=recipients});Assert.Equal(20,value.EmailRecipients!.Count);
        Assert.Throws<ApiException>(()=>NotificationPolicyValidator.Normalize(Intent() with {EmailRecipients=[..recipients,"extra@example.test"]}));
    }
    [Fact] public void DuplicateChannelsAndNormalizedRecipientsAreRejected()
    {
        Assert.Throws<ApiException>(()=>NotificationPolicyValidator.Normalize(Intent() with {RequestedChannels=["Email","Email"]}));
        Assert.Throws<ApiException>(()=>NotificationPolicyValidator.Normalize(Intent() with {EmailRecipients=["Ops@EXAMPLE.test","Ops@example.test"]}));
    }
    [Fact] public void IdnDomainsNormalizeAndLocalPartsKeepTheirCase()
    {
        Assert.Equal("Ops@xn--fsqu00a.xn--0zwm56d",NotificationPolicyValidator.NormalizeEmail("Ops@例子.测试"));
        var value=NotificationPolicyValidator.Normalize(Intent() with {EmailRecipients=["Ops@EXAMPLE.TEST","ops@example.test"]});Assert.Equal(new[]{"Ops@example.test","ops@example.test"},value.EmailRecipients);
    }
    [Theory]
    [InlineData("Name <ops@example.test>")][InlineData("ops@example.test,other@example.test")][InlineData("ops@example.test\r\nBcc: other@example.test")]
    [InlineData("ops\0@example.test")][InlineData(" ops@example.test")][InlineData("ops@example.test ")][InlineData("not-an-email")]
    public void UnsafeOrAmbiguousRecipientsAreRejected(string email)=>Assert.Throws<ApiException>(()=>NotificationPolicyValidator.Normalize(Intent() with {EmailRecipients=[email]}));
    [Fact] public void RecipientLengthIsBounded()
    {Assert.Throws<ApiException>(()=>NotificationPolicyValidator.Normalize(Intent() with {EmailRecipients=[new string('a',245)+"@example.test"]}));}
    [Theory]
    [InlineData("{\"inConsole\":true,\"inConsole\":false,\"requestedChannels\":[]}")]
    [InlineData("{\"inConsole\":true,\"requestedChannels\":[],\"retryPolicy\":{\"maxAttempts\":1,\"maxAttempts\":5,\"baseDelaySeconds\":30,\"maxDelaySeconds\":900,\"expiresAfterMinutes\":1440}}")]
    [InlineData("{\"inConsole\":true,\"requestedChannels\":[],\"unexpected\":true}")]
    public void DuplicateOrUnknownJsonFieldsAreRejected(string json)=>Assert.Throws<ApiException>(()=>NotificationPolicyValidator.Parse(json));
    [Fact] public void ExternalActivationRequiresRealChannelAndEmailTarget()
    {
        Assert.Throws<ApiException>(()=>NotificationPolicyValidator.Normalize(Intent() with {RequestedChannels=["EnterpriseIm"]}));
        Assert.Throws<ApiException>(()=>NotificationPolicyValidator.Normalize(Intent() with {EmailRecipients=[]}));
        Assert.Throws<ApiException>(()=>NotificationPolicyValidator.Normalize(Intent() with {InConsole=false}));
        Assert.Throws<ApiException>(()=>NotificationPolicyValidator.Normalize(Intent() with {RequestedChannels=["Sms"]}));
    }
    [Fact] public void NormalizedCollectionsDoNotRetainMutableInput()
    {
        var channels=new[]{"Webhook","Email"};var recipients=new[]{"Ops@EXAMPLE.TEST"};var value=NotificationPolicyValidator.Normalize(Intent() with {RequestedChannels=channels,EmailRecipients=recipients});channels[0]="EnterpriseIm";recipients[0]="changed@example.test";
        Assert.Equal(new[]{"Email","Webhook"},value.RequestedChannels);Assert.Equal(new[]{"Ops@example.test"},value.EmailRecipients);
    }
    [Fact] public void RetryAfterNeverShortened()
    {
        var decision=NotificationDecisionMachine.NextAttemptAt(NotificationRetryPolicy.Default,1,Now,Now.AddDays(1),TimeSpan.FromSeconds(7200),0);Assert.Equal(DeliveryStatus.Failed,decision.Status);Assert.Equal("RetryAfterExceedsBudget",decision.Reason);Assert.Null(decision.NextAt);
        var accepted=NotificationDecisionMachine.NextAttemptAt(NotificationRetryPolicy.Default,1,Now,Now.AddDays(1),TimeSpan.FromSeconds(1200),0);Assert.Equal(Now.AddSeconds(1200),accepted.NextAt);Assert.Equal(DeliveryStatus.RetryScheduled,accepted.Status);
    }
    [Fact] public void RetryNeverExceedsExpiryOrAttemptBudget()
    {
        var exhausted=NotificationDecisionMachine.NextAttemptAt(NotificationRetryPolicy.Default,5,Now,Now.AddDays(1),null,0);Assert.Equal(DeliveryStatus.Failed,exhausted.Status);Assert.Equal("AttemptsExhausted",exhausted.Reason);
        var expired=NotificationDecisionMachine.NextAttemptAt(NotificationRetryPolicy.Default,1,Now,Now.AddSeconds(30),null,0);Assert.Equal(DeliveryStatus.Expired,expired.Status);Assert.Null(expired.NextAt);
        var later=NotificationDecisionMachine.NextAttemptAt(NotificationRetryPolicy.Default,1,Now,Now.AddSeconds(1200),TimeSpan.FromSeconds(1200),0);Assert.Equal(DeliveryStatus.Expired,later.Status);
    }
    [Fact] public void PositiveJitterIsBoundedByMaxDelay()
    {var p=new NotificationRetryPolicy(5,30,40,1440);Assert.Equal(Now.AddSeconds(36),NotificationDecisionMachine.NextAttemptAt(p,1,Now,Now.AddDays(1),null,.2).NextAt);Assert.Equal(Now.AddSeconds(40),NotificationDecisionMachine.NextAttemptAt(p,2,Now,Now.AddDays(1),null,.2).NextAt);}
    [Theory][InlineData(-.01)][InlineData(.21)][InlineData(double.NaN)][InlineData(double.PositiveInfinity)]
    public void InvalidJitterCannotCreateImmediateRetry(double jitter)=>Assert.Throws<ArgumentException>(()=>NotificationDecisionMachine.NextAttemptAt(NotificationRetryPolicy.Default,1,Now,Now.AddDays(1),null,jitter));
    [Fact] public void SignatureUsesExactFrozenBytes()
    {
        var key=Enumerable.Range(0,32).Select(x=>(byte)x).ToArray();var id=Guid.Parse("30000000-0000-4000-8000-000000000003");const string timestamp="1791420000";var body=Encoding.UTF8.GetBytes("{\"kind\":\"Alert\",\"value\":1}");using var hmac=new HMACSHA256(key);var expected=Convert.ToHexString(hmac.ComputeHash([..Encoding.UTF8.GetBytes(timestamp+"."+id.ToString("D")+"."),..body])).ToLowerInvariant();
        Assert.Equal(expected,WebhookSignature.Sign(key,timestamp,id,body));body[^2]=(byte)'2';Assert.NotEqual(expected,WebhookSignature.Sign(key,timestamp,id,body));
    }
    [Fact] public void PayloadHasOnlyAllowedV1FieldsAndNoFreeText()
    {
        using var json=JsonDocument.Parse(NotificationPayload.Serialize(Message()));Assert.Equal(new[]{"consoleLink","environmentId","eventId","kind","metricCondition","occurredAt","occurrenceNo","schemaVersion","severity","transition"},json.RootElement.EnumerateObject().Select(p=>p.Name).Order(StringComparer.Ordinal));Assert.Equal(1,json.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Throws<ApiException>(()=>NotificationPayload.Serialize(Message() with {MetricCondition="request_rps > 1; PERSONAL_COMMENT"}));
        Assert.Throws<ApiException>(()=>NotificationPayload.Serialize(Message() with {ConsoleLink="https://user:password@console.example.test/observability/alerts"}));
    }
    [Fact] public void PayloadSizeIsCheckedAfterSerialization()
    {var error=Assert.Throws<ApiException>(()=>NotificationPayload.Serialize(Message() with {ConsoleLink="https://console.example.test/"+new string('x',17000)}));Assert.Equal("payload_too_large",error.Code);}
    [Theory][InlineData("")][InlineData("   ")]
    public void EmptyConditionHasSafeValidationFailure(string condition)=>Assert.Throws<ApiException>(()=>NotificationPayload.Serialize(Message() with {MetricCondition=condition}));
    [Fact] public void SerializedMetricConditionIsNormalized()
    {using var json=JsonDocument.Parse(NotificationPayload.Serialize(Message() with {MetricCondition="request_rps     >     +10.0"}));Assert.Equal("request_rps > 10",json.RootElement.GetProperty("metricCondition").GetString());}
    [Fact] public void JsonBoundaryRejectsCaseAliasDuplicatesAndWritesSafeDefaults()
    {
        Assert.Throws<JsonException>(()=>JsonSerializer.Deserialize<NotificationIntent>("{\"inConsole\":true,\"InConsole\":false,\"requestedChannels\":[]}",CanonicalJson.Options));
        var legacy=JsonSerializer.Deserialize<NotificationIntent>("{\"InConsole\":true,\"RequestedChannels\":[]}",CanonicalJson.Options)!;
        using var json=JsonDocument.Parse(CanonicalJson.Serialize(legacy));Assert.False(json.RootElement.GetProperty("externalEnabled").GetBoolean());Assert.Equal(0,json.RootElement.GetProperty("emailRecipients").GetArrayLength());Assert.Equal(5,json.RootElement.GetProperty("retryPolicy").GetProperty("maxAttempts").GetInt32());
        Assert.Equal("\"OutcomeUnknown\"",JsonSerializer.Serialize(DeliveryOutcome.OutcomeUnknown,CanonicalJson.Options));
        Assert.Throws<JsonException>(()=>JsonSerializer.Deserialize<NotificationChannel>("0",CanonicalJson.Options));
    }
    [Fact] public void SignatureRejectsUnboundedKeysBodyAndHeaderInjection()
    {
        var id=Guid.NewGuid();Assert.Throws<ArgumentException>(()=>WebhookSignature.Sign(new byte[31],"1791420000",id,[1]));Assert.Throws<ArgumentException>(()=>WebhookSignature.Sign(new byte[65],"1791420000",id,[1]));
        Assert.Throws<ArgumentException>(()=>WebhookSignature.Sign(new byte[32],"1791420000\r\nInjected: true",id,[1]));Assert.Throws<ArgumentException>(()=>WebhookSignature.Sign(new byte[32],"1791420000",id,new byte[16385]));
    }
    [Fact] public void ControlledLinkAndTestMessageHaveNoBusinessOrPersonalFields()
    {
        using var alert=JsonDocument.Parse(NotificationPayload.Alert(Message().EventId!.Value,1,Message().EnvironmentId!.Value,"Warning","request_rps","request_rps   >=   10.00","Resolved",Now,new Uri("https://console.example.test/platform/")));
        Assert.Equal("https://console.example.test/platform/observability/alerts?eventId="+Message().EventId!.Value.ToString("D"),alert.RootElement.GetProperty("consoleLink").GetString());Assert.Equal("request_rps >= 10",alert.RootElement.GetProperty("metricCondition").GetString());
        using var test=JsonDocument.Parse(NotificationPayload.Test(new Uri("https://console.example.test/"),Now));Assert.Equal("Test",test.RootElement.GetProperty("kind").GetString());Assert.Equal(JsonValueKind.Null,test.RootElement.GetProperty("eventId").ValueKind);
        Assert.Throws<ApiException>(()=>NotificationPayload.Test(new Uri("https://console.example.test/?secret=x"),Now));
    }
}
