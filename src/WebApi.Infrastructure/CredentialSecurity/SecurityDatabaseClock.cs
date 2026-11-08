using Microsoft.EntityFrameworkCore;
using Npgsql;
using WebApi.Contracts.Common;
using WebApi.Infrastructure.Persistence;

namespace WebApi.Infrastructure.CredentialSecurity;

// The SQL function advances a server-stamped watermark under the shared
// governance lock. Its update commits or rolls back with the caller's work;
// it does not attest external wall-clock accuracy or restore provenance.
public static class SecurityDatabaseClock
{
    public static async Task<DateTimeOffset> ObserveAsync(WebApiDbContext db, CancellationToken ct = default)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Security database time requires a locked transaction.");
        try
        {
            return await db.Database.SqlQueryRaw<DateTimeOffset>("SELECT public.security_observe_clock() AS \"Value\"").SingleAsync(ct);
        }
        catch (PostgresException error) when (error.SqlState is "42883" or "42P01" ||
            error.SqlState == "23514" && error.MessageText.StartsWith("security_database_clock_", StringComparison.Ordinal))
        {
            throw new ApiException(503, "security_clock_unavailable", "安全时间不可用或已回拨。");
        }
    }
}
