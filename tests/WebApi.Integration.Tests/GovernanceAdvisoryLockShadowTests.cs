using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Security;
using WebApi.Integration.Tests.Support;
using Xunit;

namespace WebApi.Integration.Tests;

public sealed class GovernanceAdvisoryLockShadowTests
{
    private static async Task InstallShadowAsync(WebApiDbContext db)
    {
        await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE SCHEMA attack_path;
            CREATE FUNCTION attack_path.pg_advisory_xact_lock(bigint) RETURNS void LANGUAGE plpgsql AS $$ BEGIN RETURN; END $$;
            CREATE FUNCTION attack_path.hashtextextended(text,bigint) RETURNS bigint LANGUAGE sql IMMUTABLE AS $$ SELECT 42::bigint $$;
            SET search_path=attack_path,public,pg_catalog;
            """);
    }

    [Fact]
    public async Task AuditedCommandHoldsRealGlobalLockWhenSearchPathIsShadowed()
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        await using var db = database.Context();
        await db.Database.MigrateAsync();
        var user = new UserRecord { Username = "lock-fixture", DisplayName = "Lock fixture" };
        db.Add(user);
        await db.SaveChangesAsync();
        await InstallShadowAsync(db);
        var executor = new AuditedCommandExecutor(db);
        var held = await executor.ExecuteAsync(new ActorContext(user.Id, "lock-test"), new ScopeRef(Guid.NewGuid()), "fixture.lock", async (_, ct) =>
        {
            await using var other = database.Context();
            var acquired = await other.Database.SqlQueryRaw<bool>("SELECT pg_catalog.pg_try_advisory_xact_lock(8901202) AS \"Value\"").SingleAsync(ct);
            return !acquired;
        });
        Assert.True(held, "The authenticated command must hold the real database-global lock until its transaction completes.");
    }

    [Fact]
    public async Task LoginAuditHoldsRealResourceLockWhenSearchPathIsShadowed()
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        await using var db = database.Context();
        await db.Database.MigrateAsync();
        await InstallShadowAsync(db);
        var bucket = "isolated-login-lock-fixture";
        var lease = new AuthenticationAuditLease(bucket, "fixture-token", DateTimeOffset.UtcNow);
        var resource = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(bucket)));
        await using var tx = await db.Database.BeginTransactionAsync();
        await new AuthenticationAuditWriter(db).WriteAsync(new AuthenticationAuditEvent("auth.login.throttled", null, null,
            "lock-test", null, "login_rate_limited", lease), CancellationToken.None);
        await using var other = database.Context();
        var acquired = await other.Database.SqlQuery<bool>($"SELECT pg_catalog.pg_try_advisory_xact_lock(pg_catalog.hashtextextended({resource},0)) AS \"Value\"").SingleAsync();
        Assert.False(acquired, "The login audit must hold the real per-resource lock until its transaction completes.");
        await tx.CommitAsync();
    }
}
