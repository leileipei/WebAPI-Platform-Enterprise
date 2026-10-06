using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Contracts.Common;
using WebApi.Contracts.Comparisons;
using WebApi.Infrastructure.Comparisons;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Releases;
using WebApi.Integration.Tests.Support;
using Xunit;
namespace WebApi.Integration.Tests;
public sealed class ComparisonEngineUpgradeTests
{
    private sealed record FrozenV1Fixture(ComparisonFixture F,Guid ReleaseId,Guid ComparisonId,Guid ReviewId,byte[] InputBytes,byte[] ReportBytes,byte[] CandidateBytes);
    private static async Task<FrozenV1Fixture> PrepareAsync(bool production=false)
    {
        var f=new ComparisonFixture();await f.InitializeAsync(true);var(comp,hash)=await f.ComparisonAsync();using var reviewResponse=await f.ReviewAsync(comp,hash);reviewResponse.EnsureSuccessStatusCode();var reviewId=(await reviewResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await using(var db=f.Api.Context()){(await db.Set<EnvironmentRecord>().SingleAsync()).IsProduction=production;for(var i=0;i<2;i++)db.Add(new GatewayNode{EnvironmentId=f.Api.Environment.Id,NodeName="upgrade-"+i,InstanceId=Guid.NewGuid().ToString(),AppVersion="test",IdentityHash=new string('a',64),LastHeartbeatAt=DateTimeOffset.UtcNow});await db.SaveChangesAsync();}
        var preview=await f.Api.PreviewReleaseRequestAsync(versionId:f.Target.Id);using var created=await ApiFixture.CommandAsync(f.Api.Client,$"/api/v1/environments/{f.Api.Environment.Id}/releases",new{preview.BaseConfigVersion,preview.VersionIds,preview.ResourceRevisions,riskReviewIds=new[]{reviewId}});created.EnsureSuccessStatusCode();var releaseId=(await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();using var submitted=await ApiFixture.CommandAsync(f.Api.Client,$"/api/v1/releases/{releaseId}/submit");submitted.EnsureSuccessStatusCode();Assert.Equal(production?"WaitingApproval":"Ready",(await submitted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("state").GetString());
        await using var context=f.Api.Context();var comparison=await context.Set<ApiVersionComparison>().SingleAsync(x=>x.Id==comp);var review=await context.Set<ApiVersionRiskReview>().SingleAsync(x=>x.Id==reviewId);var release=await context.Set<ReleaseRecord>().SingleAsync(x=>x.Id==releaseId);
        // Build an actual pre-upgrade v1 input shape and execute the unchanged registered v1 algorithm.
        var inputNode=JsonNode.Parse(comparison.InputBytes)!.AsObject();inputNode.Remove("formatMode");inputNode.Remove("adapterVersion");foreach(var side in new[]{"from","to"}){inputNode[side]!.AsObject().Remove("sources");inputNode[side]!.AsObject().Remove("source");inputNode[side]!.AsObject().Remove("sourceIssue");inputNode[side]!["version"]!.AsObject().Remove("dialect");}
        comparison.InputBytes=ContractNormalizer.CanonicalBytes(inputNode);var input=JsonSerializer.Deserialize<ComparisonInput>(comparison.InputBytes,CanonicalJson.Options)!;var report=new LegacyV1ComparisonEngine().Compare(input,new(MaxSideBytes:2*1024*1024,MaxPairBytes:4*1024*1024),default);
        comparison.EngineVersion=report.EngineVersion;comparison.InputFingerprint=report.InputFingerprint;comparison.ReportBytes=ContractNormalizer.CanonicalBytes(report);comparison.ReportHash=ContractNormalizer.Hash(comparison.ReportBytes);comparison.Coverage=report.Coverage;comparison.CountsJson=JsonSerializer.Serialize(report.Counts,CanonicalJson.Options);review.InputFingerprint=report.InputFingerprint;review.ReportHash=comparison.ReportHash;
        var summary=new RiskReviewSummaryDto(review.Id,comparison.Id,comparison.ApiId,comparison.FromVersionId,comparison.ToVersionId,comparison.FromVersion,comparison.ToVersion,review.Decision,review.ActorId,review.CreatedAt,comparison.InputFingerprint,comparison.ReportHash,comparison.EngineVersion,report.Coverage,report.Counts,review.Comment);
        var reference=new FrozenRiskReviewReference(comparison.ApiId,comparison.ToVersionId,comparison.Id,review.Id,comparison.InputFingerprint,comparison.ReportHash,comparison.EngineVersion,summary);var candidate=JsonNode.Parse(release.CandidateBytes!)!;candidate["riskReviewReferences"]=JsonSerializer.SerializeToNode(new[]{reference},CanonicalJson.Options);release.CandidateBytes=ContractNormalizer.CanonicalBytes(candidate);await context.SaveChangesAsync();
        var fixture=new FrozenV1Fixture(f,releaseId,comp,reviewId,comparison.InputBytes.ToArray(),comparison.ReportBytes.ToArray(),release.CandidateBytes.ToArray());
        if(production){foreach(var role in new[]{"ApiApprover","SecurityReviewer"}){var approver=await f.Api.NewReviewerAsync(role);using var approval=await ApiFixture.CommandAsync(approver.Client,$"/api/v1/releases/{releaseId}/approve",new{comment="Approve existing frozen v1 evidence"});approval.EnsureSuccessStatusCode();}await using var verify=f.Api.Context();Assert.Equal("Ready",(await verify.Set<ReleaseRecord>().SingleAsync(x=>x.Id==releaseId)).Status);}
        return fixture;
    }
    [Theory][InlineData(false)][InlineData(true)] public async Task FrozenV1ReleaseSurvivesEngineUpgrade(bool production)
    {
        var frozen=await PrepareAsync(production);await using var f=frozen.F;Assert.Equal("compatibility-v2",ContractComparisonEngine.EngineVersion);
        using var publish=await ApiFixture.CommandAsync(f.Api.Client,$"/api/v1/releases/{frozen.ReleaseId}/publish");Assert.True(publish.IsSuccessStatusCode,await publish.Content.ReadAsStringAsync());using(var scope=f.Api.Services())Assert.True(await scope.ServiceProvider.GetRequiredService<PublishCoordinator>().BuildNextAsync());
        await using var db=f.Api.Context();var release=await db.Set<ReleaseRecord>().SingleAsync(x=>x.Id==frozen.ReleaseId);Assert.Equal("Publishing",release.Status);var comp=await db.Set<ApiVersionComparison>().SingleAsync(x=>x.Id==frozen.ComparisonId);Assert.Equal(frozen.InputBytes,comp.InputBytes);Assert.Equal(frozen.ReportBytes,comp.ReportBytes);Assert.Equal(frozen.CandidateBytes,release.CandidateBytes);Assert.Equal(ContractNormalizer.Hash(frozen.ReportBytes),comp.ReportHash);Assert.Equal(2,await db.Set<ReleaseTarget>().CountAsync());
    }
    [Fact] public async Task FrozenV1WorkerBuildSurvivesEngineUpgrade()
    {
        var frozen=await PrepareAsync();await using var f=frozen.F;
        await using(var db=f.Api.Context()){var release=await db.Set<ReleaseRecord>().SingleAsync(x=>x.Id==frozen.ReleaseId);release.Status="Building";release.PublishRequestedBy=f.Api.User.Id;release.PublishTraceId="pre-upgrade-publish";await db.SaveChangesAsync();}
        using(var scope=f.Api.Services())Assert.True(await scope.ServiceProvider.GetRequiredService<PublishCoordinator>().BuildNextAsync());
        await using var check=f.Api.Context();var built=await check.Set<ReleaseRecord>().SingleAsync(x=>x.Id==frozen.ReleaseId);Assert.Equal("Publishing",built.Status);Assert.Equal(frozen.CandidateBytes,built.CandidateBytes);Assert.Equal(frozen.ReportBytes,(await check.Set<ApiVersionComparison>().SingleAsync()).ReportBytes);
    }
    [Fact] public async Task LegacyNewReviewAndUnsubmittedEvidenceAreStale()
    {
        var frozen=await PrepareAsync();await using var f=frozen.F;using var review=await f.ReviewAsync(frozen.ComparisonId,ContractNormalizer.Hash(frozen.ReportBytes));Assert.Equal(HttpStatusCode.Conflict,review.StatusCode);
        var preview=await f.Api.PreviewReleaseRequestAsync(versionId:f.Target.Id);using var create=await ApiFixture.CommandAsync(f.Api.Client,$"/api/v1/environments/{f.Api.Environment.Id}/releases",new{preview.BaseConfigVersion,preview.VersionIds,preview.ResourceRevisions,riskReviewIds=new[]{frozen.ReviewId}});Assert.Equal(HttpStatusCode.Conflict,create.StatusCode);
    }
    [Theory][InlineData("revision")][InlineData("permission")][InlineData("unknown_engine")][InlineData("report_bytes")][InlineData("input_bytes")][InlineData("candidate_reference")]
    public async Task FrozenCompatibilityNeverBypassesActualProtection(string mutation)
    {
        var frozen=await PrepareAsync();await using var f=frozen.F;
        if(mutation=="permission")await f.RevokeAsync("api.schema.read");
        else{await using var db=f.Api.Context();var comparison=await db.Set<ApiVersionComparison>().SingleAsync(x=>x.Id==frozen.ComparisonId);
            if(mutation=="revision")(await db.Set<ApiVersion>().SingleAsync(x=>x.Id==f.Api.Version.Id)).Revision++;
            if(mutation=="unknown_engine")comparison.EngineVersion="compatibility-unregistered";
            if(mutation=="report_bytes")comparison.ReportBytes=Encoding.UTF8.GetBytes("{}");
            if(mutation=="input_bytes"){var input=JsonNode.Parse(comparison.InputBytes)!;input["from"]!["version"]!["revision"]=900;comparison.InputBytes=ContractNormalizer.CanonicalBytes(input);}
            if(mutation=="candidate_reference"){var release=await db.Set<ReleaseRecord>().SingleAsync(x=>x.Id==frozen.ReleaseId);var candidate=JsonNode.Parse(release.CandidateBytes!)!;candidate["riskReviewReferences"]![0]!["reportHash"]=new string('b',64);release.CandidateBytes=ContractNormalizer.CanonicalBytes(candidate);}
            await db.SaveChangesAsync();}
        using var publish=await ApiFixture.CommandAsync(f.Api.Client,$"/api/v1/releases/{frozen.ReleaseId}/publish");Assert.False(publish.IsSuccessStatusCode);var error=await publish.Content.ReadFromJsonAsync<JsonElement>();Assert.NotEqual("invalid_release_state",error.GetProperty("code").GetString());await using var check=f.Api.Context();Assert.Empty(await check.Set<GatewayConfigSnapshot>().ToArrayAsync());
    }
    [Fact] public async Task RulesCatalogRequiresLoginAndContainsNoBusinessData()
    {
        await using var f=new ComparisonFixture();await f.InitializeAsync();using var response=await f.Api.Client.GetAsync("/api/v1/comparisons/rules");response.EnsureSuccessStatusCode();var json=await response.Content.ReadFromJsonAsync<JsonElement>();Assert.Equal("compatibility-v2",json.GetProperty("engineVersion").GetString());Assert.Contains(json.GetProperty("rules").EnumerateArray(),x=>x.GetProperty("id").GetString()=="schema.types");Assert.Contains(json.GetProperty("rules").EnumerateArray(),x=>x.GetProperty("id").GetString()=="openapi.security");
        using var anonymous=new HttpClient{BaseAddress=f.Api.Client.BaseAddress};using var denied=await anonymous.GetAsync("/api/v1/comparisons/rules");Assert.Equal(HttpStatusCode.Unauthorized,denied.StatusCode);
    }
}
