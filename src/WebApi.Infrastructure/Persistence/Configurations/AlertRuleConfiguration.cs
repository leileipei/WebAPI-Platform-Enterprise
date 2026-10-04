using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Persistence.Configurations;
internal static class AlertColumns
{
    internal static void Map<T>(EntityTypeBuilder<T> b,string table) where T:class
    {
        b.ToTable(table);b.HasKey("Id");
        foreach(var p in typeof(T).GetProperties())
        {
            var column=Regex.Replace(p.Name,"(?<=[a-z0-9])([A-Z])","_$1").ToLowerInvariant();
            var property=b.Property(p.Name).HasColumnName(column);
            if(p.PropertyType==typeof(string))property.HasColumnType("text");
            if(p.Name=="Revision")property.IsConcurrencyToken();
        }
    }
}
public sealed class AlertRuleConfiguration:IEntityTypeConfiguration<AlertRule>
{
    public void Configure(EntityTypeBuilder<AlertRule> b)
    {
        AlertColumns.Map(b,"alert_rules");b.Property(x=>x.Notification).HasColumnType("jsonb");
        b.Property(x=>x.Name).HasMaxLength(128);b.Property(x=>x.NormalizedName).HasMaxLength(128);
        b.Property(x=>x.Expression).HasMaxLength(256);
        b.HasIndex(x=>new{x.OrganizationId,x.ProjectId,x.EnvironmentId,x.NormalizedName}).IsUnique().AreNullsDistinct(false);
        b.HasAlternateKey(x=>new{x.Id,x.OrganizationId});
        b.ToTable(t=>{
            t.HasCheckConstraint("ck_alert_rule_scope","environment_id IS NULL OR project_id IS NOT NULL");
            t.HasCheckConstraint("ck_alert_rule_target","(target_type = 'Environment' AND target_id IS NULL) OR (target_type = 'Api' AND target_id IS NOT NULL AND project_id IS NOT NULL) OR (target_type = 'Destination' AND target_id IS NOT NULL AND project_id IS NOT NULL AND environment_id IS NOT NULL)");
            t.HasCheckConstraint("ck_alert_rule_limits","for_seconds BETWEEN 0 AND 86400 AND window_seconds BETWEEN 60 AND 3600 AND revision > 0 AND logic_revision > 0 AND length(normalized_name) BETWEEN 1 AND 128");
            t.HasCheckConstraint("ck_alert_rule_severity","severity IN ('Info','Warning','Critical')");
            t.HasCheckConstraint("ck_alert_rule_metric","metric IN ('request_rps','error_5xx_ratio','latency_p95_ms','unhealthy_destinations')");
        });
    }
}
