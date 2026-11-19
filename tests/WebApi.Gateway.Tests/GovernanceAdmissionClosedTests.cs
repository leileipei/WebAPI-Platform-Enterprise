using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Contracts.Common;
using WebApi.Contracts.Runtime;
using WebApi.Contracts.Security;
using WebApi.Domain.CredentialSecurity;
using WebApi.Domain.Runtime;
using WebApi.Gateway.Configuration;
using WebApi.Gateway.Security;
using WebApi.Gateway.Storage;
using WebApi.Gateway.Tests.Support;
using Xunit;

namespace WebApi.Gateway.Tests;

public sealed class GovernanceAdmissionClosedTests
{
    [Fact]
    public async Task CurrentIndependentStateQualifiesActivationAndExpiredStateCannotReapplyOrRestore()
    {
        var provider = new TestAdmission();
        await using var fixture = new GatewayFixture
        {
            ConfigureGateway = builder => builder.Services.AddSingleton<IGovernanceAdmissionStateProvider>(provider)
        };
        await fixture.InitializeAsync();
        var legacy = await fixture.PublishAsync();
        var environmentId = fixture.Control.Environment.Id;
        var generation = Guid.NewGuid();
        var clock = new SecurityTestClock();
        var now = clock.GetUtcNow();
        var snapshot = new RuntimeSnapshot("2.4", environmentId, 2, now, [], [], [], [], generation, 0, false, "2.4");
        var payload = CanonicalJson.Serialize(snapshot);
        var envelope = new SnapshotEnvelope(Guid.NewGuid(), legacy.Envelope.DeploymentSequence + 1, 2,
            Convert.ToHexStringLower(SHA256.HashData(payload)), payload.Length);
        var services = fixture.Gateways[0].Services;
        var activation = services.GetRequiredService<SnapshotActivation>();
        var generations = services.GetRequiredService<RuntimeGenerationStore>();
        Assert.False((await activation.ApplyAsync(envelope, payload)).Applied);
        Assert.Null(generations.Current);

        using var keys = new SecurityFixture();
        var checkpoint = new SecurityCheckpointPayload(SecuritySignedEncoding.Protocol, environmentId, generation, 0, new string('a', 64),
            [], false, "2.4", now, now.AddSeconds(10), "test");
        var verified = SecurityCheckpointVerifier.VerifyCheckpoint(new(keys.Signer.SignCheckpoint(checkpoint)), keys.Keys, environmentId);
        var node = Guid.NewGuid();
        var instance = Guid.NewGuid();
        var lease = new SecurityNodeLeasePayload(Guid.NewGuid(), SecuritySignedEncoding.Protocol, environmentId, generation, 0,
            verified.Hash, node, instance, SecuritySignedEncoding.Audience, now, now.AddSeconds(10), "test");
        await using var state = new SecurityStateStore(new(environmentId, () => node, instance), keys.Disk, clock);
        Assert.True((await state.ApplyAsync(verified, SecurityCheckpointVerifier.VerifyNodeLease(
            new(keys.Signer.SignNodeLease(lease)), keys.Keys, environmentId, node, instance))).Applied);
        provider.State = state;

        Assert.True((await activation.ApplyAsync(envelope, payload)).Applied);
        Assert.Equal(envelope, generations.Current?.Envelope);
        var lkg = services.GetRequiredService<LkgStore>();
        Assert.Equal(payload, (await lkg.ReadAsync())?.Payload);

        clock.Advance(10);
        Assert.False((await activation.ApplyAsync(envelope, payload)).Applied);
        using var readiness = await fixture.Clients[0].GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, readiness.StatusCode);

        await File.WriteAllBytesAsync(lkg.FilePath + ".previous", JsonSerializer.SerializeToUtf8Bytes(legacy, CanonicalJson.Options));
        fixture.ConfigureGateway = null;
        await fixture.RestartAsync(0);
        var restarted = fixture.Gateways[0].Services;
        Assert.Null(restarted.GetRequiredService<RuntimeGenerationStore>().Current);
        Assert.False((await restarted.GetRequiredService<SnapshotActivation>().ApplyAsync(legacy.Envelope, legacy.Payload)).Applied);
    }

    [Theory]
    [InlineData("2.3")]
    [InlineData("2.4")]
    public async Task BothGatewaysRejectGovernanceWithoutAdmissionAndRetainTheirPublishedRuntime(string version)
    {
        await using var fixture = new GatewayFixture();
        await fixture.InitializeAsync();
        var original = await fixture.PublishAsync();
        await fixture.ApplyBothAsync(original);
        var env = fixture.Control.Environment.Id;
        var payload = Encoding.UTF8.GetBytes($$"""
            {"schemaVersion":"{{version}}","environmentId":"{{env}}","configVersion":2,
             "generatedAt":"2026-11-04T00:00:00Z","routes":[],"clusters":[],"policies":[],"applications":[],
             "requiredSecurityGeneration":"31000000-0000-0000-0000-000000000002","requiredSecurityEpoch":0,
             "requireEnvironmentCredentials":false,"minimumSnapshotSchema":"2.0"}
            """);
        // This is a syntactically valid governance dialect; its denial must
        // come from the unavailable admission boundary, not malformed JSON.
        Assert.Equal(version, SnapshotValidator.ParsePayload(payload, env).SchemaVersion);
        var envelope = original.Envelope with
        {
            ReleaseId = Guid.NewGuid(),
            DeploymentSequence = original.Envelope.DeploymentSequence + 1,
            ConfigVersion = 2,
            PayloadHash = Convert.ToHexStringLower(SHA256.HashData(payload)),
            SizeBytes = payload.Length
        };
        for (var node = 0; node < 2; node++)
        {
            var services = fixture.Gateways[node].Services;
            var result = await services.GetRequiredService<SnapshotActivation>().ApplyAsync(envelope, payload);
            Assert.False(result.Applied);
            Assert.Equal("invalid_snapshot", result.ErrorCode);
            var current = services.GetRequiredService<RuntimeGenerationStore>().Current!;
            Assert.Equal(original.Envelope, current.Envelope);
            var lkg = await services.GetRequiredService<LkgStore>().ReadAsync();
            Assert.NotNull(lkg);
            Assert.Equal(original.Envelope, lkg.Envelope);
            Assert.Equal(original.Payload, lkg.Payload);
            using var response = await fixture.RequestAsync(node);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    private sealed class TestAdmission : IGovernanceAdmissionStateProvider
    {
        public SecurityStateStore? State { get; set; }
        public SecurityStateStore? GetActiveState(RuntimeGeneration generation) => State;
    }
}
