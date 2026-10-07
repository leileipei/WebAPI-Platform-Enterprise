using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using WebApi.Contracts.Common;
using WebApi.Contracts.Observability;
using WebApi.Contracts.Policies;
using WebApi.Infrastructure.Observability;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;
namespace WebApi.Integration.Tests;
public sealed class AdvancedPolicyObservationTests
{
    private static readonly Guid Env=Guid.NewGuid(),Destination=Guid.NewGuid();
    private static readonly string[] Types=["authentication","rate_limit","circuit_breaker","retry","cache"];
    private static readonly Dictionary<string,Guid> Policies=Types.ToDictionary(x=>x,_=>Guid.NewGuid());
    private static TrustedObservationScope Scope()=>new(Guid.NewGuid(),Guid.NewGuid(),[Env],[],new Dictionary<Guid,string>(),new Dictionary<Guid,string>(),new Dictionary<Guid,ObservationDestination>{{Destination,new(Destination,Guid.NewGuid(),Env,"visible",true)}},new Dictionary<Guid,IReadOnlySet<Guid>>{{Env,Policies.Values.ToHashSet()}});
    private static Dictionary<string,string> Attributes()
    {
        var a=new Dictionary<string,string>();foreach(var type in Types){var prefix="webapi.policy."+type+".";a[prefix+"id"]=Policies[type].ToString();a[prefix+"revision"]="1";a[prefix+"type"]=type;a[prefix+"decision"]=type switch{"retry"=>"Retried","cache"=>"Stored",_=>"Allowed"};}
        a["webapi.policy.retry.attempt_count"]="3";a["webapi.policy.cache.cache_read"]="Miss";a["webapi.policy.cache.cache_write"]="Stored";a["webapi.attempt.count"]="3";a["webapi.cache.disposition"]="Stored";a["webapi.forward.attempts"]=JsonSerializer.Serialize(Enumerable.Range(1,3).Select(n=>new{number=n,destinationId=Destination,status=n<3?503:200,outcome="Completed",durationSeconds=0.02}));a["jwt_sub"]="private-sub-canary";a["raw_token"]="private-token-canary";a["cache_key"]="private-cache-key-canary";return a;
    }
    [Fact]public void FiveDecisionsAndAttemptFieldsProjectWithScopedDestinations()
    {
        var a=Attributes();var read=PolicyObservationProjection.Read(a,Scope(),Env);Assert.Equal(5,read.Decisions.Count);Assert.False(read.Rejected);var json=JsonSerializer.SerializeToElement(read.Decisions,CanonicalJson.Options);var cache=json.EnumerateArray().Single(x=>x.GetProperty("policyType").GetString()=="cache");Assert.Equal("Miss",cache.GetProperty("cacheRead").GetString());Assert.Equal("Stored",cache.GetProperty("cacheWrite").GetString());
        var trace=TraceProjection.Project("0123456789abcdef0123456789abcdef",[new(Env,"0123456789abcdef",null,"gateway.request",DateTimeOffset.UtcNow,100,"Server","Ok",a)],Scope());Assert.False(trace.PartialTrace);var span=JsonSerializer.SerializeToElement(Assert.Single(trace.Spans),CanonicalJson.Options);Assert.Equal(3,span.GetProperty("attemptCount").GetInt32());Assert.Equal(3,span.GetProperty("forwardAttempts").GetArrayLength());Assert.Equal("Stored",span.GetProperty("cacheDisposition").GetString());
        var all=JsonSerializer.Serialize(trace);foreach(var marker in new[]{"private-sub-canary","private-token-canary","private-cache-key-canary"})Assert.DoesNotContain(marker,all);
    }
    [Theory][InlineData("foreign-policy")][InlineData("foreign-destination")][InlineData("unknown-outcome")][InlineData("oversize-attempts")][InlineData("invalid-count")]
    public void ForeignPoliciesAndUnknownOutcomeArePartialNotLeaked(string reason)
    {
        var a=Attributes();var foreign=Guid.NewGuid();
        if(reason=="foreign-policy")a["webapi.policy.authentication.id"]=foreign.ToString();
        if(reason=="invalid-count")a["webapi.attempt.count"]="999";
        if(reason=="oversize-attempts")a["webapi.forward.attempts"]=new string('x',5000);
        if(reason is "foreign-destination" or "unknown-outcome")a["webapi.forward.attempts"]=JsonSerializer.Serialize(new[]{new{number=1,destinationId=reason=="foreign-destination"?foreign:Destination,status=503,outcome=reason=="unknown-outcome"?"private-outcome-canary":"Completed",durationSeconds=0.02}});
        var trace=TraceProjection.Project("0123456789abcdef0123456789abcdef",[new(Env,"0123456789abcdef",null,"gateway.request",DateTimeOffset.UtcNow,100,"Server","Error",a)],Scope());Assert.True(trace.PartialTrace);Assert.Single(trace.Spans);var json=JsonSerializer.Serialize(trace);Assert.DoesNotContain(foreign.ToString(),json);Assert.DoesNotContain("private-outcome-canary",json);Assert.DoesNotContain(new string('x',100),json);
    }
    [Fact]public void OldDecisionJsonHasNoNewFieldsAndMissingAdvancedObservationRemainsMissing()
    {
        var old=new PolicyDecisionDto(Policies["rate_limit"],"rate_limit",1,"Allowed",null);var json=JsonSerializer.Serialize(old,CanonicalJson.Options);Assert.DoesNotContain("cacheRead",json);Assert.DoesNotContain("cacheWrite",json);Assert.DoesNotContain("attemptCount",json);
        var trace=TraceProjection.Project("0123456789abcdef0123456789abcdef",[new(Env,"0123456789abcdef",null,"gateway.request",DateTimeOffset.UtcNow,12,"Server","Ok",new Dictionary<string,string>())],Scope());var projected=JsonSerializer.Serialize(trace,CanonicalJson.Options);Assert.DoesNotContain("attemptCount",projected);Assert.DoesNotContain("forwardAttempts",projected);Assert.DoesNotContain("cacheDisposition",projected);
    }
    [Fact]public async Task LokiAndCsvKeepFiveScopedDecisionsAndAttemptsWithoutPrivateMarkers()
    {
        await using var f=new LogFixture();await File.WriteAllTextAsync(f.Secret,Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));if(!OperatingSystem.IsWindows())File.SetUnixFileMode(f.Secret,UnixFileMode.UserRead|UnixFileMode.UserWrite);
        await f.Api.InitializeAsync(b=>{b.Configuration["Observability:Enabled"]="true";b.Configuration["Observability:CursorSigningSecretFile"]=f.Secret;b.Configuration["Observability:IpHmacSecretFile"]=f.Secret;b.Services.PostConfigure<HttpClientFactoryOptions>("observability",o=>o.HttpMessageHandlerBuilderActions.Add(builder=>builder.PrimaryHandler=f.Source));});await f.Api.SeedConsumerAsync();await ObservationTestSupport.GrantAsync(f.Api,["log.read"]);using var login=await f.Api.LoginAsync();login.EnsureSuccessStatusCode();
        await using(var db=f.Api.Context()){db.Add(new GatewayNode{EnvironmentId=f.Api.Environment.Id,NodeName="logs-node"});foreach(var type in Types)db.Add(new Policy{Id=Policies[type],OrganizationId=f.Api.Organization.Id,ProjectId=f.Api.Project.Id,Name=type,Type=type});await db.SaveChangesAsync();}
        f.Source.EnvironmentId=f.Api.Environment.Id;f.Source.ApiId=f.Api.Api.Id;f.Source.ApplicationId=f.Api.Application.Id;f.Source.DestinationId=f.Api.Destination.Id;f.Source.Rows.Add(new(Guid.NewGuid(),f.Source.Nano,"GET","/orders","logs-node"));var attrs=Attributes();attrs["webapi.forward.attempts"]=JsonSerializer.Serialize(new[]{new{number=1,destinationId=f.Api.Destination.Id,status=200,outcome="Completed",durationSeconds=0.02}});attrs["webapi.attempt.count"]="1";attrs["webapi.policy.retry.attempt_count"]="1";attrs["webapi.policy.retry.decision"]="Bypass";attrs["webapi.policy.retry.rejection_reason"]="single_attempt";foreach(var (k,v) in attrs)f.Source.ExtraMetadata[k.Replace('.','_')]=v;
        var url=$"/api/v1/observability/logs?organizationId={f.Api.Organization.Id}&projectId={f.Api.Project.Id}&environmentId={f.Api.Environment.Id}&start={Uri.EscapeDataString(f.Source.Start.ToString("O"))}&end={Uri.EscapeDataString(f.Source.End.ToString("O"))}";
        var page=(await f.Api.Client.GetFromJsonAsync<ObservationEnvelope<CursorPage<AccessLogDto>>>(url))!;Assert.Equal(SourceState.Available,page.SourceState);var row=Assert.Single(page.Data!.Items);Assert.Equal(5,row.PolicyDecisions.Count);var item=JsonSerializer.SerializeToElement(row,CanonicalJson.Options);Assert.Equal(1,item.GetProperty("attemptCount").GetInt32());Assert.Single(item.GetProperty("forwardAttempts").EnumerateArray());using var exported=await f.Api.Client.GetAsync(url.Replace("/logs?","/logs/export?"));exported.EnsureSuccessStatusCode();var csv=await exported.Content.ReadAsStringAsync();Assert.Contains("AttemptCount,CacheDisposition,ForwardAttempts",csv);Assert.Contains("Stored",csv);Assert.Contains(f.Api.Destination.Id.ToString(),csv);foreach(var marker in new[]{"private-sub-canary","private-token-canary","private-cache-key-canary"}){Assert.DoesNotContain(marker,csv);Assert.DoesNotContain(marker,item.GetRawText());}
        f.Source.ExtraMetadata["webapi_policy_authentication_id"]=Guid.NewGuid().ToString();var partial=(await f.Api.Client.GetFromJsonAsync<ObservationEnvelope<CursorPage<AccessLogDto>>>(url))!;Assert.Equal(SourceState.Partial,partial.SourceState);Assert.Equal(4,Assert.Single(partial.Data!.Items).PolicyDecisions.Count);
    }
}
