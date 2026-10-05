using System.Text;
using System.Text.Json;
using WebApi.Contracts.Catalog;
using WebApi.Contracts.Common;
using WebApi.Contracts.Releases;
using WebApi.Contracts.Runtime;
using WebApi.Domain.Policies;
using WebApi.Domain.Runtime;
using WebApi.Infrastructure.Releases;
using Xunit;
namespace WebApi.Domain.Tests;
public sealed class PolicySnapshotCompilerTests
{
    private static readonly Guid Env=Guid.NewGuid(),Org=Guid.NewGuid(),Project=Guid.NewGuid(),Api=Guid.NewGuid(),Version=Guid.NewGuid(),Cluster=Guid.NewGuid(),Policy=Guid.NewGuid(),Other=Guid.NewGuid();
    private static readonly DateTimeOffset Now=new(2026,10,5,0,0,0,TimeSpan.Zero);
    private const string Rate="{\"algorithm\":\"TokenBucket\",\"keyBy\":\"Route\",\"refillTokens\":1,\"windowMs\":1000,\"burst\":3,\"redisFailureMode\":\"Reject\"}";
    private static FrozenReleaseCandidate Candidate(long revision=1,bool enabled=true)
    {
        var a=new ApiDto(Api,Org,Project,null,"ORDERS","订单",null,"Draft",Guid.NewGuid(),1);var v=new VersionDto(Version,Api,"1","Draft","compatible",null,null,null,null,Guid.NewGuid(),Now,null,1);
        var r=new RouteDto(Guid.NewGuid(),Version,Env,"订单","/orders","/orders",["GET"],Cluster,100,-100,true,30000,true,1);
        var c=new ClusterDto(Cluster,Project,Env,"集群","RoundRobin",false,"/health",30,"Active",1,[new(Guid.NewGuid(),Cluster,"backend","http://test-backend:8080/",1,true,null,1)]);
        return new(Env,Org,Project,1,[Version],[new(a,v,[],[])],[r],[c],[new(Policy,"rate_limit",Rate,enabled,revision)],[new(r.Id,Policy,0)],[],[]);
    }
    private static RuntimeSnapshot Baseline()=>new("2.0",Env,1,Now,[new(Guid.NewGuid(),Other,Guid.NewGuid(),Cluster,"/other",["GET"],-100,2500,false)],[new(Cluster,Env,"RoundRobin",false,"/health",30,[new(Guid.NewGuid(),"http://test-backend:8080/",1)])],[],[]);
    private static RuntimeSnapshot Compile(FrozenReleaseCandidate c,RuntimeSnapshot baseline)=>JsonSerializer.Deserialize<RuntimeSnapshot>(new SnapshotCompiler().Compile(c,baseline,baseline.ConfigVersion+1,Now).Payload.Span,CanonicalJson.Options)!;
    [Fact] public void IdentityCanonicalGoldenVector()
    {Assert.Equal(Guid.Parse("ae1edf66-762b-8472-444d-8fc44cb69ca1"),RuntimePolicyIdentity.Create(Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"),7,"timeout","{\"timeoutMs\":1000}"));}
    [Fact] public void SharedPolicyTwoFrozenRevisionsCoexist()
    {var c=Candidate();var old=Compile(c,Baseline());var oldRoute=old.Routes.Single(r=>r.ApiId==Api) with {ApiId=Other,Path="/other"};old=old with {Routes=[oldRoute],ConfigVersion=1};var changed=Compile(Candidate(2),old);Assert.Equal("2.1",changed.SchemaVersion);var ids=changed.Policies.Where(p=>p.SourcePolicyId==Policy).Select(p=>p.Id).ToArray();Assert.Equal(2,ids.Length);Assert.NotEqual(ids[0],ids[1]);Assert.Equal(new long?[]{1,2},changed.Policies.Select(p=>p.SourceRevision).OrderBy(x=>x));}
    [Fact] public void UnselectedApiKeepsOldBinding()
    {var old=Compile(Candidate(),Baseline());var retained=old.Routes.Single(r=>r.ApiId==Api) with {ApiId=Other,Path="/other"};old=old with {Routes=[retained],ConfigVersion=1};var changed=Compile(Candidate(2) with {Policies=[new(Policy,"rate_limit",Rate.Replace("\"burst\":3","\"burst\":4"),true,2)]},old);Assert.Equal(retained.PolicyBindings!.Single().PolicyId,changed.Routes.Single(r=>r.ApiId==Other).PolicyBindings!.Single().PolicyId);using var config=JsonDocument.Parse(changed.Policies.Single(p=>p.Id==retained.PolicyBindings!.Single().PolicyId).Config);Assert.Equal(3,config.RootElement.GetProperty("burst").GetInt32());}
    [Fact] public void DisabledPolicyPrunedFromSelectedRoute()
    {var changed=Compile(Candidate(enabled:false),Baseline());Assert.Equal("2.0",changed.SchemaVersion);Assert.Empty(changed.Policies);Assert.All(changed.Routes,r=>Assert.Null(r.PolicyBindings));}
    [Fact] public void TwoPointZeroFieldsAndBytesRemainCompatible()
    {var baseline=Baseline();var bytes=CanonicalJson.Serialize(baseline);var json=Encoding.UTF8.GetString(bytes);Assert.DoesNotContain("policyBindings",json);Assert.DoesNotContain("sourcePolicyId",json);var parsed=JsonSerializer.Deserialize<RuntimeSnapshot>(bytes,CanonicalJson.Options)!;Assert.Equal(bytes,CanonicalJson.Serialize(parsed));SnapshotValidator.Validate(parsed,Env);}
    [Fact] public void LegacyAnonymousRoutePromotionKeepsFoldedBehavior()
    {var promoted=Compile(Candidate(),Baseline());var r=promoted.Routes.Single(r=>r.ApiId==Other);Assert.False(r.RequireApiKey);Assert.Equal(2500,r.TimeoutMs);Assert.Null(r.PolicyBindings);SnapshotValidator.Validate(promoted,Env);}
    [Fact] public void TwoPointZeroCannotSmuggleAdvancedBinding()
    {var snapshot=Compile(Candidate(),Baseline());Assert.Throws<ApiException>(()=>SnapshotValidator.Validate(snapshot with {SchemaVersion="2.0"},Env));}
    [Fact] public void FoldedAuthTimeoutMustMatchBinding()
    {var c=Candidate();var auth=Guid.NewGuid();var timeout=Guid.NewGuid();c=c with {Policies=[c.Policies[0],new(auth,"authentication","{\"mode\":\"Anonymous\"}",true,1),new(timeout,"timeout","{\"timeoutMs\":1000}",true,1)],Bindings=[c.Bindings[0],new(c.Routes[0].Id,auth,1),new(c.Routes[0].Id,timeout,2)]};var s=Compile(c,Baseline());var r=s.Routes.Single(r=>r.ApiId==Api);Assert.False(r.RequireApiKey);Assert.Equal(1000,r.TimeoutMs);Assert.Throws<ApiException>(()=>SnapshotValidator.Validate(s with {Routes=s.Routes.Select(x=>x.Id==r.Id?x with {RequireApiKey=true}:x).ToArray()},Env));Assert.Throws<ApiException>(()=>SnapshotValidator.Validate(s with {Routes=s.Routes.Select(x=>x.Id==r.Id?x with {TimeoutMs=1001}:x).ToArray()},Env));}
    [Fact] public void UnknownSchemaMissingReferenceAndDuplicateTypeRejected()
    {var s=Compile(Candidate(),Baseline());Assert.Throws<ApiException>(()=>SnapshotValidator.Validate(s with {SchemaVersion="9.0"},Env));Assert.Throws<ApiException>(()=>SnapshotValidator.Validate(s with {Policies=[]},Env));var c=Candidate();var extra=new FrozenPolicy(Guid.NewGuid(),"rate_limit",Rate,false,1);Assert.Throws<ApiException>(()=>Compile(c with {Policies=[c.Policies[0],extra],Bindings=[c.Bindings[0],new(c.Routes[0].Id,extra.Id,0)]},Baseline()));}
}
