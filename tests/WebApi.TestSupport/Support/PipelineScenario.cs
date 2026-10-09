using Microsoft.EntityFrameworkCore;
using System.Net.Http.Json;
using WebApi.Contracts.Releases;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Persistence.Entities;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Infrastructure.Delivery.Pipelines;
using WebApi.Infrastructure.Gateway;
using WebApi.Contracts.Gateway;
using WebApi.Contracts.Common;
namespace WebApi.Integration.Tests.Support;
/// <summary>Real test-host commands and synthetic ACKs; not a deployed Gateway acceptance proof.</summary>
public sealed class PipelineScenario : IAsyncDisposable
{
    public DeliveryScenario Source { get; } = new();
    public ApiFixture Api => Source.Api;
    public HttpClient Creator => Api.Client;
    public Guid RootArtifactId => Source.Artifact.Id;
    public Guid[] Environments { get; private set; } = [];
    public PipelineDefinition Definition { get; private set; } = null!;
    public async Task InitializeEnvironmentsAsync(int environmentCount=2,bool nonProductionApproval=false,Func<ApiFixture,Task>? beforePublish=null)
    {
        if(environmentCount is <2 or >8)throw new ArgumentOutOfRangeException(nameof(environmentCount));
        await Source.InitializeAsync(canRecord:false,beforePublish:beforePublish is null?null:async api=>{await using(var db=api.Context())await PermissionCatalog.SeedAsync(db);await GrantAsync(api.User.Id,"policy.read","policy.write");await beforePublish(api);});
        await using(var db=Api.Context())
        {
            var source=await db.Set<EnvironmentRecord>().SingleAsync();source.Code=environmentCount==2?"TEST":"DEV";
            var environments=new List<EnvironmentRecord>{source};
            for(var i=1;i<environmentCount;i++)
            {
                var production=i==environmentCount-1;
                var code=production?"PROD":i==1?"TEST":i==2?"UAT":"ENV"+(i+1);
                var target=new EnvironmentRecord{ProjectId=Api.Project.Id,Code=code,Name=code,IsProduction=production,
                    ReleasePolicyId=production||nonProductionApproval?Api.ApprovalFlow.Id:null,
                    GatewayPublicUrl=$"https://gateway-{i+1}.example",BasePath="/api"};
                db.Add(target);environments.Add(target);
            }
            await db.SaveChangesAsync();Environments=environments.Select(e=>e.Id).ToArray();
            Definition=new("标准交付","测试宿主夹具",environments.Select((e,i)=>new PipelineStageDefinition(i+1,e.Id,
                e.IsProduction?["EntryConnectivity","AuthenticationAuthorization","CriticalBusinessCall"]:["InterfaceFunction","Integration","ContractCompatibility"],
                ApprovalFlowId:i==0?null:e.ReleasePolicyId)).ToArray());
        }
        await using(var db=Api.Context())await PermissionCatalog.SeedAsync(db);
        await GrantAsync(Api.User.Id,"pipeline.read","pipeline.manage","pipeline.run","release.test.record","release.test.accept","release.verify","policy.read","policy.write");
    }
    public async Task GrantAsync(Guid userId,params string[] codes)
    {
        await using var db=Api.Context();var roleId=await db.Set<UserRole>().Where(r=>r.UserId==userId).Select(r=>r.RoleId).FirstAsync();
        var permissions=await db.Set<Permission>().Where(p=>codes.Contains(p.Code)).ToArrayAsync();
        foreach(var permission in permissions)if(!await db.Set<RolePermission>().AnyAsync(r=>r.RoleId==roleId&&r.PermissionId==permission.Id))db.Add(new RolePermission{RoleId=roleId,PermissionId=permission.Id});
        await db.SaveChangesAsync();
    }
    public Task<HttpResponseMessage> TryStartAsync(Guid versionId,string? key=null)=>ApiFixture.CommandAsync(Creator,"/api/v1/release-pipeline-runs",new CreatePipelineRunRequest(versionId,RootArtifactId),key);
    public async Task<PipelineRunDto> StartAsync(Guid versionId,string? key=null){using var response=await TryStartAsync(versionId,key);response.EnsureSuccessStatusCode();return (await response.Content.ReadFromJsonAsync<PipelineRunDto>())!;}
    public async Task<PipelineRunStageDto> CurrentStageAsync(Guid runId){using var response=await Creator.GetAsync($"/api/v1/release-pipeline-runs/{runId}");response.EnsureSuccessStatusCode();var run=(await response.Content.ReadFromJsonAsync<PipelineRunDto>())!;return run.Stages.Single(s=>s.StageOrder==run.CurrentStageOrder);}
    public async Task ProjectAsync(Guid runId)
    {using var scope=Api.Services();await scope.ServiceProvider.GetRequiredService<PipelineProjectionService>().ProjectRunAsync(runId,CancellationToken.None);}
    public async Task PassSourceAsync(Guid runId,HttpClient tester,HttpClient acceptor)
    {var current=await CurrentStageAsync(runId);if(current.StageOrder!=1)throw new InvalidOperationException("The source stage is no longer current.");await PassCurrentAsync(runId,tester,acceptor);}
    public async Task PassCurrentAsync(Guid runId,HttpClient tester,HttpClient? acceptor=null)
    {
        var stage=await CurrentStageAsync(runId);
        if(stage.Profile!.IsProduction)
        {
            await using var db=Api.Context();var attempt=await db.Set<ReleasePipelineStageAttempt>().SingleAsync(a=>a.Id==stage.CurrentAttemptId);while(attempt.PromotionId is null&&attempt.OriginAttemptId is Guid origin)attempt=await db.Set<ReleasePipelineStageAttempt>().SingleAsync(a=>a.Id==origin);var promotion=attempt.PromotionId??throw new InvalidOperationException("A production promotion is required.");
            var context=(await tester.GetFromJsonAsync<ProductionVerificationContextDto>($"/api/v1/release-promotions/{promotion}/verification-context"))!;
            foreach(var type in stage.Profile.RequiredTypes){using var recorded=await ApiFixture.CommandAsync(tester,$"/api/v1/release-promotions/{promotion}/verifications",new RecordVerificationRequest(type,"Passed",DateTimeOffset.UtcNow,DateTimeOffset.UtcNow,ExpectedContextHash:context.Hash));recorded.EnsureSuccessStatusCode();}
        }
        else
        {
            if(stage.StageArtifactId is null){using var artifact=await ApiFixture.CommandAsync(Creator,$"/api/v1/release-pipeline-run-stages/{stage.Id}/materialize-artifact");artifact.EnsureSuccessStatusCode();}
            var context=(await tester.GetFromJsonAsync<PipelineVerificationContextDto>($"/api/v1/release-pipeline-run-stages/{stage.Id}/verification-context"))!;var ids=new List<Guid>();
            foreach(var type in context.RequiredTypes){using var recorded=await ApiFixture.CommandAsync(tester,$"/api/v1/release-pipeline-run-stages/{stage.Id}/verifications",new PipelineStageVerificationRequest(context.ContextHash,new(type,"Passed",DateTimeOffset.UtcNow,DateTimeOffset.UtcNow)));recorded.EnsureSuccessStatusCode();ids.Add((await recorded.Content.ReadFromJsonAsync<ReleaseVerificationDto>())!.Id);}
            using var requested=await ApiFixture.CommandAsync(tester,$"/api/v1/release-pipeline-run-stages/{stage.Id}/acceptance-requests",new PipelineAcceptanceRequest(context.ContextHash,ids));requested.EnsureSuccessStatusCode();var acceptance=(await requested.Content.ReadFromJsonAsync<TestAcceptanceDto>())!;
            if(acceptor is null)throw new InvalidOperationException("An explicit independent acceptor is required.");using var current=await acceptor.GetAsync($"/api/v1/test-acceptances/{acceptance.Id}");current.EnsureSuccessStatusCode();using var command=new HttpRequestMessage(HttpMethod.Post,$"/api/v1/test-acceptances/{acceptance.Id}/accept"){Content=JsonContent.Create(new TestAcceptanceActionRequest("测试夹具独立验收"))};command.Headers.Add("If-Match",current.Headers.ETag!.ToString());command.Headers.Add("Idempotency-Key",Guid.NewGuid().ToString("N"));command.Headers.Add("X-CSRF-Token",(await acceptor.GetFromJsonAsync<Dictionary<string,string>>("/api/v1/auth/csrf"))!["token"]);using var accepted=await acceptor.SendAsync(command);accepted.EnsureSuccessStatusCode();
        }
        await ProjectAsync(runId);
    }
    public async Task DeployCurrentAsync(Guid runId,HttpClient publisher,IReadOnlyList<HttpClient>? approvers=null)
    {
        var stage=await CurrentStageAsync(runId);Guid releaseId;await using(var db=Api.Context())releaseId=(await db.Set<ReleasePipelineStageAttempt>().SingleAsync(a=>a.Id==stage.CurrentAttemptId)).ActualReleaseId??throw new InvalidOperationException("Prepare, map, precheck and submit the current stage explicitly first.");
        foreach(var reviewer in approvers??[]){using var approval=await ApiFixture.CommandAsync(reviewer,$"/api/v1/releases/{releaseId}/approve",new{comment="测试夹具独立部署审核"});approval.EnsureSuccessStatusCode();}
        using(var publish=await ApiFixture.CommandAsync(publisher,$"/api/v1/releases/{releaseId}/publish"))publish.EnsureSuccessStatusCode();await Source.Deployment.BuildQueuedAsync();
        await AcknowledgeAsync(releaseId);await ProjectAsync(runId);
    }
    public async Task AcknowledgeAsync(Guid releaseId)
    {
        // Authenticated service boundary with persisted identities and frozen targets;
        // these synthetic acknowledgements are not real Gateway process acceptance.
        var desired=await Source.Deployment.DesiredAsync(releaseId);GatewayNode[] nodes;await using(var db=Api.Context())nodes=await db.Set<GatewayNode>().Where(n=>db.Set<ReleaseTarget>().Any(t=>t.ReleaseId==releaseId&&t.NodeId==n.Id)).ToArrayAsync();
        foreach(var node in nodes){using var services=Api.Services();await services.ServiceProvider.GetRequiredService<AckService>().RecordAsync(node.Id,new NodeAck(Guid.Parse(node.InstanceId),releaseId,desired.Envelope.ConfigVersion,desired.Envelope.DeploymentSequence,desired.Envelope.PayloadHash,DateTimeOffset.UtcNow,true,null),new NodeIdentity(node.Id,node.EnvironmentId,node.IdentityHash));}
    }
    public ValueTask DisposeAsync()=>Source.DisposeAsync();
}
