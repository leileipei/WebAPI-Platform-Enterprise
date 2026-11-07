using WebApi.Contracts.Common;
using WebApi.Infrastructure.Gateway;
using WebApi.Infrastructure.Persistence.Entities;
using Xunit;

namespace WebApi.Domain.Tests;

public sealed class GovernanceNodeCapabilitiesTests
{
    private static GatewayNode Node(Guid instance, IReadOnlyList<string> schemas, IReadOnlyList<string>? security = null)
    {
        var metadata = SnapshotSchemaCapabilities.Merge("{\"zone\":\"east\"}", instance, schemas);
        metadata = SecurityProtocolCapabilities.Merge(metadata, instance, security);
        return new GatewayNode { InstanceId = instance.ToString(), Metadata = metadata };
    }

    [Fact]
    public void FiveVersionsAndCurrentInstanceSecurityCapabilityAreRequiredForGovernance24()
    {
        var node = Node(Guid.NewGuid(), ["2.0", "2.1", "2.2", "2.3", "2.4"], ["security-ledger-v1"]);
        Assert.Equal(new[] { "2.0", "2.1", "2.2", "2.3", "2.4" }, SnapshotSchemaCapabilities.Read(node));
        Assert.True(SecurityProtocolCapabilities.SupportsLedger(node));
        SnapshotSchemaCapabilities.RequireSupported([node], "2.4");
        Assert.Contains("\"zone\":\"east\"", node.Metadata);
    }

    [Fact]
    public void Network23DoesNotRequireGovernanceCapability()
    {
        var node = Node(Guid.NewGuid(), ["2.0", "2.3"]);
        SnapshotSchemaCapabilities.RequireSupported([node], "2.3");
        Assert.False(SecurityProtocolCapabilities.SupportsLedger(node));
    }

    [Theory]
    [InlineData("2.3", "2.3", true, true)]
    [InlineData("2.3", "2.3", false, false)]
    [InlineData("2.3", "2.4", true, false)]
    [InlineData("2.4", "2.4", true, true)]
    [InlineData("2.4", "2.3", true, true)]
    [InlineData("2.4", "future", true, false)]
    public void GovernanceOperationNeedsSecurityAndSchemaAtTheLedgerFloor(string schema, string minimum, bool security, bool expected)
    {
        var node = Node(Guid.NewGuid(), [schema], security ? ["security-ledger-v1"] : []);
        Assert.Equal(expected, SnapshotSchemaCapabilities.SupportsGovernanceAtFloor(node, minimum));
    }

    [Fact]
    public void OneMissingCapabilityBlocksTheEntireCohort()
    {
        var first = Node(Guid.NewGuid(), ["2.3", "2.4"], ["security-ledger-v1"]);
        var second = Node(Guid.NewGuid(), ["2.3", "2.4"]);
        var third = Node(Guid.NewGuid(), ["2.3"], ["security-ledger-v1"]);
        Assert.Throws<ApiException>(() => SnapshotSchemaCapabilities.RequireSupported([first, second], "2.4"));
        Assert.Throws<ApiException>(() => SnapshotSchemaCapabilities.RequireSupported([first, third], "2.4"));
        SnapshotSchemaCapabilities.RequireSupported([first, second, third], "2.3");
    }

    [Fact]
    public void ReplacementInstanceCannotInheritEitherDeclaration()
    {
        var node = Node(Guid.NewGuid(), ["2.3", "2.4"], ["security-ledger-v1"]);
        node.InstanceId = Guid.NewGuid().ToString();
        Assert.Equal(new[] { "2.0" }, SnapshotSchemaCapabilities.Read(node));
        Assert.False(SecurityProtocolCapabilities.SupportsLedger(node));
        Assert.Throws<ApiException>(() => SnapshotSchemaCapabilities.RequireSupported([node], "2.4"));
    }

    [Theory]
    [InlineData("{\"securityCapabilityInstanceId\":\"INSTANCE\",\"securityCapabilities\":[\"security-ledger-v1\"],\"securityCapabilities\":[]}")]
    [InlineData("{\"securityCapabilityInstanceId\":\"INSTANCE\",\"securityCapabilities\":[\"security-ledger-v1\"],\"SecurityCapabilities\":[\"security-ledger-v1\"]}")]
    [InlineData("{\"securityCapabilityInstanceId\":\"INSTANCE\",\"securityCapabilities\":[\"security-ledger-v1\",123]}")]
    [InlineData("{\"securityCapabilityInstanceId\":\"INSTANCE\",\"securityCapabilities\":[\"security-ledger-v1\",\"unknown\"]}")]
    public void AmbiguousOrInvalidSecurityDeclarationsFailClosed(string metadata)
    {
        var instance = Guid.NewGuid();
        var node = new GatewayNode { InstanceId = instance.ToString(), Metadata = metadata.Replace("INSTANCE", instance.ToString()) };
        Assert.False(SecurityProtocolCapabilities.SupportsLedger(node));
    }

    [Theory]
    [InlineData("\"supportedSnapshotSchemas\":[\"2.3\"],\"supportedSnapshotSchemas\":[\"2.4\"]")]
    [InlineData("\"supportedSnapshotSchemas\":[\"2.4\"],\"SupportedSnapshotSchemas\":[\"2.4\"]")]
    [InlineData("\"snapshotSchemaInstanceId\":\"INSTANCE\",\"supportedSnapshotSchemas\":[\"2.4\"]")]
    public void AmbiguousSchemaDeclarationsFailClosed(string duplicateFields)
    {
        var instance = Guid.NewGuid();
        var metadata = $"{{\"snapshotSchemaInstanceId\":\"{instance}\",{duplicateFields},\"securityCapabilityInstanceId\":\"{instance}\",\"securityCapabilities\":[\"security-ledger-v1\"]}}";
        var node = new GatewayNode { InstanceId = instance.ToString(), Metadata = metadata.Replace("INSTANCE", instance.ToString()) };
        Assert.Equal(new[] { "2.0" }, SnapshotSchemaCapabilities.Read(node));
        Assert.Throws<ApiException>(() => SnapshotSchemaCapabilities.RequireSupported([node], "2.4"));
    }

    [Fact]
    public void InvalidSecurityInputAndEmptyInstanceAreRejected()
    {
        Assert.Throws<ApiException>(() => SecurityProtocolCapabilities.Merge(null, Guid.NewGuid(), ["security-ledger-v1", "future"]));
        Assert.Throws<ApiException>(() => SecurityProtocolCapabilities.Merge(null, Guid.Empty, ["security-ledger-v1"]));
        Assert.Throws<ApiException>(() => SecurityProtocolCapabilities.Merge("not-json", Guid.NewGuid(), []));
        Assert.Throws<ApiException>(() => SnapshotSchemaCapabilities.Merge(null, Guid.NewGuid(), ["2.0", "2.0", "2.0", "2.0", "2.0"]));
    }
}
