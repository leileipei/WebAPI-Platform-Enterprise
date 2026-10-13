using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using WebApi.Contracts.Policies;
using static WebApi.Domain.Policies.PolicyConfigurationValidator;

namespace WebApi.Domain.Policies;

internal static class IpFilterPolicyConfiguration
{
    internal static IpFilterConfiguration Parse(JsonElement root)
    {
        Fields(Names(root), "mode", "ranges");
        var mode = Text(root, "mode") switch
        {
            "AllowList" => IpFilterMode.AllowList,
            "DenyList" => IpFilterMode.DenyList,
            _ => throw Invalid()
        };
        var ranges = StringArray(root, "ranges", mode == IpFilterMode.AllowList ? 1 : 0, 256, 80)
            .Select(NormalizeRange).ToArray();
        if (ranges.Distinct(StringComparer.Ordinal).Count() != ranges.Length) throw Invalid();
        return new(mode, ranges);
    }

    private static string NormalizeRange(string source)
    {
        var parts = source.Split('/');
        if (parts.Length is < 1 or > 2) throw Invalid();
        var text = parts[0];
        if (text.Length == 0 || text.Any(c => !(c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F' or ':' or '.'))
            || !IPAddress.TryParse(text, out var address)) throw Invalid();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            if (!StrictV4(text)) throw Invalid();
        }
        else if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (text.Contains('.') && !StrictV4(text[(text.LastIndexOf(':') + 1)..])) throw Invalid();
        }
        else throw Invalid();
        var maxPrefix = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        var prefix = maxPrefix;
        if (parts.Length == 2 && (parts[1].Length == 0 || parts[1].Any(c => c is < '0' or > '9')
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out prefix)
            || prefix > maxPrefix)) throw Invalid();
        if (address.IsIPv4MappedToIPv6)
        {
            if (prefix < 96) throw Invalid();
            address = address.MapToIPv4(); prefix -= 96;
        }
        var bytes = address.GetAddressBytes();
        for (var i = 0; i < bytes.Length; i++)
        {
            var keep = Math.Clamp(prefix - i * 8, 0, 8);
            bytes[i] &= (byte)((255 << (8 - keep)) & 255);
        }
        return new IPAddress(bytes).ToString().ToLowerInvariant() + "/" + prefix.ToString(CultureInfo.InvariantCulture);
    }

    private static bool StrictV4(string text)
    {
        var components = text.Split('.');
        return components.Length == 4 && components.All(v => v.Length is >= 1 and <= 3
            && (v.Length == 1 || v[0] != '0') && v.All(c => c is >= '0' and <= '9')
            && byte.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out _));
    }
}
