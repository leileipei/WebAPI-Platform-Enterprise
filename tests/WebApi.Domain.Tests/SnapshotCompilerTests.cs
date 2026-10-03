using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WebApi.Contracts.Common;
using WebApi.Contracts.Catalog;
using WebApi.Contracts.Releases;
using WebApi.Contracts.Runtime;
using WebApi.Contracts.Applications;
using WebApi.Infrastructure.Releases;
using Xunit;
namespace WebApi.Domain.Tests;
public sealed class SnapshotCompilerTests
{
    private static readonly Guid Env=Guid.NewGuid(),Org=Guid.NewGuid(),Project=Guid.NewGuid(),SelectedApi=Guid.NewGuid(),OtherApi=Guid.NewGuid(),Version=Guid.NewGuid(),Cluster=Guid.NewGuid();
    private static readonly DateTimeOffset Now=new(2026,10,4,0,0,0,TimeSpan.Zero);
    private static FrozenReleaseCandidate Candidate()
    {
        var a=new ApiDto(SelectedApi,Org,Project,null,"ORDERS","订单",null,"Draft",Guid.NewGuid(),1);
        var v=new VersionDto(Version,SelectedApi,"2","Draft","compatible",null,null,null,null,Guid.NewGuid(),Now,null,1);
        var route=new RouteDto(Guid.NewGuid(),Version,Env,"orders","/orders/{id}","/orders/{}",["GET"],Cluster,100,1999900,true,30000,true,1);
        var destination=new DestinationDto(Guid.NewGuid(),Cluster,"backend","http://test-backend:8080/",1,true,null,1);
        var cluster=new ClusterDto(Cluster,Project,Env,"Orders","RoundRobin",false,"/health",30,"Active",1,[destination]);
        var appId=Guid.NewGuid();var app=new ApplicationDto(appId,Org,Project,"CONSUMER","消费方","Owner","Active",1);
        var secret=Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("not-a-plaintext-runtime-secret")));
        var credential=new FrozenCredential(Guid.NewGuid(),"ak_test",secret,"last","Active",Now.AddDays(-1),Now.AddDays(30),1);
        var grant=new ApplicationPermissionDto(Guid.NewGuid(),appId,SelectedApi,Env,Now.AddDays(-1),Now.AddDays(30),1);
        return new(Env,Org,Project,1,[Version],[new(a,v,[],[])],[route],[cluster],[],[],[new(app,[credential],[grant])],[new("version",Version,1)]);
    }
    private static RuntimeSnapshot Baseline()=>new("2.0",Env,1,Now.AddDays(-1),[new(Guid.NewGuid(),OtherApi,Guid.NewGuid(),Cluster,"/other",["GET"],-100,30000,true),new(Guid.NewGuid(),SelectedApi,Guid.NewGuid(),Cluster,"/old",["GET"],-100,30000,true)],[new(Cluster,Env,"RoundRobin",false,"/health",30,[new(Guid.NewGuid(),"http://test-backend:8080/",1)])],[],[]);
    private static CompiledSnapshot Compile(FrozenReleaseCandidate? c=null)=>new SnapshotCompiler().Compile(c??Candidate(),Baseline(),2,Now);
    private static RuntimeSnapshot Read(CompiledSnapshot result)=>JsonSerializer.Deserialize<RuntimeSnapshot>(result.Payload.Span,CanonicalJson.Options)!;
    [Fact] public void PayloadHashMatchesStoredExactBytes() {var result=Compile();Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(result.Payload.Span)),result.Hash);Assert.Equal(result.Payload.Length,result.Size);Assert.Equal("2.0",Read(result).SchemaVersion);}
    [Fact] public void ForeignEnvironmentReferenceRejected() {var c=Candidate();var cluster=c.Clusters[0] with {EnvironmentId=Guid.NewGuid()};Assert.Throws<ApiException>(()=>Compile(c with {Clusters=[cluster]}));}
    [Fact] public void NoPlaintextSecretOrManagementFields() {var result=Compile();var json=Encoding.UTF8.GetString(result.Payload.Span);Assert.DoesNotContain("not-a-plaintext-runtime-secret",json);foreach(var field in new[]{"owner","createdBy","revision","approval","secretHash","secretLast4","openapiSource"}) Assert.DoesNotContain("\""+field+"\"",json,StringComparison.OrdinalIgnoreCase);Assert.NotEmpty(Read(result).Applications[0].Credentials[0].Hash);}
    [Fact] public void OnlySelectedVersionReplacesItsRuntimeRoutes() {var snapshot=Read(Compile());Assert.Contains(snapshot.Routes,r=>r.ApiId==OtherApi&&r.Path=="/other");Assert.Contains(snapshot.Routes,r=>r.ApiId==SelectedApi&&r.Path=="/orders/{id}");Assert.DoesNotContain(snapshot.Routes,r=>r.Path=="/old");Assert.Equal(2,snapshot.Routes.Count);}
    [Theory] [InlineData("GET")] [InlineData("POST")] [InlineData("PUT")] [InlineData("PATCH")] [InlineData("DELETE")] [InlineData("HEAD")] [InlineData("OPTIONS")]
    public void EverySupportedMethodKeepsDeterministicStaticPriority(string method) {var c=Candidate();var dynamic=c.Routes[0] with {Methods=[method]};var stat=dynamic with {Id=Guid.NewGuid(),Path="/orders/search",NormalizedPath="/orders/search",MatchOrder=-100};var snapshot=Read(Compile(c with {Routes=[dynamic,stat]}));Assert.True(snapshot.Routes.Single(r=>r.Path==stat.Path).MatchOrder<snapshot.Routes.Single(r=>r.Path==dynamic.Path).MatchOrder);}
    [Fact] public void UnknownPolicyRejected() {var c=Candidate();Assert.Throws<ApiException>(()=>Compile(c with {Policies=[new(Guid.NewGuid(),"magic","{}",true,1)]}));}
    [Fact] public void NoDestinationRejected() {var c=Candidate();Assert.Throws<ApiException>(()=>Compile(c with {Clusters=[c.Clusters[0] with {Destinations=[]}]}));}
    [Fact] public void InvalidSchemaRejected() {var c=Candidate();var version=c.Versions[0] with {Schemas=[new(Guid.NewGuid(),Version,"request","Invalid",null,"application/json","\"not a schema\"",null,null)]};Assert.Throws<ApiException>(()=>Compile(c with {Versions=[version]}));}
    [Fact] public void SharedClusterEditDoesNotChangeUnselectedApiBackend()
    {
        var c=Candidate();var destination=c.Clusters[0].Destinations[0] with {Address="http://test-backend:8080/new/"};c=c with {Clusters=[c.Clusters[0] with {Destinations=[destination]}]};
        var snapshot=Read(Compile(c));var retained=snapshot.Routes.Single(r=>r.ApiId==OtherApi);var selected=snapshot.Routes.Single(r=>r.ApiId==SelectedApi);
        Assert.NotEqual(retained.ClusterId,selected.ClusterId);Assert.Equal("http://test-backend:8080/",snapshot.Clusters.Single(x=>x.Id==retained.ClusterId).Destinations[0].Address);Assert.Equal("http://test-backend:8080/new/",snapshot.Clusters.Single(x=>x.Id==selected.ClusterId).Destinations[0].Address);
    }
    [Fact] public void DisabledSelectedRouteRemovesOnlyThatApi()
    {
        var c=Candidate();var snapshot=Read(Compile(c with {Routes=[c.Routes[0] with {Enabled=false}]}));Assert.Single(snapshot.Routes);Assert.Equal(OtherApi,snapshot.Routes[0].ApiId);
    }
}
