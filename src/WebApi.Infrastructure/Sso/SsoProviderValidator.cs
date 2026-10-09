using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WebApi.Contracts.Common;
using WebApi.Contracts.Sso;
namespace WebApi.Infrastructure.Sso;
public static class SsoProviderValidator
{
    private static ApiException Invalid(string field)=>new(422,"invalid_sso_configuration",$"{field}：SSO字段格式、范围或完整性不符合要求。");
    public static SaveSsoProviderRequest Parse(ReadOnlyMemory<byte> utf8)=>Parse(utf8,false);
    public static SaveSsoProviderRequest Parse(ReadOnlyMemory<byte> utf8,bool allowFixtureHttp)
    {
        if(utf8.Length>16384)throw new ApiException(413,"sso_too_large","SSO请求不能超过16384字节。");
        try {
            using var doc=JsonDocument.Parse(utf8,new JsonDocumentOptions{MaxDepth=8});var root=doc.RootElement;Unique(root);
            Allowed(root,["organizationId","name","issuer","clientId","secretRef","scopes","claimMapping"]);
            var org=Required(root,"organizationId");Guid? organizationId=org.ValueKind==JsonValueKind.Null?null:org.ValueKind==JsonValueKind.String&&org.TryGetGuid(out var id)&&id!=Guid.Empty?id:throw Invalid("organizationId");
            var name=Text(root,"name",128);var issuer=Text(root,"issuer",1024);var client=Text(root,"clientId",256);var reference=Text(root,"secretRef",512);
            if(!Uri.TryCreate(issuer,UriKind.Absolute,out var uri)||uri.Scheme!="https"&&!(allowFixtureHttp&&uri.Scheme=="http")||uri.Host.Length==0||uri.UserInfo.Length>0||uri.Query.Length>0||uri.Fragment.Length>0||issuer.Any(char.IsWhiteSpace)||issuer.Contains('\\'))throw Invalid("issuer");
            if(!Regex.IsMatch(reference,"^file://sso/[A-Za-z][A-Za-z0-9_.-]{0,127}$",RegexOptions.CultureInvariant))throw Invalid("secretRef");
            var scopes=Required(root,"scopes");if(scopes.ValueKind!=JsonValueKind.Array||scopes.GetArrayLength() is <1 or >16)throw Invalid("scopes");
            var list=new List<string>();foreach(var scope in scopes.EnumerateArray()) {
                if(scope.ValueKind!=JsonValueKind.String)throw Invalid("scopes");var value=scope.GetString()!;
                if(value.Length is <1 or >128||value.Any(c=>c<'!'||c>'~'||c is '"' or '\\'))throw Invalid("scopes");
                if(!list.Contains(value,StringComparer.Ordinal))list.Add(value);
            }
            if(!list.Contains("openid")||list.Contains("offline_access"))throw Invalid("scopes");
            var mapping=Required(root,"claimMapping");Allowed(mapping,["displayName","email"],false);
            if(Encoding.UTF8.GetByteCount(mapping.GetRawText())>4096)throw Invalid("claimMapping");
            string? Claim(string key){if(!mapping.TryGetProperty(key,out var value)||value.ValueKind==JsonValueKind.Null)return null;if(value.ValueKind!=JsonValueKind.String)throw Invalid(key);var claim=value.GetString()!;if(!Regex.IsMatch(claim,"^[A-Za-z_][A-Za-z0-9_:-]{0,127}$",RegexOptions.CultureInvariant))throw Invalid(key);return claim;}
            return new(organizationId,name,issuer,client,reference,list,new(Claim("displayName"),Claim("email")));
        } catch(JsonException){throw Invalid("json");}
    }
    public static bool IsSafeReturnPath(string? path)
    {
        if(string.IsNullOrEmpty(path)||path.Length>2048||!path.StartsWith('/')||path.StartsWith("//",StringComparison.Ordinal)||path.Any(char.IsControl)||path.Contains('\\')||path.Contains('#'))return false;
        var route=path.Split('?',2)[0];if(route.Contains('%')||route.Split('/').Any(s=>s is "." or ".."))return false;
        string[] roots=["delivery","organizations","projects","environments","apis","imports","routes","clusters","applications","releases","approvals","snapshots","nodes","observability","users","roles","permissions","scopes","audit","settings","coverage"];
        return roots.Any(root=>route=="/"+root||route.StartsWith("/"+root+"/",StringComparison.Ordinal));
    }
    private static JsonElement Required(JsonElement root,string key)=>root.TryGetProperty(key,out var value)?value:throw Invalid(key);
    private static string Text(JsonElement root,string key,int max){var value=Required(root,key);if(value.ValueKind!=JsonValueKind.String)throw Invalid(key);var text=value.GetString()!;if(string.IsNullOrWhiteSpace(text)||text.Length>max||text.Any(char.IsControl))throw Invalid(key);return text;}
    private static void Allowed(JsonElement root,string[] fields,bool require=true){if(root.ValueKind!=JsonValueKind.Object)throw Invalid("json");foreach(var property in root.EnumerateObject())if(!fields.Contains(property.Name,StringComparer.Ordinal))throw Invalid(property.Name);if(require)foreach(var field in fields)Required(root,field);}
    private static void Unique(JsonElement value){if(value.ValueKind==JsonValueKind.Object){var names=new HashSet<string>(StringComparer.Ordinal);foreach(var property in value.EnumerateObject()){if(!names.Add(property.Name))throw Invalid("重复属性");Unique(property.Value);}}else if(value.ValueKind==JsonValueKind.Array)foreach(var item in value.EnumerateArray())Unique(item);}
}
