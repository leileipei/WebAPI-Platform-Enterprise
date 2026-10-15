using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using WebApi.Contracts.Common;
namespace WebApi.ControlPlane.Security;

public static class PasswordRequestReader
{
    private static readonly JsonSerializerOptions Options=new(JsonSerializerDefaults.Web){PropertyNameCaseInsensitive=false,UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow,MaxDepth=8};
    public static void RequireCommandKey(string key)
    {
        if(string.IsNullOrWhiteSpace(key)||key.Length>128||!Regex.IsMatch(key,"^[A-Za-z0-9_.:-]+$"))
            throw new ApiException(422,"idempotency_required","该命令需要有效的Idempotency-Key。");
    }
    public static async Task<T> ReadAsync<T>(HttpContext context,int maximum,CancellationToken ct)
    {
        static ApiException Large()=>new(413,"password_request_too_large","密码请求超过允许大小。");
        if(context.Request.ContentLength>maximum)throw Large();
        using var buffer=new MemoryStream();var bytes=new byte[4096];
        while(true)
        {
            var count=await context.Request.Body.ReadAsync(bytes,ct);if(count==0)break;
            if(buffer.Length+count>maximum)throw Large();buffer.Write(bytes,0,count);
        }
        try
        {
            using var json=JsonDocument.Parse(buffer.GetBuffer().AsMemory(0,checked((int)buffer.Length)),new JsonDocumentOptions{MaxDepth=8});
            if(json.RootElement.ValueKind!=JsonValueKind.Object)throw new JsonException();
            var fields=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var property in json.RootElement.EnumerateObject())if(!fields.Add(property.Name))throw new JsonException();
            return json.RootElement.Deserialize<T>(Options)??throw new JsonException();
        }
        catch(JsonException){throw new ApiException(422,"invalid_password_request","密码请求结构不合法。");}
    }
}
