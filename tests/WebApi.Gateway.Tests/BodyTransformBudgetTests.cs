using WebApi.Infrastructure.Policies;
using Xunit;
namespace WebApi.Gateway.Tests;
public sealed class BodyTransformBudgetTests
{
    [Fact]
    public void BodyBudget_Default16_Rejects17AndDoubleDisposeDoesNotMintPermit()
    {var budget=new BodyTransformBudget();var leases=new List<BodyTransformBudget.Lease>();for(var i=0;i<16;i++){Assert.True(budget.TryAcquire(out var held));leases.Add(held!);}Assert.False(budget.TryAcquire(out _));leases[0].Dispose();leases[0].Dispose();Assert.True(budget.TryAcquire(out var replacement));Assert.False(budget.TryAcquire(out _));replacement!.Dispose();foreach(var lease in leases)lease.Dispose();}
    [Fact]
    public async Task BodyBudget_ConcurrentAcquisition_IsBoundedAndRecovers()
    {var budget=new BodyTransformBudget(1);var winners=new System.Collections.Concurrent.ConcurrentBag<BodyTransformBudget.Lease>();await Task.WhenAll(Enumerable.Range(0,64).Select(_=>Task.Run(()=>{if(budget.TryAcquire(out var lease))winners.Add(lease!);})));Assert.Single(winners);foreach(var lease in winners)lease.Dispose();Assert.True(budget.TryAcquire(out var recovered));recovered!.Dispose();}
}
