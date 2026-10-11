using System.Text.RegularExpressions;
using WebApi.Contracts.Governance;
using WebApi.Domain.Routing;
namespace WebApi.Domain.Governance;
public static class ClientApiAddressBuilder
{
    private static readonly Regex parameter=new("\\{(\\*{0,2})([A-Za-z_][A-Za-z0-9_]*)\\}",RegexOptions.CultureInvariant);
    public static string BuildTemplate(string origin,string basePath,string routePath)
    {
        var settings=EnvironmentAccessAddressValidator.Normalize(new(origin,null,basePath),false);
        if(settings.PublicOrigin is null)throw new WebApi.Contracts.Common.ApiException(422,"access_address_unconfigured","尚未配置访问地址。");
        RouteNormalizer.Normalize(routePath);
        return settings.PublicOrigin+(settings.BasePath=="/"?"":settings.BasePath)+(routePath=="/"?"/":routePath.TrimEnd('/'));
    }
    public static string BuildExample(string origin,string basePath,string routePath,IReadOnlyDictionary<string,string> values)
    {
        var template=BuildTemplate(origin,basePath,routePath);
        var matches=parameter.Matches(routePath);
        if(matches.Any(m=>m.Groups[1].Length>0||!values.ContainsKey(m.Groups[2].Value)))return template;
        return parameter.Replace(template,m=>Uri.EscapeDataString(values[m.Groups[2].Value]));
    }
}
