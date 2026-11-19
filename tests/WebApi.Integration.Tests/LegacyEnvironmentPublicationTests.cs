using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Contracts.Common;
using WebApi.Contracts.Gateway;
using WebApi.Contracts.Releases;
using WebApi.Contracts.Runtime;
using WebApi.Infrastructure.Gateway;
using WebApi.Infrastructure.Messaging;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Runtime;
using WebApi.Integration.Tests.Support;
using Xunit;

namespace WebApi.Integration.Tests;

public sealed class LegacyEnvironmentPublicationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("Unknown")]
    [InlineData("FreshNonStrict")]
    public async Task LegacyPublicationRequiresAVerifiedEnvironmentOrigin(string? origin)
    {
        await using var api = new ApiFixture();
        await api.InitializeAsync();
        await api.SeedReleaseAsync(seedFreshEnvironment: origin == "FreshNonStrict");
        var environmentId = api.Environment.Id;
        var configPayload = CanonicalJson.Serialize(new RuntimeSnapshot("2.0", environmentId, 1,
            DateTimeOffset.UtcNow, [], [], [], []));
        var hash = Convert.ToHexStringLower(SHA256.HashData(configPayload));
        var node = new GatewayNode { EnvironmentId = environmentId, NodeName = "legacy-fixture-node",
            InstanceId = Guid.NewGuid().ToString("D"), IdentityHash = new string('b', 64) };
        var second = new GatewayNode { EnvironmentId = environmentId, NodeName = "legacy-fixture-node-two",
            InstanceId = Guid.NewGuid().ToString("D"), IdentityHash = new string('c', 64) };
        var release = new ReleaseRecord { EnvironmentId = environmentId, ReleaseNo = "LEGACY-PUBLICATION-1",
            RequestedBy = api.User.Id, ToConfigVersion = 1, DeploymentSequence = 1, Status = "Publishing",
            ReleaseType = "publish", DeadlineAt = DateTimeOffset.UtcNow.AddMinutes(2),
            CandidateBytes = CanonicalJson.Serialize(new FrozenReleaseCandidate(environmentId, api.Organization.Id,
                api.Project.Id, 0, [], [], [], [], [], [], [], [])) };
        var config = new GatewayConfigVersion { EnvironmentId = environmentId, VersionNo = 1,
            CreatedBy = api.User.Id, SnapshotHash = hash, Status = "Publishing" };
        var message = new OutboxMessage { EnvironmentId = environmentId, ReleaseId = release.Id, DeploymentSequence = 1,
            EventType = "desired_config", Payload = JsonSerializer.Serialize(new DeploymentEvent(Guid.NewGuid(),
                environmentId, 1, release.Id, 1), CanonicalJson.Options) };
        await using (var setup = api.Context())
        {
            if (origin == "Unknown")
                setup.Add(new EnvironmentSecurityInitialization { EnvironmentId = environmentId, ProjectId = api.Project.Id,
                    OrganizationId = api.Organization.Id, State = origin });
            var environment = await setup.Set<EnvironmentRecord>().SingleAsync();
            environment.DesiredConfigVersion = 1;
            environment.DeploymentSequence = 1;
            setup.AddRange(node, second, release, config, message,
                new ReleaseTarget { ReleaseId = release.Id, NodeId = node.Id, InstanceId = node.InstanceId },
                new ReleaseTarget { ReleaseId = release.Id, NodeId = second.Id, InstanceId = second.InstanceId },
                new GatewayConfigSnapshot { ConfigVersionId = config.Id, PayloadBytes = configPayload,
                    Payload = Encoding.UTF8.GetString(configPayload), SizeBytes = configPayload.Length });
            await setup.SaveChangesAsync();
        }

        using var login = await api.LoginAsync();
        login.EnsureSuccessStatusCode();
        using var preview = await ApiFixture.CommandAsync(api.Client, $"/api/v1/environments/{environmentId}/releases/preview",
            new PreviewReleaseRequest(1, [api.Version.Id]));
        var verified = origin == "FreshNonStrict";
        Assert.Equal(verified ? HttpStatusCode.OK : HttpStatusCode.Conflict, preview.StatusCode);
        if (!verified) Assert.Contains("legacy_environment_unverified", await preview.Content.ReadAsStringAsync());

        var instance = Guid.Parse(node.InstanceId);
        var identity = new NodeIdentity(node.Id, environmentId, node.IdentityHash);
        var ack = new NodeAck(instance, release.Id, 1, 1, hash, DateTimeOffset.UtcNow, true, null);
        using var services = api.Services();
        var registry = services.ServiceProvider.GetRequiredService<NodeRegistry>();
        var acks = services.ServiceProvider.GetRequiredService<AckService>();
        using var redis = new RedisSnapshotStore("redis:6379", "legacy-environment-publication:" + Guid.NewGuid());
        await using var db = api.Context();
        var dispatcher = new OutboxDispatcher(db, redis);

        if (verified)
        {
            Assert.Equal(hash, (await registry.DesiredAsync(identity, instanceId: instance))?.Envelope.PayloadHash);
            Assert.True(await dispatcher.DispatchNextAsync());
            Assert.Equal(hash, (await redis.GetDesiredAsync(environmentId))?.Envelope.PayloadHash);
            Assert.Equal("Publishing", (await acks.RecordAsync(node.Id, ack, identity)).ReleaseState);
            Assert.Equal("Succeeded", (await acks.RecordAsync(second.Id, ack with { InstanceId = Guid.Parse(second.InstanceId) },
                new(second.Id, environmentId, second.IdentityHash))).ReleaseState);
            return;
        }

        Assert.Equal("credential_governance_delivery_unavailable", (await Assert.ThrowsAsync<ApiException>(() =>
            registry.DesiredAsync(identity, instanceId: instance))).Code);
        Assert.False(await dispatcher.DispatchNextAsync());
        Assert.Null(await redis.GetDesiredAsync(environmentId));
        Assert.Null((await db.Set<OutboxMessage>().AsNoTracking().SingleAsync()).ProcessedAt);
        Assert.Equal("credential_governance_delivery_unavailable", (await Assert.ThrowsAsync<ApiException>(() =>
            acks.RecordAsync(node.Id, ack, identity))).Code);
        Assert.Empty(await db.Set<GatewayAck>().ToArrayAsync());
    }
}
