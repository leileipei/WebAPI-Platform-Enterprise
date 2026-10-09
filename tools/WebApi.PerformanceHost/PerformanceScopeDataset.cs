using WebApi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using WebApi.Contracts.Common;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.PerformanceHost;
public sealed record ScopeActor(string Kind,string Username,Guid OrganizationId,int LoginStatus,int ApplicationStatus,IReadOnlyList<Guid> ApplicationIds,int PolicyStatus,IReadOnlyList<Guid> PolicyIds);
public sealed record ScopeSceneManifest(int Scale,string BusinessFingerprint,IReadOnlyList<ScopeActor> Actors,bool Validated);
public static partial class PerformanceDataset
{
    public static async Task<ScopeSceneManifest> CreateScopeSceneAsync(WebApiDbContext db,DatasetSpec spec,string directory,CancellationToken ct)
    {
        var initial=await CreateAsync(db,spec,directory,ct);
        Guid Id(string kind,int index)=>ResourceId(spec.Seed,kind,index);
        var now=spec.WindowStart;
        await using var transaction=await db.Database.BeginTransactionAsync(ct);
        // Initialize a separate scene: all fifty projects have both shared and owned
        // resources. Historical snapshots retain their original frozen configuration.
        var applications=await db.Set<ApplicationRecord>().ToDictionaryAsync(a=>a.Id,ct);
        var policies=await db.Set<Policy>().ToDictionaryAsync(p=>p.Id,ct);
        var slots=new Dictionary<Guid,int>();
        for(var i=0;i<spec.Scale;i++) {
            var slot=i%50;var shared=(i/50)%2==0;slots[Id("app",i)]=slot;
            var application=applications[Id("app",i)];application.OrganizationId=Id("organization",slot/5);application.ProjectId=shared?null:Id("project",slot);
            var policy=policies[Id("policy",i)];policy.OrganizationId=Id("organization",slot/5);policy.ProjectId=shared?null:Id("project",slot);
        }
        db.AddRange(Enumerable.Range(0,400).Select(i=>new Api {Id=Id("scope-api",i),OrganizationId=Id("organization",i/8/5),ProjectId=Id("project",i/8),Code=$"scope-api{i:D3}",Name=$"Scope API {i:D3}",LifecycleStatus="Active",OwnerUserId=Id("user",0),CreatedAt=now,UpdatedAt=now}));
        db.AddRange(Enumerable.Range(0,400).Select(i=>new ApiVersion {Id=Id("scope-version",i),ApiId=Id("scope-api",i),Version="1.0.0",Status="Active",ChangeType="compatible",CreatedBy=Id("user",0),CreatedAt=now}));
        var grants=await db.Set<ApplicationApiPermission>().ToArrayAsync(ct);
        foreach(var group in grants.GroupBy(g=>g.ApplicationId)) {
            var slot=slots[group.Key];var index=0;
            foreach(var grant in group.OrderBy(g=>g.Id)){grant.ApiId=Id("scope-api",slot*8+index);grant.EnvironmentId=Id("environment",slot*3+index%3);index++;}
            if(index!=8)throw new InvalidOperationException("Scope fixture grants differ from the fixed cardinality.");
        }
        var organizationRole=new Role {Id=Id("scope-role",0),Code="ScopeOrgReader",Name="Scope organization reader",OrganizationId=Id("organization",1),CreatedAt=now};db.Add(organizationRole);
        foreach(var permission in await db.Set<Permission>().Where(p=>p.Code=="app.read"||p.Code=="policy.read").ToArrayAsync(ct))db.Add(new RolePermission {RoleId=organizationRole.Id,PermissionId=permission.Id});
        var replacementUsers=new[]{Id("user",1),Id("user",4),Id("user",8)};
        db.RemoveRange(await db.Set<UserRole>().Where(r=>replacementUsers.Contains(r.UserId)).ToArrayAsync(ct));
        db.Add(new UserRole {UserId=Id("user",1),RoleId=organizationRole.Id});db.Add(new UserRole {UserId=Id("user",8),RoleId=organizationRole.Id});
        var actorUserIds=Enumerable.Range(0,9).Select(i=>Id("user",i)).ToArray();
        var users=await db.Set<UserRecord>().Where(u=>actorUserIds.Contains(u.Id)).ToArrayAsync(ct);
        var passwordHash=users.Single(u=>u.Id==Id("user",0)).PasswordHash;
        foreach(var user in users)if(user.Id!=Id("user",0))user.PasswordHash=passwordHash;
        users.Single(u=>u.Id==Id("user",6)).Status="Disabled";
        var inactiveOrganization=Id("organization",2);
        (await db.Set<Organization>().SingleAsync(o=>o.Id==inactiveOrganization,ct)).Status="Inactive";
        foreach(var i in new[]{1,2,3,4,6,7,8})db.Add(new UserProjectScope {Id=Id("scope-actor",i),UserId=Id("user",i),OrganizationId=Id("organization",i==7?2:i==8?3:1),ProjectId=i is 2 or 3?Id("project",6):null,EnvironmentId=i==3?Id("environment",19):null,AccessMode="read_only"});
        await db.SaveChangesAsync(ct);db.ChangeTracker.Clear();
        var invalid=await (from grant in db.Set<ApplicationApiPermission>() join app in db.Set<ApplicationRecord>() on grant.ApplicationId equals app.Id join api in db.Set<Api>() on grant.ApiId equals api.Id join env in db.Set<EnvironmentRecord>() on grant.EnvironmentId equals env.Id where app.OrganizationId!=api.OrganizationId||app.ProjectId!=null&&app.ProjectId!=api.ProjectId||env.ProjectId!=api.ProjectId select grant.Id).AnyAsync(ct);
        if(invalid||applications.Count!=spec.Scale||policies.Count!=spec.Scale)throw new InvalidOperationException("Scope fixture relation validation failed.");
        await transaction.CommitAsync(ct);
        var kinds=new[]{"platform-role","organization-role","project-read-only","environment-read-only","no-function","no-scope","inactive-user","inactive-ancestor","cross-organization-role"};
        var actors=Enumerable.Range(0,9).Select(actor=>{
            bool ApplicationVisible(int i)=>actor switch {0=>i%50/5!=2,1=>i%50/5==1,2=>i%50==6&&(i/50)%2!=0,_=>false};
            bool PolicyVisible(int i)=>actor switch {0 or 1=>i%50/5==1,2=>i%50/5==1&&((i/50)%2==0||i%50==6),3=>i%50/5==1&&(i/50)%2==0,_=>false};
            return new ScopeActor(kinds[actor],actor==0?"performance_admin":$"perf_user_{actor:D5}",Id("organization",actor==7?2:actor==8?3:1),actor==6?401:200,actor==6?401:200,Enumerable.Range(0,spec.Scale).Where(ApplicationVisible).Select(i=>Id("app",i)).ToArray(),actor==6?401:actor<=3?200:404,Enumerable.Range(0,spec.Scale).Where(PolicyVisible).Select(i=>Id("policy",i)).ToArray());
        }).ToArray();
        var fingerprint=Convert.ToHexStringLower(SHA256.HashData(CanonicalJson.Serialize(new {initial.BusinessFingerprint,actors,organizations=10,projects=50,environments=150,applications=spec.Scale,policies=spec.Scale,scopeApis=400})));
        return new(spec.Scale,fingerprint,actors,true);
    }
}
