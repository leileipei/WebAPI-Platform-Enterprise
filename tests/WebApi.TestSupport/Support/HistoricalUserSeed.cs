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

// Pre-governance baselines predate the G-series columns; seeding them with
// today's model writes columns the historical schema does not have. EF 10 moved
// relationship queries/removal to the mutable model, and a property that still
// participates in an explicit key or foreign key cannot be ignored, so each G
// column first drops its foreign keys and alternate keys, then is unmapped.
public sealed class HistoricalPreGovernanceModelCustomizer(Microsoft.EntityFrameworkCore.Infrastructure.ModelCustomizerDependencies dependencies)
    : Microsoft.EntityFrameworkCore.Infrastructure.ModelCustomizer(dependencies)
{
    public override void Customize(ModelBuilder modelBuilder,DbContext context)
    {
        base.Customize(modelBuilder,context);
        DropG<UserRecord>(modelBuilder,nameof(UserRecord.MustChangePassword),nameof(UserRecord.PasswordChangedAt));
        DropG<EnvironmentRecord>(modelBuilder,nameof(EnvironmentRecord.AuthorizationRevision),nameof(EnvironmentRecord.RequireEnvironmentCredential));
        DropG<ApplicationRecord>(modelBuilder,nameof(ApplicationRecord.OwnerUserId),nameof(ApplicationRecord.OwnerRevision),nameof(ApplicationRecord.AuthorizationRevision));
        DropG<ApplicationCredential>(modelBuilder,nameof(ApplicationCredential.OrganizationId),nameof(ApplicationCredential.ProjectId),nameof(ApplicationCredential.EnvironmentId),nameof(ApplicationCredential.BindingMode),nameof(ApplicationCredential.ExpiryRevision));
        DropG<ApplicationApiPermission>(modelBuilder,nameof(ApplicationApiPermission.Mode),nameof(ApplicationApiPermission.ExpiryRevision),"OrganizationId","ProjectId","RevisionTransactionId");
        DropG<ApiRoute>(modelBuilder,nameof(ApiRoute.ApiId),nameof(ApiRoute.AuthorizationRevision),nameof(ApiRoute.AuthenticationMode));
    }
    private static void DropG<T>(ModelBuilder modelBuilder,params string[] gColumns) where T:class
    {
        var entity=modelBuilder.Model.FindEntityType(typeof(T))!;
        // G-era tables whose foreign keys point at this entity's G keys must be
        // detached first: a principal key cannot be removed while referenced.
        foreach(var fk in entity.GetReferencingForeignKeys().Where(k=>k.Properties.Any(p=>gColumns.Contains(p.Name))).ToArray())
            ((Microsoft.EntityFrameworkCore.Metadata.IMutableEntityType)fk.DeclaringEntityType).RemoveForeignKey(fk);
        foreach(var fk in entity.GetForeignKeys().Where(k=>k.Properties.Any(p=>gColumns.Contains(p.Name))).ToArray())
            entity.RemoveForeignKey(fk);
        foreach(var key in entity.GetKeys().Where(k=>!k.IsPrimaryKey()&&k.Properties.Any(p=>gColumns.Contains(p.Name))).ToArray())
            entity.RemoveKey(key);
        var builder=modelBuilder.Entity<T>();
        foreach(var name in gColumns)builder.Ignore(name);
    }
}
