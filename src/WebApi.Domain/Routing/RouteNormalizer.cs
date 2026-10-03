using System.Text.RegularExpressions;
using WebApi.Contracts.Common;
namespace WebApi.Domain.Routing;
public static class RouteNormalizer
{
    private static readonly Regex parameter=new("^\\{(\\*{0,2})([A-Za-z_][A-Za-z0-9_]*)\\}$",RegexOptions.CultureInvariant);
    public static string Normalize(string path)
    {
        if(string.IsNullOrWhiteSpace(path)||path.Length>1024||!path.StartsWith('/')||path.Contains('?')||path.Contains('#')||path.Contains('\\')||path.Contains('%')||path.Any(char.IsControl)) throw new ApiException(422,"invalid_path","路径必须以/开头，不能包含查询、编码片段或控制字符。");
        path=path=="/"?path:path.TrimEnd('/');var parts=path.Split('/');var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for(var i=1;i<parts.Length;i++)
        {
            var part=parts[i];if((part.Length==0&&path!="/")||part is "." or "..") throw new ApiException(422,"invalid_path","路径段不合法。");
            if(part.Contains('{')||part.Contains('}'))
            {
                var match=parameter.Match(part);if(!match.Success||!names.Add(match.Groups[2].Value)||match.Groups[1].Length>0&&i!=parts.Length-1) throw new ApiException(422,"invalid_path","仅支持整段命名参数和末尾通配符，参数名不能重复。");
                parts[i]=match.Groups[1].Length>0?"{*}":"{}";
            }
            else if(part.Contains('*')) throw new ApiException(422,"invalid_path","请使用末尾命名通配符。");
        }
        return string.Join('/',parts).ToLowerInvariant();
    }
    public static int MatchOrder(string path,int priority)
    {
        if(priority is <0 or >999999) throw new ApiException(422,"invalid_priority","优先级必须在0到999999之间。");
        var normalized=Normalize(path);var kind=normalized.Contains("{*}")?2:normalized.Contains("{}")?1:0;return kind*2000000-priority;
    }
}
