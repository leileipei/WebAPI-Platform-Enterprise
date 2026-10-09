using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using System.Net.Mail;
using WebApi.Contracts.Common;
using WebApi.Contracts.Settings;
namespace WebApi.Infrastructure.Settings;
public static class SystemSettingsValidator
{
    public static readonly string[] Groups=["security","release","gateway","audit","notification"];
    public static readonly IReadOnlyDictionary<string,string[]> Fields=new Dictionary<string,string[]> {
        ["security"]=["sessionTtlMinutes","passwordMinLength","passwordComplexity","allowedOrigins","loginIpMaxAttempts","loginIpWindowSeconds","loginAccountMaxAttempts","loginAccountWindowSeconds"],
        ["release"]=["productionApprovalLevels","snapshotRetentionCount"],
        ["gateway"]=["defaultRouteTimeoutMs","maxRequestBodyMb","configRefreshIntervalSeconds"],
        ["audit"]=["auditRetentionDays","auditExportEnabled"],
        ["notification"]=["smtpHost","smtpPort","fromEmail","smtpSecretRef","webhookUrl","webhookSecretRef","smtpEnabled","webhookEnabled","smtpSecurity"]};
    private static ApiException Invalid(string field)=>new(422,"invalid_settings",$"{field}：字段格式、范围或完整性不符合要求。");
    public static SettingsMutation Parse(string group,ReadOnlyMemory<byte> json)=>new(ParseEnvelope(group,json,false).GetProperty("values").Clone());
    public static SaveSettingsRequest ParseSave(string group,ReadOnlyMemory<byte> json){var root=ParseEnvelope(group,json,true);return new(root.GetProperty("values").Clone(),Text(root,"confirmationToken",false)??throw Invalid("confirmationToken"));}
    private static JsonElement ParseEnvelope(string group,ReadOnlyMemory<byte> json,bool save)
    {
        if(!Fields.ContainsKey(group)) throw new ApiException(404,"settings_group_missing","设置分组不存在。");
        if(json.Length>8192) throw new ApiException(413,"settings_too_large","设置请求不能超过8192字节。");
        try {using var doc=JsonDocument.Parse(json,new JsonDocumentOptions{MaxDepth=16});var root=doc.RootElement;Unique(root);Allowed(root,save?["values","confirmationToken"]:["values"]);if(!root.TryGetProperty("values",out var values)) throw Invalid("values");Allowed(values,Fields[group]);return root.Clone();}
        catch(JsonException){throw Invalid("json");}
    }
    private static void Unique(JsonElement value){if(value.ValueKind==JsonValueKind.Object){var seen=new HashSet<string>(StringComparer.Ordinal);foreach(var p in value.EnumerateObject()){if(!seen.Add(p.Name)) throw Invalid("重复属性");Unique(p.Value);}}else if(value.ValueKind==JsonValueKind.Array)foreach(var v in value.EnumerateArray())Unique(v);}
    private static void Allowed(JsonElement value,string[] fields){if(value.ValueKind!=JsonValueKind.Object) throw Invalid("对象");foreach(var p in value.EnumerateObject())if(!fields.Contains(p.Name,StringComparer.Ordinal)) throw Invalid("未知属性");}
    private static JsonElement Get(JsonElement values,string key){if(!values.TryGetProperty(key,out var value))throw Invalid(key);return value;}
    private static int Number(JsonElement values,string key,int min,int max){var v=Get(values,key);if(v.ValueKind!=JsonValueKind.Number||!v.TryGetInt32(out var n)||n<min||n>max)throw Invalid(key);return n;}
    private static string? Text(JsonElement values,string key,bool nullable=true){var v=Get(values,key);if(nullable&&v.ValueKind==JsonValueKind.Null)return null;if(v.ValueKind!=JsonValueKind.String)throw Invalid(key);var s=v.GetString()!;if(s.Any(char.IsControl))throw Invalid(key);if(nullable&&s.Length==0)return null;return s;}
    private static string? Reference(JsonElement values,string key,string? current){var operation=Get(values,key);Allowed(operation,["operation","reference"]);var op=Text(operation,"operation",false);string? r=operation.TryGetProperty("reference",out _)?Text(operation,"reference"):null;if(op!="Replace"&&operation.TryGetProperty("reference",out _))throw Invalid(key);return op switch{"Keep"=>current,"Clear"=>null,"Replace" when ValidReference(r)=>r,_=>throw Invalid(key)};}
    public static bool ValidReference(string? value)=>value is not null&&value.Length<=2048&&!value.Any(char.IsControl)&&Uri.TryCreate(value,UriKind.Absolute,out var uri)&&uri.Scheme=="vault"&&uri.Host.Length>0&&Uri.CheckHostName(uri.Host)!=UriHostNameType.Unknown&&uri.UserInfo.Length==0&&uri.Query.Length==0&&uri.Fragment.Length==0&&uri.AbsolutePath.Trim('/').Length>0;
    public static string? ReferenceProvider(string? value)=>value is null?null:new Uri(value).Host;
    public static ValidatedSettings Resolve(string group,SettingsMutation mutation,SettingsValues current)
    {
        if(!Fields.TryGetValue(group,out var fields))throw new ApiException(404,"settings_group_missing","设置分组不存在。");var v=mutation.Values;Unique(v);Allowed(v,fields);foreach(var key in group=="notification"?fields[..6]:fields)Get(v,key);
        SettingsValues value;
        switch(group){
        case "security":
            var ttl=Number(v,"sessionTtlMinutes",5,1440);var length=Number(v,"passwordMinLength",16,128);var complexity=Text(v,"passwordComplexity",false)!;if(complexity is not ("LengthOnly" or "LettersAndDigits" or "UpperLowerDigitSpecial"))throw Invalid("passwordComplexity");
            var origins=Get(v,"allowedOrigins");if(origins.ValueKind!=JsonValueKind.Array||origins.GetArrayLength()>32)throw Invalid("allowedOrigins");var list=new List<string>();foreach(var origin in origins.EnumerateArray()){if(origin.ValueKind!=JsonValueKind.String)throw Invalid("allowedOrigins");var text=origin.GetString()!;if(text.Length>2048||text.Any(char.IsControl)||text.Contains('*')||!Uri.TryCreate(text,UriKind.Absolute,out var uri)||uri.Scheme is not ("https" or "http")||uri.Host.Length==0||uri.UserInfo.Length>0||uri.Query.Length>0||uri.Fragment.Length>0||uri.AbsolutePath!="/")throw Invalid("allowedOrigins");list.Add(uri.GetLeftPart(UriPartial.Authority));}value=new SecuritySettings(ttl,length,complexity,list.Distinct(StringComparer.Ordinal).ToArray(),Number(v,"loginIpMaxAttempts",1,10000),Number(v,"loginIpWindowSeconds",1,3600),Number(v,"loginAccountMaxAttempts",1,10000),Number(v,"loginAccountWindowSeconds",1,3600));break;
        case "release":value=new ReleaseSettings(Number(v,"productionApprovalLevels",2,2),Number(v,"snapshotRetentionCount",10,10000));break;
        case "gateway":value=new GatewaySettings(Number(v,"defaultRouteTimeoutMs",1,300000),Number(v,"maxRequestBodyMb",1,256),Number(v,"configRefreshIntervalSeconds",1,60));break;
        case "audit":var enabled=Get(v,"auditExportEnabled");if(enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False))throw Invalid("auditExportEnabled");value=new AuditSettings(Number(v,"auditRetentionDays",30,3650),enabled.GetBoolean());break;
        default:
            var old=(NotificationSettings)current;var host=Text(v,"smtpHost");var portValue=Get(v,"smtpPort");int? port=portValue.ValueKind==JsonValueKind.Null?null:Number(v,"smtpPort",1,65535);var from=Text(v,"fromEmail");var smtpRef=Reference(v,"smtpSecretRef",old.SmtpSecretRef);var url=Text(v,"webhookUrl");var webhookRef=Reference(v,"webhookSecretRef",old.WebhookSecretRef);
            if(host is not null&&(host.Length>253||host.Any(char.IsWhiteSpace)||Uri.CheckHostName(host)==UriHostNameType.Unknown))throw Invalid("smtpHost");
            if(from is not null&&(from.Length>254||!MailAddress.TryCreate(from,out var address)||address.Address!=from))throw Invalid("fromEmail");
            if(new object?[]{host,port,from,smtpRef}.Any(x=>x is not null)&&new object?[]{host,port,from,smtpRef}.Any(x=>x is null))throw Invalid("SMTP完整性");
            if(url is not null&&(url.Length>2048||!Uri.TryCreate(url,UriKind.Absolute,out var hook)||hook.Scheme!="https"||hook.Host.Length==0||hook.UserInfo.Length>0||hook.Query.Length>0||hook.Fragment.Length>0))throw Invalid("webhookUrl");
            if((url is null)!=(webhookRef is null))throw Invalid("Webhook完整性");
            var smtpEnabled=Flag(v,"smtpEnabled",old.SmtpEnabled);var webhookEnabled=Flag(v,"webhookEnabled",old.WebhookEnabled);var security=v.TryGetProperty("smtpSecurity",out _)?Text(v,"smtpSecurity",false):old.SmtpSecurity;
            if(security is not("StartTlsRequired" or "TlsOnConnect")||smtpEnabled&&host is null||webhookEnabled&&url is null)throw Invalid("通知启用与TLS");
            value=new NotificationSettings(host,port,from,smtpRef,url,webhookRef,smtpEnabled,webhookEnabled,security);break;
        }
        var element=SettingsValueCodec.Json(value);var bytes=CanonicalJson.Serialize(element);var before=SettingsValueCodec.Json(current);var changed=fields.Where(f=>before.GetProperty(f).GetRawText()!=element.GetProperty(f).GetRawText()).ToArray();return new(value,Encoding.UTF8.GetString(bytes),Convert.ToHexStringLower(SHA256.HashData(bytes)),changed);
    }
    private static bool Flag(JsonElement values,string key,bool current)
    {if(!values.TryGetProperty(key,out var value))return current;if(value.ValueKind is not(JsonValueKind.True or JsonValueKind.False))throw Invalid(key);return value.GetBoolean();}
}
