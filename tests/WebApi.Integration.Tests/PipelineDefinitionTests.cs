using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Releases;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;
namespace WebApi.Integration.Tests;
public sealed class PipelineDefinitionTests
{
    internal static async Task<(Guid Id,long Revision)> DraftAsync(PipelineScenario s)
    {
        using var created=await ApiFixture.CommandAsync(s.Creator,$"/api/v1/projects/{s.Api.Project.Id}/release-pipelines",s.Definition);
        Assert.Equal(HttpStatusCode.OK,created.StatusCode);var value=await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("no-store",created.Headers.CacheControl?.ToString());return(value.GetProperty("id").GetGuid(),value.GetProperty("revision").GetInt64());
    }
    internal static async Task<JsonElement> PublishAsync(PipelineScenario s,Guid id,long revision=1)
    {
        using var request=new HttpRequestMessage(HttpMethod.Post,$"/api/v1/release-pipelines/{id}/versions"){Content=JsonContent.Create(new{})};
        request.Headers.Add("X-CSRF-Token",await s.Api.CsrfAsync());request.Headers.Add("Idempotency-Key",Guid.NewGuid().ToString("N"));request.Headers.Add("If-Match",$"\"{revision}\"");
        using var response=await s.Creator.SendAsync(request);Assert.Equal(HttpStatusCode.OK,response.StatusCode);return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
    [Fact] public async Task PublishDoesNotActivateAndVersionNeverMutates()
    {
        await using var s=new PipelineScenario();await s.InitializeEnvironmentsAsync();var draft=await DraftAsync(s);var version=await PublishAsync(s,draft.Id);
        using var policy=await s.Creator.GetAsync($"/api/v1/projects/{s.Api.Project.Id}/delivery-policy");Assert.Equal("Legacy",(await policy.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("mode").GetString());
        await using var db=s.Api.Context();var before=await db.Set<ReleasePipelineVersion>().AsNoTracking().SingleAsync();var count=await db.Set<ReleaseRecord>().CountAsync();
        using var edit=await s.Api.WriteAsync(HttpMethod.Put,$"/api/v1/release-pipelines/{draft.Id}",s.Definition with{Name="新版草稿"},"\"2\"");Assert.Equal(HttpStatusCode.OK,edit.StatusCode);
        var after=await db.Set<ReleasePipelineVersion>().AsNoTracking().SingleAsync();Assert.Equal(before.ContentJson,after.ContentJson);Assert.Equal(before.DefinitionHash,after.DefinitionHash);Assert.Equal(count,await db.Set<ReleaseRecord>().CountAsync());Assert.Equal(1,version.GetProperty("versionNo").GetInt32());
    }
    [Fact] public async Task DraftEtagsAndPublishedHistoryAreIndependent()
    {
        await using var s=new PipelineScenario();await s.InitializeEnvironmentsAsync(4);var draft=await DraftAsync(s);await PublishAsync(s,draft.Id);
        using var stale=await s.Api.WriteAsync(HttpMethod.Put,$"/api/v1/release-pipelines/{draft.Id}",s.Definition with{Name="冲突"},"\"1\"");Assert.Equal(HttpStatusCode.PreconditionFailed,stale.StatusCode);
        using var edit=await s.Api.WriteAsync(HttpMethod.Put,$"/api/v1/release-pipelines/{draft.Id}",s.Definition with{Name="版本2"},"\"2\"");Assert.Equal(HttpStatusCode.OK,edit.StatusCode);var second=await PublishAsync(s,draft.Id,3);
        Assert.Equal(2,second.GetProperty("versionNo").GetInt32());using var versions=await s.Creator.GetAsync($"/api/v1/release-pipelines/{draft.Id}/versions");Assert.Equal(HttpStatusCode.OK,versions.StatusCode);Assert.Equal(2,(await versions.Content.ReadFromJsonAsync<JsonElement>()).GetArrayLength());
    }
    [Fact] public async Task CreationReplaysSameKeyAndRejectsDifferentBody()
    {
        await using var s=new PipelineScenario();await s.InitializeEnvironmentsAsync();var key=Guid.NewGuid().ToString("N");var url=$"/api/v1/projects/{s.Api.Project.Id}/release-pipelines";
        using var first=await ApiFixture.CommandAsync(s.Creator,url,s.Definition,key);Assert.Equal(HttpStatusCode.OK,first.StatusCode);var text=await first.Content.ReadAsStringAsync();
        using var replay=await ApiFixture.CommandAsync(s.Creator,url,s.Definition,key);Assert.Equal(HttpStatusCode.OK,replay.StatusCode);Assert.Equal(text,await replay.Content.ReadAsStringAsync());
        using var different=await ApiFixture.CommandAsync(s.Creator,url,s.Definition with{Name="不同"},key);Assert.Equal(HttpStatusCode.Conflict,different.StatusCode);
        await using var db=s.Api.Context();Assert.Equal(1,await db.Set<ReleasePipeline>().CountAsync());
    }
    [Theory][InlineData("foreign")][InlineData("disabled")][InlineData("production_flow")]
    public async Task DraftRejectsInvalidActualEnvironmentFacts(string reason)
    {
        await using var s=new PipelineScenario();await s.InitializeEnvironmentsAsync();var input=s.Definition;await using(var db=s.Api.Context()){
            if(reason=="foreign"){var other=new Project{OrganizationId=s.Api.Organization.Id,Code="OTHER",Name="其他"};db.Add(other);var foreign=new EnvironmentRecord{ProjectId=other.Id,Code="FOREIGN",Name="其他项目",IsProduction=true,ReleasePolicyId=s.Api.ApprovalFlow.Id};db.Add(foreign);var stages=input.Stages.ToArray();stages[^1]=stages[^1] with{EnvironmentId=foreign.Id};input=input with{Stages=stages};}
            if(reason=="disabled")(await db.Set<EnvironmentRecord>().SingleAsync(e=>e.Id==s.Environments[s.Environments.Length-1])).Status="Disabled";
            if(reason=="production_flow")(await db.Set<EnvironmentRecord>().SingleAsync(e=>e.Id==s.Environments[s.Environments.Length-1])).ReleasePolicyId=null;
            await db.SaveChangesAsync();}
        using var response=await ApiFixture.CommandAsync(s.Creator,$"/api/v1/projects/{s.Api.Project.Id}/release-pipelines",input);Assert.Equal(HttpStatusCode.UnprocessableEntity,response.StatusCode);
    }
    [Fact] public async Task ManagingRequiresPipelinePermission()
    {
        await using var s=new PipelineScenario();await s.InitializeEnvironmentsAsync();await using(var db=s.Api.Context()){var permission=await db.Set<Permission>().SingleAsync(p=>p.Code=="pipeline.manage");await db.Set<RolePermission>().Where(r=>r.PermissionId==permission.Id).ExecuteDeleteAsync();}
        using var denied=await ApiFixture.CommandAsync(s.Creator,$"/api/v1/projects/{s.Api.Project.Id}/release-pipelines",s.Definition);Assert.Equal(HttpStatusCode.Forbidden,denied.StatusCode);
    }
    [Theory][InlineData("project.write")][InlineData("environment.read")]
    public async Task ManagingRequiresProjectWriteAndChainVisibility(string missing)
    {
        await using var s=new PipelineScenario();await s.InitializeEnvironmentsAsync();await using(var db=s.Api.Context()){var permission=await db.Set<Permission>().SingleAsync(p=>p.Code==missing);await db.Set<RolePermission>().Where(r=>r.PermissionId==permission.Id).ExecuteDeleteAsync();}
        using var denied=await ApiFixture.CommandAsync(s.Creator,$"/api/v1/projects/{s.Api.Project.Id}/release-pipelines",s.Definition);Assert.Equal(HttpStatusCode.Forbidden,denied.StatusCode);
    }
    [Fact] public async Task AuditCapturesDefinitionHashWithoutFrozenJsonOrAddresses()
    {
        await using var s=new PipelineScenario();await s.InitializeEnvironmentsAsync();var draft=await DraftAsync(s);var version=await PublishAsync(s,draft.Id);
        await using var db=s.Api.Context();var audits=await db.Set<AuditLog>().Where(a=>a.Action=="pipeline.version.publish").ToArrayAsync();Assert.Single(audits);
        Assert.Contains(version.GetProperty("definitionHash").GetString()!,audits[0].AfterJson);
        Assert.DoesNotContain("gateway-2.example",audits[0].AfterJson);Assert.DoesNotContain("ContentJson",audits[0].AfterJson);
    }
    [Fact] public async Task ArchiveRetainsVersionsAndPreventsFurtherEditing()
    {
        await using var s=new PipelineScenario();await s.InitializeEnvironmentsAsync();var draft=await DraftAsync(s);await PublishAsync(s,draft.Id);
        using var request=new HttpRequestMessage(HttpMethod.Post,$"/api/v1/release-pipelines/{draft.Id}/archive"){Content=JsonContent.Create(new{})};request.Headers.Add("X-CSRF-Token",await s.Api.CsrfAsync());request.Headers.Add("Idempotency-Key",Guid.NewGuid().ToString("N"));request.Headers.Add("If-Match","\"2\"");
        using var archived=await s.Creator.SendAsync(request);Assert.Equal(HttpStatusCode.OK,archived.StatusCode);
        using var edit=await s.Api.WriteAsync(HttpMethod.Put,$"/api/v1/release-pipelines/{draft.Id}",s.Definition,"\"3\"");Assert.Equal(HttpStatusCode.Conflict,edit.StatusCode);await using var db=s.Api.Context();Assert.Equal(1,await db.Set<ReleasePipelineVersion>().CountAsync());
    }
}
