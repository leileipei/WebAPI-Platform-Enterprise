using System.Data;
using Microsoft.EntityFrameworkCore;

namespace WebApi.Infrastructure.Persistence;

/// <summary>
/// Keeps existing public table rows stable while a caller-owned migration transaction is open.
/// This does not replace external isolation of old processes, credentials, or privileged roles.
/// </summary>
public static class GovernanceMigrationWriteFence
{
    public static async Task AcquireHistoryAsync(WebApiDbContext db, CancellationToken ct = default)
    {
        if (db.Database.CurrentTransaction is null || db.Database.GetDbConnection().State != ConnectionState.Open)
            throw new InvalidOperationException("Migration write fence requires an open connection and active transaction.");

        await db.Database.ExecuteSqlRawAsync("""
            DO $WEBAPI_HISTORY_FENCE$
            DECLARE timeout_ms integer;
            BEGIN
                SELECT setting::integer INTO timeout_ms FROM pg_catalog.pg_settings WHERE name='lock_timeout';
                IF timeout_ms=0 OR timeout_ms>5000 THEN
                    PERFORM pg_catalog.set_config('lock_timeout','5s',true);
                END IF;
                IF pg_catalog.to_regclass('public."__EFMigrationsHistory"') IS NOT NULL THEN
                    EXECUTE 'LOCK TABLE ONLY public."__EFMigrationsHistory" IN SHARE MODE';
                END IF;
            END $WEBAPI_HISTORY_FENCE$;
            """, ct);
    }

    public static async Task AcquireAsync(WebApiDbContext db, CancellationToken ct = default)
    {
        if (db.Database.CurrentTransaction is null || db.Database.GetDbConnection().State != ConnectionState.Open)
            throw new InvalidOperationException("Migration write fence requires an open connection and active transaction.");

        await db.Database.ExecuteSqlRawAsync("""
            DO $WEBAPI_TABLE_FENCE$
            DECLARE target record; timeout_ms integer;
            BEGIN
                SELECT setting::integer INTO timeout_ms FROM pg_catalog.pg_settings WHERE name='lock_timeout';
                IF timeout_ms=0 OR timeout_ms>5000 THEN
                    PERFORM pg_catalog.set_config('lock_timeout','5s',true);
                END IF;
                FOR target IN
                    SELECT n.nspname, c.relname
                    FROM pg_catalog.pg_class c
                    JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
                    WHERE n.nspname='public' AND c.relkind IN ('r','p')
                    ORDER BY c.relname COLLATE "C"
                LOOP
                    EXECUTE pg_catalog.format('LOCK TABLE ONLY %I.%I IN SHARE MODE',target.nspname,target.relname);
                END LOOP;
            END $WEBAPI_TABLE_FENCE$;
            """, ct);
    }
}
