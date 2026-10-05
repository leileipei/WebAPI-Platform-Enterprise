using System.Text.Json;
using WebApi.Contracts.Common;
using WebApi.Contracts.Policies;
using WebApi.Domain.Policies;
using Xunit;

namespace WebApi.Domain.Tests;

public sealed class PolicyConfigurationTests
{
    public const string Rate = """{"algorithm":"TokenBucket","keyBy":"ApplicationRoute","refillTokens":1000,"windowMs":1000,"burst":1500,"redisFailureMode":"Reject"}""";
    public const string Circuit = """{"samplingWindowMs":30000,"minimumRequests":20,"failureRatio":0.5,"openDurationMs":30000,"halfOpenMaxRequests":1,"halfOpenSuccesses":3,"failureStatusCodes":[500,502,503,504],"countTimeouts":true,"countConnectionFailures":true}""";

    [Theory]
    [InlineData("authentication", "{\"mode\":\"ApiKey\",\"extra\":true}")]
    [InlineData("authentication", "{\"mode\":\"ApiKey\",\"mode\":\"Anonymous\"}")]
    [InlineData("authentication", "{\"mode\":\"JWT\"}")]
    [InlineData("timeout", "{\"timeoutMs\":0}")]
    [InlineData("timeout", "{\"timeoutMs\":300001}")]
    [InlineData("rate_limit", "[]")]
    [InlineData("retry", "{}")]
    [InlineData("timeout", "{\"timeoutMs\":\"1000\"}")]
    public void UnknownFieldDuplicateKeyAndUnsupportedConfigurationRejected(string type, string json)
    {
        var error = Assert.Throws<ApiException>(() => PolicyConfigurationValidator.Normalize(type, json));
        Assert.Equal(422, error.Status);
    }

    [Fact]
    public void OversizedUtf8ConfigurationRejected()
    {
        var json = "{\"mode\":\"" + new string('界', 11000) + "\"}";
        Assert.Equal(422, Assert.Throws<ApiException>(() => PolicyConfigurationValidator.Normalize("authentication", json)).Status);
    }

    [Fact]
    public void ExactRateBoundsAndRefillHorizon()
    {
        using var json = JsonDocument.Parse(PolicyConfigurationValidator.Normalize("rate_limit", Rate));
        Assert.Equal(1500, json.RootElement.GetProperty("burst").GetInt32());
        Assert.Equal(1000, json.RootElement.GetProperty("refillTokens").GetInt32());
        Assert.Equal(422, Assert.Throws<ApiException>(() => PolicyConfigurationValidator.Normalize("rate_limit", Rate.Replace("\"windowMs\":1000", "\"windowMs\":9"))).Status);
        Assert.Equal(422, Assert.Throws<ApiException>(() => PolicyConfigurationValidator.Normalize("rate_limit", Rate.Replace("\"burst\":1500", "\"burst\":999"))).Status);
        var tooLong = Rate.Replace("\"refillTokens\":1000", "\"refillTokens\":1").Replace("\"windowMs\":1000", "\"windowMs\":600000");
        Assert.Equal(422, Assert.Throws<ApiException>(() => PolicyConfigurationValidator.Normalize("rate_limit", tooLong)).Status);
    }

    [Theory]
    [InlineData("\"failureRatio\":0.5", "\"failureRatio\":0")]
    [InlineData("\"failureRatio\":0.5", "\"failureRatio\":1.01")]
    [InlineData("\"samplingWindowMs\":30000", "\"samplingWindowMs\":1500")]
    [InlineData("[500,502,503,504]", "[429]")]
    [InlineData("[500,502,503,504]", "[500,500]")]
    public void CircuitBoundsAndFailureClassificationRejected(string from, string to)
    {
        Assert.Equal(422, Assert.Throws<ApiException>(() => PolicyConfigurationValidator.Normalize("circuit_breaker", Circuit.Replace(from, to))).Status);
    }

    [Fact]
    public void CircuitRequiresFailureSource()
    {
        var noSource = Circuit.Replace("[500,502,503,504]", "[]").Replace(":true", ":false");
        Assert.Throws<ApiException>(() => PolicyConfigurationValidator.Normalize("circuit_breaker", noSource));
    }

    [Theory]
    [InlineData("authentication", "{\"mode\":\"ApiKey\"}")]
    [InlineData("timeout", "{\"timeoutMs\":30000}")]
    [InlineData("rate_limit", Rate)]
    [InlineData("circuit_breaker", Circuit)]
    public void NormalizationIsStable(string type, string source)
    {
        var normalized = PolicyConfigurationValidator.Normalize(type, source);
        Assert.Equal(normalized, PolicyConfigurationValidator.Normalize(type, normalized));
        using var json = JsonDocument.Parse(normalized);
        Assert.Equal(JsonValueKind.Object, json.RootElement.ValueKind);
    }

    [Fact]
    public void AnonymousApplicationRouteRejected()
    {
        PolicyBindingConfiguration[] bindings = [new(Guid.NewGuid(), "authentication", "{\"mode\":\"Anonymous\"}", true, 0), new(Guid.NewGuid(), "rate_limit", Rate, true, 1)];
        Assert.Throws<ApiException>(() => PolicyBindingRules.Validate(bindings, true));
        var routeRate = bindings[1] with { Config = Rate.Replace("ApplicationRoute", "Route") };
        Assert.False(PolicyBindingRules.Validate([bindings[0], routeRate], true).RequireApiKey);
    }

    [Fact]
    public void DisabledDuplicateTypeStillRejected()
    {
        Assert.Throws<ApiException>(() => PolicyBindingRules.Validate([new(Guid.NewGuid(), "timeout", "{\"timeoutMs\":1000}", true, 1), new(Guid.NewGuid(), "timeout", "{\"timeoutMs\":2000}", false, 2)], true));
    }

    [Fact]
    public void DisabledAnonymousDefaultsToApiKeyAndPriorityBoundsAreChecked()
    {
        var binding = new PolicyBindingConfiguration(Guid.NewGuid(), "authentication", "{\"mode\":\"Anonymous\"}", false, 0);
        Assert.True(PolicyBindingRules.Validate([binding], true).RequireApiKey);
        Assert.Throws<ApiException>(() => PolicyBindingRules.Validate([binding with { Priority = 1001 }], true));
    }
}
