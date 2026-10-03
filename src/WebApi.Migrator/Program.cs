using Microsoft.EntityFrameworkCore;
using WebApi.Infrastructure.Persistence;
await using var db = new WebApiDbContext(new DbContextOptionsBuilder<WebApiDbContext>().UseNpgsql(DatabaseSettings.ConnectionString()).Options);
await db.Database.MigrateAsync();
Console.WriteLine("Database migrations applied successfully.");
