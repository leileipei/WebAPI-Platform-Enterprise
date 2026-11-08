using WebApi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using WebApi.Contracts.Common;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Security;
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
        // The composite (application_id, organization_id) delivery keys on credentials
        // and permission grants are immediate and non-deferrable, so moving applications
        // to other organizations would violate them mid-statement. Suspend both for the
        // scene and re-add them before commit: the re-add revalidates the final state.
        var deliveryFk=await db.Database.SqlQueryRaw<string>("SELECT con.conname AS \"Value\" FROM pg_constraint con JOIN pg_attribute a1 ON a1.attrelid=con.conrelid AND a1.attnum=(con.conkey)[1] JOIN pg_attribute a2 ON a2.attrelid=con.conrelid AND a2.attnum=(con.conkey)[2] WHERE con.contype='f' AND con.conrelid='public.application_credentials'::regclass AND con.confrelid='public.applications'::regclass AND a1.attname='application_id' AND a2.attname='organization_id'").SingleAsync(ct);
        var grantFk=await db.Database.SqlQueryRaw<string>("SELECT con.conname AS \"Value\" FROM pg_constraint con JOIN pg_attribute a1 ON a1.attrelid=con.conrelid AND a1.attnum=(con.conkey)[1] JOIN pg_attribute a2 ON a2.attrelid=con.conrelid AND a2.attnum=(con.conkey)[2] WHERE con.contype='f' AND con.conrelid='public.application_api_permissions'::regclass AND con.confrelid='public.applications'::regclass AND a1.attname='application_id' AND a2.attname='organization_id'").SingleAsync(ct);
        if(!System.Text.RegularExpressions.Regex.IsMatch(deliveryFk,"^[A-Za-z0-9_~]+$")||!System.Text.RegularExpressions.Regex.IsMatch(grantFk,"^[A-Za-z0-9_~]+$"))throw new InvalidOperationException("Unexpected delivery constraint name.");
#pragma warning disable EF1003 // Constraint names are read from the system catalog and character-set validated above.
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE application_credentials DROP CONSTRAINT \""+deliveryFk+"\"", ct);
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE application_api_permissions DROP CONSTRAINT \""+grantFk+"\"", ct);
#pragma warning restore EF1003
        // The permission guard trigger allows scope moves only between LegacyApi grants
        // and bumps the revision on every update; flip the mode while applications are
        // still in their original organizations, then reload the grants so the
        // concurrency token matches.
        await db.Set<ApplicationApiPermission>().ExecuteUpdateAsync(setters=>setters.SetProperty(g=>g.Mode,"LegacyApi"),ct);
        var grants=await db.Set<ApplicationApiPermission>().ToArrayAsync(ct);
        // Initialize a separate scene: all fifty projects have both shared and owned
        // resources. Historical snapshots retain their original frozen configuration.
        var applications=await db.Set<ApplicationRecord>().AsNoTracking().ToDictionaryAsync(a=>a.Id,ct);
        var policies=await db.Set<Policy>().ToDictionaryAsync(p=>p.Id,ct);
        var slots=new Dictionary<Guid,int>();
        for(var i=0;i<spec.Scale;i++) {
            var slot=i%50;var shared=(i/50)%2==0;slots[Id("app",i)]=slot;
            var application=applications[Id("app",i)];application.OrganizationId=Id("organization",slot/5);application.ProjectId=shared?null:Id("project",slot);
            var policy=policies[Id("policy",i)];policy.OrganizationId=Id("organization",slot/5);policy.ProjectId=shared?null:Id("project",slot);
        }
        // This isolated scene reassigns seeded applications before validating grants.
        // OrganizationId participates in the delivery model's alternate key, so use
        // set-based initialization rather than modifying a tracked principal key.
        var appIndex=new Dictionary<Guid,int>();for(var i=0;i<spec.Scale;i++)appIndex[Id("app",i)]=i;
        foreach(var group in applications.Values.GroupBy(a=>new{a.OrganizationId,a.ProjectId})) {
            var ids=group.Select(a=>a.Id).ToArray();var organization=group.Key.OrganizationId;var project=group.Key.ProjectId;
            await db.Set<ApplicationRecord>().Where(a=>ids.Contains(a.Id)).ExecuteUpdateAsync(setters=>setters
                .SetProperty(a=>a.OrganizationId,organization).SetProperty(a=>a.ProjectId,project),ct);
            // The governance guard makes a credential's (application, organization,
            // project, environment, mode) binding immutable, so an application that
            // changes organization gets new credential rows in the new scope.
            var pairs=ids.Select(appId=>{var gi=appIndex[appId];return (appId,new[]{Id("credential",gi*2),Id("credential",gi*2+1)},new[]{Id("scope-credential",gi*2),Id("scope-credential",gi*2+1)});}).ToArray();
            var movedCredentials=pairs.SelectMany(p=>p.Item2).ToArray();
            await db.Set<ApplicationCredential>().Where(c=>movedCredentials.Contains(c.Id)).ExecuteDeleteAsync(ct);
            foreach(var (appId,_,credentialIds) in pairs)for(var k=0;k<credentialIds.Length;k++){
                var serial=appIndex[appId]*2+k;
                db.Add(new ApplicationCredential{Id=credentialIds[k],ApplicationId=appId,OrganizationId=organization,AccessKey=$"ak_sc_{serial:D5}",SecretHash=ApiKeySecret.Hash($"scope:{spec.Seed}:{serial}"),SecretLast4=$"scope:{spec.Seed}:{serial}"[^4..],ValidFrom=now,ExpiresAt=spec.WindowEnd,CreatedAt=now});
            }
        }
        db.AddRange(Enumerable.Range(0,400).Select(i=>new Api {Id=Id("scope-api",i),OrganizationId=Id("organization",i/8/5),ProjectId=Id("project",i/8),Code=$"scope-api{i:D3}",Name=$"Scope API {i:D3}",LifecycleStatus="Active",OwnerUserId=Id("user",0),CreatedAt=now,UpdatedAt=now}));
        db.AddRange(Enumerable.Range(0,400).Select(i=>new ApiVersion {Id=Id("scope-version",i),ApiId=Id("scope-api",i),Version="1.0.0",Status="Active",ChangeType="compatible",CreatedBy=Id("user",0),CreatedAt=now}));
        await db.SaveChangesAsync(ct);db.ChangeTracker.Clear();
        // (id, environment_id, api_id) is an alternate key, so the grant move is
        // a set-based update; the LegacyApi guard rewrites the scope shadows.
