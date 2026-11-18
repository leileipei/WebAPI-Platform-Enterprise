using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Contracts.CredentialUsage;
using WebApi.Contracts.Policies;
using WebApi.Contracts.Runtime;
using WebApi.Domain.Policies;
using WebApi.Gateway.Configuration;
using WebApi.Gateway.CredentialUsage;
using WebApi.Gateway.Policies;
using WebApi.Gateway.Security;
using WebApi.Gateway.Tests.Support;
using WebApi.Infrastructure.Security;
using Xunit;

namespace WebApi.Gateway.Tests;

public sealed class GovernanceRequestAdmissionTests
{
    [Theory]
    [InlineData("2.3")]
    [InlineData("2.4")]
    public async Task GovernanceSnapshotWithoutTrustedProviderRejectsAuthentication(string schema)
    {
        using var fixture = new RequestFixture(schema);

        var response = await fixture.SendAsync();

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, response.Status);
        Assert.Equal("security_state_unavailable", response.Code);
        Assert.Null(response.Execution.VerifiedIdentity);
        Assert.Empty(fixture.Usage);
    }

    [Theory]
    [InlineData("2.3", "GET", 204)]
    [InlineData("2.4", "GET", 204)]
    [InlineData("2.4", "POST", 403)]
    [InlineData("2.4", "get", 403)]
    public async Task GovernanceGrantsOnlyTheExactHttpMethod(string schema, string method, int status)
    {
        using var fixture = new RequestFixture(schema);
        Assert.True((await fixture.ApplyAsync()).Applied);

        var response = await fixture.SendAsync(method, fixture.ActiveState());

        Assert.Equal(status, response.Status);
        if (status == StatusCodes.Status204NoContent)
        {
            Assert.Equal(fixture.Application.Id, response.Execution.VerifiedIdentity?.ApplicationId);
            Assert.Equal(fixture.Credential.Id, Assert.Single(fixture.Usage).Item.CredentialId);
        }
        else
        {
            Assert.Equal("api_not_granted", response.Code);
            Assert.Null(response.Execution.VerifiedIdentity);
            Assert.Empty(fixture.Usage);
        }
    }

    [Fact]
    public async Task OlderGrantDoesNotAuthorizeAChangedVersionAndRevisionOnTheSameRoute()
    {
        using var fixture = new RequestFixture("2.4");
        fixture.Snapshot = fixture.Snapshot with
        {
            Routes = [fixture.Route with { ApiVersionId = Guid.NewGuid(), AuthorizationRevision = 3 }]
        };
        Assert.True((await fixture.ApplyAsync()).Applied);

        var response = await fixture.SendAsync(stateProvider: fixture.ActiveState());

        AssertDenied(response, 403, "api_not_granted");
        Assert.Empty(fixture.Usage);
    }

    [Fact]
    public async Task ExactRevisionAndMethodForAnotherRouteOfTheSameApiDoNotGrantAccess()
    {
        using var fixture = new RequestFixture("2.4");
        fixture.Snapshot = fixture.Snapshot with
        {
            Routes = [fixture.Route, fixture.Route with { Id = Guid.NewGuid(), Path = "/other-orders" }]
        };
        Assert.True((await fixture.ApplyAsync()).Applied);

        var response = await fixture.SendAsync(stateProvider: fixture.ActiveState(), routeIndex: 1);

        AssertDenied(response, 403, "api_not_granted");
        Assert.Empty(fixture.Usage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CapturedRouteGrantsAndTheirMethodsCannotChangeWithTheSourceArrays(bool replaceGrant)
    {
        using var fixture = new RequestFixture("2.4");
        var permission = fixture.Application.Permissions[0];
        var methods = new[] { HttpMethods.Get };
        var grant = permission.RouteMethods![0] with { Methods = methods };
        var grants = new[] { grant };
        fixture.Snapshot = fixture.Snapshot with
        {
            Applications = [fixture.Application with { Permissions = [permission with { RouteMethods = grants }] }]
        };
        var generation = fixture.BuildGeneration();
        if (replaceGrant) grants[0] = grant with { Methods = [HttpMethods.Post] };
        else methods[0] = HttpMethods.Post;
        Assert.True((await fixture.ApplyAsync()).Applied);
        var provider = fixture.ActiveState();

        var allowed = await fixture.SendAsync(stateProvider: provider, generation: generation);
        var denied = await fixture.SendAsync(HttpMethods.Post, provider, generation: generation);

        Assert.Equal(StatusCodes.Status204NoContent, allowed.Status);
        AssertDenied(denied, 403, "api_not_granted");
        Assert.Single(fixture.Usage);
    }

    [Fact]
    public async Task AdmissionUsesPreparedGenerationValidationAndStillRechecksTheLease()
    {
        using var fixture = new RequestFixture("2.4");
        Assert.True((await fixture.ApplyAsync()).Applied);
        var state = fixture.Security.State;
        var generation = fixture.BuildGeneration();
        using (await state.EnterAdmissionAsync())
        {
            Assert.False(state.CanUseSnapshot(generation));
            Assert.Null(generation.PreparedSecurityValidation);
            Assert.True(state.CanUseSnapshot(generation.Snapshot));
        }
        var provider = fixture.ActiveState();

        var admitted = await fixture.SendAsync(stateProvider: provider, generation: generation);
        fixture.Security.Clock.Advance(30);
        var later = await fixture.SendAsync(stateProvider: provider, generation: generation);

        Assert.Equal(StatusCodes.Status204NoContent, admitted.Status);
        Assert.True(generation.PreparedSecurityValidation is { Valid: true, OnlyRouteMethodPermissions: true });
        AssertDenied(later, 503, "security_state_unavailable");
        Assert.Single(fixture.Usage);
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("ahead-epoch")]
    [InlineData("other-generation")]
    [InlineData("other-policy")]
    [InlineData("other-scope")]
    [InlineData("wrong-credential-environment")]
    public async Task IncompatibleCheckpointOrSnapshotCannotAuthorize(string variant)
    {
        using var fixture = new RequestFixture("2.4");
        if (variant != "absent") Assert.True((await fixture.ApplyAsync()).Applied);
        fixture.Snapshot = variant switch
        {
            "ahead-epoch" => fixture.Snapshot with { RequiredSecurityEpoch = 2 },
            "other-generation" => fixture.Snapshot with { RequiredSecurityGeneration = Guid.NewGuid() },
            "other-policy" => fixture.Snapshot with { MinimumSnapshotSchema = "2.3" },
            "other-scope" => fixture.Snapshot with { RequireEnvironmentCredentials = true },
            "wrong-credential-environment" => fixture.Snapshot with
            {
                Applications = [fixture.Application with { Credentials = [fixture.Credential with { EnvironmentId = Guid.NewGuid() }] }]
            },
            _ => fixture.Snapshot
        };

        var response = await fixture.SendAsync(stateProvider: fixture.ActiveState());

        AssertDenied(response, 503, "security_state_unavailable");
        Assert.Empty(fixture.Usage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KnownRevocationRejectsTheKeyEvenAfterLeaseExpiry(bool expired)
    {
        using var fixture = new RequestFixture("2.4");
        Assert.True((await fixture.ApplyAsync(revoked: [fixture.Credential.Id])).Applied);
        if (expired) fixture.Security.Clock.Advance(30);

        var response = await fixture.SendAsync(stateProvider: fixture.ActiveState());

        AssertDenied(response, 401, "invalid_api_key");
        Assert.Empty(fixture.Usage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdmissionRechecksLeaseAndIndependentAuthorityAfterVerification(bool leaseExpires)
    {
        using var fixture = new RequestFixture("2.4");
        Assert.True((await fixture.ApplyAsync()).Applied);
        var reads = 0;
        var provider = new StateProvider(_ =>
        {
            if (++reads == 1) return fixture.Security.State;
            if (!leaseExpires) return null;
            fixture.Security.Clock.Advance(30);
            return fixture.Security.State;
        });

        var response = await fixture.SendAsync(stateProvider: provider);

        AssertDenied(response, 503, "security_state_unavailable");
        Assert.Empty(fixture.Usage);
    }

    [Theory]
    [InlineData("api-key", 401, "invalid_api_key")]
    [InlineData("permission", 403, "api_not_granted")]
    [InlineData("jwt", 401, "invalid_jwt")]
    public async Task CredentialAndGrantWindowsMustRemainValidAtAdmission(string expires, int status, string code)
    {
        using var fixture = new RequestFixture("2.4");
        var deadline = fixture.Security.Clock.GetUtcNow().AddSeconds(2);
        using var signingKey = expires == "jwt" ? new JwtTokenVerifierTests.KeyFixture("ES256") : null;
        var bearer = signingKey is null ? null : fixture.UseJwt(signingKey, lifetimeSeconds: 2, clockSkewSeconds: 0);
        if (expires == "api-key")
            fixture.Snapshot = fixture.Snapshot with
            {
                Applications = [fixture.Application with { Credentials = [fixture.Credential with { ExpiresAt = deadline }] }]
            };
        else if (expires == "permission")
            fixture.Snapshot = fixture.Snapshot with
            {
                Applications = [fixture.Application with { Permissions = [fixture.Application.Permissions[0] with { ExpiresAt = deadline }] }]
            };
        Assert.True((await fixture.ApplyAsync()).Applied);
        var reads = 0;
        var provider = new StateProvider(_ =>
        {
            if (++reads == 2) fixture.Security.Clock.Advance(2);
            return fixture.Security.State;
        });

        var response = await fixture.SendAsync(stateProvider: provider, bearer: bearer);

        AssertDenied(response, status, code);
        Assert.Empty(fixture.Usage);
    }

    [Theory]
    [InlineData("GET", 204)]
    [InlineData("POST", 403)]
    public async Task TicketSerializesPublicationAndIsReleasedBeforeResponseOrForwarding(string method, int status)
    {
        using var fixture = new RequestFixture("2.4");
        fixture.Snapshot = fixture.Snapshot with { RequiredSecurityEpoch = 0 };
        Assert.True((await fixture.ApplyAsync(epoch: 0)).Applied);
        var reads = 0;
        Task<SecurityApplyResult>? publication = null;
        var provider = new StateProvider(_ =>
        {
            if (++reads == 2)
            {
                fixture.Security.Clock.Advance(1);
                publication = fixture.ApplyAsync(revoked: [fixture.Credential.Id]);
                Assert.False(publication.IsCompleted);
            }
            return fixture.Security.State;
        });
        var publicationObserved = false;
        async Task ObservePublication()
        {
            Assert.NotNull(publication);
            Assert.True((await publication.WaitAsync(TimeSpan.FromSeconds(3))).Applied);
            publicationObserved = true;
        }

        var response = await fixture.SendAsync(method, provider, _ => ObservePublication(), onWrite: ObservePublication);

        Assert.Equal(status, response.Status);
        Assert.True(publicationObserved);
        var later = await fixture.SendAsync(stateProvider: provider);
        AssertDenied(later, 401, "invalid_api_key");
    }

    [Theory]
    [InlineData("2.2", false)]
    [InlineData("2.3", false)]
    [InlineData("2.3", true)]
    public async Task CompatibleSnapshotsPreserveTheirMultipleGrantWindows(string schema, bool optedIn)
    {
        using var fixture = new RequestFixture(schema);
        var activeWindow = fixture.UseCompatibleSnapshot();
        if (optedIn) Assert.True((await fixture.ApplyAsync(minimum: "2.0")).Applied);

        var response = await fixture.SendAsync(stateProvider: optedIn ? fixture.ActiveState() : null);

        Assert.Equal(StatusCodes.Status204NoContent, response.Status);
        Assert.Equal(fixture.Application.Id, response.Execution.VerifiedIdentity?.ApplicationId);
        Assert.Equal(activeWindow.ValidFrom, Assert.Single(fixture.Usage).Item.GrantValidFrom);
    }

    [Fact]
    public async Task ManagedCompatibleSnapshotSelectsAWindowStillValidAtAdmission()
    {
        using var fixture = new RequestFixture("2.3");
        var window = fixture.UseCompatibleSnapshot();
        var deadline = fixture.Security.Clock.GetUtcNow().AddSeconds(2);
        var continuingWindow = window with { ValidFrom = window.ValidFrom.AddMinutes(1) };
        fixture.Snapshot = fixture.Snapshot with
        {
            Applications = [fixture.Application with { Permissions = [window with { ExpiresAt = deadline }, continuingWindow] }]
        };
        Assert.True((await fixture.ApplyAsync(minimum: "2.0")).Applied);
        var reads = 0;
        var provider = new StateProvider(_ =>
        {
            if (++reads == 2) fixture.Security.Clock.Advance(2);
            return fixture.Security.State;
        });

        var response = await fixture.SendAsync(stateProvider: provider);

        Assert.Equal(StatusCodes.Status204NoContent, response.Status);
        Assert.Equal(continuingWindow.ValidFrom, Assert.Single(fixture.Usage).Item.GrantValidFrom);
    }

    [Fact]
    public async Task ManagedNetwork23CannotBypassARequiredCheckpointOrItsCredentialAndDialectFloors()
    {
        using var fixture = new RequestFixture("2.3");
        fixture.UseCompatibleSnapshot();
        var provider = fixture.ActiveState();
        AssertDenied(await fixture.SendAsync(stateProvider: provider), 503, "security_state_unavailable");
        Assert.True((await fixture.ApplyAsync(minimum: "2.0", requireEnvironmentCredentials: true)).Applied);
        AssertDenied(await fixture.SendAsync(stateProvider: provider), 401, "invalid_api_key");
        fixture.Security.Clock.Advance(1);
        Assert.True((await fixture.ApplyAsync(epoch: 2, minimum: "2.3", requireEnvironmentCredentials: true)).Applied);
        AssertDenied(await fixture.SendAsync(stateProvider: provider), 503, "security_state_unavailable");
        Assert.Empty(fixture.Usage);
    }

    [Fact]
    public async Task AnonymousRouteDoesNotReadGovernanceState()
    {
        using var fixture = new RequestFixture("2.4");
        fixture.Snapshot = fixture.Snapshot with { Routes = [fixture.Route with { RequireApiKey = false }] };
        var provider = new StateProvider(_ => throw new InvalidOperationException("Anonymous routes do not consult the provider."));

        var response = await fixture.SendAsync(stateProvider: provider);

        Assert.Equal(StatusCodes.Status204NoContent, response.Status);
        Assert.Equal(AuthenticationMode.Anonymous, response.Execution.VerifiedIdentity?.Mode);
        Assert.Empty(fixture.Usage);
    }

    [Fact]
    public async Task JwtUsesTheSameAuthorityAndExactGrant()
    {
        using var fixture = new RequestFixture("2.4");
        using var key = new JwtTokenVerifierTests.KeyFixture("ES256");
        var bearer = fixture.UseJwt(key);

        AssertDenied(await fixture.SendAsync(bearer: bearer), 503, "security_state_unavailable");
        Assert.True((await fixture.ApplyAsync()).Applied);
        AssertDenied(await fixture.SendAsync(HttpMethods.Post, fixture.ActiveState(), bearer: bearer), 403, "api_not_granted");
        var response = await fixture.SendAsync(stateProvider: fixture.ActiveState(), bearer: bearer,
            onForward: context =>
            {
                Assert.False(context.Request.Headers.ContainsKey("Authorization"));
                return Task.CompletedTask;
            });

        Assert.Equal(StatusCodes.Status204NoContent, response.Status);
        Assert.Equal(AuthenticationMode.JWT, response.Execution.VerifiedIdentity?.Mode);
        Assert.Equal(fixture.Application.Id, response.Execution.ApplicationId);
        Assert.Empty(fixture.Usage);
    }

    private static void AssertDenied((int Status, string? Code, TrafficExecutionContext Execution) response,
        int status, string code)
    {
        Assert.Equal(status, response.Status);
        Assert.Equal(code, response.Code);
        Assert.Null(response.Execution.VerifiedIdentity);
        Assert.Null(response.Execution.ApplicationId);
    }

    private sealed class StateProvider(Func<RuntimeGeneration, SecurityStateStore?> state) : IGovernanceAdmissionStateProvider
    {
        public SecurityStateStore? GetActiveState(RuntimeGeneration generation) => state(generation);
    }

    private sealed class ResponseBody(Func<Task>? onWrite) : MemoryStream
    {
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (onWrite is not null) await onWrite();
            await base.WriteAsync(buffer, cancellationToken);
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (onWrite is not null) await onWrite();
            await base.WriteAsync(buffer, offset, count, cancellationToken);
        }
    }

    private sealed class RequestFixture : IDisposable, ICredentialUsageRecorder
    {
        public SecurityFixture Security { get; } = new();
        public RuntimeSnapshot Snapshot { get; set; }
        public RuntimeRoute Route => Snapshot.Routes[0];
        public RuntimeApplication Application => Snapshot.Applications[0];
        public RuntimeCredential Credential => Application.Credentials[0];
        public List<UsageObservation> Usage { get; } = [];
        private readonly ApiKeyMaterial key = ApiKeySecret.Create();

        public RequestFixture(string schema)
        {
            var now = Security.Clock.GetUtcNow();
            var cluster = new RuntimeCluster(Guid.NewGuid(), Security.Env, "RoundRobin", false, "/health", 30,
                [new(Guid.NewGuid(), "http://example.test/", 1)]);
            var route = new RuntimeRoute(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), cluster.Id, "/orders",
                [HttpMethods.Get, HttpMethods.Post], 1, 30000, true, AuthorizationRevision: 2, EnvironmentId: Security.Env);
            var permission = new RuntimePermission(route.ApiId, now.AddMinutes(-1), now.AddMinutes(10), "RouteMethod",
                [new(route.Id, 2, [HttpMethods.Get])], Security.Env);
            var credential = new RuntimeCredential(Guid.NewGuid(), key.AccessKey, key.Hash, "Active", now.AddMinutes(-1),
                now.AddMinutes(10), Security.Env, "EnvironmentBound");
            var application = new RuntimeApplication(Guid.NewGuid(), "Active", [credential], [permission], Security.Env);
            Snapshot = new(schema, Security.Env, 1, now, [route], [cluster], [], [application], Security.Generation, 1, false, schema);
        }

        public Task<SecurityApplyResult> ApplyAsync(long epoch = 1, Guid[]? revoked = null, string? minimum = null,
            bool requireEnvironmentCredentials = false)
        {
            var pair = Security.Pair(epoch, revoked ?? [], minimumSchema: minimum ?? Snapshot.SchemaVersion,
                requireEnvironmentCredentials: requireEnvironmentCredentials);
            return Security.State.ApplyAsync(pair.Checkpoint, pair.Lease);
        }

        public IGovernanceAdmissionStateProvider ActiveState() => new StateProvider(_ => Security.State);

        public RuntimePermission UseCompatibleSnapshot()
        {
            var now = Security.Clock.GetUtcNow();
            var expired = new RuntimePermission(Route.ApiId, now.AddHours(-2), now.AddHours(-1));
            var current = new RuntimePermission(Route.ApiId, now.AddMinutes(-30), null);
            var future = new RuntimePermission(Route.ApiId, now.AddHours(1), null);
            Snapshot = Snapshot with
            {
                RequiredSecurityEpoch = null, RequiredSecurityGeneration = null, RequireEnvironmentCredentials = null,
                MinimumSnapshotSchema = null,
                Routes = [Route with { AuthorizationRevision = null, EnvironmentId = null }],
                Applications = [Application with
                {
                    EnvironmentId = null,
                    Credentials = [Credential with { BindingMode = null, EnvironmentId = null }],
                    Permissions = [expired, current, future]
                }]
            };
            return current;
        }

        public string UseJwt(JwtTokenVerifierTests.KeyFixture key, int lifetimeSeconds = 300, int clockSkewSeconds = 30)
        {
            var claims = key.Claims();
            var now = Security.Clock.GetUtcNow().ToUnixTimeSeconds();
            claims["iat"] = now - 1;
            claims["exp"] = now + lifetimeSeconds;
            var source = Guid.NewGuid();
            var config = PolicyConfigurationValidator.Normalize("authentication", JsonSerializer.Serialize(key.Configuration() with
            {
                ApplicationMappings = [new((string)claims["azp"]!, Application.Id)],
                ClockSkewSeconds = clockSkewSeconds
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            var policy = new RuntimePolicy(RuntimePolicyIdentity.Create(source, 1, "authentication", config), "authentication", config, source, 1);
            Snapshot = Snapshot with
            {
                Routes = [Route with { RequireApiKey = false, AuthenticationMode = AuthenticationMode.JWT, PolicyBindings = [new(policy.Id, 1)] }],
                Policies = [policy]
            };
            return key.Token(claims);
        }

        public RuntimeGeneration BuildGeneration() => new(
            new(Guid.NewGuid(), 1, Snapshot.ConfigVersion, new string('a', 64), 0), [], Snapshot);

        public async Task<(int Status, string? Code, TrafficExecutionContext Execution)> SendAsync(
            string method = "GET", IGovernanceAdmissionStateProvider? stateProvider = null,
            Func<HttpContext, Task>? onForward = null, string? bearer = null, int routeIndex = 0, Func<Task>? onWrite = null,
            RuntimeGeneration? generation = null)
        {
            var options = new CredentialUsageOptions(false, 15, 1000, 10000, 67108864, 1000, 1048576, 24);
            var services = new ServiceCollection();
            services.AddSingleton(new JwtTokenVerifier());
            services.AddSingleton(new CredentialUsageAccumulator(options));
            services.AddSingleton<ICredentialUsageRecorder>(this);
            services.AddSingleton<TimeProvider>(Security.Clock);
            services.AddSingleton<RuntimeGenerationStore>();
            if (stateProvider is not null) services.AddSingleton(stateProvider);
            await using var provider = services.BuildServiceProvider();
            var builder = new ApplicationBuilder(provider);
            builder.UseMiddleware<ApiKeyMiddleware>();
            builder.Run(async context =>
            {
                if (onForward is not null) await onForward(context);
                context.Response.StatusCode = StatusCodes.Status204NoContent;
            });
            generation ??= BuildGeneration();
            var context = new DefaultHttpContext { RequestServices = provider };
            context.Request.Method = method;
            context.Request.Headers["X-API-Key"] = key.AccessKey + "." + key.Secret;
            if (bearer is not null) context.Request.Headers.Authorization = "Bearer " + bearer;
            using var body = new ResponseBody(onWrite);
            context.Response.Body = body;
            var execution = TrafficExecutionContext.Attach(context, generation, generation.Snapshot.Routes[routeIndex]);
            await builder.Build()(context).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(context.Request.Headers.ContainsKey("X-API-Key"));
            if (body.Length == 0) return (context.Response.StatusCode, null, execution);
            body.Position = 0;
            using var rejection = await JsonDocument.ParseAsync(body);
            return (context.Response.StatusCode, rejection.RootElement.GetProperty("code").GetString(), execution);
        }

        public bool TryRecord(UsageObservation observation)
        {
            Usage.Add(observation);
            return true;
        }

        public void Dispose() => Security.Dispose();
    }
}
