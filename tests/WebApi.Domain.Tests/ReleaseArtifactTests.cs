using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using WebApi.Contracts.Catalog;
using WebApi.Contracts.Common;
using WebApi.Contracts.Releases;
using WebApi.Domain.Policies;
using Xunit;
namespace WebApi.Domain.Tests;
public sealed class ReleaseArtifactTests
{
    private static ArtifactPolicyTemplate Split(string type,string config)=>WebApi.Domain.Delivery.ArtifactPolicyTemplates.Split(type,config);
    private static string Resolve(ArtifactPolicyTemplate template,string parameters){using var json=JsonDocument.Parse(parameters);return WebApi.Domain.Delivery.ArtifactPolicyTemplates.Resolve(template,json.RootElement);}
    private static string Hash(ArtifactContent content)=>WebApi.Domain.Delivery.ReleaseArtifactCanonicalizer.Hash(content);
    private static readonly Guid api=Guid.Parse("11111111-1111-1111-1111-111111111111"),version=Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static ArtifactContent Content(string[]? methods=null,string schema="{\"type\":\"string\"}",ArtifactPolicyTemplate[]? policies=null)
    {
        var contract=new ArtifactApiContract(api,version,"1.0",1,[],[new(Guid.Parse("33333333-3333-3333-3333-333333333333"),version,"request","Body",null,"application/json",schema,null,null)]);
        var route=new ArtifactRoute("",api,version,"/orders",methods??["GET"],100,true,null,policies??[]);
        return new([contract],[route]);
    }
    [Fact] public void ReorderedCollectionsAndSchemaPropertiesHaveTheSameHash()
    {
        var first=Content(["POST","GET"],"{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"integer\"}}}");
        var second=Content(["GET","POST"],"{\"properties\":{\"id\":{\"type\":\"integer\"}},\"type\":\"object\"}");Assert.Equal(Hash(first),Hash(second));
    }
    [Fact] public void MethodAndSchemaChangesRequireANewArtifactHash()
    {Assert.NotEqual(Hash(Content()),Hash(Content(["POST"])));Assert.NotEqual(Hash(Content()),Hash(Content(schema:"{\"type\":\"integer\"}")));}
    [Fact] public void ReorderingApisRoutesAndParametersDoesNotChangeArtifact()
    {
        var a=Content();var otherApi=Guid.Parse("44444444-4444-4444-4444-444444444444");var otherVersion=Guid.Parse("55555555-5555-5555-5555-555555555555");var b=a.Apis[0] with{ApiId=otherApi,VersionId=otherVersion,Schemas=[]};var route=a.Routes[0] with{ApiId=otherApi,VersionId=otherVersion,Path="/customers"};
        Assert.Equal(Hash(new([a.Apis[0],b],[a.Routes[0],route])),Hash(new([b,a.Apis[0]],[route,a.Routes[0]])));
    }
    [Fact] public void LegacyAuthenticationIsFrozenEvenWithoutAnExplicitPolicy()
    {var a=Content();Assert.NotEqual(Hash(a),Hash(a with{Routes=[a.Routes[0] with{AuthenticationMode="Anonymous"}]}));}
    [Fact] public void ChangingRetryOrCacheBehaviorChangesHash()
    {
        var retry=Split("retry","{\"maxAttempts\":2,\"retryStatusCodes\":[503],\"retryConnectionFailures\":true,\"baseDelayMs\":10,\"maxDelayMs\":20,\"jitterPercent\":0,\"perAttemptTimeoutMs\":100}");
        // Frozen behavior is already normalized by the existing strict policy validator.
        var cache=Split("cache","{\"ttlSeconds\":60,\"maxEntryBytes\":4096,\"varyHeaders\":[],\"identityPartition\":\"VerifiedIdentity\",\"redisFailureMode\":\"Bypass\"}");Assert.NotEqual(Hash(Content(policies:[cache])),Hash(Content(policies:[cache with{FrozenConfig=cache.FrozenConfig.Replace("60","120")}] )));
        var changed=retry with{FrozenConfig=retry.FrozenConfig.Replace("\"maxAttempts\":2","\"maxAttempts\":3")};Assert.NotEqual(Hash(Content(policies:[retry])),Hash(Content(policies:[changed])));
    }
    [Fact] public void EnvironmentRateAndTimeoutValuesAreSeparatedFromFrozenBehavior()
    {
        var rate=Split("rate_limit","{\"algorithm\":\"TokenBucket\",\"keyBy\":\"ApplicationRoute\",\"refillTokens\":10,\"windowMs\":1000,\"burst\":20,\"redisFailureMode\":\"Reject\"}");Assert.DoesNotContain("refillTokens",rate.FrozenConfig);Assert.Contains("ApplicationRoute",rate.FrozenConfig);Assert.Equal(["burst","refillTokens","windowMs"],rate.EnvironmentFields);
        var target=Resolve(rate,"{\"refillTokens\":40,\"windowMs\":2000,\"burst\":80}");using var doc=JsonDocument.Parse(target);Assert.Equal(40,doc.RootElement.GetProperty("refillTokens").GetInt32());Assert.Equal("Reject",doc.RootElement.GetProperty("redisFailureMode").GetString());
        var timeout=Split("timeout","{\"timeoutMs\":12000}");Assert.Equal("{}",timeout.FrozenConfig);Assert.Contains("timeoutMs",timeout.EnvironmentFields);Assert.Contains("25000",Resolve(timeout,"{\"timeoutMs\":25000}"));
    }
    [Theory] [InlineData("authentication","{\"mode\":\"ApiKey\"}")] [InlineData("authentication","{\"mode\":\"Anonymous\"}")]
    public void AuthenticationModeCannotBeChangedThroughTargetParameters(string type,string config)
    {
        var template=Split(type,config);Assert.Empty(template.EnvironmentFields);Assert.Equal(PolicyConfigurationValidator.Normalize(type,config),Resolve(template,"{}"));Assert.Equal(422,Assert.Throws<ApiException>(()=>Resolve(template,"{\"mode\":\"JWT\"}")).Status);
    }
    [Theory] [InlineData("unknown","{}")] [InlineData("timeout","{\"timeoutMs\":1,\"secretRef\":\"vault://test/key\"}")]
    public void UnknownPolicyOrFieldsCannotEnterArtifact(string type,string config)=>Assert.Equal(422,Assert.Throws<ApiException>(()=>Split(type,config)).Status);
    [Fact] public void RouteKeysAreDerivedAndDuplicateLogicalRoutesAreRejected()
    {
        var input=Content();var bytes=WebApi.Domain.Delivery.ReleaseArtifactCanonicalizer.Serialize(input);using var json=JsonDocument.Parse(bytes);var key=json.RootElement.GetProperty("routes")[0].GetProperty("key").GetString();Assert.False(string.IsNullOrEmpty(key));
        Assert.Equal(422,Assert.Throws<ApiException>(()=>Hash(input with{Routes=[input.Routes[0],input.Routes[0]]})).Status);
    }
    [Fact] public void PublicContractContainsNoVersionSourceDocuments()
    {
        var bytes=WebApi.Domain.Delivery.ReleaseArtifactCanonicalizer.Serialize(Content());var text=Encoding.UTF8.GetString(bytes);Assert.DoesNotContain("openapiSource",text);Assert.DoesNotContain("openapiDocument",text);Assert.DoesNotContain("gatewayPublicUrl",text);Assert.DoesNotContain("credentials",text);
    }
    [Fact] public void JwtEnvironmentMaterialIsAbsentAndTargetIssuerCannotChangeAlgorithms()
    {
        using var rsa=RSA.Create(2048);var key=rsa.ExportParameters(false);string Url(byte[] b)=>Convert.ToBase64String(b).TrimEnd('=').Replace('+','-').Replace('/','_');
        var jwks=new{keys=new[]{new{kid="source-public-key",kty="RSA",alg="RS256",use="sig",n=Url(key.Modulus!),e=Url(key.Exponent!)}}};
        var source=JsonSerializer.Serialize(new{mode="JWT",issuer="https://test-issuer.example",audiences=new[]{"test-audience"},allowedAlgorithms=new[]{"RS256","ES256"},allowedTokenTypes=new[]{"JWT","at+jwt"},clockSkewSeconds=30,maxTokenLifetimeSeconds=3600,applicationClaim="client_id",applicationMappings=new[]{new{claimValue="source-client",applicationId=api}},jwks,forwardBearer=false});
        var reversed=System.Text.Json.Nodes.JsonNode.Parse(source)!;reversed["allowedAlgorithms"]=new System.Text.Json.Nodes.JsonArray("ES256","RS256");reversed["allowedTokenTypes"]=new System.Text.Json.Nodes.JsonArray("at+jwt","JWT");
        var template=Split("authentication",source);Assert.Equal(template.FrozenConfig,Split("authentication",reversed.ToJsonString()).FrozenConfig);var portable=Encoding.UTF8.GetString(CanonicalJson.Serialize(template));foreach(var forbidden in new[]{"test-issuer","test-audience","source-public-key","source-client",Url(key.Modulus!)})Assert.DoesNotContain(forbidden,portable);
        var target=JsonSerializer.Serialize(new{issuer="https://prod-issuer.example",audiences=new[]{"prod-audience"},applicationMappings=new[]{new{claimValue="prod-client",applicationId=version}},jwks});var resolved=Resolve(template,target);using var doc=JsonDocument.Parse(resolved);Assert.Equal("JWT",doc.RootElement.GetProperty("mode").GetString());Assert.Contains(doc.RootElement.GetProperty("allowedAlgorithms").EnumerateArray(),x=>x.GetString()=="RS256");Assert.Equal("https://prod-issuer.example",doc.RootElement.GetProperty("issuer").GetString());
        Assert.Equal(422,Assert.Throws<ApiException>(()=>Resolve(template,target[..^1]+",\"allowedAlgorithms\":[\"ES256\"]}")).Status);
    }
    [Fact] public void AuthenticationPolicyAndExplicitRouteModeMustAgree()
    {
        var content=Content(policies:[Split("authentication","{\"mode\":\"Anonymous\"}")]);
        Assert.Equal(422,Assert.Throws<ApiException>(()=>Hash(content)).Status);
    }
}
