using Microsoft.EntityFrameworkCore;
using WebApi.Infrastructure.Persistence;
using WebApi.Migrator;
await using var db=new WebApiDbContext(new DbContextOptionsBuilder<WebApiDbContext>().UseNpgsql(DatabaseSettings.ConnectionString()).Options);
await db.Database.MigrateAsync();
Console.WriteLine("Database migrations applied successfully.");
if(args.Contains("--bootstrap",StringComparer.Ordinal))
{
    await BootstrapAccounts.RunAsync(db,System.Environment.GetEnvironmentVariable("WEBAPI_BOOTSTRAP_USERNAME")??"",System.Environment.GetEnvironmentVariable("WEBAPI_BOOTSTRAP_PASSWORD_FILE")??"");
    Console.WriteLine("Explicit administrator bootstrap completed.");
}

if(args.Contains("--seed-catalog",StringComparer.Ordinal)) await WebApi.Infrastructure.Governance.PermissionCatalog.SeedAsync(db);
