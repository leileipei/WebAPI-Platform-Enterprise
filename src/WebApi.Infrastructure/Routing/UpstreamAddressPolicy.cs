using WebApi.Contracts.Common;
using Microsoft.Extensions.Configuration;
namespace WebApi.Infrastructure.Routing;
public sealed class UpstreamAddressPolicy(IReadOnlyList<string> allowedOrigins)
{
    public static string[] ReadAllowedOrigins(IConfiguration configuration)
    {
        var origins=configuration.GetSection("Upstream:AllowedOrigins").Get<string[]>();
        if(!bool.TryParse(configuration["Upstream:RequireExplicitAllowedOrigins"],out var explicitOrigins)||!explicitOrigins)return origins??["http://test-backend:8080"];
        origins??=[];
        foreach(var origin in origins)if(!Uri.TryCreate(origin,UriKind.Absolute,out var uri)||uri.Scheme is not("http" or "https")||uri.UserInfo.Length>0||uri.Query.Length>0||uri.Fragment.Length>0||uri.AbsolutePath!="/"||origin!=uri.GetLeftPart(UriPartial.Authority))throw new InvalidOperationException("Explicit upstream allowlist requires canonical HTTP(S) origins.");
        return origins;
    }
    public string Validate(string address)
    {
        if(!Uri.TryCreate(address,UriKind.Absolute,out var uri)||uri.Scheme is not ("http" or "https")||uri.UserInfo.Length>0||uri.Fragment.Length>0||uri.Query.Length>0||address.Length>2048)
            throw new ApiException(422,"invalid_upstream","上游必须是无用户名密码、查询或片段的HTTP(S)地址。");
        var origin=uri.GetLeftPart(UriPartial.Authority);
        if(!allowedOrigins.Any(x=>Uri.TryCreate(x,UriKind.Absolute,out var allow)&&allow.Scheme==uri.Scheme&&allow.Host.Equals(uri.Host,StringComparison.OrdinalIgnoreCase)&&allow.Port==uri.Port&&allow.UserInfo.Length==0)) throw new ApiException(422,"upstream_not_allowed","上游地址不在平台配置的允许列表中。");
        return uri.AbsoluteUri.TrimEnd('/')+"/";
    }
}
