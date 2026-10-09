using WebApi.Contracts.Governance;
using WebApi.Contracts.Common;
using System.Text.RegularExpressions;
namespace WebApi.Domain.Governance;
public static class EnvironmentAccessAddressValidator
{
    private static readonly Regex originShape=new("^https?://[^/]+/?$",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
    private static readonly Regex prefixShape=new("^/[A-Za-z0-9._~-]+(?:/[A-Za-z0-9._~-]+)*\\z",RegexOptions.CultureInvariant);
    private static ApiException Invalid(string message)=>new(422,"invalid_access_address",message);
    public static EnvironmentAccessSettings Normalize(EnvironmentAccessSettings input,bool isProduction)
    {
        var publicOrigin=Origin(input.PublicOrigin);
        if(isProduction&&publicOrigin is not null&&!publicOrigin.StartsWith("https://",StringComparison.Ordinal))
            throw Invalid("生产环境的对外访问地址必须使用 HTTPS。");
        return new(publicOrigin,Origin(input.InternalOrigin),Prefix(input.BasePath));
    }
    private static string? Origin(string? value)
    {
        if(string.IsNullOrEmpty(value))return null;
        if(value.Length>2048||value.Any(c=>char.IsControl(c)||char.IsWhiteSpace(c))||value.IndexOfAny(['\\','?','#','@','%'])>=0||!originShape.IsMatch(value)
            ||!Uri.TryCreate(value,UriKind.Absolute,out var uri)||uri.Scheme is not("http" or "https")||string.IsNullOrEmpty(uri.Host)||uri.HostNameType==UriHostNameType.Unknown)
            throw Invalid("访问地址必须为 HTTP/HTTPS 主机地址，可包含端口，不能包含账号、路径、查询或片段。");
        string host;
        try{host=uri.IdnHost.ToLowerInvariant();}catch(UriFormatException){throw Invalid("访问地址主机不合法。");}
        if(uri.HostNameType==UriHostNameType.IPv6)host="["+host.Trim('[',']')+"]";
        var result=uri.Scheme+"://"+host+(uri.IsDefaultPort?"":":"+uri.Port);
        if(result.Length>2048)throw Invalid("访问地址超过长度限制。");
        return result;
    }
    private static string Prefix(string? value)
    {
        if(string.IsNullOrEmpty(value)||value=="/")return "/";
        if(value.Length>512||value.Any(char.IsControl)||value.EndsWith("//",StringComparison.Ordinal))throw Invalid("外部路径前缀不合法或超过长度限制。");
        var normalized=value.EndsWith('/')?value[..^1]:value;
        if(!prefixShape.IsMatch(normalized)||normalized.Split('/').Skip(1).Any(p=>p is "." or ".."))
            throw Invalid("外部路径前缀必须以 / 开头，仅允许安全路径段；不能包含空段、编码、查询或相对路径。");
        return normalized;
    }
}
