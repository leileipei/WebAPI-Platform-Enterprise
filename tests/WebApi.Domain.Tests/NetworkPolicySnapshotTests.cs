using System.Text.Json;
using WebApi.Contracts.Catalog;
using WebApi.Contracts.Common;
using WebApi.Contracts.Policies;
using WebApi.Contracts.Releases;
using WebApi.Contracts.Runtime;
using WebApi.Domain.Policies;
using WebApi.Domain.Runtime;
using WebApi.Infrastructure.Releases;
using Xunit;

namespace WebApi.Domain.Tests;

public sealed class NetworkPolicySnapshotTests
{
    private static readonly Guid Env=Guid.Parse("31000000-0000-0000-0000-000000000001"),Org=Guid.Parse("31000000-0000-0000-0000-000000000002"),Project=Guid.Parse("31000000-0000-0000-0000-000000000003"),Api=Guid.Parse("31000000-0000-0000-0000-000000000004"),Version=Guid.Parse("31000000-0000-0000-0000-000000000005"),Cluster=Guid.Parse("31000000-0000-0000-0000-000000000006"),Route=Guid.Parse("31000000-0000-0000-0000-000000000007"),Destination=Guid.Parse("31000000-0000-0000-0000-000000000008");
    private static readonly DateTimeOffset Now=new(2026,10,13,0,0,0,TimeSpan.Zero);
    private static PolicyBindingConfiguration[] All()=>[
        new(Guid.NewGuid(),"authentication","{\"mode\":\"ApiKey\"}",true,0),
        new(Guid.NewGuid(),"timeout","{\"timeoutMs\":30000}",true,1),
        new(Guid.NewGuid(),"rate_limit",PolicyConfigurationTests.Rate,true,2),
        new(Guid.NewGuid(),"circuit_breaker",PolicyConfigurationTests.Circuit,true,3),
        new(Guid.NewGuid(),"retry",AdvancedPolicyConfigurationTests.Retry,true,4),
        new(Guid.NewGuid(),"cache",AdvancedPolicyConfigurationTests.Cache,true,5),
        new(Guid.NewGuid(),"cors",CorsPolicyConfigurationTests.Source,true,6),
        new(Guid.NewGuid(),"ip_filter","{\"mode\":\"AllowList\",\"ranges\":[\"192.0.2.0/24\"]}",true,7),
        new(Guid.NewGuid(),"transform",JsonBodyConfigurationTests.Source,true,8)];
    private static FrozenReleaseCandidate Candidate(PolicyBindingConfiguration[] policies,long baseline=0)
    {
        var api=new ApiDto(Api,Org,Project,null,"API","API",null,"Draft",Org,1);
        var version=new VersionDto(Version,Api,"1","Draft","compatible",null,null,null,null,Org,Now,null,1);
        var route=new RouteDto(Route,Version,Env,"Route","/orders","/orders",["GET"],Cluster,100,-100,true,30000,true,1);
        var cluster=new ClusterDto(Cluster,Project,Env,"Cluster","RoundRobin",false,"/health",30,"Active",1,[new(Destination,Cluster,"Backend","http://test-backend:8080/",1,true,null,1)]);
        return new(Env,Org,Project,baseline,[Version],[new(api,version,[],[])],[route],[cluster],policies.Select(p=>new FrozenPolicy(p.PolicyId,p.Type,p.Config,p.Enabled,1)).ToArray(),policies.Select(p=>new FrozenBinding(Route,p.PolicyId,p.Priority)).ToArray(),[],[]);
    }
    private static RuntimeSnapshot Empty()=>new("2.0",Env,0,Now,[],[],[],[]);
    private static RuntimeSnapshot Compile(FrozenReleaseCandidate candidate,RuntimeSnapshot? baseline=null)
    {
        var compiled=new SnapshotCompiler().Compile(candidate,baseline??Empty(),(baseline?.ConfigVersion??0)+1,Now);
        return SnapshotValidator.ParsePayload(compiled.Payload.Span,Env);
    }

    [Fact]
    public void AllNinePolicies_BindAndCompile23()
    {
        var policies=All();var effective=PolicyBindingRules.Validate(policies,true,30000);Assert.True(effective.RequireApiKey);
        Assert.Equal(422,Assert.Throws<ApiException>(()=>PolicyBindingRules.Validate([..policies,new(Guid.NewGuid(),"timeout","{\"timeoutMs\":1000}",true,9)],true)).Status);
        var snapshot=Compile(Candidate(policies));Assert.Equal("2.3",snapshot.SchemaVersion);Assert.Equal(9,snapshot.Policies.Count);Assert.Equal(9,snapshot.Routes[0].PolicyBindings!.Count);
        foreach(var policy in snapshot.Policies)Assert.Equal(policy.Id,RuntimePolicyIdentity.Create(policy.SourcePolicyId!.Value,policy.SourceRevision!.Value,policy.Type,policy.Config));
    }

    [Theory]
    [InlineData("cors")][InlineData("ip_filter")][InlineData("transform")]
    public void EachNetworkPolicy_Requires23(string type)
    {var snapshot=Compile(Candidate(All().Where(p=>p.Type==type).ToArray()));Assert.Equal("2.3",snapshot.SchemaVersion);Assert.Single(snapshot.Policies);}

