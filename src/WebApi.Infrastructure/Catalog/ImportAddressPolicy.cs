using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using WebApi.Contracts.Catalog;
using WebApi.Contracts.Common;
namespace WebApi.Infrastructure.Catalog;
public sealed class ImportAddressPolicy(ImportSourceSettings settings)
{
    private static readonly string[] Private = ["10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "100.64.0.0/10", "fc00::/7"];
    private static readonly string[] Forbidden = ["0.0.0.0/8", "169.254.0.0/16", "192.0.0.0/24", "192.0.2.0/24", "192.88.99.0/24", "198.18.0.0/15", "198.51.100.0/24", "203.0.113.0/24", "224.0.0.0/3", "2001::/23", "2001:db8::/32", "2002::/16", "3fff::/20"];
    public static bool SameOrigin(Uri left, Uri right) => left.Scheme == right.Scheme && left.IdnHost.Equals(right.IdnHost, StringComparison.OrdinalIgnoreCase) && left.Port == right.Port;
    public IReadOnlyList<ImportSourceAllowance> RequireUri(Uri uri, ImportSourcePolicyDto policy)
    {
        if (uri.OriginalString.Length > 4096 || uri.OriginalString != uri.OriginalString.Trim() || !uri.IsAbsoluteUri || uri.Scheme is not ("https" or "http") || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0
            || uri.Scheme == "http" && !settings.AllowHttp || settings.DeniedPorts.Contains(uri.Port)
            || settings.DeniedOrigins.Any(o => Uri.TryCreate(o, UriKind.Absolute, out var denied) && SameOrigin(denied, uri))) throw Rejected();
        ValidatePath(uri.OriginalString[(uri.OriginalString.IndexOf("://", StringComparison.Ordinal) + 3)..], false);
        var matches = policy.Allowances.Where(a => ValidOrigin(a.Origin, out var origin) && SameOrigin(origin!, uri) &&
            (a.PathPrefix == "/" || uri.AbsolutePath == a.PathPrefix.TrimEnd('/') || uri.AbsolutePath.StartsWith(a.PathPrefix.TrimEnd('/') + "/", StringComparison.Ordinal))).ToArray();
        if (matches.Length == 0) throw Rejected();
        return matches;
    }
    public void ValidateAllowance(ImportSourceAllowance allowance)
    {
        if (allowance.Origin.Length > 512 || allowance.PathPrefix.Length > 2048 || allowance.PrivateCidrs.Any(c => c.Length > 64) || !ValidOrigin(allowance.Origin, out var origin) || origin!.Scheme == "http" && !settings.AllowHttp || settings.DeniedPorts.Contains(origin!.Port)
            || settings.DeniedOrigins.Any(o => Uri.TryCreate(o, UriKind.Absolute, out var denied) && SameOrigin(denied, origin!))) throw Rejected();
        ValidatePath(allowance.PathPrefix, true);
        if (allowance.PrivateCidrs.Count > 32 || allowance.PrivateCidrs.Any(c => !IPNetwork.TryParse(c, out var candidate) ||
            !settings.AllowedPrivateCidrs.Any(cap => IPNetwork.TryParse(cap, out var parent) && candidate.BaseAddress.AddressFamily == parent.BaseAddress.AddressFamily && candidate.PrefixLength >= parent.PrefixLength && parent.Contains(candidate.BaseAddress)))) throw Rejected();
    }
    public ImportSourceAllowance NormalizeAllowance(ImportSourceAllowance allowance)
    {
        ValidateAllowance(allowance);
        var origin = new Uri(allowance.Origin);
        return allowance with { Origin = origin.GetLeftPart(UriPartial.Authority), PathPrefix = new Uri(origin, allowance.PathPrefix).AbsolutePath, PrivateCidrs = allowance.PrivateCidrs.Distinct(StringComparer.Ordinal).ToArray() };
    }
    public IReadOnlyList<IPAddress> RequireAddresses(Uri uri, IPAddress[] answers, IReadOnlyList<ImportSourceAllowance> allowances)
    {
        if (answers.Length is 0 or > 32) throw Rejected();
        var addresses = answers.Select(a => a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a).Distinct().ToArray();
        foreach (var address in addresses) {
            var loopback = IPAddress.IsLoopback(address);
            var fixture = loopback && settings.FixtureOrigins.Any(o => ValidOrigin(o, out var origin) && SameOrigin(origin!, uri));
            if (Forbidden.Any(c => Contains(c, address)) || address.ScopeIdOrZero() != 0 || address.AddressFamily == AddressFamily.InterNetworkV6 && !loopback &&
                !Contains("2000::/3", address) && !Contains("fc00::/7", address) || loopback && !fixture) throw Rejected();
            var isPrivate = loopback || Private.Any(c => Contains(c, address));
            if (isPrivate && (!settings.AllowedPrivateCidrs.Any(c => Contains(c, address)) || !allowances.Any(a => a.PrivateCidrs.Any(c => Contains(c, address))))) throw Rejected();
        }
        return addresses;
    }
    private static bool ValidOrigin(string text, out Uri? uri) => Uri.TryCreate(text, UriKind.Absolute, out uri) && text == text.Trim() && uri.Scheme is "https" or "http" &&
        uri.AbsolutePath == "/" && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0 && !uri.Host.Contains('*') && !text.Contains('\\');
    private static void ValidatePath(string text, bool prefix)
    {
        if (text.Contains('\\') || prefix && (!text.StartsWith('/') || text.StartsWith("//", StringComparison.Ordinal)) || Regex.IsMatch(text, "%(?:2[fFeE]|5[cC]|25)|%(?![0-9a-fA-F]{2})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))) throw Rejected();
        var path = prefix ? text : text.Contains('/') ? text[text.IndexOf('/')..] : "/";
        if (prefix && (text.Contains('?') || text.Contains('#')) || Regex.IsMatch(path, "(?:^|/)\\.{1,2}(?:/|$)", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))) throw Rejected();
    }
    internal static bool Contains(string cidr, IPAddress address) => IPNetwork.TryParse(cidr, out var network) && network.Contains(address);
    internal static ApiException Rejected() => new(422, "import_source_rejected", "来源地址未通过部署和项目允许规则。");
}
internal static class ImportIpExtensions
{
    internal static long ScopeIdOrZero(this IPAddress address) => address.AddressFamily == AddressFamily.InterNetworkV6 ? address.ScopeId : 0;
}
