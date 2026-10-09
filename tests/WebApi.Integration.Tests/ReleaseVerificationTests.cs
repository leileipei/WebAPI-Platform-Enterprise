using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Releases;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;
namespace WebApi.Integration.Tests;
public sealed class ReleaseVerificationTests
{
    [Fact] public async Task SourceEvidenceUsesServerDeploymentAndPolicyContext()
    {
        await using var s=new DeliveryScenario();await s.InitializeAsync();await using(var db=s.Api.Context()){var target=new EnvironmentRecord{ProjectId=s.Api.Project.Id,Code="PROD",Name="生产",IsProduction=true};db.Add(target);db.Add(new ProjectDeliveryPolicy{OrganizationId=s.Api.Organization.Id,ProjectId=s.Api.Project.Id,SourceEnvironmentId=s.Api.Environment.Id,TargetEnvironmentId=target.Id,RequiredTestTypes=["InterfaceFunction","Integration","ContractCompatibility"],VerificationValidityMinutes=60,Revision=7,UpdatedBy=s.Api.User.Id});await db.SaveChangesAsync();}
        using var response=await s.RecordAsync();response.EnsureSuccessStatusCode();var v=(await response.Content.ReadFromJsonAsync<ReleaseVerificationDto>())!;Assert.True(v.IsManual);Assert.Equal(s.ReleaseId,v.ReleaseId);Assert.Equal(s.Artifact.SourceSnapshotHash,v.SnapshotHash);Assert.Equal(1,v.ConfigVersion);Assert.Equal(1,v.DeploymentSequence);Assert.Equal(1,v.AccessAddressRevision);Assert.Equal(7,v.PolicyRevision);Assert.Equal(v.FinishedAt.AddMinutes(60),v.ExpiresAt);
        await using var context=s.Api.Context();var fact=await context.Set<ReleaseVerification>().SingleAsync();Assert.Contains("source-gateway.example",fact.AccessContextJson);Assert.DoesNotContain("internal",fact.AccessContextJson,StringComparison.OrdinalIgnoreCase);Assert.Empty(await context.Set<OutboxMessage>().Where(x=>x.EventType!="desired_config").ToArrayAsync());
    }
    [Fact] public async Task FailedManualEvidenceIsRetainedWithoutClaimingAcceptance()
    {await using var s=new DeliveryScenario();await s.InitializeAsync();using var response=await s.RecordAsync("Failed");response.EnsureSuccessStatusCode();var v=(await response.Content.ReadFromJsonAsync<ReleaseVerificationDto>())!;Assert.Equal("Failed",v.Result);Assert.Equal(v.FinishedAt.AddMinutes(1440),v.ExpiresAt);await using var db=s.Api.Context();Assert.Empty(await db.Set<ReleaseTestAcceptance>().ToArrayAsync());}
    [Fact] public async Task RequestCannotOverrideDeploymentOrServerScope()
    {await using var s=new DeliveryScenario();await s.InitializeAsync();using var response=await ApiFixture.CommandAsync(s.Api.Client,$"/api/v1/release-artifacts/{s.Artifact.Id}/verifications",new{type="Integration",result="Passed",startedAt=DateTimeOffset.UtcNow,finishedAt=DateTimeOffset.UtcNow,configVersion=999,deploymentSequence=999,environmentId=Guid.NewGuid(),accessAddressRevision=999,policyRevision=999,snapshotHash=new string('f',64),isManual=false});response.EnsureSuccessStatusCode();var v=(await response.Content.ReadFromJsonAsync<ReleaseVerificationDto>())!;Assert.Equal(s.Api.Environment.Id,v.EnvironmentId);Assert.Equal(1,v.ConfigVersion);Assert.Equal(1,v.DeploymentSequence);Assert.True(v.IsManual);Assert.Equal(s.Artifact.SourceSnapshotHash,v.SnapshotHash);}
    [Theory] [InlineData("automatic")] [InlineData("invalid_result")] [InlineData("future")] [InlineData("reversed")]
    public async Task InvalidTypeResultAndTimesAreRejected(string invalid)
    {await using var s=new DeliveryScenario();await s.InitializeAsync();using var response=await s.RecordAsync(invalid=="invalid_result"?"Unknown":"Passed",invalid=="automatic"?"AutoScanner":"Integration",started:invalid=="reversed"?DateTimeOffset.UtcNow:DateTimeOffset.UtcNow.AddMinutes(-5),finished:invalid=="future"?DateTimeOffset.UtcNow.AddMinutes(2):DateTimeOffset.UtcNow.AddMinutes(-1));Assert.Equal(HttpStatusCode.UnprocessableEntity,response.StatusCode);}
    [Fact] public async Task RecordingRequiresDedicatedPermissionAndLiveSource()
    {await using var s=new DeliveryScenario();await s.InitializeAsync(false);using var denied=await s.RecordAsync();Assert.Equal(HttpStatusCode.Forbidden,denied.StatusCode);}
    [Fact] public async Task ChangedSourceSequenceCannotBeUsedForNewEvidence()
    {await using var s=new DeliveryScenario();await s.InitializeAsync();await using(var db=s.Api.Context()){await db.Set<EnvironmentRecord>().ExecuteUpdateAsync(x=>x.SetProperty(e=>e.DeploymentSequence,55));}using var stale=await s.RecordAsync();Assert.Equal(HttpStatusCode.Conflict,stale.StatusCode);}
    [Fact] public async Task RepeatedCommandKeepsOneImmutableFact()
    {await using var s=new DeliveryScenario();await s.InitializeAsync();var body=new RecordVerificationRequest("Integration","Passed",DateTimeOffset.UtcNow,DateTimeOffset.UtcNow);var key=Guid.NewGuid().ToString("N");using var first=await ApiFixture.CommandAsync(s.Api.Client,$"/api/v1/release-artifacts/{s.Artifact.Id}/verifications",body,key);first.EnsureSuccessStatusCode();using var second=await ApiFixture.CommandAsync(s.Api.Client,$"/api/v1/release-artifacts/{s.Artifact.Id}/verifications",body,key);second.EnsureSuccessStatusCode();Assert.Equal((await first.Content.ReadFromJsonAsync<ReleaseVerificationDto>())!.Id,(await second.Content.ReadFromJsonAsync<ReleaseVerificationDto>())!.Id);await using var db=s.Api.Context();Assert.Single(await db.Set<ReleaseVerification>().ToArrayAsync());}
    [Fact] public async Task ExistingSourceCommandReceiptRemainsCompatibleWithProductionOnlyOptionalField()
    {await using var s=new DeliveryScenario();await s.InitializeAsync();var body=new RecordVerificationRequest("Integration","Passed",DateTimeOffset.UtcNow,DateTimeOffset.UtcNow);var key=Guid.NewGuid().ToString("N");using var first=await ApiFixture.CommandAsync(s.Api.Client,$"/api/v1/release-artifacts/{s.Artifact.Id}/verifications",body,key);first.EnsureSuccessStatusCode();var original=(await first.Content.ReadFromJsonAsync<ReleaseVerificationDto>())!;
    var legacy=new{artifactId=s.Artifact.Id,request=new{body.Type,body.Result,body.StartedAt,body.FinishedAt,body.ReportId,body.Comment}};var hash=Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(WebApi.Contracts.Common.CanonicalJson.Serialize(legacy)));await using(var db=s.Api.Context())await db.Set<IdempotencyRecord>().Where(r=>r.Key==key&&r.Operation=="release.verification.source").ExecuteUpdateAsync(x=>x.SetProperty(r=>r.RequestHash,hash));
    using var replay=await ApiFixture.CommandAsync(s.Api.Client,$"/api/v1/release-artifacts/{s.Artifact.Id}/verifications",body,key);replay.EnsureSuccessStatusCode();Assert.Equal(original.Id,(await replay.Content.ReadFromJsonAsync<ReleaseVerificationDto>())!.Id);await using var context=s.Api.Context();Assert.Equal(1,await context.Set<ReleaseVerification>().CountAsync());}
    [Theory][InlineData(true)][InlineData(false)]
    public async Task SourceEvidenceFinishingBeforeActualDeploymentIsRejected(bool beforeCreation)
    {await using var s=new DeliveryScenario();await s.InitializeAsync();await using var db=s.Api.Context();var release=await db.Set<ReleaseRecord>().SingleAsync(r=>r.Id==s.ReleaseId);var finished=(beforeCreation?release.CreatedAt:release.CompletedAt!.Value).AddSeconds(-1);using var response=await s.RecordAsync(started:finished.AddSeconds(-1),finished:finished);Assert.Equal(HttpStatusCode.UnprocessableEntity,response.StatusCode);Assert.Equal("verification_predates_deployment",(await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());Assert.Empty(await db.Set<ReleaseVerification>().ToArrayAsync());}
    [Fact] public async Task SourceEvidenceAtActualDeploymentCompletionIsAccepted()
    {await using var s=new DeliveryScenario();await s.InitializeAsync();await using var db=s.Api.Context();var finished=(await db.Set<ReleaseRecord>().SingleAsync(r=>r.Id==s.ReleaseId)).CompletedAt!.Value;using var response=await s.RecordAsync(started:finished,finished:finished);response.EnsureSuccessStatusCode();Assert.Equal(finished,(await response.Content.ReadFromJsonAsync<ReleaseVerificationDto>())!.FinishedAt);}

}
