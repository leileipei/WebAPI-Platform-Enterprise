using Microsoft.EntityFrameworkCore;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Sso;
using WebApi.Integration.Tests.Support;
using Xunit;
namespace WebApi.Integration.Tests;
public sealed class SsoAttemptCleanupTests
{
    [Fact] public async Task CleanupCannotDeleteCurrentAttemptsOrAuditAndBatchIsBounded()
    {
        await using var database=new PostgresDatabase();await database.InitializeAsync();await using var db=database.Context();await db.Database.MigrateAsync();var now=DateTimeOffset.UtcNow;
        var provider=new SsoProvider{Name="Fixture",Issuer="https://id.example.test",ClientId="fixture",SecretRef="file://sso/fixture"};db.Add(provider);
        for(var i=0;i<1105;i++)db.Add(new SsoLoginAttempt{ProviderId=provider.Id,ProviderRevision=1,AuthRevision=1,CreatedAt=now.AddDays(-2),ExpiresAt=now.AddHours(-25),State="Failed"});
        var keep=new[]{new SsoLoginAttempt{ProviderId=provider.Id,ProviderRevision=1,AuthRevision=1,CreatedAt=now,ExpiresAt=now.AddMinutes(5)},new SsoLoginAttempt{ProviderId=provider.Id,ProviderRevision=1,AuthRevision=1,CreatedAt=now.AddHours(-2),ExpiresAt=now.AddHours(-1),State="Processing"}};
        var owner=new UserRecord{Username="cleanup_fixture",DisplayName="Fixture",SecurityStamp=Guid.NewGuid().ToString("N")};db.Add(owner);
        db.AddRange(keep);db.Add(new AuditLog{Action="auth.sso.failed",ResourceType="fixture"});db.Add(new SystemSetting{Key="system.audit",Value="{\"auditRetentionDays\":365,\"auditExportEnabled\":true}",UpdatedBy=owner.Id});await db.SaveChangesAsync();
        var cleanup=new SsoAttemptCleanupService(db);Assert.Equal(1000,await cleanup.CleanupAsync(now));Assert.Equal(105,await cleanup.CleanupAsync(now));Assert.Equal(0,await cleanup.CleanupAsync(now));
        Assert.Equal(keep.Select(x=>x.Id).Order(),(await db.Set<SsoLoginAttempt>().Select(x=>x.Id).ToArrayAsync()).Order());Assert.Equal(1,await db.Set<AuditLog>().CountAsync());Assert.Equal(1,await db.Set<SystemSetting>().CountAsync());
    }
    [Fact] public async Task CancelledCleanupDoesNotChangeRecords()
    {
        await using var database=new PostgresDatabase();await database.InitializeAsync();await using var db=database.Context();await db.Database.MigrateAsync();
        using var cancelled=new CancellationTokenSource();cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>new SsoAttemptCleanupService(db).CleanupAsync(DateTimeOffset.UtcNow,cancelled.Token));
    }
}
