using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Domain.Runtime;
using WebApi.Gateway.Configuration;
using WebApi.Gateway.Storage;
using WebApi.Gateway.Tests.Support;
using Xunit;

namespace WebApi.Gateway.Tests;

public sealed class GovernanceAdmissionClosedTests
{
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
}
