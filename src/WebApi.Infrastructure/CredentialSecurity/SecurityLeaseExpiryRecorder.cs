using Microsoft.EntityFrameworkCore;
using WebApi.Infrastructure.Persistence;

namespace WebApi.Infrastructure.CredentialSecurity;

// Call only after validating the signed issuance while holding the cooperative
// governance/environment locks. The immutable observation and the caller's
// transaction commit are inseparable; failed commits are not observations.
internal static class SecurityLeaseExpiryRecorder
{
    // Callers hold the shared governance lock. A recorded observation was
    // stamped by PostgreSQL itself and establishes a DB-wide lower time bound.
    public static async Task<bool> HasLaterObservationAsync(WebApiDbContext db, DateTimeOffset now, CancellationToken ct)
        => await db.Set<WebApi.Infrastructure.Persistence.Entities.GatewaySecurityLeaseExpiryObservation>()
            .AsNoTracking().AnyAsync(x => x.ObservedAt > now, ct)
            || await db.Set<WebApi.Infrastructure.Persistence.Entities.GatewaySecurityLeaseIssuance>()
                .AsNoTracking().AnyAsync(x => x.IssuedAt > now, ct)
            || await db.Set<WebApi.Infrastructure.Persistence.Entities.GatewaySecurityCurrentReceipt>()
                .AsNoTracking().AnyAsync(x => x.ReceivedAt > now, ct)
            || await db.Set<WebApi.Infrastructure.Persistence.Entities.SecurityAckVerification>()
                .AsNoTracking().AnyAsync(x => x.ReceivedAt > now, ct)
            || await db.Set<WebApi.Infrastructure.Persistence.Entities.SecurityAckRevalidationChallenge>()
                .AsNoTracking().AnyAsync(x => x.CreatedAt > now, ct);

    public static Task<int> RecordAsync(WebApiDbContext db, Guid leaseId, DateTimeOffset observedAt, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Lease expiry observation requires a transaction.");
        return db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO public.gateway_security_lease_expiry_observations(lease_id,observed_at) VALUES({leaseId},{observedAt}) ON CONFLICT (lease_id) DO NOTHING", ct);
    }
}
