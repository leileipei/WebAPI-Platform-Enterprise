using System.Data.Common;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Contracts.Governance;
using WebApi.Contracts.Common;
using WebApi.Contracts.Releases;
using WebApi.Contracts.Security;
using WebApi.Domain.Runtime;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Catalog;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Releases;
using System.Net.Http.Json;
using WebApi.Integration.Tests.Support;
using Xunit;
using WebApi.PerformanceHost;
using System.Text.Json.Nodes;
using Npgsql;
namespace WebApi.Integration.Tests;

public sealed class RunningContractQueryTests
{
    private const string Document="""{"openapi":"3.0.3","info":{"title":"Frozen orders","version":"1"},"paths":{"/orders":{"get":{"responses":{"200":{"description":"ok"}}}}}}""";
    private sealed class Capture : DbCommandInterceptor
    {
        public readonly List<string[]> ReleaseColumns=[];
        public void Reset()=>ReleaseColumns.Clear();
        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command,CommandExecutedEventData data,DbDataReader result,CancellationToken ct=default)
        {
            if(command.CommandText.Contains("release_records"))ReleaseColumns.Add(Enumerable.Range(0,result.FieldCount).Select(result.GetName).ToArray());
            return ValueTask.FromResult(result);
        }
    }
    private static async Task Configure(DeploymentScenario s,Capture capture)
    {
        await s.InitializeAsync(b=>b.Services.AddDbContext<WebApiDbContext>(o=>o.AddInterceptors(capture)));
        await using var db=s.Api.Context();var v=await db.Set<ApiVersion>().SingleAsync();v.OpenapiDocument=Document;v.OpenapiSource=Document;
        var e=await db.Set<EnvironmentRecord>().SingleAsync();e.GatewayPublicUrl="https://api.test";await db.SaveChangesAsync();
    }
    [Fact] public async Task RunningAddressDoesNotLoadCandidateBodies()
    {
        await using var s=new DeploymentScenario();var capture=new Capture();await Configure(s,capture);await s.PublishAsync();capture.Reset();
        using var services=s.Api.Services();var result=await services.ServiceProvider.GetRequiredService<EnvironmentApiAddressService>().GetAsync(s.Api.Environment.Id,s.Api.Api.Id,null,AccessView.Running,new(s.Api.User.Id,"address-query"));
        Assert.Single(result.Routes);Assert.Equal("https://api.test/orders",result.Routes[0].PublicTemplate);
        Assert.DoesNotContain(capture.ReleaseColumns,columns=>columns.Contains("candidate_bytes"));
    }
    [Fact] public async Task PublishedFrozenContractHasIndexedSource()
    {
        await using var s=new DeploymentScenario();await Configure(s,new Capture());await s.PublishAsync();await using var db=s.Api.Context();
        Assert.True(await db.Database.SqlQueryRaw<bool>("SELECT to_regclass('public.release_frozen_contracts') IS NOT NULL AS \"Value\"").SingleAsync(),"Published contracts need indexed provenance.");
        Assert.Equal(1,await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM release_frozen_contracts").SingleAsync());
    }
    [Fact] public async Task LegacyMigrationBackfillsWithoutRewritingCandidates()
    {
        await using var s=new DeploymentScenario();await Configure(s,new Capture());var releaseId=await s.PublishAsync();await using var db=s.Api.Context();
        var bytes=(await db.Set<ReleaseRecord>().SingleAsync(r=>r.Id==releaseId)).CandidateBytes!.ToArray();
        var migrator=db.GetService<IMigrator>();await migrator.MigrateAsync("20261010091247_GatewayQueryIndexes");var before=await MigrationProbe.InspectAsync(db);await migrator.MigrateAsync("20261012004548_ReleaseFrozenContracts");
        Assert.True(await db.Database.SqlQueryRaw<bool>("SELECT to_regclass('public.release_frozen_contracts') IS NOT NULL AS \"Value\"").SingleAsync());
        Assert.Equal(1,await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM release_frozen_contracts").SingleAsync());
        Assert.Equal(bytes,(await db.Set<ReleaseRecord>().AsNoTracking().SingleAsync(r=>r.Id==releaseId)).CandidateBytes);
        var after=await MigrationProbe.InspectAsync(db);
        Assert.Equal(before.Tables.Select(t=>(t.Name,t.Columns.Count,t.Rows,t.DataHash)),after.Tables.Where(t=>before.Tables.Any(b=>b.Name==t.Name)).Select(t=>(t.Name,t.Columns.Count,t.Rows,t.DataHash)));
        Assert.All(before.Indexes,i=>Assert.Contains(after.Indexes,a=>a.Name==i.Name&&a.Table==i.Table&&a.DefinitionHash==i.DefinitionHash&&a.Valid&&a.Ready));
        await migrator.MigrateAsync();Assert.Equal(1,await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM release_frozen_contracts").SingleAsync());
        Assert.True(await db.Database.SqlQueryRaw<bool>("SELECT to_regprocedure('public.webapi_frozen_shape_20261012004548(jsonb,text,jsonb)') IS NULL AS \"Value\"").SingleAsync());
        Assert.False(db.Database.HasPendingModelChanges());
        using var document=await s.Api.Client.GetAsync($"/api/v1/environments/{s.Api.Environment.Id}/apis/{s.Api.Api.Id}/openapi");document.EnsureSuccessStatusCode();
    }
    [Fact] public async Task InvalidLegacyCandidateBlocksMigrationAtomically()
    {
        await using var s=new DeploymentScenario();await Configure(s,new Capture());var releaseId=await s.PublishAsync();await using var db=s.Api.Context();
        var migrator=db.GetService<IMigrator>();await migrator.MigrateAsync("20261010091247_GatewayQueryIndexes");
        var bad=System.Text.Encoding.UTF8.GetBytes("{\"versions\":null}");await db.Set<ReleaseRecord>().Where(r=>r.Id==releaseId).ExecuteUpdateAsync(p=>p.SetProperty(r=>r.CandidateBytes,bad));
        var applied=(await db.Database.GetAppliedMigrationsAsync()).ToArray();
        await Assert.ThrowsAnyAsync<Exception>(()=>migrator.MigrateAsync());
        Assert.Equal(applied,(await db.Database.GetAppliedMigrationsAsync()).ToArray());
        Assert.False(await db.Database.SqlQueryRaw<bool>("SELECT to_regclass('public.release_frozen_contracts') IS NOT NULL AS \"Value\"").SingleAsync());
        Assert.Equal(bad,(await db.Set<ReleaseRecord>().AsNoTracking().SingleAsync(r=>r.Id==releaseId)).CandidateBytes);
    }
    [Theory]
    [InlineData("nullParameter")]
    [InlineData("nullSchema")]
    [InlineData("badRevision")]
    [InlineData("overflowRevision")]
    [InlineData("badCreatedAt")]
    [InlineData("stringRequired")]
    [InlineData("fractionalStatus")]
    [InlineData("nullExample")]
    public async Task StrongTypeCorruptionBlocksBeforeLegacyMigrationCommit(string fault)
    {
        await using var s=new DeploymentScenario();await Configure(s,new Capture());var id=await s.PublishAsync();await using var db=s.Api.Context();var migrator=db.GetService<IMigrator>();await migrator.MigrateAsync("20261010091247_GatewayQueryIndexes");
        var candidate=JsonNode.Parse((await db.Set<ReleaseRecord>().SingleAsync(r=>r.Id==id)).CandidateBytes!)!;var frozen=candidate["versions"]![0]!;
        JsonObject Parameter()=>new(){["id"]=Guid.NewGuid().ToString(),["apiVersionId"]=s.Api.Version.Id.ToString(),["location"]="query",["name"]="q",["dataType"]="string",["required"]=false};
        switch(fault)
        {
            case "nullParameter":frozen["parameters"]=JsonNode.Parse("[null]");break;
            case "nullSchema":frozen["schemas"]=JsonNode.Parse("[null]");break;
            case "badRevision":frozen["version"]!["revision"]=new JsonObject{["invalid"]=1};break;
            case "overflowRevision":frozen["version"]!["revision"]=JsonNode.Parse("9223372036854775808");break;
            case "badCreatedAt":frozen["version"]!["createdAt"]="invalid-timestamp";break;
            case "stringRequired":var p=Parameter();p["required"]="false";frozen["parameters"]=new JsonArray(p);break;
            case "nullExample":var example=Parameter();example["examples"]=JsonNode.Parse("[null]");frozen["parameters"]=new JsonArray(example);break;
            case "fractionalStatus":frozen["schemas"]=new JsonArray(new JsonObject{["id"]=Guid.NewGuid().ToString(),["apiVersionId"]=s.Api.Version.Id.ToString(),["schemaType"]="response",["name"]="ok",["statusCode"]=200.5,["contentType"]="application/json",["schemaJson"]="{}"});break;
        }
        var bad=System.Text.Encoding.UTF8.GetBytes(candidate.ToJsonString());await db.Set<ReleaseRecord>().Where(r=>r.Id==id).ExecuteUpdateAsync(p=>p.SetProperty(r=>r.CandidateBytes,bad));var applied=(await db.Database.GetAppliedMigrationsAsync()).ToArray();
        var error=await Assert.ThrowsAsync<PostgresException>(()=>migrator.MigrateAsync());Assert.Equal("22023",error.SqlState);
        Assert.Equal(applied,(await db.Database.GetAppliedMigrationsAsync()).ToArray());Assert.False(await db.Database.SqlQueryRaw<bool>("SELECT to_regclass('public.release_frozen_contracts') IS NOT NULL AS \"Value\"").SingleAsync());Assert.Equal(bad,(await db.Set<ReleaseRecord>().AsNoTracking().SingleAsync(r=>r.Id==id)).CandidateBytes);
        Assert.True(await db.Database.SqlQueryRaw<bool>("SELECT to_regprocedure('public.webapi_frozen_shape_20261012004548(jsonb,text,jsonb)') IS NULL AS \"Value\"").SingleAsync());
    }
    [Fact] public async Task LegacyOmittedOptionalFieldsKeepConstructorDefaults()
    {
        await using var s=new DeploymentScenario();await Configure(s,new Capture());var id=await s.PublishAsync();await using var db=s.Api.Context();var migrator=db.GetService<IMigrator>();await migrator.MigrateAsync("20261010091247_GatewayQueryIndexes");
        var node=JsonNode.Parse((await db.Set<ReleaseRecord>().SingleAsync(r=>r.Id==id)).CandidateBytes!)!.AsObject();node.Remove("riskReviewReferences");node.Remove("sharedCredentialImpact");
        node["versions"]![0]!["version"]!.AsObject().Remove("dialect");node["versions"]![0]!["version"]!.AsObject().Remove("sealedAt");
        foreach(var app in node["applications"]!.AsArray())app!["application"]!.AsObject().Remove("activation");
        var old=System.Text.Encoding.UTF8.GetBytes(node.ToJsonString());Assert.NotNull(JsonSerializer.Deserialize<FrozenReleaseCandidate>(old,CanonicalJson.Options));await db.Set<ReleaseRecord>().Where(r=>r.Id==id).ExecuteUpdateAsync(p=>p.SetProperty(r=>r.CandidateBytes,old));
        await migrator.MigrateAsync();Assert.Equal(old,(await db.Set<ReleaseRecord>().AsNoTracking().SingleAsync(r=>r.Id==id)).CandidateBytes);Assert.Equal(1,await db.Set<ReleaseFrozenContract>().CountAsync());
        using var response=await s.Api.Client.GetAsync($"/api/v1/environments/{s.Api.Environment.Id}/apis/{s.Api.Api.Id}/openapi");response.EnsureSuccessStatusCode();
    }
    [Fact] public async Task RunningOpenApiDoesNotLoadWholeEnvironmentCandidates()
    {
        await using var s=new DeploymentScenario();var capture=new Capture();await Configure(s,capture);var published=await s.PublishAsync();
        await using(var db=s.Api.Context())
        {
            var original=await db.Set<ReleaseRecord>().SingleAsync(x=>x.Id==published);var frozen=JsonSerializer.Deserialize<FrozenReleaseCandidate>(original.CandidateBytes!,CanonicalJson.Options)!;
            var unrelated=frozen.Versions[0] with {Api=frozen.Versions[0].Api with {Id=Guid.NewGuid(),Name=new string('x',10000)},Version=frozen.Versions[0].Version with {Id=Guid.NewGuid()}};
            var bytes=CanonicalJson.Serialize(frozen with {Versions=[unrelated]});
            for(var i=0;i<100;i++)db.Add(new ReleaseRecord{EnvironmentId=s.Api.Environment.Id,ReleaseNo="HISTORY-"+i,ReleaseType="publish",Status="Failed",ToConfigVersion=1,DeploymentSequence=-1,CandidateBytes=bytes,ApprovalPolicy="[]",RequestedBy=s.Api.User.Id});
            await db.SaveChangesAsync();
        }
        capture.Reset();using var services=s.Api.Services();using var doc=await services.ServiceProvider.GetRequiredService<EnvironmentApiDocumentService>().BuildAsync(s.Api.Environment.Id,s.Api.Api.Id,null,AccessView.Running,new(s.Api.User.Id,"openapi-query"));
        Assert.Equal("Frozen orders",doc.RootElement.GetProperty("info").GetProperty("title").GetString());
        Assert.DoesNotContain(capture.ReleaseColumns,columns=>columns.Contains("candidate_bytes"));
    }
    [Fact] public async Task IncrementalPublishRetainsOlderApiContract()
    {
        await using var s=new DeploymentScenario();await Configure(s,new Capture());await s.PublishAsync();Guid otherVersion;
        await using(var db=s.Api.Context())
        {
            var other=new Api{OrganizationId=s.Api.Organization.Id,ProjectId=s.Api.Project.Id,Code="OTHER",Name="Other",OwnerUserId=s.Api.User.Id};
            var version=new ApiVersion{ApiId=other.Id,Version="1.0.0",CreatedBy=s.Api.User.Id};otherVersion=version.Id;db.AddRange(other,version);
            db.Add(new ApiRoute{ApiVersionId=version.Id,EnvironmentId=s.Api.Environment.Id,ClusterId=s.Api.Cluster.Id,RouteName="Other",Path="/other",NormalizedPath="/other",Methods=["GET"],TimeoutMs=30000,Enabled=true});await db.SaveChangesAsync();
        }
        using var created=await ApiFixture.CommandAsync(s.Api.Client,$"/api/v1/environments/{s.Api.Environment.Id}/releases",await s.Api.PreviewReleaseRequestAsync(1,otherVersion));created.EnsureSuccessStatusCode();
        var id=(await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        using var submitted=await ApiFixture.CommandAsync(s.Api.Client,$"/api/v1/releases/{id}/submit");submitted.EnsureSuccessStatusCode();await s.ApproveAsync(id);await s.BuildAsync(id);await s.AckAllAsync(id);
        using var response=await s.Api.Client.GetAsync($"/api/v1/environments/{s.Api.Environment.Id}/apis/{s.Api.Api.Id}/openapi");response.EnsureSuccessStatusCode();using var doc=JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Frozen orders",doc.RootElement.GetProperty("info").GetProperty("title").GetString());
    }
    [Fact] public async Task RetryUsesFailedPublishSourceWithoutAddingDuplicateSource()
    {
        await using var s=new DeploymentScenario();await Configure(s,new Capture());var failed=await s.PublishAsync(false);
        await using(var db=s.Api.Context()){await db.Set<ReleaseRecord>().Where(r=>r.Id==failed).ExecuteUpdateAsync(p=>p.SetProperty(r=>r.Status,"Failed").SetProperty(r=>r.FailureCode,"ack_timeout"));}
        using var retried=await ApiFixture.CommandAsync(s.Api.Client,$"/api/v1/releases/{failed}/retry");retried.EnsureSuccessStatusCode();var id=(await retried.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();await s.BuildQueuedAsync();await s.AckAllAsync(id);
        using var response=await s.Api.Client.GetAsync($"/api/v1/environments/{s.Api.Environment.Id}/apis/{s.Api.Api.Id}/openapi");response.EnsureSuccessStatusCode();
        await using var check=s.Api.Context();Assert.Equal(1,await check.Set<ReleaseFrozenContract>().CountAsync());Assert.Equal(failed,(await check.Set<ReleaseFrozenContract>().SingleAsync()).ReleaseId);
    }
    [Fact] public async Task MissingSourceFailsClosedAndRevokedScopeRemainsHidden()
    {
        await using var s=new DeploymentScenario();await Configure(s,new Capture());await s.PublishAsync();await using(var db=s.Api.Context())await db.Set<ReleaseFrozenContract>().ExecuteDeleteAsync();
        var entry=$"/api/v1/environments/{s.Api.Environment.Id}/apis/{s.Api.Api.Id}/";
        foreach(var suffix in new[]{"access-addresses","openapi"}){using var response=await s.Api.Client.GetAsync(entry+suffix);Assert.Equal(HttpStatusCode.UnprocessableEntity,response.StatusCode);Assert.Contains("historical_contract_missing",await response.Content.ReadAsStringAsync());}
        await using(var db=s.Api.Context())await db.Set<UserProjectScope>().ExecuteDeleteAsync();
        using var hidden=await s.Api.Client.GetAsync(entry+"openapi");Assert.Equal(HttpStatusCode.NotFound,hidden.StatusCode);
    }
    [Fact] public async Task FutureContractCannotOverrideRunningVersionCeiling()
    {
        await using var s=new DeploymentScenario();await Configure(s,new Capture());var original=await s.PublishAsync();await using(var db=s.Api.Context())
        {
            var stored=await db.Set<ReleaseRecord>().SingleAsync(r=>r.Id==original);var frozen=JsonSerializer.Deserialize<FrozenReleaseCandidate>(stored.CandidateBytes!,CanonicalJson.Options)!;
            var version=frozen.Versions[0] with {Version=frozen.Versions[0].Version with {OpenapiDocument=Document.Replace("Frozen orders","Future contract")}};
            var future=new ReleaseRecord{EnvironmentId=s.Api.Environment.Id,ReleaseNo="FUTURE",ReleaseType="publish",Status="Failed",ToConfigVersion=2,DeploymentSequence=-1,RequestedBy=s.Api.User.Id,ApprovalPolicy="[]",CandidateBytes=CanonicalJson.Serialize(frozen with {Versions=[version]})};
            db.Add(future);db.Add(new ReleaseFrozenContract{ReleaseId=future.Id,Ordinal=1,EnvironmentId=s.Api.Environment.Id,ConfigVersion=2,ApiId=s.Api.Api.Id,ApiVersionId=s.Api.Version.Id,FrozenJson=System.Text.Encoding.UTF8.GetString(CanonicalJson.Serialize(version))});await db.SaveChangesAsync();
        }
        using var response=await s.Api.Client.GetAsync($"/api/v1/environments/{s.Api.Environment.Id}/apis/{s.Api.Api.Id}/openapi");response.EnsureSuccessStatusCode();using var doc=JsonDocument.Parse(await response.Content.ReadAsStringAsync());Assert.Equal("Frozen orders",doc.RootElement.GetProperty("info").GetProperty("title").GetString());
    }
    [Fact] public async Task InvalidatedLatestOriginFallsBackToEligibleOlderOrigin()
    {
        await using var s=new DeploymentScenario();await Configure(s,new Capture());var original=await s.PublishAsync();await using(var db=s.Api.Context())
        {
            var stored=await db.Set<ReleaseRecord>().SingleAsync(r=>r.Id==original);var frozen=JsonSerializer.Deserialize<FrozenReleaseCandidate>(stored.CandidateBytes!,CanonicalJson.Options)!;var version=frozen.Versions[0];
            var invalid=new ReleaseRecord{Id=Guid.Parse("00000000-0000-0000-0000-000000000001"),EnvironmentId=s.Api.Environment.Id,ReleaseNo="INVALIDATED",ReleaseType="publish",Status="Failed",ToConfigVersion=1,DeploymentSequence=-1,RequestedBy=s.Api.User.Id,ApprovalPolicy=null,CandidateBytes=stored.CandidateBytes};
            // The invalid source sorts first within the tied config version. An eligible
            // older source must still be found rather than trusting the first projection.
            db.Add(invalid);db.Add(new ReleaseFrozenContract{ReleaseId=invalid.Id,Ordinal=1,EnvironmentId=s.Api.Environment.Id,ConfigVersion=1,ApiId=s.Api.Api.Id,ApiVersionId=s.Api.Version.Id,FrozenJson=System.Text.Encoding.UTF8.GetString(CanonicalJson.Serialize(version))});await db.SaveChangesAsync();
        }
        var path=$"/api/v1/environments/{s.Api.Environment.Id}/apis/{s.Api.Api.Id}/openapi";using(var response=await s.Api.Client.GetAsync(path))response.EnsureSuccessStatusCode();
        await using(var db=s.Api.Context())await db.Set<ReleaseRecord>().Where(r=>r.Id==original).ExecuteUpdateAsync(p=>p.SetProperty(r=>r.ApprovalPolicy,(string?)null));
        using var missing=await s.Api.Client.GetAsync(path);Assert.Equal(HttpStatusCode.UnprocessableEntity,missing.StatusCode);Assert.Contains("historical_contract_missing",await missing.Content.ReadAsStringAsync());
    }
}
