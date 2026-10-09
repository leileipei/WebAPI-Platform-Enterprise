using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Catalog;
using WebApi.Contracts.Releases;
using WebApi.Contracts.Runtime;
using WebApi.Domain.Policies;
using WebApi.Domain.Runtime;
using WebApi.Infrastructure.Applications;
using WebApi.Infrastructure.Catalog;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Releases;
using WebApi.Infrastructure.Routing;
using WebApi.Infrastructure.Security;

namespace WebApi.PerformanceHost;
public static partial class PerformanceDataset
{
    // Management cardinalities are independent of the gateway wire fixture. Histories
    // contain eight full versions, not N copies of the largest current snapshot.
    public static async Task<DatasetManifest> CreateAsync(WebApiDbContext db,DatasetSpec spec,string privateDirectory,CancellationToken ct)
    {
        if(OperatingSystem.IsWindows())throw new PlatformNotSupportedException("Performance fixtures require Unix private-file modes.");
        if(spec.Scale is not (100 or 1000 or 10000)||spec.WindowEnd<=spec.WindowStart)throw new ArgumentException("Invalid performance scale or window.");
        if(await db.Set<Organization>().AnyAsync(ct)||await db.Set<UserRecord>().AnyAsync(ct)||await db.Set<Api>().AnyAsync(ct))
            throw new InvalidOperationException("Performance seeding requires an empty isolated business database.");
        using var keys=PerformanceJwtMaterial.LoadOrCreate(privateDirectory);
        var passwordPath=Path.Combine(privateDirectory,"admin-password");
        if(!File.Exists(passwordPath)) {
            using var file=new FileStream(passwordPath,FileMode.CreateNew,FileAccess.Write,FileShare.None);
            File.SetUnixFileMode(passwordPath,UnixFileMode.UserRead|UnixFileMode.UserWrite);
            file.Write(Encoding.UTF8.GetBytes(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));
        }
        if(new FileInfo(passwordPath).LinkTarget is not null||new FileInfo(passwordPath).Length>256||
            (File.GetUnixFileMode(passwordPath)&~(UnixFileMode.UserRead|UnixFileMode.UserWrite))!=0)
            throw new InvalidOperationException("Invalid private password file.");
        Guid Id(string kind,int i)=>ResourceId(spec.Seed,kind,i);
        var n=spec.Scale;var now=spec.WindowStart;var user=Id("user",0);var org=Id("organization",0);var project=Id("project",0);var env=Id("environment",0);
        var oldDetection=db.ChangeTracker.AutoDetectChangesEnabled;db.ChangeTracker.AutoDetectChangesEnabled=false;
        await using var tx=await db.Database.BeginTransactionAsync(ct);
        async Task Save(){await db.SaveChangesAsync(ct);db.ChangeTracker.Clear();}
        try {
            var users=Enumerable.Range(0,n).Select(i=>new UserRecord {Id=Id("user",i),Username=i==0?"performance_admin":$"perf_user_{i:D5}",DisplayName=$"Performance user {i:D5}",CreatedAt=now,UpdatedAt=now,SecurityStamp=Id("stamp",i).ToString("N")}).ToArray();
            users[0].PasswordHash=new PasswordHasher<UserRecord>().HashPassword(users[0],(await File.ReadAllTextAsync(passwordPath,ct)).Trim());db.AddRange(users);await Save();
            var permissions=await db.Set<Permission>().ToDictionaryAsync(p=>p.Code,ct);
            foreach(var code in PermissionCatalog.Codes)if(!permissions.ContainsKey(code)) {
                var permission=new Permission {Id=Id("permission",Array.IndexOf(PermissionCatalog.Codes,code)),Code=code,Module=code.Split('.')[0],Name=code};permissions.Add(code,permission);db.Add(permission);
            }
            var role=await db.Set<Role>().SingleOrDefaultAsync(r=>r.Code=="PlatformAdmin"&&r.OrganizationId==null,ct);
            if(role is null){role=new Role {Id=Id("role",0),Code="PlatformAdmin",Name="Performance administrator",IsSystem=true,CreatedAt=now};db.Add(role);}
            if(!role.IsSystem)throw new InvalidOperationException("PlatformAdmin is not a system role.");
            var assigned=await db.Set<RolePermission>().Where(r=>r.RoleId==role.Id).Select(r=>r.PermissionId).ToArrayAsync(ct);
            foreach(var permission in permissions.Values)if(!assigned.Contains(permission.Id))db.Add(new RolePermission {RoleId=role.Id,PermissionId=permission.Id});
            db.AddRange(users.Select(u=>new UserRole {UserId=u.Id,RoleId=role.Id}));await Save();
            db.AddRange(Enumerable.Range(0,10).Select(i=>new Organization {Id=Id("organization",i),Code=$"org{i:D2}",Name=$"Performance organization {i:D2}",CreatedAt=now,UpdatedAt=now}));await Save();
            db.AddRange(Enumerable.Range(0,50).Select(i=>new Project {Id=Id("project",i),OrganizationId=Id("organization",i/5),OwnerUserId=user,Code=$"project{i:D2}",Name=$"Performance project {i:D2}",CreatedAt=now,UpdatedAt=now}));await Save();
            var environments=Enumerable.Range(0,150).Select(i=>new EnvironmentRecord {Id=Id("environment",i),ProjectId=Id("project",i/3),Code=new[]{"dev","test","prod"}[i%3],Name=$"Performance environment {i:D3}",SortOrder=i%3,IsProduction=i%3==2,DesiredConfigVersion=i==0?n:null,DeploymentSequence=i==0?n:0}).ToArray();
            db.AddRange(environments);
            // Real explicit scopes are still required, even for the platform role.
            db.AddRange(Enumerable.Range(0,10).Select(i=>new UserProjectScope {Id=Id("scope",i),UserId=user,OrganizationId=Id("organization",i),AccessMode="read_write"}));await Save();
            var apis=Enumerable.Range(0,n).Select(i=>new Api {Id=Id("api",i),OrganizationId=org,ProjectId=project,Code=$"api{i:D5}",Name=$"Performance API {i:D5}",LifecycleStatus="Active",OwnerUserId=user,CreatedAt=now,UpdatedAt=now}).ToArray();
            var versions=Enumerable.Range(0,n).Select(i=>new ApiVersion {Id=Id("version",i),ApiId=apis[i].Id,Version="1.0.0",Status="Active",ChangeType="compatible",CreatedBy=user,CreatedAt=now}).ToArray();
            db.AddRange(apis);db.AddRange(versions);await Save();
            var clusters=Enumerable.Range(0,n).Select(i=>new UpstreamCluster {Id=Id("management-cluster",i),ProjectId=project,EnvironmentId=env,Name=$"cluster{i:D5}",LoadBalancingPolicy="RoundRobin",HealthCheckPath="/",HealthCheckIntervalSec=30}).ToArray();
            var destinations=Enumerable.Range(0,n*2).Select(i=>new UpstreamDestination {Id=Id("management-destination",i),ClusterId=clusters[i/2].Id,Name=$"destination{i:D5}",Address="http://backend:8080/",Weight=1,CreatedAt=now}).ToArray();
            db.AddRange(clusters);db.AddRange(destinations);await Save();
            var routes=Enumerable.Range(0,n).Select(i=>new ApiRoute {Id=Id("route",i),ApiVersionId=versions[i].Id,EnvironmentId=env,RouteName=$"route{i:D5}",Path=$"/r/{i}",NormalizedPath=$"/r/{i}",Methods=["GET"],ClusterId=clusters[i].Id,TimeoutMs=30000,CreatedAt=now,UpdatedAt=now}).ToArray();
            db.AddRange(routes);db.AddRange(routes.Select(r=>new RouteMethod {RouteId=r.Id,EnvironmentId=env,Method="GET",NormalizedPath=r.NormalizedPath}));await Save();
            var authentication=PolicyConfigurationValidator.Normalize("authentication","{\"mode\":\"ApiKey\"}");
            var policies=Enumerable.Range(0,n).Select(i=>new Policy {Id=Id("policy",i),OrganizationId=org,ProjectId=i%2==0?null:project,Name=$"policy{i:D5}",Type="authentication",Config=authentication,CreatedAt=now,UpdatedAt=now}).ToArray();
            db.AddRange(policies);db.AddRange(routes.Select(r=>new RoutePolicyBinding {RouteId=r.Id,PolicyId=policies[0].Id,Priority=0}));await Save();
            var apps=Enumerable.Range(0,n).Select(i=>new ApplicationRecord {Id=Id("app",i),OrganizationId=org,ProjectId=i%2==0?null:project,Code=$"app{i:D5}",Name=$"Application {i:D5}",Owner="Performance",CreatedAt=now,UpdatedAt=now}).ToArray();
            var credentials=Enumerable.Range(0,n*2).Select(i=>new ApplicationCredential {Id=Id("credential",i),ApplicationId=apps[i/2].Id,AccessKey=$"ak_{i:D5}",SecretHash=ApiKeySecret.Hash(keys.CredentialSecret(i)),SecretLast4=keys.CredentialSecret(i)[^4..],ValidFrom=now,ExpiresAt=spec.WindowEnd,CreatedAt=now}).ToArray();
            var grants=Enumerable.Range(0,n*8).Select(i=>new ApplicationApiPermission {Id=Id("grant",i),ApplicationId=apps[i/8].Id,ApiId=apis[(i/8+i%8)%n].Id,EnvironmentId=env,ValidFrom=now,CreatedAt=now,CreatedBy=user}).ToArray();
            db.AddRange(apps);db.AddRange(credentials);db.AddRange(grants);await Save();
            var nodes=Enumerable.Range(0,n).Select(i=>new GatewayNode {Id=Id("node",i),EnvironmentId=env,NodeName=$"history-node-{i:D5}",InstanceId=$"history-instance-{i:D5}",AppVersion="performance-history",CurrentConfigVersion=n,CurrentDeploymentSequence=n,LastHeartbeatAt=now,IdentityHash=Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"node:{spec.Seed}:{i}")))}).ToArray();
            db.AddRange(nodes);db.AddRange(Enumerable.Range(0,n).Select(i=>new GatewayNodeEvent {GatewayNodeId=nodes[0].Id,EventType="Applied",Message=$"Performance event {i:D5}",CreatedAt=now.AddSeconds(i)}));await Save();
            var flow=new ApprovalFlow {Id=Id("flow",0),OrganizationId=org,Name="Performance approval",ScopeType="Environment"};db.Add(flow);
            db.AddRange(Enumerable.Range(1,2).Select(i=>new ApprovalStep {Id=Id("approval-step",i),FlowId=flow.Id,StepOrder=i,RoleCode="PlatformAdmin",RequiredCount=1}));await Save();
            var frozenClusters=clusters.Take(8).Select(c=>new ClusterDto(c.Id,c.ProjectId,c.EnvironmentId,c.Name,c.LoadBalancingPolicy,c.HealthCheckEnabled,c.HealthCheckPath,c.HealthCheckIntervalSec,c.Status,c.Revision,destinations.Where(d=>d.ClusterId==c.Id).Select(ClusterService.Dto).ToArray())).ToArray();
            var frozenApp=new FrozenApplication(ApplicationService.Dto(apps[0]),credentials.Take(2).Select(c=>new FrozenCredential(c.Id,c.AccessKey,c.SecretHash,c.SecretLast4,c.Status,c.ValidFrom,c.ExpiresAt,c.Revision)).ToArray(),grants.Take(8).Select(ApplicationService.Dto).ToArray());
            var candidate=new FrozenReleaseCandidate(env,org,project,0,versions.Take(8).Select(v=>v.Id).ToArray(),Enumerable.Range(0,8).Select(i=>new FrozenApiVersion(CatalogService.Dto(apis[i]),CatalogService.Dto(versions[i]),[],[])).ToArray(),routes.Take(8).Select(r=>RouteService.Dto(r)).ToArray(),frozenClusters,[new(policies[0].Id,"authentication",authentication,true,1)],routes.Take(8).Select(r=>new FrozenBinding(r.Id,policies[0].Id,0)).ToArray(),[frozenApp],versions.Take(8).Select(v=>new ResourceRevision("version",v.Id,v.Revision)).ToArray());
            var compiler=new SnapshotCompiler(new UpstreamAddressPolicy(["http://backend:8080"]));
            RuntimeSnapshot baseline=new("2.0",env,0,now,[],[],[],[]);
            var hashes=new Dictionary<string,string>();
            for(var i=0;i<n;i++) {
                var frozen=candidate with {BaselineConfigVersion=i};var compiled=compiler.Compile(frozen,baseline,i+1,now.AddSeconds(i));
                baseline=SnapshotValidator.ParsePayload(compiled.Payload.ToArray(),env);hashes[$"history-{i+1}"]=compiled.Hash;
                var config=new GatewayConfigVersion {Id=Id("config",i),EnvironmentId=env,VersionNo=i+1,Status="Published",SnapshotHash=compiled.Hash,CreatedBy=user,CreatedAt=now.AddSeconds(i),PublishedAt=now.AddSeconds(i)};
                db.Add(config);db.Add(new GatewayConfigSnapshot {ConfigVersionId=config.Id,Payload=Encoding.UTF8.GetString(compiled.Payload.ToArray()),PayloadBytes=compiled.Payload.ToArray(),SizeBytes=compiled.Size,CreatedAt=now.AddSeconds(i)});
                var release=new ReleaseRecord {Id=Id("release",i),EnvironmentId=env,ReleaseNo=$"PERF-{i:D5}",FromConfigVersion=i,ToConfigVersion=i+1,BaselineConfigVersion=i,DeploymentSequence=i+1,ReleaseType="publish",Status="Succeeded",RequestedBy=user,ApprovedBy=users[2].Id,PublishRequestedBy=user,CreatedAt=now.AddSeconds(i),CompletedAt=now.AddSeconds(i),CandidateBytes=CanonicalJson.Serialize(frozen),ApprovalPolicy=Encoding.UTF8.GetString(CanonicalJson.Serialize(new[]{new ApprovalRule(1,"PlatformAdmin",1),new ApprovalRule(2,"PlatformAdmin",1)}))};db.Add(release);
                for(var j=1;j<=2;j++)db.Add(new ApprovalTask {Id=Id("approval-task",i*2+j-1),FlowId=flow.Id,ReleaseId=release.Id,StepOrder=j,AssigneeUserId=users[j].Id,Status="Approved",Comment="Performance approval",ActedAt=now.AddSeconds(i)});
                for(var j=0;j<6;j++) {
                    db.Add(new ReleaseTarget {ReleaseId=release.Id,NodeId=nodes[j].Id,InstanceId=nodes[j].InstanceId,CreatedAt=now.AddSeconds(i)});
                    db.Add(new GatewayAck {ReleaseId=release.Id,NodeId=nodes[j].Id,InstanceId=nodes[j].InstanceId,ConfigVersion=i+1,DeploymentSequence=i+1,PayloadHash=compiled.Hash,Success=true,AppliedAt=now.AddSeconds(i)});
                }
                if((i+1)%200==0)await Save();
            }
            await Save();
            for(var i=0;i<n;i++) {
                var rule=new AlertRule {Id=Id("alert-rule",i),OrganizationId=org,ProjectId=project,EnvironmentId=env,Name=$"Rule {i:D5}",NormalizedName=$"RULE {i:D5}",Metric="request_rps",Expression="request_rps > 100",CreatedBy=user,UpdatedBy=user,CreatedAt=now,UpdatedAt=now};db.Add(rule);
                var resourceKey="Environment";var alert=new AlertEvent {Id=Id("alert-event",i),RuleId=rule.Id,RuleRevision=1,LogicRevision=1,OrganizationId=org,ProjectId=project,EnvironmentId=env,ResourceKey=resourceKey,ResourceType="Environment",ResourceId=null,OccurrenceNo=1,Message="Performance threshold",RuleSummary=rule.Expression,StartedAt=now,ConditionStartedAt=now};db.Add(alert);
                db.Add(new AlertEvaluationState {Id=Id("alert-state",i),RuleId=rule.Id,LogicRevision=1,OrganizationId=org,ProjectId=project,EnvironmentId=env,ResourceKey=resourceKey,ResourceType="Environment",ResourceId=null,LastEventId=alert.Id,NextOccurrenceNo=2,Phase="Firing"});
                db.Add(new AlertEventTransition {Id=Id("alert-transition",i),EventId=alert.Id,ToStatus="Open",Reason="Performance threshold",OccurredAt=now,CorrelationId=Id("alert-transition",i).ToString("N")});
                if((i+1)%200==0)await Save();
            }
            await Save();
            var counts=new Dictionary<string,long> {
                ["organizations"]=await db.Set<Organization>().LongCountAsync(ct),["projects"]=await db.Set<Project>().LongCountAsync(ct),["environments"]=await db.Set<EnvironmentRecord>().LongCountAsync(ct),
                ["apis"]=await db.Set<Api>().LongCountAsync(ct),["clusters"]=await db.Set<UpstreamCluster>().LongCountAsync(ct),["destinations"]=await db.Set<UpstreamDestination>().LongCountAsync(ct),["routes"]=await db.Set<ApiRoute>().LongCountAsync(ct),
                ["applications"]=await db.Set<ApplicationRecord>().LongCountAsync(ct),["credentials"]=await db.Set<ApplicationCredential>().LongCountAsync(ct),["grants"]=await db.Set<ApplicationApiPermission>().LongCountAsync(ct),["policies"]=await db.Set<Policy>().LongCountAsync(ct),
                ["users"]=await db.Set<UserRecord>().LongCountAsync(ct),["nodes"]=await db.Set<GatewayNode>().LongCountAsync(ct),["nodeEvents"]=await db.Set<GatewayNodeEvent>().LongCountAsync(ct),["snapshots"]=await db.Set<GatewayConfigSnapshot>().LongCountAsync(ct),
                ["releases"]=await db.Set<ReleaseRecord>().LongCountAsync(ct),["approvalTasks"]=await db.Set<ApprovalTask>().LongCountAsync(ct),["releaseTargets"]=await db.Set<ReleaseTarget>().LongCountAsync(ct),["acks"]=await db.Set<GatewayAck>().LongCountAsync(ct),
                ["alertRules"]=await db.Set<AlertRule>().LongCountAsync(ct),["alertEvents"]=await db.Set<AlertEvent>().LongCountAsync(ct),["alertStates"]=await db.Set<AlertEvaluationState>().LongCountAsync(ct),["alertTransitions"]=await db.Set<AlertEventTransition>().LongCountAsync(ct)
            };
            if(counts["apis"]!=n||counts["releaseTargets"]!=n*6||counts["grants"]!=n*8)throw new InvalidOperationException("Dataset cardinality mismatch.");
            await tx.CommitAsync(ct);
            var fingerprint=Convert.ToHexStringLower(SHA256.HashData(CanonicalJson.Serialize(new {spec.Scale,spec.Seed,spec.WindowStart,spec.WindowEnd,Counts=counts.OrderBy(p=>p.Key,StringComparer.Ordinal)})));
            return new(n,spec.Seed,counts,fingerprint,hashes,true);
        }finally{db.ChangeTracker.AutoDetectChangesEnabled=oldDetection;db.ChangeTracker.Clear();}
    }
}
