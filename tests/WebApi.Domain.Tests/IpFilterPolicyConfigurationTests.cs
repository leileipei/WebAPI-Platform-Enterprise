using System.Text.Json;
using WebApi.Contracts.Common;
using WebApi.Domain.Policies;
using Xunit;

namespace WebApi.Domain.Tests;

public sealed class IpFilterPolicyConfigurationTests
{
    [Theory]
    [InlineData("192.0.2.7/24", "192.0.2.0/24")]
    [InlineData("192.0.2.7", "192.0.2.7/32")]
    [InlineData("2001:db8:abcd::1234/32", "2001:db8::/32")]
    [InlineData("2001:DB8::1", "2001:db8::1/128")]
    [InlineData("192.0.2.1/0", "0.0.0.0/0")]
    [InlineData("2001:db8::1/0", "::/0")]
    public void IpFilter_CidrHostBits_NormalizeToNetwork(string source, string expected)
    {
        var normalized = Normalize("AllowList", [source]);
        using var json = JsonDocument.Parse(normalized);
        Assert.Equal(expected, json.RootElement.GetProperty("ranges")[0].GetString());
        Assert.Equal(normalized, PolicyConfigurationValidator.Normalize("ip_filter", normalized));
    }

    [Theory]
    [InlineData("::ffff:192.0.2.7/120", "192.0.2.0/24")]
    [InlineData("::ffff:192.0.2.7", "192.0.2.7/32")]
    [InlineData("::ffff:c000:207/128", "192.0.2.7/32")]
    [InlineData("::ffff:192.0.2.7/96", "0.0.0.0/0")]
    public void IpFilter_MappedCidr_NormalizesFamily(string source, string expected)
    {
        using var json = JsonDocument.Parse(Normalize("AllowList", [source]));
        Assert.Equal(expected, json.RootElement.GetProperty("ranges")[0].GetString());
    }

    [Theory]
    [InlineData("::ffff:192.0.2.7/95")][InlineData("::ffff:192.0.2.7/129")]
    [InlineData("192.0.2.1/33")][InlineData("2001:db8::/129")]
    [InlineData("192.0.2.1/-1")][InlineData("192.0.2.1/24/32")]
    [InlineData("example.com")][InlineData("192.0.2.1:443")][InlineData("[2001:db8::1]:443")]
    [InlineData("fe80::1%eth0")][InlineData("0xc0000201")][InlineData("3221225985")]
    [InlineData("192.2.1")][InlineData("0300.0.2.1")][InlineData("192.0.02.1")]
    [InlineData("::ffff:192.0.02.1")][InlineData("192.0.2.1\r\n")][InlineData(" 192.0.2.1")]
    [InlineData("192.0.2.1/ 24")]
    public void IpFilter_InvalidAddress_Rejects(string source) => Reject("AllowList", [source]);

    [Fact]
    public void IpFilter_ListModes_HaveExplicitEmptyBehavior()
    {
        Reject("AllowList", []);
        using var json = JsonDocument.Parse(Normalize("DenyList", []));
        Assert.Equal("DenyList", json.RootElement.GetProperty("mode").GetString());
        Assert.Empty(json.RootElement.GetProperty("ranges").EnumerateArray());
        Reject("allowlist", ["192.0.2.1"]);
    }

    [Fact]
    public void IpFilter_NormalizedDuplicateNetworks_Rejects()
    {
        Reject("AllowList", ["192.0.2.7/24", "192.0.2.8/24"]);
        Reject("AllowList", ["::ffff:192.0.2.1", "192.0.2.1/32"]);
    }

    [Fact]
    public void IpFilter_RangeCountBoundary_IsEnforced()
    {
        var addresses = Enumerable.Range(0, 257).Select(i => $"10.0.{i / 256}.{i % 256}").ToArray();
        Assert.NotEmpty(Normalize("AllowList", addresses[..256]));
        Reject("AllowList", addresses);
    }

    [Theory]
    [InlineData("{\"mode\":0,\"ranges\":[\"192.0.2.1\"]}")]
    [InlineData("{\"mode\":\"AllowList\",\"ranges\":null}")]
    [InlineData("{\"mode\":\"AllowList\",\"ranges\":[null]}")]
    [InlineData("{\"mode\":\"AllowList\",\"ranges\":[\"192.0.2.1\"],\"extra\":true}")]
    [InlineData("{\"mode\":\"AllowList\",\"mode\":\"DenyList\",\"ranges\":[\"192.0.2.1\"]}")]
    [InlineData("{\"ranges\":[\"192.0.2.1\"]}")]
    public void IpFilter_FieldShapes_AreStrict(string source) => Assert.Equal("invalid_policy_config", Assert.Throws<ApiException>(() => PolicyConfigurationValidator.Normalize("ip_filter", source)).Code);

    [Fact]
    public void IpFilter_TypedParseAndInputCopy_PreserveImmutableNetworks()
    {
        var config = PolicyConfigurationValidator.ParseIpFilter("{\"mode\":\"AllowList\",\"ranges\":[\"192.0.2.7/24\"]}");
        Assert.Equal(WebApi.Contracts.Policies.IpFilterMode.AllowList, config.Mode);
        Assert.Equal("192.0.2.0/24", config.Ranges[0]);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)config.Ranges)[0] = "0.0.0.0/0");
        string[] input = ["192.0.2.0/24"];
        var copied = new WebApi.Contracts.Policies.IpFilterConfiguration(WebApi.Contracts.Policies.IpFilterMode.DenyList, input);
        input[0] = "0.0.0.0/0"; Assert.Equal("192.0.2.0/24", copied.Ranges[0]);
    }

    private static string Normalize(string mode, string[] ranges) => PolicyConfigurationValidator.Normalize("ip_filter", JsonSerializer.Serialize(new {mode, ranges}));
    private static void Reject(string mode, string[] ranges) => Assert.Equal(422, Assert.Throws<ApiException>(() => Normalize(mode, ranges)).Status);
}
