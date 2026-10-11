using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WebApi.Contracts.Common;
using WebApi.Contracts.Releases;
using WebApi.Contracts.Security;
using WebApi.ControlPlane;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Releases;
using WebApi.Integration.Tests.Support;
using Xunit;
namespace WebApi.Integration.Tests;
public sealed class DeliveryListBatchQueryTests
{
 private sealed class Capture:DbCommandInterceptor
 {
  public int Commands;
  public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,CommandEventData data,InterceptionResult<DbDataReader> result,CancellationToken ct=default){Commands++;return ValueTask.FromResult(result);}
 }
 // A release page with distinct formal promotions must retain trace rights and
 // bounded SQL; caching only repeated recovery roots cannot satisfy this case.
 [Theory][InlineData(false,false)][InlineData(false,true)][InlineData(true,false)][InlineData(true,true)]
 public async Task IndependentPromotionPagesKeepCompleteTracesWithBoundedSql(bool pipeline,bool restricted)
 {
  await using IAsyncDisposable fixture=pipeline?new PipelinePromotionFixture():new PromotionScenario();ApiFixture api;PromotionDto formal;PipelineRunDto? template=null;
  if(fixture is PipelinePromotionFixture f){await f.InitializeAsync(2);formal=await PipelineExecutionTests.SubmittedAsync(f);api=f.Scenario.Api;template=f.Run;}
  else{var s=(PromotionScenario)fixture;await s.InitializeAsync();formal=await PromotionSubmissionTests.Submitted(s);api=s.Api;}
  var releaseIds=new List<Guid>();var promotions=new List<ReleasePromotion>();var releases=new List<ReleaseRecord>();
  await using(var db=api.Context())
  {
   for(var i=0;i<100;i++)
   {
    Guid? stageId=null,attemptId=null;
    if(template is not null)
    {
     var run=new ReleasePipelineRun{OrganizationId=formal.OrganizationId,ProjectId=formal.ProjectId,PipelineVersionId=template.PipelineVersionId,DefinitionHash=template.DefinitionHash,RootArtifactId=formal.ArtifactId,RootArtifactHash=formal.ArtifactHash,SourceEnvironmentId=formal.SourceEnvironmentId,SourceReleaseId=formal.SourceReleaseId,SourceConfigVersion=1,SourceDeploymentSequence=1,PolicyRevision=1,CreatedBy=api.User.Id,Status="Cancelled",CompletedAt=DateTimeOffset.UtcNow};db.Add(run);Guid? previous=null;
     foreach(var profile in template.Stages)
     {var stage=new ReleasePipelineRunStage{RunId=run.Id,ProjectId=run.ProjectId,StageOrder=profile.StageOrder,EnvironmentId=profile.EnvironmentId,SourceStageId=previous,StageArtifactId=previous is null?formal.ArtifactId:null,ProfileJson=JsonSerializer.Serialize(profile.Profile,CanonicalJson.Options),ProfileHash=profile.ProfileHash,Status="Cancelled"};db.Add(stage);previous=stage.Id;if(stage.EnvironmentId==formal.TargetEnvironmentId)stageId=stage.Id;}
     var attempt=new ReleasePipelineStageAttempt{RunId=run.Id,RunStageId=stageId!.Value,ProjectId=run.ProjectId,EnvironmentId=formal.TargetEnvironmentId,AttemptNo=1,Status="Cancelled",DeadlineAt=DateTimeOffset.UtcNow.AddHours(1)};db.Add(attempt);attemptId=attempt.Id;
    }
    var p=new ReleasePromotion{OrganizationId=formal.OrganizationId,ProjectId=formal.ProjectId,ArtifactId=formal.ArtifactId,SourceEnvironmentId=formal.SourceEnvironmentId,SourceReleaseId=formal.SourceReleaseId,TargetEnvironmentId=formal.TargetEnvironmentId,RequestedBy=api.User.Id,Status="Completed",CompletedAt=DateTimeOffset.UtcNow,GateOrigin=pipeline?"PipelineRunStage":"ProjectConnection",PipelineRunStageId=stageId,StageAttemptId=attemptId};promotions.Add(p);db.Add(p);
    var release=new ReleaseRecord{EnvironmentId=formal.TargetEnvironmentId,ReleaseNo="BATCH-"+i,ReleaseType="publish",Status="Succeeded",RequestedBy=api.User.Id,PromotionId=p.Id,ArtifactId=p.ArtifactId,SourceReleaseId=p.SourceReleaseId,CreatedAt=DateTimeOffset.UtcNow.AddSeconds(i)};releases.Add(release);releaseIds.Add(release.Id);
   }
   await db.SaveChangesAsync();db.AddRange(releases);await db.SaveChangesAsync();for(var i=0;i<100;i++)promotions[i].TargetReleaseId=releases[i].Id;await db.SaveChangesAsync();
   if(restricted)await db.Set<UserProjectScope>().Where(g=>g.UserId==api.User.Id).ExecuteUpdateAsync(x=>x.SetProperty(g=>g.ProjectId,formal.ProjectId).SetProperty(g=>g.EnvironmentId,formal.TargetEnvironmentId));
  }
  var capture=new Capture();var actor=new ActorContext(api.User.Id,"trace-batch");
  await using var app=ControlPlaneApp.Build(["--environment","Development"],b=>{b.Configuration["ConnectionStrings:WebApi"]=api.Database.ConnectionString;b.Logging.ClearProviders();LoginProtectionTestConfiguration.Apply(b,api.LoginProtectionDirectory,api.LoginProtectionDeploymentId);b.Services.AddDbContext<WebApiDbContext>(o=>o.AddInterceptors(capture));});
  foreach(var size in new[]{1,50,100})
  {
   capture.Commands=0;using var scope=app.Services.CreateScope();var service=scope.ServiceProvider.GetRequiredService<ReleaseService>();var page=await service.ListAsync(formal.TargetEnvironmentId,actor,1,size);
   Assert.Equal(size,page.Items.Count);Assert.True(capture.Commands<=80,$"{size} independent promotions executed {capture.Commands} SQL commands; budget is 80");
   foreach(var row in page.Items){Assert.NotNull(row.Delivery);Assert.Equal(restricted?"Restricted":"Visible",row.Delivery.Visibility);if(restricted){Assert.Null(row.Delivery.PromotionId);Assert.Null(row.Delivery.ArtifactHash);Assert.Null(row.Delivery.Pipeline);}else{Assert.NotNull(row.Delivery.PromotionId);Assert.Equal(formal.ArtifactHash,row.Delivery.ArtifactHash);if(pipeline)Assert.NotNull(row.Delivery.Pipeline);}}
   var detail=await service.GetAsync(page.Items[0].Id,actor);Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(detail,CanonicalJson.Options),JsonSerializer.SerializeToElement(page.Items[0],CanonicalJson.Options)),"batch trace differs from detail");
  }
 }
 [Theory][InlineData("api.schema.read")][InlineData("policy.read")][InlineData("missing-version")][InlineData("corrupt-hash")]
 public async Task BatchArtifactVisibilityMatchesDetailForContractAndPolicyFailures(string fault)
 {
  await using var scenario=new PromotionScenario();await scenario.InitializeAsync(timeout:true);var promotion=await PromotionSubmissionTests.Submitted(scenario);
  await using(var db=scenario.Api.Context())
  {
   if(fault.EndsWith(".read")){var permission=await db.Set<Permission>().SingleAsync(p=>p.Code==fault);await db.Set<RolePermission>().Where(r=>r.PermissionId==permission.Id).ExecuteDeleteAsync();}
   else{var artifact=await db.Set<ReleaseArtifact>().SingleAsync(a=>a.Id==promotion.ArtifactId);if(fault=="corrupt-hash")artifact.ArtifactHash=new string('f',64);else{var content=JsonSerializer.Deserialize<ArtifactContent>(artifact.CanonicalContent,CanonicalJson.Options)!;var missingVersions=content.Apis.ToDictionary(a=>a.VersionId,_=>Guid.NewGuid());
    content=content with
    {
     Apis=content.Apis.Select(a=>a with{VersionId=missingVersions[a.VersionId],Parameters=a.Parameters.Select(p=>p with{ApiVersionId=missingVersions[a.VersionId]}).ToArray(),Schemas=a.Schemas.Select(s=>s with{ApiVersionId=missingVersions[a.VersionId]}).ToArray()}).ToArray(),
     Routes=content.Routes.Select(r=>r with{VersionId=missingVersions[r.VersionId],Key=""}).ToArray()
    };artifact.CanonicalContent=System.Text.Encoding.UTF8.GetString(WebApi.Domain.Delivery.ReleaseArtifactCanonicalizer.Serialize(content));artifact.ArtifactHash=WebApi.Domain.Delivery.ReleaseArtifactCanonicalizer.Hash(content);}await db.SaveChangesAsync();}
  }
  using var list=await scenario.Api.Client.GetAsync($"/api/v1/environments/{scenario.TargetEnvironmentId}/releases?page=1&pageSize=100");using var detail=await scenario.Api.Client.GetAsync($"/api/v1/releases/{promotion.TargetReleaseId}");
  if(fault=="corrupt-hash"){Assert.Equal(HttpStatusCode.UnprocessableEntity,list.StatusCode);Assert.Equal(detail.StatusCode,list.StatusCode);Assert.Contains("corrupt_release_artifact_source",await list.Content.ReadAsStringAsync());}
  else{list.EnsureSuccessStatusCode();detail.EnsureSuccessStatusCode();var page=await list.Content.ReadFromJsonAsync<JsonObject>();var row=page!["items"]!.AsArray().Single(r=>r!["id"]!.GetValue<string>()==promotion.TargetReleaseId.ToString());Assert.Equal("Restricted",row!["delivery"]!["visibility"]!.GetValue<string>());Assert.Null(row["delivery"]!["artifactHash"]);Assert.True(JsonNode.DeepEquals(await detail.Content.ReadFromJsonAsync<JsonObject>(),row));}
 }

}
