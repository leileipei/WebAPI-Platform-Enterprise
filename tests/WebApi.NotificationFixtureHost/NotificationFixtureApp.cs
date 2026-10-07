using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
namespace WebApi.NotificationFixtureHost;
public static class NotificationFixtureApp
{
    public static WebApplication Build(string[] args,Action<WebApplicationBuilder>? configure=null)
    {
        var builder=WebApplication.CreateBuilder(args);configure?.Invoke(builder);
        builder.WebHost.ConfigureKestrel(options=>{options.Limits.MaxRequestBodySize=16384;if(builder.Configuration.GetValue<int>("Fixture:ManagementPort") is var management && management>0){var certificate=X509Certificate2.CreateFromPemFile(builder.Configuration["Fixture:CertificateFile"]!,builder.Configuration["Fixture:CertificateKeyFile"]!);options.ListenAnyIP(management);options.ListenAnyIP(builder.Configuration.GetValue<int>("Fixture:HttpsPort"),listen=>listen.UseHttps(certificate));}});
        builder.Services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        builder.Services.TryAddSingleton<SmtpFixtureOptions>(_=>ReadSmtp(builder.Configuration));builder.Services.TryAddSingleton<WebhookFixtureOptions>(_=>ReadWebhook(builder.Configuration));
        builder.Services.AddSingleton(_=>new FixtureJournal(builder.Configuration["Fixture:JournalDirectory"],builder.Configuration["Fixture:OwnerId"],builder.Configuration["Fixture:ProjectName"]));
        builder.Services.AddSingleton<FixtureRuntimeIdentity>();
        builder.Services.AddSingleton<SmtpFixture>();builder.Services.AddHostedService(sp=>sp.GetRequiredService<SmtpFixture>());builder.Services.AddSingleton<WebhookFixture>();
        var app=builder.Build();app.MapGet("/health",(SmtpFixture smtp)=>Results.Ok(new{status="ready",smtpPort=smtp.Port}));
        app.MapPost("/notify",(HttpContext context,WebhookFixture fixture)=>fixture.ReceiveAsync(context));app.MapPost("/redirect-target",(HttpContext context,WebhookFixture fixture)=>fixture.ReceiveAsync(context,true));
        var owner=builder.Configuration["Fixture:OwnerId"];var project=builder.Configuration["Fixture:ProjectName"];var tokenFile=builder.Configuration["Fixture:OwnerTokenFile"];var token=tokenFile is null?null:File.ReadAllText(tokenFile).Trim();
        bool Authorized(HttpContext context)=>owner is not null&&token is not null&&token.Length>=32&&context.Request.Headers["X-WebAPI-Fixture-Owner"].Count==1&&CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(context.Request.Headers["X-WebAPI-Fixture-Owner"].ToString()),Encoding.UTF8.GetBytes(token))&&(builder.Configuration.GetValue<int>("Fixture:ManagementPort")==0||context.Connection.LocalPort==builder.Configuration.GetValue<int>("Fixture:ManagementPort"));
        app.MapGet("/owner/observations",(HttpContext context,SmtpFixture smtp,WebhookFixture webhook,FixtureRuntimeIdentity identity)=>!Authorized(context)?Results.NotFound():Results.Ok(new{ownerId=owner,projectName=project,identity.SourceRevision,identity.BinaryHash,identity.BinaryVerified,protocol=new{smtp=smtp.Receipts,webhook=webhook.Receipts},webhook.BusinessAcceptCount}));
        app.MapPost("/owner/mode",async(HttpContext context,SmtpFixture smtp,WebhookFixture webhook)=>{
            if(!Authorized(context))return Results.NotFound();try{using var document=await JsonDocument.ParseAsync(context.Request.Body,new JsonDocumentOptions{MaxDepth=2},context.RequestAborted);var root=document.RootElement;var fields=root.EnumerateObject().ToArray();if(fields.Any(f=>f.Name is not("smtp" or "webhook" or "retryAfter")||f.Value.ValueKind!=JsonValueKind.String)||fields.Select(f=>f.Name).Distinct().Count()!=fields.Length||!root.TryGetProperty("smtp",out var s)||!root.TryGetProperty("webhook",out var w))return Results.BadRequest();var sm=s.GetString()!;var wh=w.GetString()!;var retry=root.TryGetProperty("retryAfter",out var r)?r.GetString()!:"1";if(sm is not("Accept" or "Temporary" or "Permanent" or "AcceptThenDisconnect" or "AcceptThenQuitDisconnect" or "Delay")||wh is not("Accept" or "Temporary" or "Permanent" or "RateLimited" or "Redirect" or "Disconnect" or "Delay" or "Large")||retry.Length>1024||retry.Any(char.IsControl))return Results.BadRequest();smtp.SetMode(sm);webhook.SetMode(wh,retry);return Results.Ok(new{applied=true});}catch(JsonException){return Results.BadRequest();}});
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
