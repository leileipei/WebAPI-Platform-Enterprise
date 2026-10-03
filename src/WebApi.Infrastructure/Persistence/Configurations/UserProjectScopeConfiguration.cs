using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Persistence.Configurations;
public sealed class UserProjectScopeConfiguration : IEntityTypeConfiguration<UserProjectScope>
{
    public void Configure(EntityTypeBuilder<UserProjectScope> b)
    {
        b.ToTable("user_project_scopes"); b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().HasDefaultValueSql("gen_random_uuid()");
        b.Property(x => x.UserId).HasColumnName("user_id").HasColumnType("uuid").IsRequired();
        b.Property(x => x.OrganizationId).HasColumnName("organization_id").HasColumnType("uuid").IsRequired();
        b.Property(x => x.ProjectId).HasColumnName("project_id").HasColumnType("uuid");
        b.Property(x => x.EnvironmentId).HasColumnName("environment_id").HasColumnType("uuid");
        b.Property(x => x.AccessMode).HasColumnName("access_mode").HasColumnType("varchar(16)").IsRequired();
        b.Property(x => x.Revision).HasColumnName("revision").HasColumnType("bigint").IsRequired().IsConcurrencyToken().HasDefaultValue(1L);
    }
}
