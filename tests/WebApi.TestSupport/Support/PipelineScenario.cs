using Microsoft.EntityFrameworkCore;
using System.Net.Http.Json;
using WebApi.Contracts.Releases;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Persistence.Entities;
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
    public async Task InitializeEnvironmentsAsync(int environmentCount=2,bool nonProductionApproval=false)
    {
        if(environmentCount is <2 or >8)throw new ArgumentOutOfRangeException(nameof(environmentCount));
        await Source.InitializeAsync(canRecord:false);
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
    public ValueTask DisposeAsync()=>Source.DisposeAsync();
}
