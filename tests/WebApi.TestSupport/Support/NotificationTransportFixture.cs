using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WebApi.Contracts.Notifications;
using WebApi.Infrastructure.Notifications;
using WebApi.NotificationFixtureHost;
namespace WebApi.Integration.Tests.Support;
public sealed class NotificationTransportFixture:IAsyncDisposable
{
    private readonly DirectoryInfo directory=Directory.CreateTempSubdirectory("notification-transport-");
    private readonly byte[] webhookKey=RandomNumberGenerator.GetBytes(32);
    private X509Certificate2? root,certificate;
    private WebApplication? app;
    private NotificationSecretResolver? secrets;
    public string RootCertificatePath=>Path.Combine(directory.FullName,"root.pem");
    public SmtpFixture Smtp=>app!.Services.GetRequiredService<SmtpFixture>();
    public WebhookFixture Webhook=>app!.Services.GetRequiredService<WebhookFixture>();
    public Uri WebhookUrl {get;private set;}=null!;
    public async Task InitializeAsync(bool tlsOnConnect=false,bool advertiseStartTls=true,bool expiredCertificate=false)
    {
        using var rootKey=RSA.Create(2048);var rootRequest=new CertificateRequest("CN=WebAPI Notification Fixture Root "+Guid.NewGuid().ToString("D"),rootKey,System.Security.Cryptography.HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true,false,0,true));rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign|X509KeyUsageFlags.CrlSign,true));
        root=rootRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),DateTimeOffset.UtcNow.AddDays(7));
        using var key=RSA.Create(2048);var request=new CertificateRequest("CN=hook.fixture.test",key,System.Security.Cryptography.HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);var names=new SubjectAlternativeNameBuilder();names.AddDnsName("smtp.fixture.test");names.AddDnsName("hook.fixture.test");request.CertificateExtensions.Add(names.Build());request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false,false,0,true));request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature|X509KeyUsageFlags.KeyEncipherment,true));request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection{new("1.3.6.1.5.5.7.3.1")},true));
        using(var unsigned=request.Create(root,expiredCertificate?DateTimeOffset.UtcNow.AddHours(-12):DateTimeOffset.UtcNow.AddHours(-1),expiredCertificate?DateTimeOffset.UtcNow.AddHours(-1):DateTimeOffset.UtcNow.AddDays(3),RandomNumberGenerator.GetBytes(16)))certificate=unsigned.CopyWithPrivateKey(key);
        await PrivateWrite(RootCertificatePath,root.ExportCertificatePem());var smtpPath=Path.Combine(directory.FullName,"smtp.json");var hookPath=Path.Combine(directory.FullName,"webhook.json");var username="fixture_"+Guid.NewGuid().ToString("N");var password=Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        await PrivateWrite(smtpPath,JsonSerializer.Serialize(new{username,password}));await PrivateWrite(hookPath,JsonSerializer.Serialize(new{keyBase64=Convert.ToBase64String(webhookKey)}));
        secrets=new(new(new Dictionary<string,string>{{"vault://fixture/smtp",smtpPath},{"vault://fixture/webhook",hookPath}}));
        var serverCertificate=certificate??throw new InvalidOperationException("Fixture certificate unavailable.");
        app=NotificationFixtureApp.Build(["--environment","Development"],builder=>{
            builder.Logging.ClearProviders();builder.Services.AddSingleton(new SmtpFixtureOptions(serverCertificate,username,password,tlsOnConnect:tlsOnConnect,advertiseStartTls:advertiseStartTls));builder.Services.AddSingleton(new WebhookFixtureOptions(webhookKey));builder.WebHost.ConfigureKestrel(options=>options.Listen(IPAddress.Loopback,0,listen=>listen.UseHttps(serverCertificate)));
        });await app.StartAsync();var address=app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();WebhookUrl=new Uri("https://hook.fixture.test:"+new Uri(address).Port+"/notify");
    }
    public Task<NotificationSecret> SecretAsync(NotificationChannel channel)=>secrets!.ResolveAsync(channel==NotificationChannel.Email?"vault://fixture/smtp":"vault://fixture/webhook",channel);
    private static async Task PrivateWrite(string path,string value)
    {await File.WriteAllTextAsync(path,value);if(!OperatingSystem.IsWindows())File.SetUnixFileMode(path,UnixFileMode.UserRead|UnixFileMode.UserWrite);}
    public async ValueTask DisposeAsync()
    {if(app is not null){await app.StopAsync();await app.DisposeAsync();}certificate?.Dispose();root?.Dispose();directory.Delete(true);}
}
