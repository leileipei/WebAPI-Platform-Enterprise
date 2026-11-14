using Microsoft.Extensions.DependencyInjection;
using WebApi.Gateway.Policies;
using WebApi.Gateway.Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace WebApi.Gateway.Tests;

public sealed class CacheConnectionReadinessTests(ITestOutputHelper output)
{
    [Fact]
    public async Task ColdBypassRemainsVisibleAndReadinessDoesNotPrimeBusinessEntries()
    {
        await using var connection = new GatedRedisConnection();
        await using var fixture = new CachePipelineFixture();
        var configure = fixture.F.ConfigureGateway;
        fixture.F.ConfigureGateway = builder =>
        {
            configure?.Invoke(builder);
            builder.Configuration["GatewayPolicies:Cache:RedisConnection"] = connection.ConnectionString;
        };
        await fixture.Init();
        using var first = await fixture.Get();
        first.EnsureSuccessStatusCode();
        Assert.Equal("business-data-1", await first.Content.ReadAsStringAsync());
        var cold = Assert.Single(fixture.Stores);
        Assert.Equal(CacheReadKind.Unavailable, Assert.Single(cold.Reads));
        Assert.Equal(1, fixture.Calls);
        Assert.Equal(0, cold.WriteAttempts);
        Assert.Equal(0, cold.Writes);
        Assert.Contains(cold.Phases, e => e.Phase == ResponseCachePhase.Decision && e.Outcome == ResponseCacheOutcome.BudgetElapsed);
        // Inner connect cancellation may clear WaitingFor before the outer decision records.
        Assert.Contains(cold.Phases, e =>
            e.Phase == ResponseCachePhase.Decision && e.WaitingFor == ResponseCachePhase.Connect ||
            e.Phase == ResponseCachePhase.Connect && e.Outcome == ResponseCacheOutcome.BudgetElapsed);
        var coldOperations = cold.Operations.ToArray();

        connection.Release();
        await fixture.PrepareCacheConnectionsAsync();
        Assert.Equal(2, fixture.Stores.Count);
        Assert.Equal(1, fixture.Calls);
        Assert.Equal(coldOperations, cold.Operations.ToArray());
        Assert.Equal(CacheReadKind.Unavailable, Assert.Single(cold.Reads));
        Assert.Empty(fixture.Stores[1].Reads);
        for (var node = 0; node < 2; node++)
        {
            var store = fixture.Stores[node];
            Assert.Same(store, fixture.F.Gateways[node].Services.GetRequiredService<IResponseCacheStore>());
            Assert.Equal(0, store.Writes);
            Assert.Equal(0, store.WriteAttempts);
            Assert.Contains(store.PreparationOperations, e => e.Operation == "read" && e.Outcome == "Miss");
            Assert.All(store.PreparationPhases, e => Assert.Equal("get", e.Operation));
            Assert.Empty(store.PreparationPhases.Select(e => e.OperationOrdinal).Intersect(store.Phases.Select(e => e.OperationOrdinal)));
        }

        using var fill = await fixture.Get();
        Assert.Equal("business-data-2", await fill.Content.ReadAsStringAsync());
        await fixture.WaitWrites(1);
        using var hit = await fixture.Get(1);
        Assert.Equal("business-data-2", await hit.Content.ReadAsStringAsync());
        Assert.Equal(2, fixture.Calls);
        Assert.Equal(new[] { CacheReadKind.Unavailable, CacheReadKind.Miss }, cold.Reads.ToArray());
        Assert.Equal(CacheReadKind.Hit, Assert.Single(fixture.Stores[1].Reads));
        Assert.Equal(1, fixture.Stores.Sum(s => s.WriteAttempts));
        Assert.Equal(1, fixture.Stores.Sum(s => s.Writes));
        Assert.Contains(cold.Phases, e => e.Phase == ResponseCachePhase.Decision && e.Outcome == ResponseCacheOutcome.BudgetElapsed);
        output.WriteLine(fixture.CacheDiagnostics());
    }

    [Fact]
    public async Task UnavailablePreparationFailsWithoutBusinessTrafficOrWrites()
    {
        await using var connection = new GatedRedisConnection();
        await using var fixture = new CachePipelineFixture();
        var configure = fixture.F.ConfigureGateway;
        fixture.F.ConfigureGateway = builder =>
        {
            configure?.Invoke(builder);
            builder.Configuration["GatewayPolicies:Cache:RedisConnection"] = connection.ConnectionString;
        };
        await fixture.Init();
        var failure = await Assert.ThrowsAsync<Xunit.Sdk.FailException>(() => fixture.PrepareCacheConnectionsAsync());
        Assert.Contains("setup deadline", failure.Message);
        Assert.Equal(0, fixture.Calls);
        var store = Assert.Single(fixture.Stores);
        Assert.Empty(store.Reads);
        Assert.Empty(store.Operations);
        Assert.Empty(store.Phases);
        Assert.Equal(0, store.WriteAttempts);
        Assert.Equal(0, store.Writes);
        Assert.Contains(store.PreparationOperations, e => e.Outcome == "Unavailable");
        Assert.Contains(store.PreparationPhases, e => e.Phase == ResponseCachePhase.Decision && e.Outcome == ResponseCacheOutcome.BudgetElapsed);
        output.WriteLine(failure.Message);
    }
}
