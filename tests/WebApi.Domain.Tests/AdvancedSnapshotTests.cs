using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WebApi.Contracts.Catalog;
using WebApi.Contracts.Common;
using WebApi.Contracts.Releases;
using WebApi.Contracts.Runtime;
using WebApi.Domain.Runtime;
using WebApi.Infrastructure.Releases;
using WebApi.Infrastructure.Runtime;
using Xunit;

namespace WebApi.Domain.Tests;

public sealed class AdvancedSnapshotTests
{
    private static readonly Guid Env=Guid.Parse("10000000-0000-0000-0000-000000000001"), Org=Guid.Parse("10000000-0000-0000-0000-000000000002"), Project=Guid.Parse("10000000-0000-0000-0000-000000000003"), Api=Guid.Parse("10000000-0000-0000-0000-000000000004"), Version=Guid.Parse("10000000-0000-0000-0000-000000000005"), Cluster=Guid.Parse("10000000-0000-0000-0000-000000000006"), Policy=Guid.Parse("10000000-0000-0000-0000-000000000007"), App=Guid.Parse("10000000-0000-0000-0000-000000000008"), Route=Guid.Parse("10000000-0000-0000-0000-000000000009"), Destination=Guid.Parse("10000000-0000-0000-0000-00000000000a");
    private static readonly DateTimeOffset Now=new(2026,10,7,0,0,0,TimeSpan.Zero);
    private const string Rate="""{"algorithm":"TokenBucket","keyBy":"Route","refillTokens":1,"windowMs":1000,"burst":3,"redisFailureMode":"Reject"}""";
    private static string Jwt()
    {
        var n=new byte[256]; n[0]=128; n[^1]=1;
        return JsonSerializer.Serialize(new {mode="JWT",issuer="https://issuer.example",audiences=new[]{"api"},allowedAlgorithms=new[]{"RS256"},allowedTokenTypes=new[]{"JWT"},clockSkewSeconds=30,maxTokenLifetimeSeconds=3600,applicationClaim="azp",applicationMappings=new[]{new {claimValue="app",applicationId=App}},jwks=new {keys=new[]{new {kid="key",kty="RSA",alg="RS256",use="sig",n=Convert.ToBase64String(n).TrimEnd('=').Replace('+','-').Replace('/','_'),e="AQAB"}}},forwardBearer=false});
    }
    private static FrozenReleaseCandidate Candidate(string type,string config,long baseline=0)
    {
        var api=new ApiDto(Api,Org,Project,null,"API","API",null,"Draft",Org,1);
        var version=new VersionDto(Version,Api,"1","Draft","compatible",null,null,null,null,Org,Now,null,1);
        var route=new RouteDto(Route,Version,Env,"Route","/orders","/orders",["GET"],Cluster,100,-100,true,30000,true,1);
        var cluster=new ClusterDto(Cluster,Project,Env,"Cluster","RoundRobin",false,"/health",30,"Active",1,[new(Destination,Cluster,"Backend","http://test-backend:8080/",1,true,null,1)]);
        return new(Env,Org,Project,baseline,[Version],[new(api,version,[],[])],[route],[cluster],[new(Policy,type,config,true,1)],[new(Route,Policy,0)],[new(new(App,Org,Project,"app","App","Owner","Active",1),[],[])],[]);
    }
    private static RuntimeSnapshot Empty()=>new("2.0",Env,0,Now,[],[],[],[]);
    private static CompiledSnapshot Compile(FrozenReleaseCandidate candidate,RuntimeSnapshot? baseline=null)=>new SnapshotCompiler().Compile(candidate,baseline??Empty(),(baseline?.ConfigVersion??0)+1,Now);
    private static RuntimeSnapshot Read(CompiledSnapshot compiled)=>JsonSerializer.Deserialize<RuntimeSnapshot>(compiled.Payload.Span,CanonicalJson.Options)!;

