using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using WebApi.Contracts.Common;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Persistence;
public sealed record AuditRequestMetadata(System.Net.IPAddress? Ip);
public sealed class AuditedCommandExecutor(WebApiDbContext db,AuditRequestMetadata? metadata=null)
{
    private static readonly HashSet<string> fields=["Id","Code","Name","Status","DisplayName","Revision","EnvironmentId","ProjectId","OrganizationId","Version","Path","NormalizedPath","Methods","Enabled","TimeoutMs","ApplicationId","ApiId","ConfigVersion","DeploymentSequence","ReleaseNo","RoleId","UserId","PermissionId","AccessMode","ValidFrom","ExpiresAt","ReleaseId","ApiVersionId","ClusterId","CreatedBy","AssigneeUserId","StepOrder","ActedAt","Priority","Weight","OwnerUserId","LifecycleStatus","ReleaseType","RollbackOf","RecoveryOf","FromConfigVersion","ToConfigVersion","DeadlineAt","FailureCode"];
    public async Task<T> ExecuteAsync<T>(ActorContext actor,ScopeRef scope,string action,Func<WebApiDbContext,CancellationToken,Task<T>> command,CancellationToken cancellationToken=default)
    {
        var owned=db.Database.CurrentTransaction is null?await db.Database.BeginTransactionAsync(cancellationToken):null;
        try
        {
            // All authorization-changing commands and protected writes share this transaction lock.
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(8901202)",cancellationToken);
            if(!await db.Set<UserRecord>().AsNoTracking().AnyAsync(x=>x.Id==actor.UserId && x.Status=="Active",cancellationToken)) throw new ApiException(401,"inactive_session","会话已失效。");
            var value=await command(db,cancellationToken);
            db.ChangeTracker.DetectChanges();
            var changes=db.ChangeTracker.Entries().Where(e=>e.Entity is not AuditLog && e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).ToArray();
            var first=changes.FirstOrDefault();
            if(changes.Length>0) db.Add(new AuditLog {
                UserId=actor.UserId,OrganizationId=scope.OrganizationId==Guid.Empty?null:scope.OrganizationId,ProjectId=scope.ProjectId,EnvironmentId=scope.EnvironmentId,
                Action=action,ResourceType=first?.Metadata.ClrType.Name??action.Split('.')[0],
                ResourceId=first?.Properties.FirstOrDefault(p=>p.Metadata.IsPrimaryKey())?.CurrentValue?.ToString()??"",
                BeforeJson=JsonSerializer.Serialize(changes.Where(e=>e.State!=EntityState.Added).Select(e=>Capture(e,true))),
                AfterJson=JsonSerializer.Serialize(changes.Where(e=>e.State!=EntityState.Deleted).Select(e=>Capture(e,false))),TraceId=actor.TraceId,Ip=metadata?.Ip
            });
            await db.SaveChangesAsync(cancellationToken);
            if(owned is not null) await owned.CommitAsync(cancellationToken);
            return value;
        }
        catch {if(owned is not null) await owned.RollbackAsync(CancellationToken.None);throw;}
        finally {if(owned is not null) await owned.DisposeAsync();}
    }
    private static object Capture(EntityEntry e,bool original) => new {Type=e.Metadata.ClrType.Name,Fields=e.Properties.Where(p=>fields.Contains(p.Metadata.Name)).ToDictionary(p=>p.Metadata.Name,p=>original?p.OriginalValue:p.CurrentValue)};
}
