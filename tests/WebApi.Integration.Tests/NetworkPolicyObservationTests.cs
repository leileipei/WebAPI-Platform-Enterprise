using System.Text.Json;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using WebApi.Contracts.Observability;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using WebApi.Contracts.Common;
using WebApi.Contracts.Policies;
using WebApi.Infrastructure.Observability;
using Xunit;
namespace WebApi.Integration.Tests;
public sealed class NetworkPolicyObservationTests
{
    private static readonly Guid Env=Guid.NewGuid();
    private static readonly Dictionary<string,Guid> Ids=PolicyDecisionValues.Types.ToDictionary(t=>t,_=>Guid.NewGuid());
    private static TrustedObservationScope Scope()=>new(Guid.NewGuid(),Guid.NewGuid(),[Env],[],new Dictionary<Guid,string>(),new Dictionary<Guid,string>(),new Dictionary<Guid,ObservationDestination>(),new Dictionary<Guid,IReadOnlySet<Guid>>{{Env,Ids.Values.ToHashSet()}});
    private static Dictionary<string,string> Attributes()
    {var attributes=new Dictionary<string,string>();foreach(var type in Ids.Keys){var prefix="webapi.policy."+type+".";attributes[prefix+"id"]=Ids[type].ToString();attributes[prefix+"type"]=type;attributes[prefix+"revision"]="1";attributes[prefix+"decision"]=type switch{"cache"=>"Miss","retry"=>"Bypass","transform"=>"Rejected",_=>"Allowed"};if(type=="retry")attributes[prefix+"rejection_reason"]="single_attempt";if(type=="transform"){attributes[prefix+"rejection_reason"]="transform_json_operation_failed";attributes[prefix+"operation_index"]="2";attributes[prefix+"body_operation_count"]="0";}}return attributes;}
    [Fact]
    public void Observation_EightTypesAndControlledBodyFields_ProjectWithoutRawValues()
    {var attributes=Attributes();attributes["body_sample"]="private-body-canary";attributes["origin"]="https://private-origin.example";var read=PolicyObservationProjection.Read(attributes,Scope(),Env);Assert.False(read.Rejected);Assert.Equal(8,read.Decisions.Count);var transform=JsonSerializer.SerializeToElement(Assert.Single(read.Decisions,d=>d.PolicyType=="transform"),CanonicalJson.Options);Assert.Equal(2,transform.GetProperty("operationIndex").GetInt32());Assert.Equal(0,transform.GetProperty("bodyOperationCount").GetInt32());var all=JsonSerializer.Serialize(read,CanonicalJson.Options);Assert.DoesNotContain("private-body-canary",all);Assert.DoesNotContain("private-origin",all);}
    [Theory][InlineData("rate_limit","operation_index","2")][InlineData("transform","operation_index","0")][InlineData("transform","operation_index","33")][InlineData("transform","body_operation_count","65")][InlineData("transform","operation_index","-1")][InlineData("transform","operation_index","bad")]
    public void Observation_IllegalBodyFieldsRejectTheDecision(string type,string field,string value)
    {var attributes=Attributes();attributes["webapi.policy."+type+"."+field]=value;var read=PolicyObservationProjection.Read(attributes,Scope(),Env);Assert.True(read.Rejected);Assert.DoesNotContain(read.Decisions,d=>d.PolicyType==type);}
    [Fact]
    public void Observation_LegacyFiveTypesStillReadableWithNoNewNullFields()
    {var attributes=Attributes();foreach(var key in attributes.Keys.Where(k=>k.StartsWith("webapi.policy.cors.",StringComparison.Ordinal)||k.StartsWith("webapi.policy.ip_filter.",StringComparison.Ordinal)||k.StartsWith("webapi.policy.transform.",StringComparison.Ordinal)).ToArray())attributes.Remove(key);var read=PolicyObservationProjection.Read(attributes,Scope(),Env);Assert.False(read.Rejected);Assert.Equal(5,read.Decisions.Count);var json=JsonSerializer.Serialize(read,CanonicalJson.Options);Assert.DoesNotContain("operationIndex",json);Assert.DoesNotContain("bodyOperationCount",json);}
    [Fact]
    public void Observation_FixedNetworkReasonsAndFiltersRespectScope()
    {var a=Attributes();a["webapi.policy.cors.decision"]="Rejected";a["webapi.policy.cors.rejection_reason"]="ip_denied";var rejected=PolicyObservationProjection.Read(a,Scope(),Env);Assert.True(rejected.Rejected);Assert.DoesNotContain(rejected.Decisions,d=>d.PolicyType=="cors");PolicyObservationProjection.RequireFilter(Scope(),Ids["transform"],"Rejected");Assert.Equal(404,Assert.Throws<ApiException>(()=>PolicyObservationProjection.RequireFilter(Scope(),Guid.NewGuid(),"Rejected")).Status);}
    [Fact]
    public async Task Observation_LokiHttpProjection_KeepsEightScopedDecisionsAndBodyFields()
    {
        await using var fixture=new LogFixture();await File.WriteAllTextAsync(fixture.Secret,Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));if(!OperatingSystem.IsWindows())File.SetUnixFileMode(fixture.Secret,UnixFileMode.UserRead|UnixFileMode.UserWrite);
        await fixture.Api.InitializeAsync(b=>{b.Configuration["Observability:Enabled"]="true";b.Configuration["Observability:CursorSigningSecretFile"]=fixture.Secret;b.Configuration["Observability:IpHmacSecretFile"]=fixture.Secret;b.Services.PostConfigure<HttpClientFactoryOptions>("observability",o=>o.HttpMessageHandlerBuilderActions.Add(builder=>builder.PrimaryHandler=fixture.Source));});await fixture.Api.SeedConsumerAsync();await ObservationTestSupport.GrantAsync(fixture.Api,["log.read"]);using var login=await fixture.Api.LoginAsync();login.EnsureSuccessStatusCode();
        await using(var db=fixture.Api.Context()){db.Add(new GatewayNode{EnvironmentId=fixture.Api.Environment.Id,NodeName="logs-node"});foreach(var type in Ids.Keys)db.Add(new Policy{Id=Ids[type],OrganizationId=fixture.Api.Organization.Id,ProjectId=fixture.Api.Project.Id,Name=type,Type=type});await db.SaveChangesAsync();}
        fixture.Source.EnvironmentId=fixture.Api.Environment.Id;fixture.Source.ApiId=fixture.Api.Api.Id;fixture.Source.ApplicationId=fixture.Api.Application.Id;fixture.Source.DestinationId=fixture.Api.Destination.Id;fixture.Source.Rows.Add(new(Guid.NewGuid(),fixture.Source.Nano,"GET","/orders","logs-node"));foreach(var (key,value) in Attributes())fixture.Source.ExtraMetadata[key.Replace('.','_')]=value;fixture.Source.ExtraMetadata["body_sample"]="private-body-canary";
        var url=$"/api/v1/observability/logs?organizationId={fixture.Api.Organization.Id}&projectId={fixture.Api.Project.Id}&environmentId={fixture.Api.Environment.Id}&start={Uri.EscapeDataString(fixture.Source.Start.ToString("O"))}&end={Uri.EscapeDataString(fixture.Source.End.ToString("O"))}&policyId={Ids["transform"]}&policyDecision=Rejected";
        var page=(await fixture.Api.Client.GetFromJsonAsync<ObservationEnvelope<CursorPage<AccessLogDto>>>(url))!;Assert.Equal(SourceState.Available,page.SourceState);var row=Assert.Single(page.Data!.Items);Assert.Equal(8,row.PolicyDecisions.Count);var transform=JsonSerializer.SerializeToElement(Assert.Single(row.PolicyDecisions,d=>d.PolicyType=="transform"),CanonicalJson.Options);Assert.Equal(2,transform.GetProperty("operationIndex").GetInt32());Assert.Equal(0,transform.GetProperty("bodyOperationCount").GetInt32());Assert.DoesNotContain("private-body-canary",JsonSerializer.Serialize(page));
    }

}
