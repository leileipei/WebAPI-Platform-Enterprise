using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http.Features;
using WebApi.Contracts.Policies;

namespace WebApi.Gateway.Policies;

public sealed record TransformPlanResult(TransformPlan? Plan,int? ErrorStatus,string? Reason);
public sealed class TransformPlan(PathString path,QueryString query,TransformConfiguration configuration)
{
    public PathString Path {get;}=path;
    public QueryString Query {get;}=query;
    public IReadOnlyList<HeaderTransformOperation> RequestHeaders {get;}=Array.AsReadOnly(configuration.RequestHeaders.ToArray());
    public IReadOnlyList<HeaderTransformOperation> ResponseHeaders {get;}=Array.AsReadOnly(configuration.ResponseHeaders.ToArray());
    public bool RewritePath {get;}=configuration.Path is not null;
    public bool RewriteQuery {get;}=configuration.Query.Count>0;
}
public static class TransformPlanner
{
    public static TransformPlanResult Plan(HttpRequest request,TransformConfiguration configuration)
    {
        var path=request.Path;
        if(configuration.Path is { } rewrite)
        {
            var raw=request.HttpContext.Features.Get<IHttpRequestFeature>()?.RawTarget;
            var rawPath=string.IsNullOrEmpty(raw)?path.Value??"/":raw.Split('?')[0];
            if(!ValidPath(rawPath)||!ValidPath(path.Value??"/"))return new(null,422,"transform_path_invalid");
            var value=path.Value??"/";
            if(rewrite.RemovePrefix is { } remove&&remove!="/")
            {
                if(!value.Equals(remove,StringComparison.Ordinal)&&!value.StartsWith(remove+"/",StringComparison.Ordinal))return new(null,422,"transform_path_mismatch");
                value=value[remove.Length..];if(value.Length==0)value="/";
            }
            if(rewrite.AddPrefix is { } add&&add!="/")value=add+"/"+value.TrimStart('/');
            path=new(value);
        }
        var query=request.QueryString;
        if(configuration.Query.Count>0)
        {
            // Preserve parameter order, duplicates and case; ASP.NET Query collections fold case.
            var pairs=new List<(string Name,string Value)>();
            foreach(var part in (query.Value??"").TrimStart('?').Split('&',StringSplitOptions.RemoveEmptyEntries))
            {var separator=part.IndexOf('=');pairs.Add((WebUtility.UrlDecode(separator<0?part:part[..separator]),WebUtility.UrlDecode(separator<0?"":part[(separator+1)..])));}
            foreach(var operation in configuration.Query)
            {pairs.RemoveAll(p=>p.Name.Equals(operation.Name,StringComparison.Ordinal));if(operation.Action==TransformAction.Set)pairs.Add((operation.Name,operation.Value!));}
            query=pairs.Count==0?QueryString.Empty:new("?"+string.Join("&",pairs.Select(p=>Uri.EscapeDataString(p.Name)+"="+Uri.EscapeDataString(p.Value))));
        }
        if(Encoding.UTF8.GetByteCount(path.ToUriComponent())+Encoding.UTF8.GetByteCount(query.Value??"")>16384)return new(null,414,"transform_budget_exceeded");
        return new(new(path,query,configuration),null,null);
    }
    private static bool ValidPath(string path)
    {
        if(!path.StartsWith('/')||path.Contains('\\')||path.Contains("//",StringComparison.Ordinal)||path.Any(char.IsControl)||path.Split('/').Any(s=>s is "." or ".."))return false;
        for(var i=0;i<path.Length;i++)if(path[i]=='%')
        {
            if(i+2>=path.Length||!byte.TryParse(path.AsSpan(i+1,2),System.Globalization.NumberStyles.HexNumber,System.Globalization.CultureInfo.InvariantCulture,out var code)||code is 0x2f or 0x5c or 0x2e or 0x25)return false;
            i+=2;
        }
        return true;
    }
}
