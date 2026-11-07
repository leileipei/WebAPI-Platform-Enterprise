using System.Text;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using WebApi.Contracts.Common;
using WebApi.Contracts.Runtime;
using WebApi.Domain.Applications;
using WebApi.Domain.Policies;
using WebApi.Domain.Runtime;
using WebApi.Infrastructure.Runtime;
using Xunit;

namespace WebApi.Domain.Tests;

public sealed class SnapshotDialectTests
{
    private static readonly Guid EnvironmentId = Guid.Parse("31000000-0000-0000-0000-000000000001");

    private static byte[] Legacy23(string extra) => Encoding.UTF8.GetBytes($$"""
        {"schemaVersion":"2.3","environmentId":"{{EnvironmentId}}","configVersion":1,
         "generatedAt":"2026-11-04T00:00:00Z","routes":[],"clusters":[],"policies":[],"applications":[],{{extra}}}
        """);

    [Theory]
    [InlineData("\"requiredSecurityGeneration\":\"31000000-0000-0000-0000-000000000002\"")]
    [InlineData("\"requiredSecurityEpoch\":0")]
    [InlineData("\"requireEnvironmentCredentials\":false")]
    [InlineData("\"minimumSnapshotSchema\":\"2.3\"")]
    [InlineData("\"RequiredSecurityEpoch\":0")]
    public void PartialGovernanceFieldsCannotBeIgnoredOnLegacy23(string field)
    {
        var error = Assert.Throws<ApiException>(() => SnapshotValidator.ParsePayload(Legacy23(field), EnvironmentId));
        Assert.Equal(422, error.Status);
    }

    [Fact]
    public void Legacy23RetainsNonSecurityExtensionCompatibility()
    {
        var snapshot = SnapshotValidator.ParsePayload(Legacy23("\"futureHint\":true"), EnvironmentId);
        Assert.Equal("2.3", snapshot.SchemaVersion);
    }

