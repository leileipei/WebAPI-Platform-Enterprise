using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using WebApi.Infrastructure.Persistence;
using Xunit;

namespace WebApi.Integration.Tests.Support;

// Test-only command interception makes rollback tests deterministic without
// relying on a shadowable function in the production search path.
internal sealed class FrozenSecurityClockFixture : DbCommandInterceptor
{
    private DateTimeOffset? instant;
    public int HitCount { get; private set; }
    public void Freeze(DateTimeOffset value) => instant = value;

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        if (instant is { } value && (command.CommandText.Contains("pg_catalog.clock_timestamp()", StringComparison.Ordinal) ||
            command.CommandText.Contains("public.security_observe_clock()", StringComparison.Ordinal)))
        {
            command.CommandText = command.CommandText.Replace("pg_catalog.clock_timestamp()", "@test_frozen_security_clock", StringComparison.Ordinal);
            // Synthetic-time scenarios bypass the database watermark function;
            // its real transactional behavior has separate PostgreSQL tests.
            command.CommandText = command.CommandText.Replace("public.security_observe_clock()", "@test_frozen_security_clock", StringComparison.Ordinal);
            var parameter = command.CreateParameter();
            parameter.ParameterName = "test_frozen_security_clock";
            parameter.Value = value;
            command.Parameters.Add(parameter);
            HitCount++;
        }
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    // Used only by the adversarial search-path case in its disposable DB.
    public static async Task InstallShadowAsync(WebApiDbContext db, DateTimeOffset instant)
    {
        await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlRawAsync("CREATE TABLE public.test_security_clock (observed_at timestamptz NOT NULL)");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO public.test_security_clock(observed_at) VALUES({instant})");
        await db.Database.ExecuteSqlRawAsync("CREATE FUNCTION public.clock_timestamp() RETURNS timestamptz LANGUAGE sql VOLATILE AS 'SELECT observed_at FROM public.test_security_clock'; SET search_path=public,pg_catalog");
        var actual = await db.Database.SqlQueryRaw<DateTimeOffset>("SELECT clock_timestamp() AS \"Value\"").SingleAsync();
        Assert.Equal(instant, actual);
    }
}
