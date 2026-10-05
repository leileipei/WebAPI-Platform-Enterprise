using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
namespace WebApi.Infrastructure.Sso;
public sealed class SsoAttemptCleanupService(WebApiDbContext db)
{
    public Task<int> CleanupAsync(DateTimeOffset now,CancellationToken ct=default)
    {
        ct.ThrowIfCancellationRequested();var cutoff=now.ToUniversalTime().AddHours(-24);
        var expired=db.Set<SsoLoginAttempt>().Where(x=>x.ExpiresAt<=cutoff).OrderBy(x=>x.ExpiresAt).ThenBy(x=>x.Id).Take(1000).Select(x=>x.Id);
        return db.Set<SsoLoginAttempt>().Where(x=>expired.Contains(x.Id)&&x.ExpiresAt<=cutoff).ExecuteDeleteAsync(ct);
    }
}
