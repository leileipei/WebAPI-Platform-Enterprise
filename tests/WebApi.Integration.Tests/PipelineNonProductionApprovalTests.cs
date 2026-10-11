using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Releases;
using WebApi.Contracts.Common;
using WebApi.Infrastructure.Persistence.Entities;
using Xunit;
namespace WebApi.Integration.Tests;
public sealed class PipelineNonProductionApprovalTests
{
 [Fact] public async Task FormalReleaseIsBoundToTheOriginalStageAttempt()
 {await using var f=new PipelinePromotionFixture();await f.InitializeAsync();var p=await f.PreparedAsync();var result=await f.PrecheckAsync(p);Assert.True(result.CanSubmit,JsonSerializer.Serialize(result));using var response=await f.SubmitAsync(p.Id,result.Revision);Assert.True(response.IsSuccessStatusCode,await response.Content.ReadAsStringAsync());var submitted=(await response.Content.ReadFromJsonAsync<PromotionDto>())!;await using var db=f.Scenario.Api.Context();var promotion=await db.Set<ReleasePromotion>().SingleAsync(p=>p.Id==submitted.Id);var attempt=await db.Set<ReleasePipelineStageAttempt>().SingleAsync(a=>a.Id==promotion.StageAttemptId);Assert.Equal(submitted.TargetReleaseId,attempt.ActualReleaseId);Assert.Equal(submitted.Id,attempt.PromotionId);}
 [Theory][InlineData(false)][InlineData(true)]
 public async Task OptionalTwoLevelTemplateIsFrozenAtSubmission(bool approval)
 {await using var f=new PipelinePromotionFixture();await f.InitializeAsync(approval:approval);var p=await f.PreparedAsync();var result=await f.PrecheckAsync(p);Assert.True(result.CanSubmit);using var response=await f.SubmitAsync(p.Id,result.Revision);Assert.True(response.StatusCode==HttpStatusCode.OK,await response.Content.ReadAsStringAsync());var submitted=(await response.Content.ReadFromJsonAsync<PromotionDto>())!;Assert.Equal(approval?"WaitingApproval":"Ready",submitted.Status);await using var db=f.Scenario.Api.Context();var release=await db.Set<ReleaseRecord>().SingleAsync(r=>r.Id==submitted.TargetReleaseId);Assert.Equal(approval?2:0,await db.Set<ApprovalTask>().CountAsync(t=>t.ReleaseId==release.Id));var rules=JsonSerializer.Deserialize<ApprovalRule[]>(release.ApprovalPolicy!,CanonicalJson.Options)!;Assert.Equal(approval?2:0,rules.Length);Assert.Null((await db.Set<EnvironmentRecord>().SingleAsync(e=>e.Id==f.TargetEnvironmentId)).DesiredConfigVersion);Assert.False(await db.Set<OutboxMessage>().AnyAsync(m=>m.ReleaseId==release.Id));}
 [Theory][InlineData("revision")][InlineData("disabled")][InlineData("rules")]
 public async Task ChangedFrozenNonProductionTemplateCannotSubmit(string fault)
 {await using var f=new PipelinePromotionFixture();await f.InitializeAsync(approval:true);var p=await f.PreparedAsync();var precheck=await f.PrecheckAsync(p);Assert.True(precheck.CanSubmit);await using(var db=f.Scenario.Api.Context()){var flow=await db.Set<ApprovalFlow>().SingleAsync();if(fault=="revision")flow.Revision++;if(fault=="disabled")flow.Enabled=false;if(fault=="rules")(await db.Set<ApprovalStep>().OrderBy(s=>s.StepOrder).FirstAsync()).RequiredCount=2;await db.SaveChangesAsync();}using var response=await f.SubmitAsync(p.Id,precheck.Revision);Assert.Equal(HttpStatusCode.Conflict,response.StatusCode);await using var verify=f.Scenario.Api.Context();Assert.False(await verify.Set<ReleaseRecord>().AnyAsync(r=>r.PromotionId==p.Id));}
 [Fact] public async Task SubmittedCandidateCannotBeEditedUsingOldApproval()
 {await using var f=new PipelinePromotionFixture();await f.InitializeAsync(approval:true);var p=await f.PreparedAsync();var result=await f.PrecheckAsync(p);using var submit=await f.SubmitAsync(p.Id,result.Revision);Assert.Equal(HttpStatusCode.OK,submit.StatusCode);var submitted=(await submit.Content.ReadFromJsonAsync<PromotionDto>())!;using var edit=await f.Scenario.Api.WriteAsync(HttpMethod.Put,$"/api/v1/release-promotions/{p.Id}/mapping",f.Mapping(),RevisionTag.Format(submitted.Revision));Assert.Equal(HttpStatusCode.Conflict,edit.StatusCode);}
}
