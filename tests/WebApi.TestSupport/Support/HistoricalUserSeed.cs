using Microsoft.EntityFrameworkCore;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Integration.Tests.Support;

// Historical migrations must be seeded with their own columns, not today's EF model.
public static class HistoricalUserSeed
{
    public static Task<int> InsertHistoricalUserAsync(this WebApiDbContext db,UserRecord user,CancellationToken ct=default) =>
        db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO users(id,username,display_name,email,password_hash,status,auth_source,created_at,updated_at,security_stamp,revision) VALUES ({user.Id},{user.Username},{user.DisplayName},{user.Email},{user.PasswordHash},{user.Status},{user.AuthSource},{user.CreatedAt},{user.UpdatedAt},{user.SecurityStamp},{user.Revision})",ct);
}

public sealed class HistoricalUserModelCustomizer(Microsoft.EntityFrameworkCore.Infrastructure.ModelCustomizerDependencies dependencies)
    : Microsoft.EntityFrameworkCore.Infrastructure.ModelCustomizer(dependencies)
{
    public override void Customize(ModelBuilder modelBuilder,DbContext context)
    {
        base.Customize(modelBuilder,context);
        modelBuilder.Entity<UserRecord>().Ignore(x=>x.MustChangePassword).Ignore(x=>x.PasswordChangedAt);
    }
}
