using System.Text.Json;
using WebApi.Contracts.Policies;
using WebApi.Contracts.Runtime;
using WebApi.Domain.Policies;
using WebApi.Domain.Runtime;
using System.Runtime.CompilerServices;
using WebApi.Gateway.Configuration;
using WebApi.Infrastructure.Security;
using Xunit;

namespace WebApi.Gateway.Tests;

public sealed class RuntimeLookupIndexTests
{
    private sealed class Fixture
    {
        internal readonly Guid EnvironmentId = Guid.NewGuid(), ApiId = Guid.NewGuid(), ClusterId = Guid.NewGuid(), RouteId = Guid.NewGuid(), SourcePolicyId = Guid.NewGuid(), AppId = Guid.NewGuid(), OtherAppId = Guid.NewGuid();
        internal readonly Guid PolicyId;
        internal readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
        internal Fixture() => PolicyId = RuntimePolicyIdentity.Create(SourcePolicyId, 1, "authentication", "{\"mode\":\"ApiKey\"}");
        internal RuntimeSnapshot Snapshot()
        {
            var hash = ApiKeySecret.Hash("isolated-index-test-secret");
            return new("2.2", EnvironmentId, 1, Now,
                [new(RouteId, ApiId, Guid.NewGuid(), ClusterId, "/orders", ["GET"], 0, 30000, true, [new(PolicyId, 0)])],
                [new(ClusterId, EnvironmentId, "RoundRobin", false, "/health", 30, [new(Guid.NewGuid(), "http://127.0.0.1:8080/", 1)])],
                [new(PolicyId, "authentication", "{\"mode\":\"ApiKey\"}", SourcePolicyId, 1)],
                [new(AppId, "Active", [new(Guid.NewGuid(), "ak_equal", hash, "Active", Now.AddDays(-1), Now.AddDays(1))],
                    [new(ApiId, Now.AddDays(-1), Now), new(ApiId, Now.AddDays(1), null)]),
                 new(OtherAppId, "Disabled", [new(Guid.NewGuid(), "AK_equal", hash, "Revoked", Now.AddDays(1), Now.AddDays(2))], [new(ApiId, Now.AddDays(-1), null)])]);
        }
        internal RuntimeGeneration Generation(RuntimeSnapshot snapshot) => new(new(Guid.NewGuid(), 1, 1, new string('a', 64), 0), [], snapshot);
    }

    [Fact]
    public void BuildRetainsFrozenObjectIdentityAndAllWindows()
    {
        var f = new Fixture(); var original = f.Snapshot(); var generation = f.Generation(original); var index = generation.Indexes;
        SnapshotValidator.Validate(original, f.EnvironmentId);
        Assert.Single(index.Routes); Assert.Single(index.Clusters); Assert.Equal(2, index.Applications.Count);
        Assert.Same(index, generation.Indexes);
        Assert.Same(generation.Snapshot.Routes[0], index.Routes[f.RouteId]);
        Assert.Same(generation.Snapshot.Clusters[0], index.Clusters[f.ClusterId]);
        Assert.Same(generation.Snapshot.Applications[0], index.Applications[f.AppId]);
        Assert.NotSame(original.Applications[0], index.Applications[f.AppId]);
        Assert.Same(generation.Snapshot.Applications[0], index.Credentials["ak_equal"].Application);
        Assert.Same(generation.Snapshot.Applications[0].Credentials[0], index.Credentials["ak_equal"].Credential);
        Assert.Equal(generation.Snapshot.Applications[0].Permissions, index.PermissionWindows[(f.AppId, f.ApiId)]);
        Assert.Equal(2, index.PermissionWindows[(f.AppId, f.ApiId)].Count);
        Assert.Throws<NotSupportedException>(() => ((IList<RuntimePermission>)index.PermissionWindows[(f.AppId, f.ApiId)]).Clear());
        Assert.Same(generation.Authentication[f.PolicyId], index.RouteAuthentication[f.RouteId].Configuration);
        Assert.Same(generation.Snapshot.Policies[0], index.RouteAuthentication[f.RouteId].Policy);
    }

