using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Http.Json;
using WebApi.Integration.Tests.Support;
using WebApi.Infrastructure.Settings;
using WebApi.Contracts.Alerts;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Persistence;
using WebApi.Worker;
using WebApi.Contracts.Common;
using WebApi.Contracts.Notifications;
using WebApi.Infrastructure.Notifications;
using WebApi.Infrastructure.Security;
using WebApi.Infrastructure.Sso;
using WebApi.Infrastructure.Persistence.Entities;
using Xunit;
namespace WebApi.Integration.Tests;
public sealed class NotificationConfigurationTests
{
    private sealed class Files:IAsyncDisposable
    {
        public DirectoryInfo Directory {get;}=System.IO.Directory.CreateTempSubdirectory("notification-secret-");
        public string Path=>System.IO.Path.Combine(Directory.FullName,"secret.json");
        public NotificationSecretResolver Resolver=>new(new(new Dictionary<string,string>{{"vault://local/channel",Path}}));
        public async Task Write(string value){await File.WriteAllTextAsync(Path,value);if(!OperatingSystem.IsWindows())File.SetUnixFileMode(Path,UnixFileMode.UserRead|UnixFileMode.UserWrite);}
        public ValueTask DisposeAsync(){Directory.Delete(true);return ValueTask.CompletedTask;}
    }
    [Fact] public async Task ExactMappingAndStrictSecretFields()
    {
        await using var files=new Files();await files.Write("{\"username\":\"ops\",\"password\":\"  untouched password  \"}");
        var secret=await files.Resolver.ResolveAsync("vault://local/channel",NotificationChannel.Email);Assert.Equal("ops",secret.Username);Assert.Equal("  untouched password  ",secret.Password);Assert.Equal("{}",JsonSerializer.Serialize(secret));
        foreach(var reference in new[]{"vault://local/missing","file:///etc/passwd","vault://LOCAL/channel"})await Assert.ThrowsAsync<ApiException>(()=>files.Resolver.ResolveAsync(reference,NotificationChannel.Email));
    }
    [Theory]
    [InlineData("{}")] [InlineData("{\"username\":\"ops\",\"username\":\"other\",\"password\":\"x\"}")]
    [InlineData("{\"username\":\"ops\",\"password\":\"x\",\"extra\":1}")] [InlineData("{\"username\":\"ops\",\"password\":\"bad\\r\\nvalue\"}")]
    [InlineData("{\"username\":\"\",\"password\":\"x\"}")] [InlineData("{\"username\":\"ops\",\"password\":\"bad\\u0000value\"}")]
    public async Task InvalidSmtpFileFailsWithoutEcho(string json)
    {await using var files=new Files();await files.Write(json);var error=await Assert.ThrowsAsync<ApiException>(()=>files.Resolver.ResolveAsync("vault://local/channel",NotificationChannel.Email));Assert.DoesNotContain(files.Path,error.Message);Assert.DoesNotContain("bad",error.Message);}
    [Fact] public async Task MissingPublicSymlinkAndOversizeFilesFail()
    {
        await using var files=new Files();await Assert.ThrowsAsync<ApiException>(()=>files.Resolver.ResolveAsync("vault://local/channel",NotificationChannel.Email));
        await files.Write(new string('x',16385));await Assert.ThrowsAsync<ApiException>(()=>files.Resolver.ResolveAsync("vault://local/channel",NotificationChannel.Email));
        await files.Write("{\"username\":\"ops\",\"password\":\"x\"}");
        if(!OperatingSystem.IsWindows()){File.SetUnixFileMode(files.Path,UnixFileMode.UserRead|UnixFileMode.GroupRead);await Assert.ThrowsAsync<ApiException>(()=>files.Resolver.ResolveAsync("vault://local/channel",NotificationChannel.Email));}
        var link=System.IO.Path.Combine(files.Directory.FullName,"link.json");File.CreateSymbolicLink(link,files.Path);var resolver=new NotificationSecretResolver(new(new Dictionary<string,string>{{"vault://local/link",link}}));await Assert.ThrowsAsync<ApiException>(()=>resolver.ResolveAsync("vault://local/link",NotificationChannel.Email));
        var directoryLink=System.IO.Path.Combine(files.Directory.FullName,"linked-directory");System.IO.Directory.CreateSymbolicLink(directoryLink,files.Directory.FullName);resolver=new(new(new Dictionary<string,string>{{"vault://local/link",System.IO.Path.Combine(directoryLink,"secret.json")}}));await Assert.ThrowsAsync<ApiException>(()=>resolver.ResolveAsync("vault://local/link",NotificationChannel.Email));
    }
    [Theory][InlineData(31,false)][InlineData(32,true)][InlineData(64,true)][InlineData(65,false)]
    public async Task WebhookKeyBudget(int size,bool valid)
    {await using var files=new Files();await files.Write(JsonSerializer.Serialize(new{keyBase64=Convert.ToBase64String(RandomNumberGenerator.GetBytes(size))}));if(valid){var key=(await files.Resolver.ResolveAsync("vault://local/channel",NotificationChannel.Webhook)).Key;Assert.NotNull(key);Assert.Equal(size,key.Length);}else await Assert.ThrowsAsync<ApiException>(()=>files.Resolver.ResolveAsync("vault://local/channel",NotificationChannel.Webhook));}
    [Fact] public void RecipientListNormalizesDomainWithoutChangingLocalPart()
    {
        var rules=new NotificationDeploymentSettings(allowedRecipients:["Ops@例子.测试"],allowedDomains:["company.example"]);
        rules.RequireAllowedRecipient("Ops@xn--fsqu00a.xn--0zwm56d");rules.RequireAllowedRecipient("any@COMPANY.EXAMPLE");
        Assert.Throws<ApiException>(()=>rules.RequireAllowedRecipient("ops@例子.测试"));Assert.Throws<ApiException>(()=>rules.RequireAllowedRecipient("a@sub.company.example"));Assert.Throws<ApiException>(()=>rules.RequireAllowedRecipient("a@company.example\r\nInjected"));Assert.Throws<ApiException>(()=>new NotificationDeploymentSettings().RequireAllowedRecipient("a@company.example"));
    }
    [Fact] public void EndpointRequiresExactDeploymentTarget()
    {
        var rules=new NotificationDeploymentSettings(smtpEndpoints:["mail.example.test:587"],webhookUrls:["https://hook.example.test/notify"]);rules.RequireAllowedEndpoint(NotificationChannel.Email,"mail.example.test",587);rules.RequireAllowedEndpoint(NotificationChannel.Webhook,"hook.example.test",443,new Uri("https://hook.example.test/notify"));
        Assert.Throws<ApiException>(()=>rules.RequireAllowedEndpoint(NotificationChannel.Email,"mail.example.test",25));Assert.Throws<ApiException>(()=>rules.RequireAllowedEndpoint(NotificationChannel.Webhook,"hook.example.test",443,new Uri("https://hook.example.test/other")));Assert.Throws<ApiException>(()=>new NotificationDeploymentSettings().RequireAllowedEndpoint(NotificationChannel.Email,"mail.example.test",587));
    }
    [Fact] public void EndpointIdnHostIsNormalizedWhilePortRemainsExact()
    {
        var rules=new NotificationDeploymentSettings(smtpEndpoints:["例子.测试:587"]);rules.RequireAllowedEndpoint(NotificationChannel.Email,"例子.测试",587);rules.RequireAllowedEndpoint(NotificationChannel.Email,"xn--fsqu00a.xn--0zwm56d",587);Assert.Throws<ApiException>(()=>rules.RequireAllowedEndpoint(NotificationChannel.Email,"例子.测试",25));
    }
    [Fact] public async Task PinSurvivesWorkerRestartAndSsoKeysUnchanged()
    {
        await using var files=new Files();await files.Write("{\"username\":\"ops\",\"password\":\"first\"}");var secret=await files.Resolver.ResolveAsync("vault://local/channel",NotificationChannel.Email);var keys=System.IO.Path.Combine(files.Directory.FullName,"keys");
        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"DataProtection:KeysDirectory",keys},{"DataProtection:ApplicationName","WebApiEnterprise"}}).Build();
        var a=new ServiceCollection();PersistentProtectionConfiguration.Configure(a,config);using var first=a.BuildServiceProvider();var pinning=new NotificationSecretVersion(first.GetRequiredService<IDataProtectionProvider>());var id=Guid.NewGuid();var pin=pinning.Pin(id,secret);
        var sso=new SsoProvider{AuthRevision=3};new SsoSecretVersion(first.GetRequiredService<IDataProtectionProvider>()).Pin(sso,"old-sso-key");
        var b=new ServiceCollection();PersistentProtectionConfiguration.Configure(b,config,true);using var second=b.BuildServiceProvider();var restarted=new NotificationSecretVersion(second.GetRequiredService<IDataProtectionProvider>());
        Assert.True(restarted.Matches(id,pin,secret));Assert.False(restarted.Matches(Guid.NewGuid(),pin,secret));Assert.True(new SsoSecretVersion(second.GetRequiredService<IDataProtectionProvider>()).Matches(sso,"old-sso-key"));
        await files.Write("{\"username\":\"ops\",\"password\":\"replacement\"}");Assert.False(restarted.Matches(id,pin,await files.Resolver.ResolveAsync("vault://local/channel",NotificationChannel.Email)));Assert.DoesNotContain("first",pin);
        Assert.False(second.GetRequiredService<IOptions<KeyManagementOptions>>().Value.AutoGenerateKeys);
        Assert.True(first.GetRequiredService<IOptions<KeyManagementOptions>>().Value.AutoGenerateKeys);
        using var worker=WorkerApp.Build(["--environment","Development"],builder=>{builder.Configuration["ConnectionStrings:WebApi"]="Host=unused;Database=unused;Username=unused;Password=design-only";builder.Configuration["DataProtection:KeysDirectory"]=keys;builder.Configuration["DataProtection:ApplicationName"]="WebApiEnterprise";builder.Logging.ClearProviders();});
        Assert.False(worker.Services.GetRequiredService<IOptions<KeyManagementOptions>>().Value.AutoGenerateKeys);Assert.True(worker.Services.GetRequiredService<NotificationSecretVersion>().Matches(id,pin,secret));Assert.True(new SsoSecretVersion(worker.Services.GetRequiredService<IDataProtectionProvider>()).Matches(sso,"old-sso-key"));
    }
    private static object Values(bool smtp=true,bool webhook=true,string url="https://hook.example.test/notify",string operation="Keep")=>new{smtpHost="mail.example.test",smtpPort=587,fromEmail="sender@example.test",smtpSecretRef=new{operation},webhookUrl=url,webhookSecretRef=new{operation},smtpEnabled=smtp,webhookEnabled=webhook,smtpSecurity="StartTlsRequired"};
    private static async Task<ApiFixture> Setup(Files files)
    {
        await files.Write("{\"username\":\"ops\",\"password\":\"first\"}");var hook=System.IO.Path.Combine(files.Directory.FullName,"hook.json");await File.WriteAllTextAsync(hook,JsonSerializer.Serialize(new{keyBase64=Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))}));if(!OperatingSystem.IsWindows())File.SetUnixFileMode(hook,UnixFileMode.UserRead|UnixFileMode.UserWrite);
        var f=new ApiFixture();await f.InitializeAsync(b=>{
            b.Configuration["DataProtection:KeysDirectory"]=System.IO.Path.Combine(files.Directory.FullName,"keys");b.Configuration["DataProtection:ApplicationName"]="WebApiEnterprise";
            b.Services.AddSingleton(new NotificationDeploymentSettings(new Dictionary<string,string>{{"vault://local/channel",files.Path},{"vault://local/hook",hook}},allowedDomains:["example.test"],smtpEndpoints:["mail.example.test:587"],webhookUrls:["https://hook.example.test/notify","https://hook.example.test/other"]));
        });await f.SeedScopeAsync(platformAdmin:true);await SystemSettingsFixture.PromoteAsync(f);(await f.LoginAsync()).EnsureSuccessStatusCode();return f;
    }
    private static async Task InitialSave(ApiFixture f)
    {
        var value=new{smtpHost="mail.example.test",smtpPort=587,fromEmail="sender@example.test",smtpSecretRef=new{operation="Replace",reference="vault://local/channel"},webhookUrl="https://hook.example.test/notify",webhookSecretRef=new{operation="Replace",reference="vault://local/hook"},smtpEnabled=true,webhookEnabled=true,smtpSecurity="StartTlsRequired"};using var saved=await SystemSettingsFixture.SaveAsync(f,"notification",value);saved.EnsureSuccessStatusCode();
    }
    [Fact] public async Task NotificationPreviewDoesNotEchoSenderOrReferences()
    {
        await using var files=new Files();await using var f=await Setup(files);var value=new{smtpHost="mail.example.test",smtpPort=587,fromEmail="private-sender@example.test",smtpSecretRef=new{operation="Replace",reference="vault://local/channel"},webhookUrl=(string?)null,webhookSecretRef=new{operation="Keep"},smtpEnabled=true,webhookEnabled=false,smtpSecurity="StartTlsRequired"};
        var (preview,_)=await SystemSettingsFixture.PreviewAsync(f,"notification",value);Assert.DoesNotContain("private-sender@example.test",preview.GetRawText());Assert.DoesNotContain("vault://local/channel",preview.GetRawText());Assert.Contains("fromEmail",preview.GetRawText());
    }
    [Fact] public async Task SameReferenceChangedFileRequiresExplicitActivation()
    {
        await using var files=new Files();await using var f=await Setup(files);await InitialSave(f);NotificationChannelProfile old;
        await using(var db=f.Context())old=await db.Set<NotificationChannelProfile>().SingleAsync(x=>x.Channel=="Email");
        await files.Write("{\"username\":\"ops\",\"password\":\"replacement\"}");using(var scope=f.Services()){var versions=scope.ServiceProvider.GetRequiredService<NotificationSecretVersion>();Assert.False(versions.Matches(old.Id,old.ProtectedSecretFingerprint,await files.Resolver.ResolveAsync("vault://local/channel",NotificationChannel.Email)));}
        using(var saved=await SystemSettingsFixture.SaveAsync(f,"notification",Values()))saved.EnsureSuccessStatusCode();
        await using(var db=f.Context())
        {
            var state=await db.Set<NotificationChannelState>().SingleAsync(x=>x.Channel=="Email");Assert.NotEqual(old.Id,state.ProfileId);Assert.Equal(2,await db.Set<NotificationChannelProfile>().CountAsync(x=>x.Channel=="Email"));Assert.Equal(1,await db.Set<NotificationChannelProfile>().CountAsync(x=>x.Channel=="Webhook"));Assert.Equal(old.ProtectedSecretFingerprint,(await db.Set<NotificationChannelProfile>().SingleAsync(x=>x.Id==old.Id)).ProtectedSecretFingerprint);
            foreach(var audit in await db.Set<AuditLog>().ToArrayAsync()){Assert.DoesNotContain("replacement",audit.AfterJson??"");Assert.DoesNotContain("vault://local/channel",audit.AfterJson??"");Assert.DoesNotContain("sender@example.test",audit.AfterJson??"");}
        }
        using(var same=await SystemSettingsFixture.SaveAsync(f,"notification",Values()))same.EnsureSuccessStatusCode();await using(var db=f.Context())Assert.Equal(3,await db.Set<NotificationChannelProfile>().CountAsync());
    }
    [Fact] public async Task OtherChannelEditDoesNotChangeProfile()
    {
        await using var files=new Files();await using var f=await Setup(files);await InitialSave(f);Guid? before;
        await using(var db=f.Context())before=(await db.Set<NotificationChannelState>().SingleAsync(x=>x.Channel=="Email")).ProfileId;
        await files.Write("malformed replaced SMTP file");using(var saved=await SystemSettingsFixture.SaveAsync(f,"notification",Values(url:"https://hook.example.test/other")))saved.EnsureSuccessStatusCode();
        await using(var db=f.Context()){Assert.Equal(before,(await db.Set<NotificationChannelState>().SingleAsync(x=>x.Channel=="Email")).ProfileId);Assert.Equal(1,await db.Set<NotificationChannelProfile>().CountAsync(x=>x.Channel=="Email"));Assert.Equal(2,await db.Set<NotificationChannelProfile>().CountAsync(x=>x.Channel=="Webhook"));}
    }
    [Fact] public async Task DisabledConfigDoesNotResolveAndEnabledFailureIsAtomic()
    {
        await using var files=new Files();await using var f=await Setup(files);await InitialSave(f);using(var disabled=await SystemSettingsFixture.SaveAsync(f,"notification",Values(smtp:false,webhook:false)))disabled.EnsureSuccessStatusCode();await files.Write("malformed");
        using(var kept=await SystemSettingsFixture.SaveAsync(f,"notification",Values(smtp:false,webhook:false)))kept.EnsureSuccessStatusCode();
        using(var invalid=await SystemSettingsFixture.SaveAsync(f,"notification",Values()))Assert.Equal(HttpStatusCode.UnprocessableEntity,invalid.StatusCode);
        await using var db=f.Context();Assert.All(await db.Set<NotificationChannelState>().ToArrayAsync(),state=>Assert.False(state.Enabled));Assert.False(((NotificationSettings)(await new SystemSettingsReader(db).ReadAsync("notification")).Value).SmtpEnabled);Assert.Equal(2,await db.Set<NotificationChannelProfile>().CountAsync());
    }
    [Fact] public void StoredLegacyHasOffDefaultsAndPartialNewDataFails()
    {
        const string legacy="{\"smtpHost\":null,\"smtpPort\":null,\"fromEmail\":null,\"smtpSecretRef\":null,\"webhookUrl\":null,\"webhookSecretRef\":null}";
        var value=(NotificationSettings)SettingsValueCodec.Decode("notification",legacy);Assert.False(value.SmtpEnabled);Assert.False(value.WebhookEnabled);Assert.Equal("StartTlsRequired",value.SmtpSecurity);
        Assert.Throws<ApiException>(()=>SettingsValueCodec.Decode("notification",legacy[..^1]+",\"smtpEnabled\":true}"));Assert.Throws<ApiException>(()=>SettingsValueCodec.Decode("notification",legacy[..^1]+",\"unknown\":false}"));
    }
    [Fact] public void ReadOnlyWorkerCannotMintReplacementKeys()
    {
        var directory=System.IO.Directory.CreateTempSubdirectory("notification-readonly-keys-");
        try
        {
            var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"DataProtection:KeysDirectory",directory.FullName},{"DataProtection:ApplicationName","WebApiEnterprise"}}).Build();var services=new ServiceCollection();PersistentProtectionConfiguration.Configure(services,config,true);using var provider=services.BuildServiceProvider();
            Assert.ThrowsAny<CryptographicException>(()=>provider.GetRequiredService<IDataProtectionProvider>().CreateProtector("fixture-purpose").Protect([1]));Assert.Empty(directory.GetFiles("*.xml"));
        }
        finally{directory.Delete(true);}
    }
    [Theory][InlineData(321,1)][InlineData(1,4097)]
    public async Task SmtpIndividualFieldBudgetsAreEnforced(int usernameSize,int passwordSize)
    {await using var files=new Files();await files.Write(JsonSerializer.Serialize(new{username=new string('u',usernameSize),password=new string('p',passwordSize)}));await Assert.ThrowsAsync<ApiException>(()=>files.Resolver.ResolveAsync("vault://local/channel",NotificationChannel.Email));}
    [Fact] public async Task Utf8AndWebhookDuplicateFieldsFailSafely()
    {
        await using var files=new Files();await files.Write("{}");await File.WriteAllBytesAsync(files.Path,[0x7b,0xff,0x7d]);await Assert.ThrowsAsync<ApiException>(()=>files.Resolver.ResolveAsync("vault://local/channel",NotificationChannel.Email));
        var key=Convert.ToBase64String(new byte[32]);await files.Write("{\"keyBase64\":\""+key+"\",\"keyBase64\":\""+key+"\"}");await Assert.ThrowsAsync<ApiException>(()=>files.Resolver.ResolveAsync("vault://local/channel",NotificationChannel.Webhook));
    }
    [Fact] public async Task RuleAuditContainsSummaryAndCountWithoutRecipientText()
    {
        await using var f=new ApiFixture();await f.InitializeAsync();await f.SeedScopeAsync();await using var db=f.Context();var commands=new AuditedCommandExecutor(db);
        var rule=new AlertRule{OrganizationId=f.Organization.Id,ProjectId=f.Project.Id,EnvironmentId=f.Environment.Id,Name="Fixture",NormalizedName="FIXTURE",Metric="request_rps",Expression="request_rps > 10",CreatedBy=f.User.Id,UpdatedBy=f.User.Id,Notification=JsonSerializer.Serialize(new NotificationIntent(true,["Email"],true,["private-recipient@example.test"]),CanonicalJson.Options)};
        await commands.ExecuteAsync(new ActorContext(f.User.Id,"notification-audit"),new ScopeRef(f.Organization.Id,f.Project.Id,f.Environment.Id),"alert.rule.fixture",(_,ct)=>{db.Add(rule);return Task.FromResult(true);});
        var audit=await db.Set<AuditLog>().SingleAsync();Assert.DoesNotContain("private-recipient@example.test",audit.AfterJson!);using var json=JsonDocument.Parse(audit.AfterJson!);var fields=json.RootElement[0].GetProperty("Fields");Assert.False(fields.TryGetProperty("Notification",out _));Assert.Equal(1,fields.GetProperty("NotificationSummary").GetProperty("recipientCount").GetInt32());Assert.Equal(64,fields.GetProperty("NotificationHash").GetString()!.Length);
    }
}
