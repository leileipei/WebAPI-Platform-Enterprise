using System.Text.Json;
using WebApi.Contracts.Common;
using WebApi.Contracts.Releases;
using WebApi.Domain.Delivery;
using WebApi.Domain.Policies;
using Xunit;
namespace WebApi.Domain.Tests;
public sealed class NetworkPolicyArtifactTests
{
    public const string Cors="{\"allowedOrigins\":[\"https://test.example.com\"],\"allowCredentials\":false,\"allowedMethods\":[\"GET\"],\"allowedHeaders\":[\"X-API-Key\"],\"exposedHeaders\":[],\"maxAgeSeconds\":600}";
    public const string Ip="{\"mode\":\"AllowList\",\"ranges\":[\"10.0.0.0/8\"]}";
    public const string Transform="{\"requestHeaders\":[{\"action\":\"Set\",\"name\":\"X-Business\",\"value\":\"v2\"}],\"responseHeaders\":[],\"path\":null,\"query\":[],\"requestBody\":{\"methods\":[\"POST\"],\"maxBodyBytes\":1024,\"operations\":[{\"op\":\"add\",\"path\":\"/source\",\"value\":9007199254740993123456789}]},\"responseBody\":{\"statusCodes\":[200],\"maxBodyBytes\":1024,\"operations\":[{\"op\":\"remove\",\"path\":\"/debug\"}]}}";
    private static string Resolve(ArtifactPolicyTemplate template,string json){using var doc=JsonDocument.Parse(json);return ArtifactPolicyTemplates.Resolve(template,doc.RootElement);}
    [Theory][InlineData("cors","allowedOrigins","{\"allowedOrigins\":[\"https://prod.example.com\"]}")][InlineData("ip_filter","ranges","{\"ranges\":[\"192.168.0.0/16\"]}")]
    public void Artifact_NetworkPolicy_RequiresExplicitTargetFieldAndFreezesOtherBehavior(string type,string field,string parameters)
    {var original=PolicyConfigurationValidator.Normalize(type,type=="cors"?Cors:Ip);var template=ArtifactPolicyTemplates.Split(type,original);Assert.Equal(new[]{field},template.EnvironmentFields);using var frozen=JsonDocument.Parse(template.FrozenConfig);Assert.False(frozen.RootElement.TryGetProperty(field,out _));var resolved=Resolve(template,parameters);var roundtrip=ArtifactPolicyTemplates.Split(type,resolved);Assert.Equal(template.FrozenConfig,roundtrip.FrozenConfig);Assert.NotEqual(original,resolved);Assert.Equal(422,Assert.Throws<ApiException>(()=>Resolve(template,"{}")).Status);}
    [Theory][InlineData("cors","{\"allowedOrigins\":\"https://prod.example.com\"}")][InlineData("ip_filter","{\"ranges\":\"192.168.0.0/16\"}")][InlineData("cors","{\"allowedOrigins\":[\"https://prod.example.com\"],\"allowCredentials\":true}")][InlineData("ip_filter","{\"ranges\":[\"192.168.0.0/16\"],\"mode\":\"DenyList\"}")]
    public void Artifact_TargetFieldsCannotOverrideFrozenBehaviorOrUseWrongTypes(string type,string parameters)
    {var template=ArtifactPolicyTemplates.Split(type,type=="cors"?Cors:Ip);Assert.Equal(422,Assert.Throws<ApiException>(()=>Resolve(template,parameters)).Status);}
    [Fact]
    public void Artifact_TransformIncludingBothBodies_IsFullyFrozenWithExactNumbers()
    {var template=ArtifactPolicyTemplates.Split("transform",Transform);Assert.Empty(template.EnvironmentFields);var resolved=Resolve(template,"{}");Assert.Equal(PolicyConfigurationValidator.Normalize("transform",Transform),resolved);Assert.Contains("9007199254740993123456789",resolved);Assert.Equal(422,Assert.Throws<ApiException>(()=>Resolve(template,"{\"requestBody\":null}")).Status);}
    [Theory][InlineData("cors","{\"allowCredentials\":false,\"allowedMethods\":[\"INVALID\"],\"allowedHeaders\":[],\"exposedHeaders\":[],\"maxAgeSeconds\":600}","allowedOrigins")][InlineData("ip_filter","{\"mode\":\"Wrong\"}","ranges")]
    public void Artifact_NormalizeRejectsInvalidFrozenFieldsBeforeResolution(string type,string frozen,string field)
    {Assert.Equal(422,Assert.Throws<ApiException>(()=>ArtifactPolicyTemplates.Normalize(new(type,frozen,[field]))).Status);}
}
