using Microsoft.AspNetCore.Http;
namespace WebApi.HttpSecurity;
public static class ForwardingHeaderSanitizer
{
    public static void Remove(IHeaderDictionary headers)
    {
        foreach(var key in headers.Keys.Where(k=>k.Equals("Forwarded",StringComparison.OrdinalIgnoreCase)||k.StartsWith("X-Forwarded-",StringComparison.OrdinalIgnoreCase)||k.StartsWith("X-Original-",StringComparison.OrdinalIgnoreCase)).ToArray())headers.Remove(key);
    }
}
