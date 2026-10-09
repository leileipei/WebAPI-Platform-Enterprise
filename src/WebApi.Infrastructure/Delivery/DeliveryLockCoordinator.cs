using Microsoft.EntityFrameworkCore;
using WebApi.Infrastructure.Persistence;
namespace WebApi.Infrastructure.Delivery;
public sealed class DeliveryLockCoordinator(WebApiDbContext db)
{
 public async Task LockAsync(Guid sourceEnvironmentId,Guid targetEnvironmentId,CancellationToken ct)
 {if(db.Database.CurrentTransaction is null)throw new InvalidOperationException("Delivery locks require the business transaction.");await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(8901202)",ct);foreach(var id in new[]{sourceEnvironmentId,targetEnvironmentId}.Distinct().Order())await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM environments WHERE id={id} FOR UPDATE",ct);}
}
