using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Releases;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Security;
namespace WebApi.Infrastructure.Releases;
public sealed class ReleaseAccessContextService(WebApiDbContext db,AuthorizationService auth,ScopeResolver scopes)
{
    public async Task<ReleaseAccessContext> CaptureAsync(ReleaseRecord release,EnvironmentRecord environment,CancellationToken ct=default)
    {
        if(db.Database.CurrentTransaction is null||release.EnvironmentId!=environment.Id)throw new InvalidOperationException("Entry capture requires the release environment transaction.");
        var existing=db.Set<ReleaseAccessContext>().Local.SingleOrDefault(x=>x.ReleaseId==release.Id)??await db.Set<ReleaseAccessContext>().SingleOrDefaultAsync(x=>x.ReleaseId==release.Id,ct);if(existing is not null)return existing;
        var context=new ReleaseAccessContext{ReleaseId=release.Id,EnvironmentId=environment.Id,AccessAddressRevision=environment.AccessAddressRevision,PublicOrigin=environment.GatewayPublicUrl,InternalOrigin=environment.GatewayInternalUrl,BasePath=environment.BasePath};db.Add(context);return context;
    }
    public async Task<ReleaseAccessContextDto?> ReadAsync(Guid releaseId,ActorContext actor,CancellationToken ct=default)
    {
        var r=await db.Set<ReleaseRecord>().AsNoTracking().SingleOrDefaultAsync(x=>x.Id==releaseId,ct)??throw ScopeResolver.Missing();var scope=await scopes.EnvironmentAsync(r.EnvironmentId,ct);
        if(!await auth.CanAsync(actor,"release.read",new("release",releaseId,scope),ct))throw ScopeResolver.Missing();return await ProjectAsync(r,actor,ct);
    }
    internal async Task<ReleaseAccessContextDto?> ProjectAsync(ReleaseRecord r,ActorContext? actor,CancellationToken ct)
    {
        var context=db.Set<ReleaseAccessContext>().Local.SingleOrDefault(x=>x.ReleaseId==r.Id)??await db.Set<ReleaseAccessContext>().AsNoTracking().SingleOrDefaultAsync(x=>x.ReleaseId==r.Id,ct);if(context is null)return null;
        var showInternal=actor is not null&&await auth.CanAsync(actor,"environment.write",new("environment",r.EnvironmentId,await scopes.EnvironmentAsync(r.EnvironmentId,ct)),ct);
        return new(context.ReleaseId,context.EnvironmentId,context.AccessAddressRevision,context.PublicOrigin,context.BasePath,context.CapturedAt,showInternal?context.InternalOrigin:null,r.RecoveryOf);
    }
}
