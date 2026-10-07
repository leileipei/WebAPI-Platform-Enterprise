using System.Net;
using WebApi.Contracts.Common;
using WebApi.Contracts.Notifications;
namespace WebApi.Infrastructure.Notifications;
public interface INotificationDnsResolver {Task<IPAddress[]> ResolveAsync(string host,CancellationToken ct);}
public sealed class NotificationDnsResolver:INotificationDnsResolver
{public Task<IPAddress[]> ResolveAsync(string host,CancellationToken ct)=>Dns.GetHostAddressesAsync(host,ct);}
public interface INotificationAddressPolicy
{Task<IReadOnlyList<IPAddress>> ResolveAsync(NotificationChannel channel,string host,int port,Uri? url,CancellationToken ct);}
public sealed class NotificationAddressPolicy(NotificationDeploymentSettings settings,INotificationDnsResolver dns):INotificationAddressPolicy
{
    public async Task<IReadOnlyList<IPAddress>> ResolveAsync(NotificationChannel channel,string host,int port,Uri? url,CancellationToken ct)
    {
        settings.RequireAllowedEndpoint(channel,host,port,url);
        var addresses=IPAddress.TryParse(host.Trim('[',']'),out var literal)?[literal]:await dns.ResolveAsync(new System.Globalization.IdnMapping().GetAscii(host),ct);
        if(addresses.Length==0||addresses.Any(address=>!Allowed(address)))throw Rejected();
        return Array.AsReadOnly(addresses.Select(Normalize).Distinct().ToArray());
    }
    private bool Allowed(IPAddress original)
    {
        var address=Normalize(original);var bytes=address.GetAddressBytes();
        if(bytes.Length==4)
        {
            if(bytes[0]==0||bytes[0]>=224||bytes[0]==169&&bytes[1]==254||address.Equals(IPAddress.Parse("100.100.100.200")))return false;
            var privateAddress=bytes[0] is 10 or 127||bytes[0]==172&&bytes[1] is >=16 and <=31||bytes[0]==192&&bytes[1]==168||bytes[0]==100&&bytes[1] is >=64 and <=127||bytes[0]==198&&bytes[1] is 18 or 19;
            return !privateAddress||settings.AllowedPrivateCidrs.Any(cidr=>NotificationNetworkPrefix.Parse(cidr).Contains(address));
        }
        if(bytes.Length!=16||address.ScopeId!=0||address.Equals(IPAddress.IPv6Any)||address.IsIPv6Multicast||address.IsIPv6LinkLocal||address.Equals(IPAddress.Parse("fd00:ec2::254")))return false;
        // Reject transitional encodings which can hide a second IPv4 destination.
        if(bytes.Take(12).All(b=>b==0)&&!address.Equals(IPAddress.IPv6Loopback)||bytes[0]==0x20&&bytes[1]==0x02||bytes[0]==0x20&&bytes[1]==0x01&&bytes[2]==0&&bytes[3]==0||bytes[0]==0&&bytes[1]==0x64&&bytes[2]==0xff&&bytes[3]==0x9b)return false;
        return !(address.Equals(IPAddress.IPv6Loopback)||(bytes[0]&0xfe)==0xfc||address.IsIPv6SiteLocal)||settings.AllowedPrivateCidrs.Any(cidr=>NotificationNetworkPrefix.Parse(cidr).Contains(address));
    }
    private static IPAddress Normalize(IPAddress address)=>address.IsIPv4MappedToIPv6?address.MapToIPv4():address;
    private static ApiException Rejected()=>new(422,"notification_address_rejected","通知地址未获部署策略允许。");
}
internal sealed class NotificationNetworkPrefix
{
    private readonly byte[] network;private readonly int prefix;
    private NotificationNetworkPrefix(byte[] network,int prefix){this.network=network;this.prefix=prefix;}
    internal static NotificationNetworkPrefix Parse(string value)
    {
        var parts=value.Split('/');
        if(parts.Length!=2||!IPAddress.TryParse(parts[0],out var address)||address.IsIPv4MappedToIPv6||!int.TryParse(parts[1],System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out var prefix)||prefix<0||prefix>address.GetAddressBytes().Length*8||address.ScopeIdOrZero()!=0)throw new InvalidOperationException("Invalid notification private CIDR.");
        var bytes=address.GetAddressBytes();for(var bit=prefix;bit<bytes.Length*8;bit++)if((bytes[bit/8]&(1<<(7-bit%8)))!=0)throw new InvalidOperationException("Invalid notification private CIDR.");
        return new(bytes,prefix);
    }
    internal bool Contains(IPAddress address)
    {var bytes=address.GetAddressBytes();if(bytes.Length!=network.Length)return false;for(var bit=0;bit<prefix;bit++)if((bytes[bit/8]&(1<<(7-bit%8)))!=(network[bit/8]&(1<<(7-bit%8))))return false;return true;}
}
internal static class NotificationAddressExtensions
{internal static long ScopeIdOrZero(this IPAddress address)=>address.AddressFamily==System.Net.Sockets.AddressFamily.InterNetworkV6?address.ScopeId:0;}