    [Fact]
    public void DuplicateKeysAndMissingReferencesReject()
    {
        var f = new Fixture(); var snapshot = f.Snapshot();
        SnapshotValidator.Validate(snapshot, f.EnvironmentId);
        RuntimeLookupIndex Build(RuntimeSnapshot value) => RuntimeLookupIndex.Build(value, new Dictionary<Guid, AuthenticationConfiguration> { [f.PolicyId] = new(AuthenticationMode.ApiKey) });
        Assert.Throws<InvalidOperationException>(() => BuildDuplicateAccessKey());
        Assert.Throws<InvalidOperationException>(() => Build(snapshot with { Routes = [snapshot.Routes[0], snapshot.Routes[0]] }));
        Assert.Throws<InvalidOperationException>(() => Build(snapshot with { Clusters = [snapshot.Clusters[0], snapshot.Clusters[0]] }));
        Assert.Throws<InvalidOperationException>(() => Build(snapshot with { Applications = [snapshot.Applications[0], snapshot.Applications[0]] }));
        Assert.Throws<InvalidOperationException>(() => Build(snapshot with { Policies = [snapshot.Policies[0], snapshot.Policies[0]] }));
        Assert.Throws<InvalidOperationException>(() => Build(snapshot with { Clusters = [] }));
        Assert.Throws<InvalidOperationException>(() => Build(snapshot with { Routes = [snapshot.Routes[0] with { PolicyBindings = [new(Guid.NewGuid(), 0)] }] }));
        Assert.Throws<InvalidOperationException>(() => Build(snapshot with { Applications = [snapshot.Applications[0] with { Permissions = [new(Guid.NewGuid(), f.Now, null)] }] }));
        Assert.Throws<InvalidOperationException>(() => RuntimeLookupIndex.Build(snapshot, new Dictionary<Guid, AuthenticationConfiguration>()));
    }

    private static RuntimeLookupIndex BuildDuplicateAccessKey()
    {
        var f = new Fixture(); var snapshot = f.Snapshot(); var duplicate = snapshot.Applications[1] with
        { Credentials = [snapshot.Applications[1].Credentials[0] with { AccessKey = snapshot.Applications[0].Credentials[0].AccessKey }] };
        return RuntimeLookupIndex.Build(snapshot with { Applications = [snapshot.Applications[0], duplicate] }, new Dictionary<Guid, AuthenticationConfiguration> { [f.PolicyId] = new(AuthenticationMode.ApiKey) });
    }

