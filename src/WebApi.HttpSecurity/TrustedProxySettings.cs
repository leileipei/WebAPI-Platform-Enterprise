using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
namespace WebApi.HttpSecurity;
public sealed record TrustedProxySettings(IPAddress[] Ips,System.Net.IPNetwork[] Networks)
{
    public bool Enabled=>Ips.Length>0||Networks.Length>0;
    public static TrustedProxySettings Read(IConfiguration configuration)
    {
        static InvalidOperationException Invalid()=>new("Trusted proxy configuration must contain explicit IPs or narrow CIDR networks.");
        var ips=new List<IPAddress>();var networks=new List<System.Net.IPNetwork>();
        foreach(var item in configuration.GetSection("HttpSecurity:TrustedProxyIps").GetChildren())
        {
            if(!IPAddress.TryParse(item.Value,out var ip)||ip.Equals(IPAddress.Any)||ip.Equals(IPAddress.IPv6Any)||ip.Equals(IPAddress.Broadcast))throw Invalid();
            if(ip.IsIPv4MappedToIPv6)ip=ip.MapToIPv4();ips.Add(ip);
        }
        foreach(var item in configuration.GetSection("HttpSecurity:TrustedProxyNetworks").GetChildren())
        {
            if(!System.Net.IPNetwork.TryParse(item.Value,out var network)||network.BaseAddress.IsIPv4MappedToIPv6||network.PrefixLength<(network.BaseAddress.AddressFamily==AddressFamily.InterNetwork?24:64))throw Invalid();
            networks.Add(network);
        }
        return new(ips.Distinct().ToArray(),networks.Distinct().ToArray());
    }
}
