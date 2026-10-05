using System.Text.Json;
using System.Text.RegularExpressions;
using WebApi.Contracts.Common;
using WebApi.Contracts.Sso;
namespace WebApi.Infrastructure.Sso;
public static class SsoIdentityValidator
{
    public static CreateSsoUserRequest ParseCreate(ReadOnlyMemory<byte> bytes)
    {
        using var document=Parse(bytes,["username","displayName","email","providerId","subject"]);
        var root=document.RootElement;
        var request=new CreateSsoUserRequest(Text(root,"username"),Text(root,"displayName"),Optional(root,"email"),Id(root,"providerId"),Text(root,"subject"));
        Validate(request);return request;
    }
    public static UpdateExternalIdentityRequest ParseSave(ReadOnlyMemory<byte> bytes)
    {
        using var document=Parse(bytes,["providerId","subject","enabled"]);var root=document.RootElement;
        if(!root.TryGetProperty("enabled",out var enabled)||enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False))throw Invalid();
        var request=new UpdateExternalIdentityRequest(Id(root,"providerId"),Text(root,"subject"),enabled.GetBoolean());ValidateSubject(request.Subject);return request;
    }
    public static void Validate(CreateSsoUserRequest request)
    {
        if(string.IsNullOrWhiteSpace(request.Username)||request.Username.Length>128||!Regex.IsMatch(request.Username,"^[A-Za-z0-9][A-Za-z0-9_.@+-]*$")||
            string.IsNullOrWhiteSpace(request.DisplayName)||request.DisplayName.Length>128||request.DisplayName.Any(char.IsControl)||
            request.Email?.Length>256||request.Email?.Any(char.IsControl)==true||request.ProviderId==Guid.Empty)throw Invalid();
        ValidateSubject(request.Subject);
    }
    public static void ValidateSubject(string subject)
    {
        if(string.IsNullOrWhiteSpace(subject)||subject.Length>255||subject.Any(char.IsControl))throw Invalid();
    }
    private static JsonDocument Parse(ReadOnlyMemory<byte> bytes,string[] fields)
    {
        if(bytes.Length>8192)throw new ApiException(413,"sso_request_too_large","身份绑定请求不能超过8192字节。");
        JsonDocument document;try{document=JsonDocument.Parse(bytes,new(){MaxDepth=4});}catch(JsonException){throw Invalid();}
        if(document.RootElement.ValueKind!=JsonValueKind.Object){document.Dispose();throw Invalid();}
        var seen=new HashSet<string>(StringComparer.Ordinal);
        foreach(var property in document.RootElement.EnumerateObject())
            if(!fields.Contains(property.Name,StringComparer.Ordinal)||!seen.Add(property.Name)){document.Dispose();throw Invalid();}
        return document;
    }
    private static string Text(JsonElement root,string name){if(!root.TryGetProperty(name,out var value)||value.ValueKind!=JsonValueKind.String)throw Invalid();return value.GetString()!;}
    private static string? Optional(JsonElement root,string name){if(!root.TryGetProperty(name,out var value)||value.ValueKind==JsonValueKind.Null)return null;if(value.ValueKind!=JsonValueKind.String)throw Invalid();return value.GetString();}
    private static Guid Id(JsonElement root,string name){if(!root.TryGetProperty(name,out var value)||value.ValueKind!=JsonValueKind.String||!value.TryGetGuid(out var id)||id==Guid.Empty)throw Invalid();return id;}
    private static ApiException Invalid()=>new(422,"invalid_external_identity","用户或身份绑定字段不合法。");
}
