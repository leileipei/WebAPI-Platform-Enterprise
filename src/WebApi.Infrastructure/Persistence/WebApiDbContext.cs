using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
namespace WebApi.Infrastructure.Persistence;
public sealed class WebApiDbContext(DbContextOptions<WebApiDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(WebApiDbContext).Assembly);
        CoreConstraints.Apply(modelBuilder);
    }
}
public sealed class WebApiDbContextFactory : IDesignTimeDbContextFactory<WebApiDbContext>
{
    public WebApiDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<WebApiDbContext>().UseNpgsql(DatabaseSettings.ConnectionString()).Options);
}
