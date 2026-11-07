using System.Text.Json;
using WebApi.Infrastructure.Gateway;
using WebApi.Infrastructure.Persistence.Entities;
using Xunit;

namespace WebApi.Domain.Tests;

public sealed class SecurityApplicationReceiptTests
{
    private static (GatewayNode Node, CurrentSecurityReceipt Receipt, string Json) Fixture()
    {
        var node = new GatewayNode { Id = Guid.NewGuid(), EnvironmentId = Guid.NewGuid(), InstanceId = Guid.NewGuid().ToString() };
        var receipt = new CurrentSecurityReceipt(node.Id, node.EnvironmentId, Guid.Parse(node.InstanceId), Guid.NewGuid(), 3, new string('a', 64), DateTimeOffset.UtcNow);
        var json = JsonSerializer.Serialize(receipt, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return (node, receipt, json);
    }

    [Fact]
    public void CurrentReceiptRoundTripsButAnotherInstanceIsRejected()
    {
        var (node, receipt, _) = Fixture();
        node.Metadata = SecurityApplicationReceipt.Merge("{\"zone\":\"east\"}", receipt);
        Assert.Equal(receipt, SecurityApplicationReceipt.Read(node));
        node.InstanceId = Guid.NewGuid().ToString();
        Assert.Null(SecurityApplicationReceipt.Read(node));
    }

    [Theory]
    [InlineData("duplicate-root")]
    [InlineData("case-conflict-root")]
    [InlineData("duplicate-receipt")]
    [InlineData("case-conflict-receipt")]
    public void AmbiguousSecurityReceiptFailsClosed(string variant)
    {
        var (node, receipt, json) = Fixture();
        node.Metadata = variant switch
        {
            "duplicate-root" => $"{{\"securityApplication\":null,\"securityApplication\":{json}}}",
            "case-conflict-root" => $"{{\"SecurityApplication\":null,\"securityApplication\":{json}}}",
            "duplicate-receipt" => $"{{\"securityApplication\":{json.Replace("\"nodeId\":", $"\"nodeId\":\"{Guid.NewGuid()}\",\"nodeId\":")}}}",
            _ => $"{{\"securityApplication\":{json.Replace("\"nodeId\":", $"\"NodeId\":\"{Guid.NewGuid()}\",\"nodeId\":")}}}"
        };
        Assert.Null(SecurityApplicationReceipt.Read(node));
        Assert.Throws<InvalidDataException>(() => SecurityApplicationReceipt.Merge(node.Metadata, receipt));
    }
}