    [Theory]
    [InlineData("route", "authorizationRevision", "1")]
    [InlineData("route", "environmentId", "\"31000000-0000-0000-0000-000000000001\"")]
    [InlineData("application", "environmentId", "\"31000000-0000-0000-0000-000000000001\"")]
    [InlineData("credential", "bindingMode", "\"EnvironmentBound\"")]
    [InlineData("credential", "environmentId", "\"31000000-0000-0000-0000-000000000001\"")]
    [InlineData("permission", "mode", "\"LegacyApi\"")]
    [InlineData("permission", "routeMethods", "[]")]
    [InlineData("permission", "environmentId", "\"31000000-0000-0000-0000-000000000001\"")]
    public void NestedGovernanceFieldsCannotBeHiddenInNetwork23(string kind, string field, string value)
    {
        var root = LegacyDocument();
        var item = kind switch
        {
            "route" => root["routes"]![0]!,
            "application" => root["applications"]![0]!,
            "credential" => root["applications"]![0]!["credentials"]![0]!,
            "permission" => root["applications"]![0]!["permissions"]![0]!,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        item[field] = JsonNode.Parse(value);

        var error = Assert.Throws<ApiException>(() => SnapshotValidator.ParsePayload(CanonicalJson.Serialize(root), EnvironmentId));
        Assert.Equal(422, error.Status);
    }

    [Theory]
    [InlineData("\"schemaVersion\":\"2.3\"")]
    [InlineData("\"SchemaVersion\":\"2.3\"")]
    public void DuplicateOrCaseConflictingSchemaCannotSelectAnotherParser(string field)
    {
        Assert.Throws<ApiException>(() => SnapshotValidator.ParsePayload(Legacy23(field), EnvironmentId));
    }

    [Theory]
    [InlineData("2.3")]
    [InlineData("2.4")]
    public void GovernanceWireCanBeValidatedButCannotActivateWithoutSecurityAdmission(string version)
    {
        var payload = GovernanceDocument(version);
        var snapshot = SnapshotValidator.ParsePayload(payload, EnvironmentId);
        Assert.Equal(version, snapshot.SchemaVersion);
        var envelope = new SnapshotEnvelope(Guid.NewGuid(), 1, 1,
            Convert.ToHexStringLower(SHA256.HashData(payload)), payload.Length);

        Assert.Throws<InvalidDataException>(() => RedisSnapshotStore.Validate(EnvironmentId, envelope, payload));
    }

    private static byte[] GovernanceDocument(string version) => Encoding.UTF8.GetBytes($$"""
        {"schemaVersion":"{{version}}","environmentId":"{{EnvironmentId}}","configVersion":1,
         "generatedAt":"2026-11-04T00:00:00Z","routes":[],"clusters":[],"policies":[],"applications":[],
         "requiredSecurityGeneration":"31000000-0000-0000-0000-000000000002","requiredSecurityEpoch":0,
         "requireEnvironmentCredentials":false,"minimumSnapshotSchema":"2.0"}
        """);

    [Theory]
    [InlineData("foreignCredential")]
    [InlineData("missingCredentialEnvironment")]
    [InlineData("globalInEnvironmentOnly")]
    [InlineData("missingRouteRevision")]
    [InlineData("missingPermissionMode")]
    [InlineData("unknownPermissionField")]
    public void Governance24RejectsInvalidNestedSecurityFacts(string scenario)
    {
        var root = GovernanceWithCredential();
        var credential = root["applications"]![0]!["credentials"]![0]!;
        var permission = root["applications"]![0]!["permissions"]![0]!;
        switch (scenario)
        {
            case "foreignCredential": credential["environmentId"] = Guid.NewGuid().ToString(); break;
            case "missingCredentialEnvironment": credential.AsObject().Remove("environmentId"); break;
            case "globalInEnvironmentOnly":
                root["requireEnvironmentCredentials"] = true;
                credential["bindingMode"] = "LegacyGlobal";
                credential["environmentId"] = null;
                break;
            case "missingRouteRevision": root["routes"]![0]!.AsObject().Remove("authorizationRevision"); break;
            case "missingPermissionMode": permission.AsObject().Remove("mode"); break;
            case "unknownPermissionField": permission["routeMethodz"] = new JsonArray(); break;
            default: throw new ArgumentOutOfRangeException(nameof(scenario));
        }

        var error = Assert.Throws<ApiException>(() => SnapshotValidator.ParsePayload(CanonicalJson.Serialize(root), EnvironmentId));
        Assert.Equal(422, error.Status);
    }

    [Fact]
    public void Governance24AllowsExplicitNullEnvironmentForPermittedLegacyGlobal()
    {
        var root = GovernanceWithCredential();
        var credential = root["applications"]![0]!["credentials"]![0]!;
        credential["bindingMode"] = "LegacyGlobal";
        credential["environmentId"] = null;

        Assert.Equal("2.4", SnapshotValidator.ParsePayload(CanonicalJson.Serialize(root), EnvironmentId).SchemaVersion);
    }

    [Fact]
    public void CanonicalGovernanceRoundTripKeepsExplicitNullForLegacyGlobal()
    {
        var root = GovernanceWithCredential();
        var credential = root["applications"]![0]!["credentials"]![0]!;
        credential["bindingMode"] = "LegacyGlobal";
        credential["environmentId"] = null;
        var parsed = SnapshotValidator.ParsePayload(CanonicalJson.Serialize(root), EnvironmentId);

        var roundTrip = CanonicalJson.Serialize(parsed);
        var resultingCredential = JsonNode.Parse(roundTrip)!["applications"]![0]!["credentials"]![0]!.AsObject();
        Assert.True(resultingCredential.ContainsKey("environmentId"));
        Assert.Null(resultingCredential["environmentId"]);
        Assert.Equal("LegacyGlobal", SnapshotValidator.ParsePayload(roundTrip, EnvironmentId).Applications[0].Credentials[0].BindingMode);
    }

    [Fact]
    public void CanonicalLegacyNetworkRoundTripRetainsOriginalBytes()
    {
        var original = CanonicalJson.Serialize(LegacyDocument());
        var parsed = SnapshotValidator.ParsePayload(original, EnvironmentId);
        Assert.Equal(original, CanonicalJson.Serialize(parsed));
    }

    [Fact]
    public void Governance24RetainsCorsIpAndJsonTransformPolicies()
    {
        var root = GovernanceWithCredential();
        var policies = new JsonArray();
        var bindings = new JsonArray();
        var sources = new (string Type, string Config)[]
        {
            ("cors", CorsPolicyConfigurationTests.Source),
            ("ip_filter", "{\"mode\":\"AllowList\",\"ranges\":[\"192.0.2.0/24\"]}"),
            ("transform", JsonBodyConfigurationTests.Source)
        };
        for (var i = 0; i < sources.Length; i++)
        {
            var source = sources[i];
            var sourceId = Guid.NewGuid();
            var config = PolicyConfigurationValidator.Normalize(source.Type, source.Config);
            var policyId = RuntimePolicyIdentity.Create(sourceId, 1, source.Type, config);
            policies.Add(JsonNode.Parse(CanonicalJson.Serialize(new RuntimePolicy(policyId, source.Type, config, sourceId, 1))));
            bindings.Add(JsonNode.Parse(CanonicalJson.Serialize(new RuntimePolicyBinding(policyId, i))));
        }
        root["policies"] = policies;
        root["routes"]![0]!["policyBindings"] = bindings;

        var parsed = SnapshotValidator.ParsePayload(CanonicalJson.Serialize(root), EnvironmentId);
        Assert.Equal(new[] { "cors", "ip_filter", "transform" }, parsed.Policies.Select(policy => policy.Type));
        Assert.Equal(3, parsed.Routes[0].PolicyBindings!.Count);
    }

    [Fact]
    public void StaleRouteAuthorizationRemainsReadableButCannotGrantTraffic()
    {
        var root = GovernanceWithCredential();
        root["routes"]![0]!["authorizationRevision"] = 2;
        var stale = SnapshotValidator.ParsePayload(CanonicalJson.Serialize(root), EnvironmentId);
        var now = new DateTimeOffset(2026, 11, 4, 0, 0, 0, TimeSpan.Zero);
        Assert.False(PermissionEvaluator.IsAllowed(stale.Applications[0], stale.Routes[0], "GET", now));

        root["applications"]![0]!["permissions"]![0]!["routeMethods"]![0]!["authorizationRevision"] = 2;
        var current = SnapshotValidator.ParsePayload(CanonicalJson.Serialize(root), EnvironmentId);
        Assert.True(PermissionEvaluator.IsAllowed(current.Applications[0], current.Routes[0], "GET", now));
        Assert.False(PermissionEvaluator.IsAllowed(current.Applications[0], current.Routes[0], "POST", now));
    }

    [Fact]
    public void TypedLegacySnapshotCannotContainHiddenGovernanceFacts()
    {
        var root = GovernanceWithCredential();
        root["schemaVersion"] = "2.3";
        foreach (var name in new[] { "requiredSecurityGeneration", "requiredSecurityEpoch", "requireEnvironmentCredentials", "minimumSnapshotSchema" })
            root.AsObject().Remove(name);
        var typed = JsonSerializer.Deserialize<RuntimeSnapshot>(CanonicalJson.Serialize(root), CanonicalJson.Options)!;

        Assert.Throws<ApiException>(() => SnapshotValidator.Validate(typed, EnvironmentId));
    }

    [Fact]
    public void Governance23CannotClaimA24MinimumFloor()
    {
        var root = GovernanceWithCredential();
        root["schemaVersion"] = "2.3";
        root["minimumSnapshotSchema"] = "2.4";
        Assert.Throws<ApiException>(() => SnapshotValidator.ParsePayload(CanonicalJson.Serialize(root), EnvironmentId));
    }

    [Fact]
    public void StrictGovernanceFloorCannotContainLegacyApiGrant()
    {
        var root = GovernanceWithCredential();
        var permission = root["applications"]![0]!["permissions"]![0]!;
        permission["mode"] = "LegacyApi";
        permission["routeMethods"] = new JsonArray();
        Assert.Equal("2.4", SnapshotValidator.ParsePayload(CanonicalJson.Serialize(root), EnvironmentId).SchemaVersion);

        root["minimumSnapshotSchema"] = "2.3";
        Assert.Throws<ApiException>(() => SnapshotValidator.ParsePayload(CanonicalJson.Serialize(root), EnvironmentId));
    }

    [Theory]
    [InlineData("requiredSecurityGeneration")]
    [InlineData("requiredSecurityEpoch")]
    [InlineData("requireEnvironmentCredentials")]
    [InlineData("minimumSnapshotSchema")]
    public void Governance24RequiresEverySecurityIdentityField(string field)
    {
        var root = GovernanceWithCredential();
        root.AsObject().Remove(field);
        Assert.Throws<ApiException>(() => SnapshotValidator.ParsePayload(CanonicalJson.Serialize(root), EnvironmentId));
    }

    [Fact]
    public void Governance24RejectsDuplicateCredentialIdentityEvenWithDifferentAccessKeys()
    {
        var root = GovernanceWithCredential();
        var credentials = root["applications"]![0]!["credentials"]!.AsArray();
        var duplicate = credentials[0]!.DeepClone();
        duplicate["accessKey"] = "another-access-key";
        credentials.Add(duplicate);

        Assert.Throws<ApiException>(() => SnapshotValidator.ParsePayload(CanonicalJson.Serialize(root), EnvironmentId));
    }

    [Fact]
    public void ShapeClassificationRejectsOversizedEvenValidLegacyJson()
    {
        var oversized = Legacy23("\"futureHint\":\"" + new string('a', 16 * 1024 * 1024) + "\"");
        Assert.Equal(SnapshotDialect.Invalid, SnapshotDialectClassifier.Classify(oversized));
    }

    private static JsonNode GovernanceWithCredential()
    {
        var root = JsonNode.Parse(GovernanceDocument("2.4"))!;
        root["routes"] = JsonNode.Parse($$"""
            [{"id":"31000000-0000-0000-0000-000000000003","apiId":"31000000-0000-0000-0000-000000000004",
              "apiVersionId":"31000000-0000-0000-0000-000000000005","clusterId":"31000000-0000-0000-0000-000000000006",
              "path":"/orders","methods":["GET"],"matchOrder":1,"timeoutMs":1000,"requireApiKey":true,
              "authorizationRevision":1,"environmentId":"{{EnvironmentId}}"}]
            """);
        root["clusters"] = JsonNode.Parse($$"""
            [{"id":"31000000-0000-0000-0000-000000000006","environmentId":"{{EnvironmentId}}",
              "loadBalancingPolicy":"RoundRobin","healthCheckEnabled":false,"healthCheckPath":"/health","healthCheckIntervalSec":30,
              "destinations":[{"id":"31000000-0000-0000-0000-000000000007","address":"http://test-backend:8080/","weight":1}]}]
            """);
        root["applications"] = JsonNode.Parse($$"""
            [{"id":"31000000-0000-0000-0000-000000000008","status":"Active","environmentId":"{{EnvironmentId}}",
              "credentials":[{"id":"31000000-0000-0000-0000-000000000009","accessKey":"ak-test","hash":"{{new string('a', 64)}}",
                "status":"Active","validFrom":"2026-11-03T00:00:00Z","expiresAt":"2026-11-05T00:00:00Z",
                "environmentId":"{{EnvironmentId}}","bindingMode":"EnvironmentBound"}],
              "permissions":[{"apiId":"31000000-0000-0000-0000-000000000004","validFrom":"2026-11-03T00:00:00Z","expiresAt":null,
                "mode":"RouteMethod","routeMethods":[{"routeId":"31000000-0000-0000-0000-000000000003","authorizationRevision":1,"methods":["GET"]}],
                "environmentId":"{{EnvironmentId}}"}]}]
            """);
        return root;
    }

    private static JsonNode LegacyDocument()
    {
        var api = Guid.Parse("31000000-0000-0000-0000-000000000002");
        var cluster = Guid.Parse("31000000-0000-0000-0000-000000000003");
        var now = new DateTimeOffset(2026, 11, 4, 0, 0, 0, TimeSpan.Zero);
        var snapshot = new RuntimeSnapshot("2.3", EnvironmentId, 1, now,
            [new(Guid.NewGuid(), api, Guid.NewGuid(), cluster, "/orders", ["GET"], 1, 1000, false)],
            [new(cluster, EnvironmentId, "RoundRobin", false, "/health", 30, [new(Guid.NewGuid(), "http://test-backend:8080/", 1)])],
            [],
            [new(Guid.NewGuid(), "Active", [new(Guid.NewGuid(), "ak-test", new string('a', 64), "Active", now, now.AddDays(1))],
                [new(api, now, now.AddDays(1))])]);
        return JsonNode.Parse(CanonicalJson.Serialize(snapshot))!;
    }
}
