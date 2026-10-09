using Xunit;
using System.Text;
using System.Text.Json;
using WebApi.Contracts.Common;
using WebApi.Contracts.Settings;
using WebApi.Infrastructure.Settings;
namespace WebApi.Domain.Tests;
public sealed class SystemSettingsValidationTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"sessionTtlMinutes\":480,\"passwordMinLength\":0,\"passwordComplexity\":\"LengthOnly\",\"allowedOrigins\":[]}")]
    [InlineData("{\"sessionTtlMinutes\":480,\"passwordMinLength\":16,\"passwordComplexity\":\"LengthOnly\",\"allowedOrigins\":[],\"extra\":true}")]
    public void CorruptStoredSecurityCannotWeakenConsumers(string json)
    {
        var error=Assert.Throws<ApiException>(()=>SettingsValueCodec.Decode("security",json));
        Assert.Equal(503,error.Status);Assert.DoesNotContain(json,error.Message);
    }
    [Fact]
    public void LegacySecurityGetsLoginDefaultsWithoutPersisting()
    {
        const string json="""{"sessionTtlMinutes":480,"passwordMinLength":16,"passwordComplexity":"LengthOnly","allowedOrigins":[]}""";
        var decoded=SettingsValueCodec.Json(SettingsValueCodec.Decode("security",json));
        Assert.Equal(60,decoded.GetProperty("loginIpMaxAttempts").GetInt32());
        Assert.Equal(60,decoded.GetProperty("loginIpWindowSeconds").GetInt32());
        Assert.Equal(10,decoded.GetProperty("loginAccountMaxAttempts").GetInt32());
        Assert.Equal(300,decoded.GetProperty("loginAccountWindowSeconds").GetInt32());
    }
    private static Dictionary<string,object> CompleteSecurity()=>new(){["sessionTtlMinutes"]=480,["passwordMinLength"]=16,["passwordComplexity"]="LengthOnly",["allowedOrigins"]=Array.Empty<string>(),["loginIpMaxAttempts"]=60,["loginIpWindowSeconds"]=60,["loginAccountMaxAttempts"]=10,["loginAccountWindowSeconds"]=300};
    [Theory]
    [InlineData("loginIpMaxAttempts",10000)][InlineData("loginAccountMaxAttempts",10000)]
    [InlineData("loginIpWindowSeconds",3600)][InlineData("loginAccountWindowSeconds",3600)]
    public void LoginLimitsAreRequiredAndBounded(string field,int maximum)
    {
        var values=CompleteSecurity();values.Remove(field);
        Assert.Equal(422,Assert.Throws<ApiException>(()=>SystemSettingsValidator.Resolve("security",Input(values),Security)).Status);
        foreach(var bad in new[]{0,maximum+1}){values[field]=bad;Assert.Equal(422,Assert.Throws<ApiException>(()=>SystemSettingsValidator.Resolve("security",Input(values),Security)).Status);}
        foreach(var valid in new[]{1,maximum}){values[field]=valid;Assert.IsType<SecuritySettings>(SystemSettingsValidator.Resolve("security",Input(values),Security).Value);}
        Assert.Equal("LoginRequest",SystemSettingsService.Effect(field));
    }
    [Fact]
    public void LegacyMissingDuplicateAndUnknownFieldsFailClosed()
    {
        foreach(var json in new[]{"""{"sessionTtlMinutes":480,"passwordMinLength":16,"passwordComplexity":"LengthOnly"}""", """{"sessionTtlMinutes":480,"sessionTtlMinutes":480,"passwordMinLength":16,"passwordComplexity":"LengthOnly","allowedOrigins":[]}"""})
            Assert.Equal(503,Assert.Throws<ApiException>(()=>SettingsValueCodec.Decode("security",json)).Status);
    }
    private static SettingsMutation Input(object value)=>SystemSettingsValidator.Parse("security",Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new {values=value})));
    private static SettingsMutation Mutation(string group,object value)=>SystemSettingsValidator.Parse(group,Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new {values=value})));
    private static SecuritySettings Security=>new(480,16,"LengthOnly",[]);
    private static NotificationSettings Notification=>new(null,null,null,null,null,null);
    [Theory][InlineData("Keep")][InlineData("Clear")]
    public void NonReplaceCannotCarryEvenNullReference(string operation)
    {
        var values=new {smtpHost=(string?)null,smtpPort=(int?)null,fromEmail=(string?)null,smtpSecretRef=new {operation,reference=(string?)null},webhookUrl=(string?)null,webhookSecretRef=new {operation="Keep"}};
        Assert.Equal(422,Assert.Throws<ApiException>(()=>SystemSettingsValidator.Resolve("notification",Mutation("notification",values),Notification)).Status);
    }
    [Theory]
    [InlineData(4,16,"LengthOnly")][InlineData(1441,16,"LengthOnly")][InlineData(480,15,"LengthOnly")][InlineData(480,129,"LengthOnly")][InlineData(480,16,"Unknown")]
    public void RejectsInvalidBoundsAndTypes(int ttl,int length,string complexity)=>Assert.Equal(422,Assert.Throws<ApiException>(()=>SystemSettingsValidator.Resolve("security",Input(new {sessionTtlMinutes=ttl,passwordMinLength=length,passwordComplexity=complexity,allowedOrigins=Array.Empty<string>(),loginIpMaxAttempts=60,loginIpWindowSeconds=60,loginAccountMaxAttempts=10,loginAccountWindowSeconds=300}),Security)).Status);
    [Theory][InlineData(5,16)][InlineData(1440,128)]
    public void AcceptsBoundarySecurityValues(int ttl,int length)=>Assert.IsType<SecuritySettings>(SystemSettingsValidator.Resolve("security",Input(new {sessionTtlMinutes=ttl,passwordMinLength=length,passwordComplexity="LettersAndDigits",allowedOrigins=Array.Empty<string>(),loginIpMaxAttempts=60,loginIpWindowSeconds=60,loginAccountMaxAttempts=10,loginAccountWindowSeconds=300}),Security).Value);
    [Theory][InlineData("defaultRouteTimeoutMs",0)][InlineData("defaultRouteTimeoutMs",300001)][InlineData("maxRequestBodyMb",0)][InlineData("maxRequestBodyMb",257)][InlineData("configRefreshIntervalSeconds",0)][InlineData("configRefreshIntervalSeconds",61)]
    public void GatewayBounds(string field,int bad){var values=new Dictionary<string,object>{{"defaultRouteTimeoutMs",30000},{"maxRequestBodyMb",20},{"configRefreshIntervalSeconds",2}};values[field]=bad;Assert.Equal(422,Assert.Throws<ApiException>(()=>SystemSettingsValidator.Resolve("gateway",Mutation("gateway",values),new GatewaySettings(30000,20,2))).Status);}
    [Theory][InlineData(1,50)][InlineData(2,9)][InlineData(2,10001)]
    public void ProductionApprovalCannotBeOne(int levels,int count)=>Assert.Throws<ApiException>(()=>SystemSettingsValidator.Resolve("release",Mutation("release",new {productionApprovalLevels=levels,snapshotRetentionCount=count}),new ReleaseSettings(2,50)));
    [Theory][InlineData(29)][InlineData(3651)]
    public void AuditBounds(int days)=>Assert.Throws<ApiException>(()=>SystemSettingsValidator.Resolve("audit",Mutation("audit",new {auditRetentionDays=days,auditExportEnabled=true}),new AuditSettings(365,true)));
    [Theory]
    [InlineData("{\"values\":{},\"values\":{}}")][InlineData("{\"values\":{\"sessionTtlMinutes\":5,\"sessionTtlMinutes\":6}}")][InlineData("{\"values\":{},\"scopeId\":null}")][InlineData("null")][InlineData("[]")][InlineData("{\"values\":{\"extra\":1}}")]
    public void RejectsDuplicateUnknownAndOversizeJson(string json)=>Assert.Equal(422,Assert.Throws<ApiException>(()=>SystemSettingsValidator.Parse("security",Encoding.UTF8.GetBytes(json))).Status);
    [Fact]public void OversizeIs413(){Assert.Equal(413,Assert.Throws<ApiException>(()=>SystemSettingsValidator.Parse("security",new byte[8193])).Status);var prefix="{\"values\":{}}";Assert.NotNull(SystemSettingsValidator.Parse("security",Encoding.UTF8.GetBytes(prefix.PadRight(8192))));}
    [Theory][InlineData("https://*.example.com")][InlineData("https://u:p@example.com")][InlineData("https://example.com?x=1")][InlineData("https://example.com/#x")][InlineData("https://example.com/path")]
    public void RejectsUnsafeOriginsAndNotificationInputs(string origin)=>Assert.Throws<ApiException>(()=>SystemSettingsValidator.Resolve("security",Input(new {sessionTtlMinutes=480,passwordMinLength=16,passwordComplexity="LengthOnly",allowedOrigins=new[]{origin},loginIpMaxAttempts=60,loginIpWindowSeconds=60,loginAccountMaxAttempts=10,loginAccountWindowSeconds=300}),Security));
    private static object Notice(string operation="Keep",string? reference=null,string? host=null,int? port=null,string? from=null,string? url=null,string webOperation="Keep",string? webReference=null)=>new {smtpHost=host,smtpPort=port,fromEmail=from,smtpSecretRef=reference is null?new Dictionary<string,object>{{"operation",operation}}:new Dictionary<string,object>{{"operation",operation},{"reference",reference}},webhookUrl=url,webhookSecretRef=webReference is null?new Dictionary<string,object>{{"operation",webOperation}}:new Dictionary<string,object>{{"operation",webOperation},{"reference",webReference}}};
    [Fact]public void ReferenceOperationsPreserveAndValidateCompleteness(){var current=new NotificationSettings("mail.example.com",587,"sender@example.com","vault://test/smtp",null,null);var kept=(NotificationSettings)SystemSettingsValidator.Resolve("notification",Mutation("notification",Notice(host:"mail.example.com",port:587,from:"sender@example.com")),current).Value;Assert.Equal("vault://test/smtp",kept.SmtpSecretRef);Assert.Throws<ApiException>(()=>SystemSettingsValidator.Resolve("notification",Mutation("notification",Notice("Clear",host:"mail.example.com",port:587,from:"sender@example.com")),current));var cleared=(NotificationSettings)SystemSettingsValidator.Resolve("notification",Mutation("notification",Notice("Clear")),current).Value;Assert.Null(cleared.SmtpSecretRef);Assert.Throws<ApiException>(()=>SystemSettingsValidator.Resolve("notification",Mutation("notification",Notice("Keep","vault://test/ignored")),Notification));}
    [Theory][InlineData("vault:///path")][InlineData("vault://test/")][InlineData("vault://u:p@test/path")][InlineData("vault://test/path?x=1")][InlineData("vault://test/path#x")][InlineData("plaintext")]
    public void InvalidReferenceNeverLeaks(string value){var ex=Assert.Throws<ApiException>(()=>SystemSettingsValidator.Resolve("notification",Mutation("notification",Notice("Replace",value,"mail.example.com",587,"from@example.com")),Notification));Assert.DoesNotContain(value,ex.Message);}
    [Theory][InlineData("https://u:p@example.com/notify")][InlineData("http://example.com")][InlineData("https://example.com?secret=1")]
    public void WebhookMustBeSafeHttps(string url)=>Assert.Throws<ApiException>(()=>SystemSettingsValidator.Resolve("notification",Mutation("notification",Notice(url:url,webOperation:"Replace",webReference:"vault://test/hook")),Notification));
    [Fact]public void NotificationRequiresCompleteGroup()=>Assert.Throws<ApiException>(()=>SystemSettingsValidator.Resolve("notification",Mutation("notification",Notice(host:"mail.example.com")),Notification));
}
