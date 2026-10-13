using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Contracts.Common;
using WebApi.Contracts.Policies;
using WebApi.Gateway.Observability;
using WebApi.Gateway.Tests.Support;
using WebApi.Infrastructure.Persistence.Entities;
using Xunit;
using static WebApi.Gateway.Tests.Support.RecordingTelemetrySink;
using SigningKey=WebApi.Gateway.Tests.JwtTokenVerifierTests.KeyFixture;
namespace WebApi.Gateway.Tests;
public sealed class NetworkPolicyTelemetryTests
{
    [Fact]
    public void Telemetry_AllEightControlledTypes_AreEmittedWithinCapacity()
    {var items=PolicyDecisionValues.Types.Select(type=>new PolicyDecisionDto(Guid.NewGuid(),type,1,type switch{"cache"=>"Miss","retry"=>"Bypass","transform"=>"Applied",_=>"Allowed"},type=="retry"?"single_attempt":null)).ToArray();var values=TelemetryAttributes.Values(new(){PolicyDecisions=items});foreach(var type in PolicyDecisionValues.Types)Assert.Contains("webapi.policy."+type+".decision",values.Keys);Assert.Equal(8,values.Keys.Count(k=>k.EndsWith(".decision",StringComparison.Ordinal)));}
    [Theory][InlineData("rate_limit",2,0,false)][InlineData("transform",0,0,false)][InlineData("transform",33,0,false)][InlineData("transform",2,65,false)][InlineData("transform",2,0,true)]
    public void Decision_NewBodyFieldsAreBoundedAndTransformOnly(string type,int index,int count,bool valid)
    {var json=JsonSerializer.Serialize(new{policyId=Guid.NewGuid(),policyType=type,policyRevision=1,decision=type=="transform"?"Rejected":"Allowed",rejectionReason=type=="transform"?"transform_json_operation_failed":null,operationIndex=index,bodyOperationCount=count});var dto=JsonSerializer.Deserialize<PolicyDecisionDto>(json,CanonicalJson.Options)!;Assert.Equal(valid,PolicyDecisionValues.Valid(dto));if(valid){var roundtrip=JsonSerializer.SerializeToElement(dto,CanonicalJson.Options);Assert.Equal(2,roundtrip.GetProperty("operationIndex").GetInt32());Assert.Equal(0,roundtrip.GetProperty("bodyOperationCount").GetInt32());}}
    [Fact]
    public void Decision_LegacyRecordsDoNotGainNullBodyFields()
    {var dto=new PolicyDecisionDto(Guid.NewGuid(),"authentication",1,"Allowed",null);var json=JsonSerializer.SerializeToElement(dto,CanonicalJson.Options);Assert.False(json.TryGetProperty("operationIndex",out _));Assert.False(json.TryGetProperty("bodyOperationCount",out _));Assert.True(PolicyDecisionValues.Valid(JsonSerializer.Deserialize<PolicyDecisionDto>(json,CanonicalJson.Options)!));}
    private static void Telemetry(GatewayFixture f,RecordingTelemetrySink sink)
    {var prior=f.ConfigureGateway;f.ConfigureGateway=b=>{prior?.Invoke(b);b.Configuration["Observability:Enabled"]="true";b.Configuration["Observability:TraceSampleRatio"]="1";b.Configuration["Observability:BatchDelayMs"]="20";b.Services.AddSingleton<ITelemetryBatchSink>(sink);};}
    [Theory][InlineData(false)][InlineData(true)]
    public async Task Telemetry_AtomicResponseFailure_HasIndexAndOnlyCompletedRequestOperations(bool requestBody)
    {
        var sink=new RecordingTelemetrySink();await using var f=new GatewayFixture{ConfigureBackendA=a=>a.Use(async(HttpContext ctx,RequestDelegate next)=>await ctx.Response.WriteAsJsonAsync(new{data="private-body-canary"}))};Telemetry(f,sink);await f.InitializeAsync();if(requestBody)await TransformPipelineTests.RouteAsync(f,"/orders","POST");var config=JsonResponseBodyPipelineTests.Config(both:requestBody,operations:"[{\"op\":\"add\",\"path\":\"/first\",\"value\":\"private-operation-canary\"},{\"op\":\"remove\",\"path\":\"/absent\"}]");await TransformPipelineTests.BindAsync(f,config);await f.ApplyBothAsync(await f.PublishAsync());
        using var message=new HttpRequestMessage(requestBody?HttpMethod.Post:HttpMethod.Get,"/orders");message.Headers.Add("X-API-Key",f.Credential);if(requestBody)message.Content=new StringContent("{\"user_id\":123}",System.Text.Encoding.UTF8,"application/json");using var response=await f.Clients[0].SendAsync(message);Assert.Equal(HttpStatusCode.BadGateway,response.StatusCode);await sink.WaitAsync(()=>sink.Logs.Length==1);Assert.Equal("2",Attribute(sink.Logs[0],"webapi.policy.transform.operation_index"));Assert.Equal(requestBody?"2":"0",Attribute(sink.Logs[0],"webapi.policy.transform.body_operation_count"));var all=string.Join("\n",sink.Items.Select(i=>i.Item.GetRawText()));Assert.DoesNotContain("private-body-canary",all);Assert.DoesNotContain("private-operation-canary",all);Assert.DoesNotContain("/absent",all);
    }
    [Fact]
    public async Task Telemetry_RealJwtNineBindings_EmitEightDecisionsWithoutCanaries()
    {
        using var key=new SigningKey();var sink=new RecordingTelemetrySink();await using var fixture=new CachePipelineFixture();Telemetry(fixture.F,sink);fixture.Respond=async(ctx,n)=>await ctx.Response.WriteAsJsonAsync(new{items=new[]{"original"}});await fixture.Init(circuit:true,rate:true,jwt:key);await CorsPipelineTests.BindAsync(fixture.F);var jwtConfig=fixture.F.Gateways[0].Services.GetRequiredService<WebApi.Gateway.Configuration.RuntimeGenerationStore>().Current!.PoliciesById.Values.Single(p=>p.Type=="authentication").Config;await IpFilterPipelineTests.BindAsync(fixture.F,"AllowList",["127.0.0.0/8"],auth:"JWT",authConfig:jwtConfig);await RetryPipelineTests.Bind(fixture.F,RetryPipelineTests.Config());var config=JsonResponseBodyPipelineTests.Config(headers:true,operations:"[{\"op\":\"add\",\"path\":\"/items/-\",\"value\":\"private-transform-canary\"}]");await TransformPipelineTests.BindAsync(fixture.F,config);
        await using(var db=fixture.F.Control.Context()){var policy=new Policy{OrganizationId=fixture.F.Control.Organization.Id,ProjectId=fixture.F.Control.Project.Id,Name="Timeout",Type="timeout",Config="{\"timeoutMs\":10000}"};db.Add(policy);db.Add(new RoutePolicyBinding{RouteId=fixture.F.RouteId,PolicyId=policy.Id});await db.SaveChangesAsync();}await fixture.F.ApplyBothAsync(await fixture.F.PublishAsync());
        var claims=key.Claims();var now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();claims["iat"]=now-60;claims["exp"]=now+1800;claims["sub"]="private-subject-canary";var token=key.Token(claims);var active=fixture.F.Gateways[0].Services.GetRequiredService<WebApi.Gateway.Configuration.RuntimeGenerationStore>().Current!;Assert.Equal(AuthenticationMode.JWT,active.Indexes.RouteAuthentication[fixture.F.RouteId].Configuration!.Mode);Assert.True(new WebApi.Gateway.Security.JwtTokenVerifier().Verify(token,active.Authentication.Values.Single().Jwt!,DateTimeOffset.UtcNow).Success,"Fixture token invalid against activated JWT config");using var request=new HttpRequestMessage(HttpMethod.Get,"/orders");request.Headers.Authorization=new("Bearer",token);request.Headers.Add("Origin","https://portal.example.com");using var response=await fixture.F.Clients[0].SendAsync(request);response.EnsureSuccessStatusCode();await sink.WaitAsync(()=>sink.Logs.Length==1);foreach(var type in PolicyDecisionValues.Types)Assert.NotNull(Attribute(sink.Logs[0],"webapi.policy."+type+".decision"));Assert.Equal("1",Attribute(sink.Logs[0],"webapi.policy.transform.body_operation_count"));var all=string.Join("\n",sink.Items.Select(i=>i.Item.GetRawText()));foreach(var canary in new[]{token,"private-subject-canary","private-transform-canary","https://portal.example.com"})Assert.DoesNotContain(canary,all);
    }
    [Fact]
    public async Task Telemetry_IpEarlyRejection_OnlyIncludesExecutedStage()
    {var sink=new RecordingTelemetrySink();await using var f=new GatewayFixture();Telemetry(f,sink);await f.InitializeAsync();await CorsPipelineTests.BindAsync(f);await IpFilterPipelineTests.BindAsync(f,"DenyList",["127.0.0.0/8"]);await TransformPipelineTests.BindAsync(f,JsonResponseBodyPipelineTests.Config());await f.ApplyBothAsync(await f.PublishAsync());using var response=await f.RequestAsync();Assert.Equal(HttpStatusCode.Forbidden,response.StatusCode);await sink.WaitAsync(()=>sink.Logs.Length==1);Assert.Equal("Rejected",Attribute(sink.Logs[0],"webapi.policy.ip_filter.decision"));Assert.Null(Attribute(sink.Logs[0],"webapi.policy.cors.decision"));Assert.Null(Attribute(sink.Logs[0],"webapi.policy.authentication.decision"));Assert.Null(Attribute(sink.Logs[0],"webapi.policy.transform.decision"));}
}
