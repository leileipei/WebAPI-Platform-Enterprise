using System.Text.Json;
using WebApi.Contracts.Governance;
using Xunit;
namespace WebApi.Domain.Tests;
public sealed class EnvironmentRequestSerializationTests
{
    [Fact] public void LegacyTypedRequestsOmitUnspecifiedAddresses()
    {
        var options=new JsonSerializerOptions(JsonSerializerDefaults.Web);
        foreach(var request in new object[]{new CreateEnvironmentRequest("DEV","开发"),new UpdateEnvironmentRequest("DEV","开发","Active",false,0,null)})
        {
            using var json=JsonDocument.Parse(JsonSerializer.Serialize(request,options));
            Assert.False(json.RootElement.TryGetProperty("gatewayPublicUrl",out _));
            Assert.False(json.RootElement.TryGetProperty("gatewayInternalUrl",out _));
            Assert.False(json.RootElement.TryGetProperty("basePath",out _));
        }
    }
    [Fact] public void ExplicitNullSurvivesTypedSerializationAndRoundTrip()
    {
        var options=new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var request=new UpdateEnvironmentRequest("DEV","开发","Active",false,0,null,new(true,null));
        var raw=JsonSerializer.Serialize(request,options);using var json=JsonDocument.Parse(raw);
        Assert.Equal(JsonValueKind.Null,json.RootElement.GetProperty("gatewayPublicUrl").ValueKind);
        var restored=JsonSerializer.Deserialize<UpdateEnvironmentRequest>(raw,options)!;
        Assert.True(restored.GatewayPublicUrl.IsSpecified);Assert.False(restored.GatewayInternalUrl.IsSpecified);Assert.Null(restored.GatewayPublicUrl.Value);
    }
}
