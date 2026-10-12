using WebApi.Contracts.Common;
using WebApi.Contracts.Policies;
using WebApi.PerformanceHost;
using Xunit;
namespace WebApi.Integration.Tests;
public sealed class CounterfactualMicroProbeTests
{
    [Fact]
    public void BaselineLabelCannotTurnFrozenContainerIntoScan()
    {
        using var keys=PerformanceJwtMaterial.Create();var now=DateTimeOffset.UtcNow;var snapshot=PerformanceDataset.BuildSnapshot(new(100,20261009,now.AddMinutes(-1),now.AddHours(20)),AuthenticationMode.JWT,"RS256",keys);var results=LookupMicroProbe.Measure(snapshot,CanonicalJson.Serialize(snapshot),10000);
        Assert.Equal(21,results.Count);Assert.All(results,r=>{Assert.Equal("scan",r.Mode);Assert.Equal("runtime-linear-map-v1",Assert.IsType<string>(r.GetType().GetProperty("Backend")?.GetValue(r)));Assert.Equal(10000,r.Iterations);Assert.Equal(10000,r.Hits);});
    }
}
