using Microsoft.EntityFrameworkCore;

namespace WebApi.Infrastructure.Persistence;

/// <summary>Reads an absent migration history without leaving a PostgreSQL transaction aborted.</summary>
public static class GovernanceMigrationHistory
{
    public static async Task<bool> ExistsAsync(WebApiDbContext db, CancellationToken ct = default) =>
        await db.Database.SqlQueryRaw<bool>("SELECT pg_catalog.to_regclass('public.\"__EFMigrationsHistory\"') IS NOT NULL AS \"Value\"").SingleAsync(ct);

    public static async Task<string[]> ReadAppliedAsync(WebApiDbContext db, CancellationToken ct = default) =>
        await ExistsAsync(db, ct) ? (await db.Database.GetAppliedMigrationsAsync(ct)).ToArray() : [];
}
