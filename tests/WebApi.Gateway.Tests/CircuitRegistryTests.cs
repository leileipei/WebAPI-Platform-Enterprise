using WebApi.Contracts.Policies;
using WebApi.Gateway.Policies;
using Xunit;
namespace WebApi.Gateway.Tests;
public sealed class CircuitRegistryTests
{
    private static CircuitBreakerConfiguration Config()=>new(30000,20,0.5,30000,1,3,[500],true,true);
    [Fact] public void SameRuntimeIdentitySharesUntilLastGenerationReleases()
    {var registry=new CircuitStateRegistry();var key=new CircuitKey(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid());var a=registry.Acquire(key,Config());var b=registry.Acquire(key,Config());Assert.Same(a.State,b.State);Assert.Equal(1,registry.Count);a.Dispose();a.Dispose();Assert.Equal(1,registry.Count);b.Dispose();Assert.Equal(0,registry.Count);using var c=registry.Acquire(key,Config());Assert.NotSame(a.State,c.State);}
    [Fact] public void ChangedInstanceOrRouteOrClusterOrPolicyHasIndependentState()
    {var registry=new CircuitStateRegistry();var key=new CircuitKey(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid());using var original=registry.Acquire(key,Config());foreach(var changed in new[]{key with {InstanceId=Guid.NewGuid()},key with {RuntimePolicyId=Guid.NewGuid()},key with {RouteId=Guid.NewGuid()},key with {RuntimeClusterId=Guid.NewGuid()}}) {using var other=registry.Acquire(changed,Config());Assert.NotSame(original.State,other.State);}Assert.Equal(1,registry.Count);}
    [Fact] public void SameIdentityCannotSilentlyReplaceConfiguration()
    {var registry=new CircuitStateRegistry();var key=new CircuitKey(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid());using var lease=registry.Acquire(key,Config());Assert.Throws<InvalidOperationException>(()=>registry.Acquire(key,Config() with {FailureRatio=0.9}));Assert.Equal(1,registry.Count);}
}