#pragma warning disable EF1003 // The statement below is fixed SQL text; every value travels as a positional uuid parameter.
        foreach(var group in grants.GroupBy(g=>g.ApplicationId)) {
            var slot=slots[group.Key];
            var rows=group.OrderBy(g=>g.Id).Select((grant,index)=>(Id:grant.Id,Api:Id("scope-api",slot*8+index),Environment:Id("environment",slot*3+index%3))).ToArray();
            if(rows.Length!=8)throw new InvalidOperationException("Scope fixture grants differ from the fixed cardinality.");
            var args=rows.SelectMany(r=>new[]{r.Id.ToString(),r.Api.ToString(),r.Environment.ToString()}).ToArray();
            var apiCase=string.Join(" ",rows.Select((r,i)=>"WHEN {"+(i*3)+"}::uuid THEN {"+(i*3+1)+"}::uuid"));
            var envCase=string.Join(" ",rows.Select((r,i)=>"WHEN {"+(i*3)+"}::uuid THEN {"+(i*3+2)+"}::uuid"));
            var ids=string.Join(",",rows.Select((r,i)=>"{"+(i*3)+"}::uuid"));
            await db.Database.ExecuteSqlRawAsync("UPDATE application_api_permissions SET api_id=CASE id "+apiCase+" END, environment_id=CASE id "+envCase+" END WHERE id IN ("+ids+")",args);
        }
#pragma warning restore EF1003
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
        // The governance permission triggers are initially deferred, so the grant
        // updates queued constraint events that block a table ALTER until commit;
        // drain them now so the re-Adds below can validate the final state.
        await db.Database.ExecuteSqlRawAsync("SET CONSTRAINTS ALL IMMEDIATE", ct);
#pragma warning disable EF1003 // Same catalog-derived, validated identifiers as the drops above.
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE application_credentials ADD CONSTRAINT \""+deliveryFk+"\" FOREIGN KEY (application_id, organization_id) REFERENCES applications (id, organization_id) ON DELETE RESTRICT", ct);
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE application_api_permissions ADD CONSTRAINT \""+grantFk+"\" FOREIGN KEY (application_id, organization_id) REFERENCES applications (id, organization_id) ON DELETE RESTRICT", ct);
#pragma warning restore EF1003
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
