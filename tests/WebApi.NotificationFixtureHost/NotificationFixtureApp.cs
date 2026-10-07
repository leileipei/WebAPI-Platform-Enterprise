using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
namespace WebApi.NotificationFixtureHost;
public static class NotificationFixtureApp
{
    public static WebApplication Build(string[] args,Action<WebApplicationBuilder>? configure=null)
    {
        var builder=WebApplication.CreateBuilder(args);configure?.Invoke(builder);
        builder.WebHost.ConfigureKestrel(options=>options.Limits.MaxRequestBodySize=16384);
        builder.Services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        builder.Services.TryAddSingleton<SmtpFixtureOptions>(_=>ReadSmtp(builder.Configuration));builder.Services.TryAddSingleton<WebhookFixtureOptions>(_=>ReadWebhook(builder.Configuration));
        builder.Services.AddSingleton<SmtpFixture>();builder.Services.AddHostedService(sp=>sp.GetRequiredService<SmtpFixture>());builder.Services.AddSingleton<WebhookFixture>();
        var app=builder.Build();app.MapGet("/health",(SmtpFixture smtp)=>Results.Ok(new{status="ready",smtpPort=smtp.Port}));
        app.MapPost("/notify",(HttpContext context,WebhookFixture fixture)=>fixture.ReceiveAsync(context));app.MapPost("/redirect-target",(HttpContext context,WebhookFixture fixture)=>fixture.ReceiveAsync(context,true));
        return app;
    }
    private static SmtpFixtureOptions ReadSmtp(IConfiguration configuration)
    {
        var cert=configuration["Fixture:CertificateFile"];var key=configuration["Fixture:CertificateKeyFile"];var credentials=configuration["Fixture:SmtpCredentialsFile"];
        if(cert is null||key is null||credentials is null)throw new InvalidOperationException("Notification fixture configuration is required.");
        using var document=JsonDocument.Parse(File.ReadAllText(credentials));return new(X509Certificate2.CreateFromPemFile(cert,key),document.RootElement.GetProperty("username").GetString()!,document.RootElement.GetProperty("password").GetString()!,configuration.GetValue<int>("Fixture:SmtpPort"),configuration.GetValue<bool>("Fixture:TlsOnConnect"),!configuration.GetValue<bool>("Fixture:DisableStartTls"),System.Net.IPAddress.Any);
    }
    private static WebhookFixtureOptions ReadWebhook(IConfiguration configuration)
    {var path=configuration["Fixture:WebhookKeyFile"]??throw new InvalidOperationException("Notification fixture key is required.");using var document=JsonDocument.Parse(File.ReadAllText(path));return new(Convert.FromBase64String(document.RootElement.GetProperty("keyBase64").GetString()!));}
}
