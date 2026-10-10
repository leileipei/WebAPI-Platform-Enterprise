using Microsoft.EntityFrameworkCore;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Delivery;
public sealed class DeliveryLockCoordinator(WebApiDbContext db)
{
    private void RequireTransaction(){if(db.Database.CurrentTransaction is null)throw new InvalidOperationException("Delivery locks require the business transaction.");}
    public async Task LockProjectAsync(Guid projectId,CancellationToken ct)
    {RequireTransaction();await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(8901202)",ct);await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM projects WHERE id={projectId} FOR UPDATE",ct);}
    public async Task LockEnvironmentsAsync(IReadOnlyCollection<Guid> environmentIds,CancellationToken ct)
    {RequireTransaction();foreach(var id in environmentIds.Distinct().Order())await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM environments WHERE id={id} FOR UPDATE",ct);}
    public async Task LockAsync(Guid sourceEnvironmentId,Guid targetEnvironmentId,CancellationToken ct)
    {
        RequireTransaction();await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(8901202)",ct);
        var ids=new[]{sourceEnvironmentId,targetEnvironmentId};var projects=await db.Set<EnvironmentRecord>().Where(e=>ids.Contains(e.Id)).Select(e=>e.ProjectId).Distinct().OrderBy(id=>id).ToArrayAsync(ct);
        foreach(var project in projects)await LockProjectAsync(project,ct);await LockEnvironmentsAsync(ids,ct);
    }
}
