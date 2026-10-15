using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.CredentialUsage;
using WebApi.Contracts.Runtime;
using WebApi.Domain.Runtime;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Runtime;
namespace WebApi.Infrastructure.CredentialUsage;

public sealed class UsageSnapshotProofReader(WebApiDbContext db)
{
    public async Task ValidateAsync(Guid environmentId,IReadOnlyList<UsageItem> items,CancellationToken ct=default)
    {
        var sources=items.Select(x=>x.Source).Distinct().ToArray();if(sources.Length>2)throw Invalid();if(sources.Length==0)return;
        var releaseIds=sources.Select(s=>s.ReleaseId).Distinct().ToArray();
        var rows=await (from release in db.Set<ReleaseRecord>().AsNoTracking()
            join version in db.Set<GatewayConfigVersion>().AsNoTracking() on new{release.EnvironmentId,VersionNo=release.ToConfigVersion} equals new{version.EnvironmentId,version.VersionNo}
            join snapshot in db.Set<GatewayConfigSnapshot>().AsNoTracking() on version.Id equals snapshot.ConfigVersionId
            where release.EnvironmentId==environmentId&&releaseIds.Contains(release.Id)&&release.DeploymentSequence>0
            select new{ReleaseId=release.Id,version.VersionNo,Sequence=release.DeploymentSequence!.Value,Hash=version.SnapshotHash,snapshot.SizeBytes,snapshot.PayloadBytes}).ToArrayAsync(ct);
        var parsed=new Dictionary<UsageSource,RuntimeSnapshot>();
        foreach(var source in sources)
        {
            var row=rows.SingleOrDefault(x=>x.ReleaseId==source.ReleaseId&&x.VersionNo==source.ConfigVersion&&x.Sequence==source.DeploymentSequence&&x.Hash==source.PayloadHash)??throw Invalid();
            try{RedisSnapshotStore.Validate(environmentId,new(source.ReleaseId,source.DeploymentSequence,source.ConfigVersion,source.PayloadHash,row.SizeBytes),row.PayloadBytes);parsed[source]=SnapshotValidator.ParsePayload(row.PayloadBytes,environmentId);}
            catch(Exception error) when(error is InvalidDataException or ApiException or ArgumentException){throw Invalid();}
        }
        // Build all lookup sets once per immutable source; never read mutable application/credential/grant rows.
        foreach(var source in sources)
        {
            var snapshot=parsed[source];var apps=snapshot.Applications.ToDictionary(a=>a.Id);var apiKeys=snapshot.Routes.Where(r=>r.RequireApiKey).Select(r=>r.ApiId).ToHashSet();
            var credentials=snapshot.Applications.SelectMany(a=>a.Credentials.Select(c=>(Application:a.Id,Credential:c))).ToLookup(x=>(x.Application,x.Credential.Id));
            var grants=snapshot.Applications.SelectMany(a=>a.Permissions.Select(p=>(a.Id,p.ApiId,p.ValidFrom,p.ExpiresAt))).ToHashSet();
            foreach(var item in items.Where(i=>i.Source==source))
            {
                if(!apiKeys.Contains(item.ApiId)||!apps.TryGetValue(item.ApplicationId,out var app)||app.Status!="Active")throw Invalid();
                var matches=credentials[(item.ApplicationId,item.CredentialId)].ToArray();if(matches.Length!=1)throw Invalid();var credential=matches[0].Credential;
                if(credential.Status!="Active"||item.FirstObservedAt<credential.ValidFrom||item.LastObservedAt>=credential.ExpiresAt||!grants.Contains((item.ApplicationId,item.ApiId,item.GrantValidFrom,item.GrantExpiresAt)))throw Invalid();
            }
        }
    }
    private static ApiException Invalid()=>new(422,"invalid_usage_source","统计来源、凭证或冻结API授权窗口无法验证。");
}