    [Fact]
    public void PartialRelease_RetainedNetworkPolicy_Requires23()
    {
        var baseline=Compile(Candidate(All().Where(p=>p.Type=="transform").ToArray()));
        var retained=baseline.Routes[0] with {Id=Guid.NewGuid(),ApiId=Guid.NewGuid(),Path="/retained"};baseline=baseline with {Routes=[retained]};
        var next=Candidate(All().Where(p=>p.Type=="authentication").ToArray(),1);var snapshot=Compile(next,baseline);
        Assert.Equal("2.3",snapshot.SchemaVersion);Assert.Equal(CanonicalJson.Serialize(retained),CanonicalJson.Serialize(snapshot.Routes.Single(r=>r.Id==retained.Id)));
        Assert.Equal(baseline.Policies[0],snapshot.Policies.Single(p=>p.Id==baseline.Policies[0].Id));
    }

    [Theory]
    [InlineData("2.0")][InlineData("2.1")][InlineData("2.2")]
    public void OldSchemas_RejectNetworkTypes(string schema)
    {
        var candidate=Candidate(All().Where(p=>p.Type=="authentication").ToArray());var snapshot=Compile(candidate);
        foreach(var source in All().Where(p=>p.Type is "cors" or "ip_filter" or "transform")) {
            var config=PolicyConfigurationValidator.Normalize(source.Type,source.Config);var id=RuntimePolicyIdentity.Create(source.PolicyId,1,source.Type,config);
            var invalid=snapshot with {SchemaVersion=schema,Policies=[new(id,source.Type,config,source.PolicyId,1)],Routes=[snapshot.Routes[0] with {PolicyBindings=schema=="2.0"?null:[new(id,0)]}]};
            Assert.Equal(422,Assert.Throws<ApiException>(()=>SnapshotValidator.Validate(invalid,Env)).Status);
        }
    }

    [Fact]
    public void DisabledOrUnboundNetworkPolicy_DoesNotUpgradeLegacySchema()
    {
        var source=All().Single(p=>p.Type=="cors");var disabled=Compile(Candidate([source with {Enabled=false}]));Assert.Equal("2.0",disabled.SchemaVersion);Assert.Empty(disabled.Policies);
        var unbound=Compile(Candidate([source]) with {Bindings=[]});Assert.Equal("2.0",unbound.SchemaVersion);Assert.Empty(unbound.Policies);
    }

    [Theory]
    [InlineData("cors","Allowed",null,true)][InlineData("cors","Preflight",null,true)][InlineData("cors","Bypass","no_origin",true)]
    [InlineData("cors","Rejected","cors_origin_rejected",true)][InlineData("cors","Rejected","ip_denied",false)]
    [InlineData("ip_filter","Allowed",null,true)][InlineData("ip_filter","Rejected","client_ip_unavailable",true)]
    [InlineData("ip_filter","Rejected","cors_origin_rejected",false)]
    [InlineData("transform","Applied",null,true)][InlineData("transform","Rejected","transform_body_busy",true)]
    [InlineData("transform","Rejected","transform_body_signature_unsupported",true)][InlineData("transform","Rejected","transform_json_operation_failed",true)]
    [InlineData("transform","Bypass","head_response",true)][InlineData("transform","Bypass","cache_hit",true)]
    [InlineData("transform","Rejected","dynamic-private-reason",false)][InlineData("transform","Applied","transform_body_busy",false)]
    [InlineData("retry","Bypass","response_transform_rejected",true)][InlineData("retry","Retried","response_transform_rejected",false)]
    public void NewPolicyDecisionContract_AcceptsOnlyControlledTypeReasonPairs(string type,string decision,string? reason,bool valid)
    {Assert.Equal(valid,PolicyDecisionValues.Valid(new(Guid.NewGuid(),type,1,decision,reason)));}

    [Fact]
    public void NetworkSnapshot_ContentIdentityAndLimits_RemainStrict()
    {
        var snapshot=Compile(Candidate(All().Where(p=>p.Type=="transform").ToArray()));
        var wrong=snapshot.Policies[0] with {Config=JsonBodyConfigurationTests.Source.Replace("262144","524288")};
        Assert.Equal(422,Assert.Throws<ApiException>(()=>SnapshotValidator.Validate(snapshot with {Policies=[wrong]},Env)).Status);
        Assert.Equal(422,Assert.Throws<ApiException>(()=>SnapshotValidator.ParsePayload(new byte[16*1024*1024+1],Env)).Status);
        Assert.Equal(422,Assert.Throws<ApiException>(()=>SnapshotValidator.Validate(snapshot with {Routes=Enumerable.Repeat(snapshot.Routes[0],10001).ToArray()},Env)).Status);
        Assert.Equal(422,Assert.Throws<ApiException>(()=>SnapshotValidator.Validate(snapshot with {Policies=Enumerable.Repeat(snapshot.Policies[0],40001).ToArray()},Env)).Status);
    }
}
