using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using WebApi.Migrator;
using Xunit;
namespace WebApi.Integration.Tests;
public sealed class BootstrapTests
{
    [Fact] public async Task InitializationRequiresExplicitSecret()
    {
        await using var database=new PostgresDatabase();await database.InitializeAsync();await using var db=database.Context();await db.Database.MigrateAsync();
        Assert.Empty(await db.Set<UserRecord>().ToListAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(()=>BootstrapAccounts.RunAsync(db,"admin",""));
        Assert.Empty(await db.Set<UserRecord>().ToListAsync());
    }
    [Fact] public async Task ExplicitBootstrapIsIdempotentAndStoresPasswordHash()
    {
        await using var database=new PostgresDatabase();await database.InitializeAsync();await using var db=database.Context();await db.Database.MigrateAsync();
        var path=Path.Combine(Path.GetTempPath(),"bootstrap-"+Guid.NewGuid().ToString("N"));
        var password=Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        try
        {
            await File.WriteAllTextAsync(path,password);if(!OperatingSystem.IsWindows()) File.SetUnixFileMode(path,UnixFileMode.UserRead|UnixFileMode.UserWrite);
            await BootstrapAccounts.RunAsync(db,"initial-admin",path);await BootstrapAccounts.RunAsync(db,"initial-admin",path);
            var user=await db.Set<UserRecord>().SingleAsync();Assert.NotEqual(password,user.PasswordHash);
            Assert.NotEqual(PasswordVerificationResult.Failed,new PasswordHasher<UserRecord>().VerifyHashedPassword(user,user.PasswordHash!,password));
            Assert.Single(await db.Set<UserRole>().ToListAsync());Assert.Single(await db.Set<AuditLog>().ToListAsync());
        }
        finally {File.Delete(path);}
    }
}
