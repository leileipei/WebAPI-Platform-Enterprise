using System.Net;
namespace WebApi.Infrastructure.Contracts;
public interface IContractDnsResolver { Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct); }
public sealed class SystemContractDnsResolver : IContractDnsResolver
{
    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct) => Dns.GetHostAddressesAsync(host, ct);
}
