using System.Net;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting;
using System.Net.Sockets;
using WebApi.Contracts.Common;
namespace WebApi.Infrastructure.Sso;
public interface ISsoDnsResolver {Task<IPAddress[]> ResolveAsync(string host,CancellationToken ct);}
public interface IOidcAddressPolicy {Task<IReadOnlyList<IPAddress>> ResolveAllowedAsync(Uri uri,CancellationToken ct=default);}
public sealed class SystemSsoDnsResolver:ISsoDnsResolver
{
    public Task<IPAddress[]> ResolveAsync(string host,CancellationToken ct)=>Dns.GetHostAddressesAsync(host,ct);
}
public sealed class OidcAddressPolicy(IOptions<SsoOptions> options,IHostEnvironment environment,ISsoDnsResolver resolver):IOidcAddressPolicy
{
    public async Task<IReadOnlyList<IPAddress>> ResolveAllowedAsync(Uri uri,CancellationToken ct=default)
    {
        var config=options.Value;
        if(!uri.IsAbsoluteUri||uri.UserInfo.Length!=0||uri.Fragment.Length!=0||
           !config.AllowedOrigins.Any(origin=>Uri.TryCreate(origin,UriKind.Absolute,out var parsed)&&
               parsed.AbsolutePath=="/"&&parsed.Query.Length==0&&parsed.Fragment.Length==0&&parsed.UserInfo.Length==0&&
               SameOrigin(parsed,uri))) throw Rejected();
        var fixture=environment.IsDevelopment()&&config.FixtureEnabled&&uri.Scheme=="http"&&
            (uri.Host=="localhost"||IPAddress.TryParse(uri.Host,out var literal)&&IPAddress.IsLoopback(literal));
        if(uri.Scheme!="https"&&!fixture) throw Rejected();
        var host=uri.DnsSafeHost;
        // A fixture override changes the socket destination only, never its URL, Host or issuer.
        var origin=uri.GetLeftPart(UriPartial.Authority);
        var overridden=false;
        if(fixture&&config.FixtureConnectOverrides.TryGetValue(origin,out var target))
        {
            if(string.IsNullOrWhiteSpace(target)||target.Contains('/')||target.Contains(':')&&!IPAddress.TryParse(target,out _))
                throw Rejected();
            host=target;overridden=true;
        }
        IPAddress[] addresses;
        try {addresses=IPAddress.TryParse(host,out var ip)?[ip]:await resolver.ResolveAsync(host,ct);}
        catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
        catch(Exception ex) when(ex is SocketException or ArgumentException){throw Rejected();}
        if(addresses.Length==0||addresses.Length>32) throw Rejected();
        var normalized=addresses.Select(a=>a.IsIPv4MappedToIPv6?a.MapToIPv4():a).Distinct().ToArray();
        foreach(var address in normalized)
        {
            if(IsNeverAllowed(address)) throw Rejected();
            if(IsPrivate(address)&&!config.AllowedPrivateCidrs.Any(cidr=>Contains(cidr,address))&&
               !(fixture&&(IPAddress.IsLoopback(address)||overridden))) throw Rejected();
        }
        return normalized;
    }
    public static bool SameOrigin(Uri left,Uri right)=>left.Scheme==right.Scheme&&
        string.Equals(left.DnsSafeHost,right.DnsSafeHost,StringComparison.OrdinalIgnoreCase)&&left.Port==right.Port;
    private static bool IsNeverAllowed(IPAddress address)
    {
        var b=address.GetAddressBytes();
        return address.Equals(IPAddress.Any)||address.Equals(IPAddress.IPv6Any)||address.IsIPv6LinkLocal||
            address.IsIPv6Multicast||address.Equals(IPAddress.IPv6None)||
            address.AddressFamily==AddressFamily.InterNetwork&&(b[0]==0||b[0]>=224||b[0]==169&&b[1]==254);
    }
    private static bool IsPrivate(IPAddress address)
    {
        var b=address.GetAddressBytes();
        return IPAddress.IsLoopback(address)||(address.AddressFamily==AddressFamily.InterNetwork?
            b[0]==10||b[0]==172&&b[1]>=16&&b[1]<=31||b[0]==192&&b[1]==168||b[0]==100&&b[1]>=64&&b[1]<=127:
            (b[0]&0xfe)==0xfc||address.IsIPv6SiteLocal);
    }
    private static bool Contains(string cidr,IPAddress address)
    {
        var parts=cidr.Split('/');
        if(parts.Length!=2||!IPAddress.TryParse(parts[0],out var subnet)||!int.TryParse(parts[1],out var bits)) return false;
        if(subnet.IsIPv4MappedToIPv6)subnet=subnet.MapToIPv4();
        var network=subnet.GetAddressBytes();var value=address.GetAddressBytes();
        if(network.Length!=value.Length||bits<0||bits>network.Length*8)return false;
        for(var i=0;i<network.Length;i++){var width=Math.Clamp(bits-i*8,0,8);var mask=(byte)(0xff<<(8-width));if((network[i]&mask)!=(value[i]&mask))return false;}
        return true;
    }
    private static ApiException Rejected()=>new(422,"sso_endpoint_rejected","身份服务地址未通过访问策略。");
}
