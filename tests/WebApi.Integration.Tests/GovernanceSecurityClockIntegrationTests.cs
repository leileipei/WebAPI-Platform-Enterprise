using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using WebApi.Integration.Tests.Support;
using Xunit;

namespace WebApi.Integration.Tests;

public sealed class GovernanceSecurityClockIntegrationTests
{
    [Theory]
    [InlineData("issuance")]
    [InlineData("horizon")]
    [InlineData("ack")]
    public async Task UpgradeRetainsFutureLegacyEvidenceAndStartsFailClosed(string kind)
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        await using var db = database.Context();
        await db.GetService<IMigrator>().MigrateAsync("20261104070000_Governance24LeaseExpiryObservation");
        var lease = Guid.NewGuid(); var node = Guid.NewGuid(); var instance = Guid.NewGuid(); var environment = Guid.NewGuid();
        var organization = Guid.NewGuid(); var project = Guid.NewGuid(); var generation = Guid.NewGuid();
        var hash = new string('a', 64); var payloadHash = new string('b', 64);
        var future = DateTimeOffset.UtcNow.AddYears(1); future = future.AddTicks(-(future.Ticks % 10));
        await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlRawAsync("SET session_replication_role=replica");
        try
        {
            if (kind == "issuance")
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO public.gateway_security_lease_issuances(lease_id,node_id,instance_id,environment_id,organization_id,project_id,security_generation,epoch,checkpoint_hash,payload_hash,issued_at,fresh_until,signing_key_id) VALUES({lease},{node},{instance},{environment},{organization},{project},{generation},0,{hash},{payloadHash},{future},{future.AddSeconds(20)},'isolated-fixture')");
            else if (kind == "horizon")
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO public.gateway_security_leases(node_id,instance_id,environment_id,organization_id,project_id,security_generation,epoch,checkpoint_hash,lease_id,issued_at,fresh_until,last_issued_lease_until,signing_key_id,lease_seconds,revision) VALUES({node},{instance},{environment},{organization},{project},{generation},0,{hash},{lease},{future},{future.AddSeconds(20)},{future.AddSeconds(20)},'isolated-fixture',30,1)");
            else
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO public.security_acks(operation_id,environment_id,node_id,instance_guid,security_generation,epoch,checkpoint_hash,challenge_nonce,applied_at,received_at,success,durable,admission_enforced,error_code) VALUES({lease},{environment},{node},{instance},{generation},1,{hash},{Guid.NewGuid()},{future.AddSeconds(-1)},{future},true,true,true,NULL)");
        }
        finally { await db.Database.ExecuteSqlRawAsync("SET session_replication_role=origin"); }