    [Theory]
    [InlineData("authentication")]
    [InlineData("retry")]
    [InlineData("cache")]
    public void JwtRetryOrCacheSelects22(string type)
    {
        var config=type=="authentication"?Jwt():type=="retry"?AdvancedPolicyConfigurationTests.Retry:AdvancedPolicyConfigurationTests.Cache;
        var compiled=Compile(Candidate(type,config)); var snapshot=Read(compiled);
        Assert.Equal("2.2",snapshot.SchemaVersion); Assert.Single(snapshot.Routes[0].PolicyBindings!); SnapshotValidator.Validate(snapshot,Env);
        if(type=="authentication")
        {
            Assert.False(snapshot.Routes[0].RequireApiKey);
            using var json=JsonDocument.Parse(compiled.Payload); Assert.Equal("JWT",json.RootElement.GetProperty("routes")[0].GetProperty("authenticationMode").GetString());
        }
    }
    [Fact]
    public void JwtWithoutGrantRetainsApplicationAndReturnsNoAnonymousFold()
    {
        var compiled=Compile(Candidate("authentication",Jwt())); var snapshot=Read(compiled); var app=Assert.Single(snapshot.Applications);
        Assert.Equal(App,app.Id); Assert.Empty(app.Permissions); Assert.False(snapshot.Routes[0].RequireApiKey);
        Assert.Throws<ApiException>(()=>SnapshotValidator.Validate(snapshot with {Applications=[]},Env));
        Assert.Throws<ApiException>(()=>SnapshotValidator.Validate(snapshot with {Routes=[snapshot.Routes[0] with {PolicyBindings=null}]},Env));
    }
    [Fact]
    public void PartialPublishPreservesOldJwtApplication()
    {
        var old=Read(Compile(Candidate("authentication",Jwt()))); var oldRoute=old.Routes[0] with {ApiId=Guid.Parse("20000000-0000-0000-0000-000000000004"),Path="/retained"}; old=old with {Routes=[oldRoute]};
        var candidate=Candidate("retry",AdvancedPolicyConfigurationTests.Retry,1);
        candidate=candidate with {Routes=[candidate.Routes[0] with {Id=Guid.Parse("20000000-0000-0000-0000-000000000009")}],Bindings=[new(Guid.Parse("20000000-0000-0000-0000-000000000009"),Policy,0)]};
        var snapshot=Read(Compile(candidate,old)); Assert.Equal("2.2",snapshot.SchemaVersion);
        Assert.Equal(CanonicalJson.Serialize(oldRoute),CanonicalJson.Serialize(snapshot.Routes.Single(r=>r.ApiId==oldRoute.ApiId)));
        Assert.Equal(old.Policies.Single(),snapshot.Policies.Single(p=>p.Id==old.Policies.Single().Id)); Assert.Equal(App,Assert.Single(snapshot.Applications).Id); Assert.Empty(snapshot.Applications[0].Permissions);
    }
    [Theory]
    [InlineData("2.0")]
    [InlineData("2.1")]
    public void Legacy20And21BytesHashesUnchangedAndRejectNewFields(string schema)
    {
        var golden = schema=="2.0" ? Golden20 : Golden21;
        var expectedHash = schema=="2.0" ? Hash20 : Hash21;
        var bytes=Encoding.UTF8.GetBytes(golden); var snapshot=JsonSerializer.Deserialize<RuntimeSnapshot>(bytes,CanonicalJson.Options)!;
        Assert.Equal(schema,snapshot.SchemaVersion); ValidatePayload(bytes);
        Assert.Equal(bytes,CanonicalJson.Serialize(snapshot)); Assert.Equal(expectedHash,Convert.ToHexStringLower(SHA256.HashData(CanonicalJson.Serialize(snapshot))));
        Assert.DoesNotContain("authenticationMode",Encoding.UTF8.GetString(bytes));
        foreach(var mode in new JsonNode?[]{null,JsonValue.Create("JWT"),JsonValue.Create("ApiKey")})
        {
            var json=JsonNode.Parse(bytes)!; json["routes"]![0]!["authenticationMode"]=mode; Assert.ThrowsAny<Exception>(()=>ValidatePayload(Encoding.UTF8.GetBytes(json.ToJsonString())));
        }
        var forged=snapshot with {Policies=[new(Policy,"authentication",Jwt())]}; Assert.Throws<ApiException>(()=>SnapshotValidator.Validate(forged,Env));
    }
    private const string Golden20 = """{"applications":[],"clusters":[{"destinations":[{"address":"http://test-backend:8080/","id":"10000000-0000-0000-0000-00000000000a","weight":1}],"environmentId":"10000000-0000-0000-0000-000000000001","healthCheckEnabled":false,"healthCheckIntervalSec":30,"healthCheckPath":"/health","id":"10000000-0000-0000-0000-000000000006","loadBalancingPolicy":"RoundRobin","sourceId":"10000000-0000-0000-0000-000000000006"}],"configVersion":1,"environmentId":"10000000-0000-0000-0000-000000000001","generatedAt":"2026-10-07T00:00:00\u002B00:00","policies":[],"routes":[{"apiId":"10000000-0000-0000-0000-000000000004","apiVersionId":"10000000-0000-0000-0000-000000000005","clusterId":"10000000-0000-0000-0000-000000000006","id":"10000000-0000-0000-0000-000000000009","matchOrder":-100,"methods":["GET"],"path":"/orders","requireApiKey":true,"timeoutMs":30000}],"schemaVersion":"2.0"}""";
    private const string Hash20 = "eee07c7de6507576b6a2497112deaee42a8f9e073b9309fe3a01e65fa1bb6b0d";
    private const string Golden21 = """{"applications":[],"clusters":[{"destinations":[{"address":"http://test-backend:8080/","id":"10000000-0000-0000-0000-00000000000a","weight":1}],"environmentId":"10000000-0000-0000-0000-000000000001","healthCheckEnabled":false,"healthCheckIntervalSec":30,"healthCheckPath":"/health","id":"10000000-0000-0000-0000-000000000006","loadBalancingPolicy":"RoundRobin","sourceId":"10000000-0000-0000-0000-000000000006"}],"configVersion":1,"environmentId":"10000000-0000-0000-0000-000000000001","generatedAt":"2026-10-07T00:00:00\u002B00:00","policies":[{"config":"{\u0022algorithm\u0022:\u0022TokenBucket\u0022,\u0022burst\u0022:3,\u0022keyBy\u0022:\u0022Route\u0022,\u0022redisFailureMode\u0022:\u0022Reject\u0022,\u0022refillTokens\u0022:1,\u0022windowMs\u0022:1000}","id":"4a05c937-7aba-4e0d-e4a8-e4b609fc4ba9","sourcePolicyId":"10000000-0000-0000-0000-000000000007","sourceRevision":1,"type":"rate_limit"}],"routes":[{"apiId":"10000000-0000-0000-0000-000000000004","apiVersionId":"10000000-0000-0000-0000-000000000005","clusterId":"10000000-0000-0000-0000-000000000006","id":"10000000-0000-0000-0000-000000000009","matchOrder":-100,"methods":["GET"],"path":"/orders","policyBindings":[{"policyId":"4a05c937-7aba-4e0d-e4a8-e4b609fc4ba9","priority":0}],"requireApiKey":true,"timeoutMs":30000}],"schemaVersion":"2.1"}""";
    private const string Hash21 = "6f3f43f538192e3a253dede6d8bf732d7260192edc91fb2c4116b0e7eeec44b7";
    private static void ValidatePayload(byte[] bytes)
    {
        var envelope=new SnapshotEnvelope(Org,1,1,Convert.ToHexStringLower(SHA256.HashData(bytes)),bytes.Length); RedisSnapshotStore.Validate(Env,envelope,bytes);
    }
}
