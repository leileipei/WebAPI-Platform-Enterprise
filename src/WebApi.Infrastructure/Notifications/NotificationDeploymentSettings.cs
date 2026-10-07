using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using System.Globalization;
using System.Net;
using System.Text.Json;
using WebApi.Contracts.Common;
using WebApi.Contracts.Notifications;
using WebApi.Domain.Notifications;
using WebApi.Infrastructure.Settings;
namespace WebApi.Infrastructure.Notifications;
public sealed class NotificationDeploymentSettings
{
    internal IReadOnlyDictionary<string,string> SecretFiles {get;}
    private readonly HashSet<string> recipients,domains,smtp,webhooks;
    public IReadOnlyList<string> AllowedRecipients {get;}
    public IReadOnlyList<string> AllowedDomains {get;}
    public IReadOnlyList<string> AllowedPrivateCidrs {get;}
    public Uri? ConsoleBaseUrl {get;}
    public int Concurrency {get;private init;}=4;
    public int MaxClaimsPerPoll=>50;
    public int PollSeconds=>1;
    public int LeaseSeconds=>60;
    public int SendTimeoutSeconds=>10;
    public int MaxResponseBytes=>4096;
    public int MaxRetryAfterSeconds=>3600;
    public NotificationDeploymentSettings(IReadOnlyDictionary<string,string>? secretFiles=null,IReadOnlyList<string>? allowedRecipients=null,IReadOnlyList<string>? allowedDomains=null,IReadOnlyList<string>? smtpEndpoints=null,IReadOnlyList<string>? webhookUrls=null,IReadOnlyList<string>? privateCidrs=null,Uri? consoleBaseUrl=null)
    {
        var mappings=new Dictionary<string,string>(StringComparer.Ordinal);
        foreach(var entry in secretFiles??new Dictionary<string,string>())
            if(!SystemSettingsValidator.ValidReference(entry.Key)||!Path.IsPathFullyQualified(entry.Value)||!mappings.TryAdd(entry.Key,entry.Value))throw BadDeployment();
        SecretFiles=new System.Collections.ObjectModel.ReadOnlyDictionary<string,string>(mappings);
        recipients=new((allowedRecipients??[]).Select(NotificationPolicyValidator.NormalizeEmail),StringComparer.Ordinal);
        domains=new((allowedDomains??[]).Select(NormalizeDomain),StringComparer.Ordinal);
        smtp=new(StringComparer.Ordinal);webhooks=new(StringComparer.Ordinal);
        foreach(var endpoint in smtpEndpoints??[])
        {
            if(!Uri.TryCreate("smtp://"+endpoint,UriKind.Absolute,out var uri)||uri.UserInfo.Length!=0||uri.AbsolutePath!="/"||uri.Query.Length!=0||uri.Fragment.Length!=0||uri.Port is <1 or >65535||Uri.CheckHostName(uri.Host)==UriHostNameType.Unknown)throw BadDeployment();
            smtp.Add(NormalizeHost(uri.IdnHost)+":"+uri.Port.ToString(CultureInfo.InvariantCulture));
        }
        foreach(var endpoint in webhookUrls??[])
        {
            if(!Uri.TryCreate(endpoint,UriKind.Absolute,out var uri)||uri.Scheme!="https"||uri.UserInfo.Length!=0||uri.Query.Length!=0||uri.Fragment.Length!=0||uri.Host.Length==0)throw BadDeployment();
            webhooks.Add(uri.AbsoluteUri);
        }
        if(consoleBaseUrl is not null&&(!consoleBaseUrl.IsAbsoluteUri||consoleBaseUrl.Scheme is not("http" or "https")||consoleBaseUrl.UserInfo.Length!=0||consoleBaseUrl.Query.Length!=0||consoleBaseUrl.Fragment.Length!=0))throw BadDeployment();
        ConsoleBaseUrl=consoleBaseUrl;AllowedRecipients=Array.AsReadOnly(recipients.Order(StringComparer.Ordinal).ToArray());AllowedDomains=Array.AsReadOnly(domains.Order(StringComparer.Ordinal).ToArray());AllowedPrivateCidrs=Array.AsReadOnly((privateCidrs??[]).ToArray());
    }
    public static NotificationDeploymentSettings Read(IConfiguration configuration,IHostEnvironment environment)
    {
        var section=configuration.GetSection("Notifications");var raw=section["SecretFilesJson"]??"{}";
        try
        {
            if(raw.Length>65536)throw BadDeployment();using var json=JsonDocument.Parse(raw,new JsonDocumentOptions{MaxDepth=2});var files=new Dictionary<string,string>(StringComparer.Ordinal);
            foreach(var field in json.RootElement.EnumerateObject())if(field.Value.ValueKind!=JsonValueKind.String||!files.TryAdd(field.Name,field.Value.GetString()!))throw BadDeployment();
            var concurrency=section.GetValue<int?>("Concurrency")??4;if(concurrency is <1 or >8)throw BadDeployment();
            var url=section["ConsoleBaseUrl"];Uri? console=null;if(url is not null&&!Uri.TryCreate(url,UriKind.Absolute,out console))throw BadDeployment();
            return new(files,section.GetSection("AllowedRecipients").Get<string[]>(),section.GetSection("AllowedRecipientDomains").Get<string[]>(),section.GetSection("AllowedSmtpEndpoints").Get<string[]>(),section.GetSection("AllowedWebhookUrls").Get<string[]>(),section.GetSection("AllowedPrivateCidrs").Get<string[]>(),console){Concurrency=concurrency};
        }
        catch(Exception error)when(error is JsonException or InvalidOperationException or ArgumentException or ApiException){throw BadDeployment();}
    }
    public void RequireAllowedRecipient(string email)
    {
        var normalized=NotificationPolicyValidator.NormalizeEmail(email);
        if(!recipients.Contains(normalized)&&!domains.Contains(normalized[(normalized.LastIndexOf('@')+1)..]))throw Rejected();
    }
    public void RequireAllowedEndpoint(NotificationChannel channel,string host,int port,Uri? url=null)
    {
        if(port is <1 or >65535||string.IsNullOrEmpty(host)||host.Any(char.IsWhiteSpace))throw Rejected();
        if(channel==NotificationChannel.Email)
        {if(!smtp.Contains(NormalizeHost(host)+":"+port.ToString(CultureInfo.InvariantCulture)))throw Rejected();}
        else if(channel==NotificationChannel.Webhook)
        {if(url is null||url.Scheme!="https"||url.UserInfo.Length!=0||url.Query.Length!=0||url.Fragment.Length!=0||NormalizeHost(url.IdnHost)!=NormalizeHost(host)||url.Port!=port||!webhooks.Contains(url.AbsoluteUri))throw Rejected();}
        else throw Rejected();
    }
    private static string NormalizeDomain(string value)=>NotificationPolicyValidator.NormalizeEmail("allowed@"+value)[8..];
    private static string NormalizeHost(string value)
    {
        if(IPAddress.TryParse(value.Trim('[',']'),out var address))return address.ToString();
        try{return new IdnMapping{UseStd3AsciiRules=true}.GetAscii(value).ToLowerInvariant();}catch(ArgumentException){throw Rejected();}
    }
    private static ApiException Rejected()=>new(422,"notification_target_not_allowed","通知目标未获部署策略允许。");
    private static InvalidOperationException BadDeployment()=>new("Invalid notification deployment configuration.");
}