    [Fact]
    public void JwtMappingNamespacesAreDistinctAndOrdinal()
    {
        using var key = new JwtTokenVerifierTests.KeyFixture(); var f = new Fixture(); var snapshot = f.Snapshot();
        RuntimePolicy Policy(Guid app)
        {
            var config = PolicyConfigurationValidator.Normalize("authentication", JsonSerializer.Serialize(key.Configuration() with { ApplicationMappings = [new("same", app)] }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            return new(RuntimePolicyIdentity.Create(f.SourcePolicyId, 1, "authentication", config), "authentication", config, f.SourcePolicyId, 1);
        }
        var first = Policy(f.AppId); var second = Policy(f.OtherAppId); var firstPolicy = first.Id; var secondPolicy = second.Id;
        snapshot = snapshot with { Policies = [first, second], Routes = [snapshot.Routes[0] with { RequireApiKey = false, AuthenticationMode = AuthenticationMode.JWT, PolicyBindings = [new(firstPolicy, 0)] }] };
        SnapshotValidator.Validate(snapshot, f.EnvironmentId);
        var generation = f.Generation(snapshot); var index = generation.Indexes;
        Assert.Equal(2, index.JwtApplications.Count);
        Assert.Equal(f.AppId, index.JwtApplications[(firstPolicy, "same")]);
        Assert.Equal(f.OtherAppId, index.JwtApplications[(secondPolicy, "same")]);
        Assert.NotEqual(index.JwtApplications[(firstPolicy, "same")], index.JwtApplications[(secondPolicy, "same")]);
        Assert.False(index.JwtApplications.ContainsKey((firstPolicy, "Same")));
        Assert.False(index.JwtApplications.ContainsKey((f.SourcePolicyId, "same")));
        var config = generation.Authentication[firstPolicy];
        Assert.Throws<InvalidOperationException>(() => RuntimeLookupIndex.Build(snapshot, new Dictionary<Guid, AuthenticationConfiguration>
        { [firstPolicy] = config with { Jwt = config.Jwt! with { ApplicationMappings = [new("same", Guid.NewGuid())] } }, [secondPolicy] = generation.Authentication[secondPolicy] }));
        Assert.Throws<InvalidOperationException>(() => RuntimeLookupIndex.Build(snapshot, new Dictionary<Guid, AuthenticationConfiguration>
        { [firstPolicy] = config with { Jwt = config.Jwt! with { ApplicationMappings = [new("same", f.AppId), new("same", f.OtherAppId)] } }, [secondPolicy] = generation.Authentication[secondPolicy] }));
    }

    [Fact]
    public void RevokedAndFutureCredentialsRemainIndexed()
    {
        var f = new Fixture(); var generation = f.Generation(f.Snapshot()); var index = generation.Indexes;
        SnapshotValidator.Validate(generation.Snapshot, f.EnvironmentId);
        Assert.Equal(2, index.Credentials.Count);
        Assert.Equal(f.AppId, index.Credentials["ak_equal"].Application.Id);
        Assert.Equal(f.OtherAppId, index.Credentials["AK_equal"].Application.Id);
        Assert.Equal("Revoked", index.Credentials["AK_equal"].Credential.Status);
        Assert.Equal("Disabled", index.Credentials["AK_equal"].Application.Status);
        Assert.True(index.Credentials["AK_equal"].Credential.ValidFrom > f.Now);
        Assert.False(index.Credentials.ContainsKey("Ak_equal"));
    }

    [Fact]
    public void MutatingInputCollectionsCannotChangeFrozenGenerationOrIndexes()
    {
        var f = new Fixture(); var snapshot = f.Snapshot(); var methods = snapshot.Routes[0].Methods.ToList(); var bindings = snapshot.Routes[0].PolicyBindings!.ToList();
        var credentials = snapshot.Applications[0].Credentials.ToList(); var permissions = snapshot.Applications[0].Permissions.ToList(); var destinations = snapshot.Clusters[0].Destinations.ToList();
        var routes = new List<RuntimeRoute> { snapshot.Routes[0] with { Methods = methods, PolicyBindings = bindings } }; var policies = snapshot.Policies.ToList();
        var applications = new List<RuntimeApplication> { snapshot.Applications[0] with { Credentials = credentials, Permissions = permissions }, snapshot.Applications[1] };
        var clusters = new List<RuntimeCluster> { snapshot.Clusters[0] with { Destinations = destinations } };
        var payload = new byte[] { 1, 2, 3 }; var generation = new RuntimeGeneration(new(Guid.NewGuid(), 1, 1, new string('a', 64), payload.Length), payload,
            snapshot with { Routes = routes, Clusters = clusters, Policies = policies, Applications = applications });
        payload[0] = 99; methods.Clear(); bindings.Clear(); credentials.Clear(); permissions.Clear(); destinations.Clear(); routes.Clear(); policies.Clear(); applications.Clear(); clusters.Clear();
        Assert.Equal(new byte[] { 1, 2, 3 }, generation.Payload.ToArray());
        Assert.Single(generation.Indexes.Routes); Assert.Equal("GET", Assert.Single(generation.Indexes.Routes[f.RouteId].Methods));
        Assert.Single(generation.Indexes.Clusters[f.ClusterId].Destinations); Assert.Equal(2, generation.Indexes.Credentials.Count);
        Assert.Equal(2, generation.Indexes.PermissionWindows[(f.AppId, f.ApiId)].Count);
        Assert.Equal(AuthenticationMode.ApiKey, generation.Indexes.RouteAuthentication[f.RouteId].Configuration!.Mode);
        SnapshotValidator.Validate(generation.Snapshot, f.EnvironmentId);
    }

    [Fact]
    public void RetiredGenerationCollectsAfterDoubleDisposeAndDuplicateSequenceRejects()
    {
        var weak = RetireGeneration(new Fixture());
        for (var i = 0; i < 3 && weak.IsAlive; i++) { GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true); GC.WaitForPendingFinalizers(); }
        Assert.False(weak.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference RetireGeneration(Fixture f)
    {
        using var store = new RuntimeGenerationStore(); var snapshot = f.Snapshot(); var old = f.Generation(snapshot); store.Register(old); store.Activate(1);
        var held = store.Acquire(1); var next = new RuntimeGeneration(old.Envelope with { DeploymentSequence = 2 }, [], snapshot); store.Register(next); store.Activate(2);
        Assert.Same(old.Indexes, held.Generation.Indexes); using (var another = store.Acquire(1)) Assert.Same(old, another.Generation);
        Assert.Throws<InvalidOperationException>(() => store.Register(next)); Assert.Same(next, store.Current);
        held.Dispose(); held.Dispose(); Assert.Throws<InvalidOperationException>(() => store.Acquire(1));
        return new(old);
    }
}