        await db.Database.MigrateAsync();
        var watermark = await db.Database.SqlQueryRaw<DateTimeOffset>("SELECT observed_at AS \"Value\" FROM public.security_database_clock_watermark WHERE singleton").SingleAsync();
        Assert.Equal(future, watermark);
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            await db.Database.ExecuteSqlRawAsync("SELECT pg_catalog.pg_advisory_xact_lock(8901202)");
            var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.SqlQueryRaw<DateTimeOffset>("SELECT public.security_observe_clock() AS \"Value\"").SingleAsync());
            Assert.Equal("23514", error.SqlState);
            await tx.RollbackAsync();
        }
        var preserved = kind switch
        {
            "issuance" => await db.Database.SqlQuery<bool>($"SELECT EXISTS(SELECT 1 FROM public.gateway_security_lease_issuances WHERE lease_id={lease} AND issued_at={future}) AS \"Value\"").SingleAsync(),
            "horizon" => await db.Database.SqlQuery<bool>($"SELECT EXISTS(SELECT 1 FROM public.gateway_security_leases WHERE lease_id={lease} AND issued_at={future}) AS \"Value\"").SingleAsync(),
            _ => await db.Database.SqlQuery<bool>($"SELECT EXISTS(SELECT 1 FROM public.security_acks WHERE operation_id={lease} AND received_at={future}) AS \"Value\"").SingleAsync()
        };
        Assert.True(preserved);
    }

    [Fact]
    public async Task ClockGuardRetainsTransactionLockAfterSessionLockIsReleased()
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        await using var db = database.Context();
        await db.Database.MigrateAsync();
        await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlRawAsync("SELECT pg_catalog.pg_advisory_lock(8901202)");
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.SqlQueryRaw<DateTimeOffset>("SELECT public.security_observe_clock() AS \"Value\"").SingleAsync();
        Assert.True(await db.Database.SqlQueryRaw<bool>("SELECT pg_catalog.pg_advisory_unlock(8901202) AS \"Value\"").SingleAsync());
        await using var observer = database.Context();
        var acquired = await observer.Database.SqlQueryRaw<bool>("SELECT pg_catalog.pg_try_advisory_lock(8901202) AS \"Value\"").SingleAsync();
        if (acquired)
            await observer.Database.ExecuteSqlRawAsync("SELECT pg_catalog.pg_advisory_unlock(8901202)");
        Assert.False(acquired, "The database guard must retain its own transaction lock until commit.");
        await tx.CommitAsync();
    }

    [Fact]
    public async Task DatabaseClockObservationRollsBackWithBusinessTransaction()
    {
        // This is a characterization of an outstanding safety boundary, not
        // evidence that clock regression or crash recovery is protected.
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        await using var db = database.Context();
        await db.Database.MigrateAsync();
        var before = await db.Database.SqlQueryRaw<DateTimeOffset>("SELECT observed_at AS \"Value\" FROM public.security_database_clock_watermark WHERE singleton").SingleAsync();
        await db.Database.ExecuteSqlRawAsync("SELECT pg_catalog.pg_sleep(0.02)");
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            await db.Database.ExecuteSqlRawAsync("SELECT pg_catalog.pg_advisory_xact_lock(8901202)");
            var observed = await db.Database.SqlQueryRaw<DateTimeOffset>("SELECT public.security_observe_clock() AS \"Value\"").SingleAsync();
            Assert.True(observed > before);
            await tx.RollbackAsync();
        }
        var after = await db.Database.SqlQueryRaw<DateTimeOffset>("SELECT observed_at AS \"Value\" FROM public.security_database_clock_watermark WHERE singleton").SingleAsync();
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task DatabaseClockWatermarkRequiresLockAndCannotBeForgedOrRegressed()
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        await using var db = database.Context();
        await db.Database.MigrateAsync();

        await using (var noLock = await db.Database.BeginTransactionAsync())
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.SqlQueryRaw<DateTimeOffset>("SELECT public.security_observe_clock() AS \"Value\"").SingleAsync());
            Assert.Equal("23514", error.SqlState);
            await noLock.RollbackAsync();
        }

        await using (var sharedLock = await db.Database.BeginTransactionAsync())
        {
            await db.Database.ExecuteSqlRawAsync("SELECT pg_catalog.pg_advisory_xact_lock_shared(8901202)");
            var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.SqlQueryRaw<DateTimeOffset>("SELECT public.security_observe_clock() AS \"Value\"").SingleAsync());
            Assert.Equal("23514", error.SqlState);
            await sharedLock.RollbackAsync();
        }

        DateTimeOffset recorded;
        await using (var locked = await db.Database.BeginTransactionAsync())
        {
            await db.Database.ExecuteSqlRawAsync("SELECT pg_catalog.pg_advisory_xact_lock(8901202)");
            var first = await db.Database.SqlQueryRaw<DateTimeOffset>("SELECT public.security_observe_clock() AS \"Value\"").SingleAsync();
            var forgedFuture = first.AddYears(1);
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE public.security_database_clock_watermark SET observed_at={forgedFuture} WHERE singleton");
            recorded = await db.Database.SqlQueryRaw<DateTimeOffset>("SELECT observed_at AS \"Value\" FROM public.security_database_clock_watermark WHERE singleton").SingleAsync();
            Assert.True(recorded >= first && recorded < forgedFuture);
            await locked.CommitAsync();
        }

        foreach (var sql in new[] { "DELETE FROM public.security_database_clock_watermark", "TRUNCATE public.security_database_clock_watermark" })
            Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql))).SqlState);

        // Test-only corruption simulates a restored/future watermark; the
        // production trigger may not be bypassed by the application role.
        var future = recorded.AddYears(1);
        await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlRawAsync("SET session_replication_role=replica");
        try { await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE public.security_database_clock_watermark SET observed_at={future} WHERE singleton"); }
        finally { await db.Database.ExecuteSqlRawAsync("SET session_replication_role=origin"); }
        await using (var regressed = await db.Database.BeginTransactionAsync())
        {
            await db.Database.ExecuteSqlRawAsync("SELECT pg_catalog.pg_advisory_xact_lock(8901202)");
            var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.SqlQueryRaw<DateTimeOffset>("SELECT public.security_observe_clock() AS \"Value\"").SingleAsync());
            Assert.Equal("23514", error.SqlState);
            await regressed.RollbackAsync();
        }
        Assert.Equal(future, await db.Database.SqlQueryRaw<DateTimeOffset>("SELECT observed_at AS \"Value\" FROM public.security_database_clock_watermark WHERE singleton").SingleAsync());
    }
}
