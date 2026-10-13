using System.Net;
using WebApi.Contracts.Policies;

namespace WebApi.Gateway.Policies;

public sealed class CompiledIpFilter
{
    private readonly IpFilterMode mode;
    private readonly IPNetwork[] networks;
    public CompiledIpFilter(IpFilterConfiguration configuration)
    { mode = configuration.Mode; networks = configuration.Ranges.Select(IPNetwork.Parse).ToArray(); }
    public IpFilterDecision Evaluate(IPAddress? address)
    {
        if (address is null) return new(false, "client_ip_unavailable");
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var matched = networks.Any(network => network.Contains(address));
        return mode == IpFilterMode.AllowList
            ? matched ? new(true, null) : new(false, "ip_not_allowed")
            : matched ? new(false, "ip_denied") : new(true, null);
    }
}
public sealed record IpFilterDecision(bool Allowed, string? Reason);
