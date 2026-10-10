using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Contracts.Common;
using WebApi.Contracts.Releases;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Delivery;
using WebApi.Infrastructure.Delivery.Pipelines;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Security;
using WebApi.Integration.Tests.Support;
using Xunit;
namespace WebApi.Integration.Tests;
public sealed class PipelineGateContextTests
{
 internal sealed record Seed(Guid RunId,Guid StageId,Guid PromotionId,Guid FormalAttemptId,Guid CurrentAttemptId);
 internal static async Task<Seed> SeedPipelineFactsAsync(PipelineScenario s)
 {
  var definition=s.Definition with{Stages=s.Definition.Stages.Select((p,i)=>i==0?p with{EvidenceValidityMinutes=30}:p).ToArray()};
  using var created=await s.Api.WriteAsync(HttpMethod.Post,$"/api/v1/projects/{s.Api.Project.Id}/release-pipelines",definition);created.EnsureSuccessStatusCode();var id=(await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();var published=await PipelineDefinitionTests.PublishAsync(s,id);
  var versionId=published.GetProperty("id").GetGuid();var content=published.GetProperty("content").Deserialize<PipelineVersionContent>(CanonicalJson.Options)!;
  await using var db=s.Api.Context();var version=await db.Set<ReleasePipelineVersion>().SingleAsync(v=>v.Id==versionId);
  var run=new ReleasePipelineRun{OrganizationId=s.Api.Organization.Id,ProjectId=s.Api.Project.Id,PipelineVersionId=versionId,DefinitionHash=version.DefinitionHash,RootArtifactId=s.RootArtifactId,RootArtifactHash=s.Source.Artifact.ArtifactHash,SourceEnvironmentId=s.Environments[0],SourceReleaseId=s.Source.ReleaseId,SourceConfigVersion=1,SourceDeploymentSequence=1,PolicyRevision=1,CurrentStageOrder=2,CreatedBy=s.Api.User.Id};
  var first=new ReleasePipelineRunStage{RunId=run.Id,ProjectId=run.ProjectId,StageOrder=1,EnvironmentId=s.Environments[0],StageArtifactId=s.RootArtifactId,ProfileJson=Json(content.Profiles[0]),ProfileHash=Hash(content.Profiles[0]),Status="Passed"};
  var stage=new ReleasePipelineRunStage{RunId=run.Id,ProjectId=run.ProjectId,StageOrder=2,EnvironmentId=s.Environments[1],SourceStageId=first.Id,ProfileJson=Json(content.Profiles[1]),ProfileHash=Hash(content.Profiles[1]),Status="AwaitingPrecheck"};
  var formal=new ReleasePipelineStageAttempt{RunStageId=stage.Id,RunId=run.Id,ProjectId=run.ProjectId,EnvironmentId=stage.EnvironmentId,AttemptNo=1,Status="DeploymentFailed",DeadlineAt=DateTimeOffset.UtcNow.AddHours(1)};
  var current=new ReleasePipelineStageAttempt{RunStageId=stage.Id,RunId=run.Id,ProjectId=run.ProjectId,EnvironmentId=stage.EnvironmentId,AttemptNo=2,OriginAttemptId=formal.Id,DeadlineAt=DateTimeOffset.UtcNow.AddHours(1)};
  var policy=new ProjectDeliveryPolicy{OrganizationId=run.OrganizationId,ProjectId=run.ProjectId,SourceEnvironmentId=s.Environments[^2],TargetEnvironmentId=s.Environments[^1],Mode="PipelineRequired",ActivePipelineVersionId=versionId,UpdatedBy=s.Api.User.Id};
  var promotion=new ReleasePromotion{OrganizationId=run.OrganizationId,ProjectId=run.ProjectId,ArtifactId=s.RootArtifactId,SourceEnvironmentId=first.EnvironmentId,TargetEnvironmentId=stage.EnvironmentId,SourceReleaseId=s.Source.ReleaseId,RequestedBy=s.Api.User.Id,PipelineRunStageId=stage.Id,StageAttemptId=formal.Id,GateOrigin="PipelineRunStage",FrozenPolicyJson=Json(new DeliveryPolicyDto(policy.Id,run.ProjectId,policy.SourceEnvironmentId,policy.TargetEnvironmentId,policy.Mode,policy.RequiredTestTypes,policy.VerificationValidityMinutes,policy.Revision,versionId))};
  // This fixture persists scoped relationships; it is not a deploy/run API acceptance test.
  db.AddRange(run,first,stage,formal,current,policy);await db.SaveChangesAsync();stage.CurrentAttemptId=current.Id;db.Add(promotion);await db.SaveChangesAsync();formal.PromotionId=promotion.Id;await db.SaveChangesAsync();return new(run.Id,stage.Id,promotion.Id,formal.Id,current.Id);
 }
 private static string Json<T>(T value)=>System.Text.Encoding.UTF8.GetString(CanonicalJson.Serialize(value));
 private static string Hash<T>(T value)=>Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(CanonicalJson.Serialize(value)));
 internal static DeliveryGateContextResolver Resolver(WebApiDbContext db,IServiceProvider services)=>new(db,services.GetRequiredService<ScopeResolver>(),services.GetRequiredService<AuthorizationService>());
 [Fact] public async Task AdjacentProfilesDoNotCollapseIntoProjectSummary()
 {await using var s=new PipelineScenario();await s.InitializeEnvironmentsAsync(4);var seed=await SeedPipelineFactsAsync(s);using var services=s.Api.Services();await using var db=s.Api.Context();var context=await Resolver(db,services.ServiceProvider).ResolvePromotionAsync(seed.PromotionId,new(s.Api.User.Id,"gate-test"),default);Assert.Equal("PipelineRunStage",context.Origin);Assert.Equal(s.Environments[0],context.SourceEnvironmentId);Assert.Equal(s.Environments[1],context.TargetEnvironmentId);Assert.Equal(30,context.SourceEvidence.ValidityMinutes);Assert.Equal(1440,context.TargetEvidence.ValidityMinutes);Assert.Equal(seed.FormalAttemptId,context.Pipeline!.FormalAttemptId);Assert.Equal(seed.CurrentAttemptId,context.Pipeline.CurrentAttemptId);Assert.NotEqual(context.SourceEvidence.ProfileHash,context.TargetEvidence.ProfileHash);}
 [Fact] public async Task DirectStageUsesItsOwnProfile()
 {await using var s=new PipelineScenario();await s.InitializeEnvironmentsAsync(4);var seed=await SeedPipelineFactsAsync(s);using var services=s.Api.Services();await using var db=s.Api.Context();var c=await Resolver(db,services.ServiceProvider).ResolveStageAsync(seed.StageId,new(s.Api.User.Id,"gate-test"),default);Assert.Equal(1440,c.TargetEvidence.ValidityMinutes);Assert.Equal(seed.StageId,c.Pipeline!.StageId);Assert.Equal(seed.CurrentAttemptId,c.Pipeline.CurrentAttemptId);}
 [Theory][InlineData("hash")][InlineData("adjacency")][InlineData("policy")]
 public async Task CorruptOrStaleServerBindingCannotBecomeAGate(string change)
 {await using var s=new PipelineScenario();await s.InitializeEnvironmentsAsync(4);var seed=await SeedPipelineFactsAsync(s);await using var db=s.Api.Context();if(change=="hash")(await db.Set<ReleasePipelineRunStage>().SingleAsync(x=>x.Id==seed.StageId)).ProfileHash=new string('f',64);if(change=="adjacency")(await db.Set<ReleasePipelineRunStage>().SingleAsync(x=>x.Id==seed.StageId)).SourceStageId=null;if(change=="policy")(await db.Set<ProjectDeliveryPolicy>().SingleAsync()).Revision++;await db.SaveChangesAsync();using var services=s.Api.Services();var error=await Assert.ThrowsAsync<ApiException>(()=>Resolver(db,services.ServiceProvider).ResolvePromotionAsync(seed.PromotionId,new(s.Api.User.Id,"gate-test"),default));Assert.Equal(409,error.Status);}
 [Fact] public async Task PipelineCannotResolveThroughConnectionOrigin()
 {await using var s=new PipelineScenario();await s.InitializeEnvironmentsAsync(4);await SeedPipelineFactsAsync(s);using var services=s.Api.Services();await using var db=s.Api.Context();var e=await Assert.ThrowsAsync<ApiException>(()=>Resolver(db,services.ServiceProvider).ResolveConnectionAsync(s.Api.Project.Id,new(s.Api.User.Id,"gate-test"),default));Assert.Equal(409,e.Status);}
 [Fact] public async Task OriginalConnectionRetainsTypesValidityAndNoPipelineBinding()
 {await using var s=new PromotionScenario();await s.InitializeAsync();using var services=s.Api.Services();await using var db=s.Api.Context();var policy=await db.Set<ProjectDeliveryPolicy>().SingleAsync();var c=await Resolver(db,services.ServiceProvider).ResolveConnectionAsync(s.Api.Project.Id,new(s.Api.User.Id,"gate-test"),default);Assert.Null(c.Pipeline);Assert.Equal("ProjectConnection",c.Origin);Assert.Equal(policy.SourceEnvironmentId,c.SourceEnvironmentId);Assert.Equal(policy.TargetEnvironmentId,c.TargetEnvironmentId);Assert.Equal(policy.RequiredTestTypes,c.SourceEvidence.RequiredTypes);Assert.Equal(policy.VerificationValidityMinutes,c.SourceEvidence.ValidityMinutes);}
 [Fact] public async Task MissingStageIsHidden()
 {await using var s=new PipelineScenario();await s.InitializeEnvironmentsAsync();using var services=s.Api.Services();await using var db=s.Api.Context();var e=await Assert.ThrowsAsync<ApiException>(()=>Resolver(db,services.ServiceProvider).ResolveStageAsync(Guid.NewGuid(),new(s.Api.User.Id,"gate-test"),default));Assert.Equal(404,e.Status);}
 [Fact] public async Task WritableContextRejectsForgedRootProvenance()
 {await using var s=new PipelineScenario();await s.InitializeEnvironmentsAsync(4);var seed=await SeedPipelineFactsAsync(s);using var services=s.Api.Services();await using var db=s.Api.Context();var resolver=Resolver(db,services.ServiceProvider);var actor=new ActorContext(s.Api.User.Id,"gate-test");var c=await resolver.ResolveStageAsync(seed.StageId,actor,default);var forged=c with{Pipeline=c.Pipeline! with{RootArtifactHash=new string('f',64)}};var e=await Assert.ThrowsAsync<ApiException>(()=>resolver.RequireStageWritableAsync(forged,actor,default));Assert.Equal(409,e.Status);}
 [Fact] public async Task MissingEnvironmentScopeHidesStageAndPromotion()
 {await using var s=new PipelineScenario();await s.InitializeEnvironmentsAsync(4);var seed=await SeedPipelineFactsAsync(s);await using(var db=s.Api.Context()){await db.Set<UserProjectScope>().Where(x=>x.UserId==s.Api.User.Id).ExecuteDeleteAsync();db.Add(new UserProjectScope{UserId=s.Api.User.Id,OrganizationId=s.Api.Organization.Id,ProjectId=s.Api.Project.Id,EnvironmentId=s.Environments[0],AccessMode="read_write"});await db.SaveChangesAsync();}using var services=s.Api.Services();await using var context=s.Api.Context();var resolver=Resolver(context,services.ServiceProvider);var actor=new ActorContext(s.Api.User.Id,"scope-test");Assert.Equal(404,(await Assert.ThrowsAsync<ApiException>(()=>resolver.ResolveStageAsync(seed.StageId,actor,default))).Status);Assert.Equal(404,(await Assert.ThrowsAsync<ApiException>(()=>resolver.ResolvePromotionAsync(seed.PromotionId,actor,default))).Status);}
 [Fact] public async Task OriginalEvidencePermissionDoesNotRequirePipelineRunGrant()
 {await using var s=new PipelineScenario();await s.InitializeEnvironmentsAsync(4);var seed=await SeedPipelineFactsAsync(s);await using(var db=s.Api.Context()){var permission=await db.Set<Permission>().SingleAsync(p=>p.Code=="pipeline.run");await db.Set<RolePermission>().Where(p=>p.PermissionId==permission.Id).ExecuteDeleteAsync();}using var services=s.Api.Services();await using var context=s.Api.Context();var resolver=Resolver(context,services.ServiceProvider);var actor=new ActorContext(s.Api.User.Id,"original-permission-test");var c=await resolver.ResolveStageAsync(seed.StageId,actor,default);await resolver.RequireStageWritableAsync(c,actor,default);}
}
