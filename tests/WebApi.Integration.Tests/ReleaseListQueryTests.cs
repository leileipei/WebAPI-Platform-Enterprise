using System.Data.Common;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WebApi.Contracts.Common;
using WebApi.Contracts.Comparisons;
using WebApi.Contracts.Releases;
using WebApi.Contracts.Security;
using WebApi.ControlPlane;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Releases;
using WebApi.Integration.Tests.Support;
using WebApi.PerformanceHost;
using Xunit;

namespace WebApi.Integration.Tests;

public sealed class ReleaseListQueryTests
{
    private sealed class Capture:DbCommandInterceptor
    {
        internal int Data;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,CommandEventData data,InterceptionResult<DbDataReader> result,CancellationToken ct=default)
        {
            if(new[]{"FROM release_records ","FROM approval_tasks ","FROM release_targets ","FROM gateway_acks ","FROM apis "}.Any(command.CommandText.Contains))Data++;
            return ValueTask.FromResult(result);
        }
    }
    private sealed class Fixture:IAsyncDisposable
    {
        private const int Seed=20261009;
        internal readonly PostgresDatabase Database=new();internal readonly Capture Sql=new();internal readonly RequestSqlProbe Probe=new();
        internal readonly string DirectoryPath=Path.Combine(Path.GetTempPath(),"release-list-"+Guid.NewGuid().ToString("N"));
        private WebApplication? app;internal readonly List<ReleaseRecord> Releases=[];
        internal Guid Id(string kind,int index)=>PerformanceDataset.ResourceId(Seed,kind,index);
        internal Guid EnvironmentId=>Id("environment",0);internal Guid OrganizationId=>Id("organization",0);internal Guid ProjectId=>Id("project",0);
        internal ActorContext Actor=>new(Id("user",0),"release-list");internal ScopeRef Scope=>new(OrganizationId,ProjectId,EnvironmentId);
        internal IServiceScope Services()=>app!.Services.CreateScope();
        internal async Task InitializeAsync()
        {
            if(OperatingSystem.IsWindows())throw new PlatformNotSupportedException("Private fixtures require Unix modes.");Directory.CreateDirectory(DirectoryPath,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);await Database.InitializeAsync();await using var db=Database.Context();await db.Database.MigrateAsync();
            var now=DateTimeOffset.UtcNow.AddMinutes(-1);await PerformanceDataset.CreateAsync(db,new(100,Seed,now,now.AddHours(10)),DirectoryPath,CancellationToken.None);db.ChangeTracker.Clear();
            var source=await db.Set<ReleaseRecord>().AsNoTracking().OrderBy(r=>r.CreatedAt).FirstAsync();var flow=await db.Set<ApprovalFlow>().Select(f=>f.Id).SingleAsync();
            var candidate=JsonSerializer.Deserialize<FrozenReleaseCandidate>(source.CandidateBytes!,CanonicalJson.Options)!;
            var sourceHash=await db.Set<GatewayConfigVersion>().Where(v=>v.EnvironmentId==EnvironmentId&&v.VersionNo==source.ToConfigVersion).Select(v=>v.SnapshotHash).SingleAsync();
            var fromVersion=new ApiVersion{ApiId=candidate.Versions[0].Api.Id,Version="0.9.0",Status="Active",ChangeType="compatible",CreatedBy=Actor.UserId};db.Add(fromVersion);
            var approver=new Role{OrganizationId=OrganizationId,Code="QueryApprover",Name="冻结审批角色"};db.Add(approver);
            db.Add(new UserProjectScope{UserId=Id("user",3),OrganizationId=OrganizationId,AccessMode="read_write"});
            var failedId=Guid.NewGuid();
            for(var i=100;i<151;i++)
            {
                var state=(i%5) switch{0=>"WaitingApproval",1=>"Ready",2=>"Failed",3=>"RolledBack",_=>"Ready"};
                var release=new ReleaseRecord{Id=i==102?failedId:Guid.NewGuid(),EnvironmentId=EnvironmentId,ReleaseNo="QUERY-"+i,FromConfigVersion=source.FromConfigVersion,ToConfigVersion=source.ToConfigVersion,BaselineConfigVersion=source.BaselineConfigVersion,RequestedBy=state=="WaitingApproval"?Id("user",3):Actor.UserId,Status=state,ReleaseType=i%5==3?"rollback":i%5==4?"retry":"publish",RollbackOf=i%5==3?source.Id:null,RecoveryOf=i%5==4?failedId:null,FailureCode=state=="Failed"?"gateway_apply_failed":null,CreatedAt=now.AddSeconds(i),CandidateBytes=source.CandidateBytes,ApprovalPolicy=Encoding.UTF8.GetString(CanonicalJson.Serialize(new[]{new ApprovalRule(1,"QueryApprover",1),new ApprovalRule(2,"QueryApprover",1)}))};
                if(state is "Failed" or "RolledBack")release.DeploymentSequence=i+1;
                db.Add(release);
                for(var step=1;step<=2;step++)db.Add(new ApprovalTask{ReleaseId=release.Id,FlowId=flow,StepOrder=step,Status=state=="WaitingApproval"?"Pending":"Approved",AssigneeUserId=state=="WaitingApproval"?null:Id("user",step),Comment=state=="WaitingApproval"?null:"冻结审批意见",ActedAt=state=="WaitingApproval"?null:now});
                if(state is "Failed" or "RolledBack")for(var node=0;node<2;node++)
                {var entity=await db.Set<GatewayNode>().AsNoTracking().SingleAsync(n=>n.Id==Id("node",node));db.Add(new ReleaseTarget{ReleaseId=release.Id,NodeId=entity.Id,InstanceId=entity.InstanceId,CreatedAt=now});db.Add(new GatewayAck{ReleaseId=release.Id,NodeId=entity.Id,InstanceId=entity.InstanceId,ConfigVersion=release.ToConfigVersion,DeploymentSequence=release.DeploymentSequence!.Value,PayloadHash=sourceHash!,Success=state=="RolledBack",ErrorCode=state=="Failed"?"apply_failed":null,AppliedAt=now});}
            }
            await db.SaveChangesAsync();db.ChangeTracker.Clear();
            var records=await db.Set<ReleaseRecord>().OrderByDescending(r=>r.CreatedAt).ThenByDescending(r=>r.Id).ToArrayAsync();Assert.Equal(151,records.Length);
            // Frozen historical risk references follow the existing inbox fixture contract;
            // the list must resolve actual Api IDs and current read rights, never trust the summary alone.
            foreach(var release in records)
            {
                var frozen=JsonSerializer.Deserialize<FrozenReleaseCandidate>(release.CandidateBytes!,CanonicalJson.Options)!;var version=frozen.Versions[0];var review=Guid.NewGuid();var comparison=Guid.NewGuid();
                var summary=new RiskReviewSummaryDto(review,comparison,version.Api.Id,fromVersion.Id,version.Version.Id,"0.9.0","1.0.0","AcceptedRisk",Actor.UserId,now,"input","report","engine","Partial",new(1,2,3,4,5,6),"PRIVATE_REVIEW_COMMENT");
                release.CandidateBytes=CanonicalJson.Serialize(frozen with{RiskReviewReferences=[new(version.Api.Id,version.Version.Id,comparison,review,"input","report","engine",summary)]});
            }
            await db.SaveChangesAsync();Releases.AddRange(records);
            app=ControlPlaneApp.Build(["--environment","Development"],b=>{b.Configuration["ConnectionStrings:WebApi"]=Database.ConnectionString;b.Logging.ClearProviders();LoginProtectionTestConfiguration.Apply(b,DirectoryPath,Guid.NewGuid().ToString("N"));b.Services.AddDbContext<WebApiDbContext>(o=>o.AddInterceptors(Sql,Probe));});
        }
        internal async Task<ActorContext> AddActorAsync(string name,bool approval=true,string mode="read_write",string? missingApiPermission=null,bool inactive=false,string roleCode="QueryApprover")
        {
            await using var db=Database.Context();var user=new UserRecord{Username="release-"+name,DisplayName=name,SecurityStamp=Guid.NewGuid().ToString("N"),Status=inactive?"Disabled":"Active"};var role=new Role{OrganizationId=OrganizationId,Code=name+"-"+Guid.NewGuid().ToString("N"),Name=name};db.AddRange(user,role);db.Add(new UserRole{UserId=user.Id,RoleId=role.Id});
            if(roleCode=="QueryApprover")db.Add(new UserRole{UserId=user.Id,RoleId=await db.Set<Role>().Where(r=>r.Code=="QueryApprover").Select(r=>r.Id).SingleAsync()});
            foreach(var permission in await db.Set<Permission>().Where(p=>p.Code=="release.read"||approval&&p.Code=="approval.act"||p.Code=="api.read"||p.Code=="api.version.read"||p.Code=="api.schema.read").ToArrayAsync())if(permission.Code!=missingApiPermission)db.Add(new RolePermission{RoleId=role.Id,PermissionId=permission.Id});
            db.Add(new UserProjectScope{UserId=user.Id,OrganizationId=OrganizationId,AccessMode=mode});await db.SaveChangesAsync();return new(user.Id,name);
        }
        internal async Task<(PageResult<ReleaseDto> Page,int Data)> ReadAsync(ActorContext actor,int page,int size)
        {
            Sql.Data=0;var id=Guid.NewGuid().ToString("N");using var probe=Probe.Begin(id);using var services=Services();var result=await services.ServiceProvider.GetRequiredService<ReleaseService>().ListAsync(EnvironmentId,actor,page,size);
            Assert.InRange(Sql.Data,1,12);Assert.True(Probe.Read(id).Total<=40,$"total SQL {Probe.Read(id).Total} > 40");return(result,Sql.Data);
        }
        public async ValueTask DisposeAsync(){if(app is not null)await app.DisposeAsync();await Database.DisposeAsync();if(Directory.Exists(DirectoryPath))Directory.Delete(DirectoryPath,true);}
    }
    [Fact]
    public async Task ReleaseListMatchesDetailFieldsWithConstantSql()
    {
        await using var f=new Fixture();await f.InitializeAsync();using var services=f.Services();var service=services.ServiceProvider.GetRequiredService<ReleaseService>();
        var details=new Dictionary<Guid,ReleaseDto>();foreach(var release in f.Releases)details[release.Id]=await service.GetAsync(release.Id,f.Actor);
        int? pageSql=null;
        foreach(var size in new[]{1,50,100})foreach(var page in new[]{1,2})
        {
            var result=await f.ReadAsync(f.Actor,page,size);Assert.Equal(151,result.Page.Total);Assert.Equal(f.Releases.Skip((page-1)*size).Take(size).Select(r=>r.Id),result.Page.Items.Select(r=>r.Id));
            foreach(var item in result.Page.Items)Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(details[item.Id],CanonicalJson.Options),JsonSerializer.SerializeToElement(item,CanonicalJson.Options)),"Full release DTO differs from detail oracle");
            if(result.Page.Items.Count>0){pageSql??=result.Data;Assert.Equal(pageSql,result.Data);}
        }
        var empty=await f.ReadAsync(f.Actor,1000001,100);Assert.Equal(1000000,empty.Page.Page);Assert.Equal(151,empty.Page.Total);Assert.Empty(empty.Page.Items);
        var json=JsonSerializer.Serialize(details.Values,CanonicalJson.Options);Assert.DoesNotContain("secretHash",json);Assert.DoesNotContain("candidateBytes",json);Assert.Contains("PRIVATE_REVIEW_COMMENT",json);
    }
    [Fact]
    public async Task EligibilityPreservesErrorPrecedenceAndIndependence()
    {
        await using var f=new Fixture();await f.InitializeAsync();var waiting=f.Releases.First(r=>r.Status=="WaitingApproval");var used=await f.AddActorAsync("used");
        await using(var db=f.Database.Context()){var task=await db.Set<ApprovalTask>().SingleAsync(t=>t.ReleaseId==waiting.Id&&t.StepOrder==1);task.Status="Approved";task.AssigneeUserId=used.UserId;task.ActedAt=DateTimeOffset.UtcNow;await db.SaveChangesAsync();}
        var cases=new[]{(await f.AddActorAsync("no-function",approval:false),waiting,new ApprovalEligibility(false,null,"permission_denied")),(await f.AddActorAsync("readonly",mode:"read"),waiting,new ApprovalEligibility(false,null,"scope_denied")),(new ActorContext(f.Id("user",3),"self"),waiting,new ApprovalEligibility(false,2,"self_approval")),(used,waiting,new ApprovalEligibility(false,2,"independent_approval_required")),(await f.AddActorAsync("wrong-role",roleCode:"Wrong"),waiting,new ApprovalEligibility(false,2,"approval_role_required")),(await f.AddActorAsync("valid"),waiting,new ApprovalEligibility(true,2,null)),(f.Actor,f.Releases.First(r=>r.Status=="Succeeded"),new ApprovalEligibility(false,null,"invalid_release_state"))};
        foreach(var(actor,release,expected)in cases){using var scope=f.Services();var service=scope.ServiceProvider.GetRequiredService<ApprovalEligibilityService>();Assert.Equal(expected,await service.EvaluateAsync(release,f.Scope,actor,CancellationToken.None));var batch=await service.EvaluateBatchAsync([release],f.Scope,actor,CancellationToken.None);Assert.Single(batch);Assert.Equal(expected,batch[release.Id]);}
        var inactive=await f.AddActorAsync("inactive",inactive:true);using var services=f.Services();var eligibility=services.ServiceProvider.GetRequiredService<ApprovalEligibilityService>();Assert.Equal(401,(await Assert.ThrowsAsync<ApiException>(()=>eligibility.EvaluateBatchAsync([waiting],f.Scope,inactive,CancellationToken.None))).Status);
    }
    [Theory]
    [InlineData(null)] [InlineData("api.read")] [InlineData("api.version.read")] [InlineData("api.schema.read")]
    public async Task RiskCommentRequiresAllThreePermissions(string? missing)
    {
        await using var f=new Fixture();await f.InitializeAsync();var actor=await f.AddActorAsync("risk-reader",missingApiPermission:missing);var result=await f.ReadAsync(actor,1,100);
        Assert.Equal(151,result.Page.Total);Assert.Equal(100,result.Page.Items.Count);Assert.All(result.Page.Items,r=>{var risk=Assert.Single(r.FrozenCandidate!.RiskReviews!);Assert.Equal(missing is null?"PRIVATE_REVIEW_COMMENT":null,risk.Comment);});
        using var services=f.Services();foreach(var release in result.Page.Items.Take(2)){var detail=await services.ServiceProvider.GetRequiredService<ReleaseService>().GetAsync(release.Id,actor);Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(detail,CanonicalJson.Options),JsonSerializer.SerializeToElement(release,CanonicalJson.Options)));}
    }
}
