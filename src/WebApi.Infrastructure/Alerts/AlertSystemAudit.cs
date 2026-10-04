using System.Text.Json;
using WebApi.Contracts.Common;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Alerts;
public static class AlertSystemAudit
{
    internal static void Add(WebApiDbContext db,AlertEvent alert,string action,string reason,DateTimeOffset now,string correlation)
    {
        db.Add(new AuditLog{OrganizationId=alert.OrganizationId,ProjectId=alert.ProjectId,EnvironmentId=alert.EnvironmentId,UserId=null,Action=action,ResourceType="AlertEvent",ResourceId=alert.Id.ToString(),TraceId=correlation,CreatedAt=now,
            AfterJson=JsonSerializer.Serialize(new{source="worker",alertId=alert.Id,alert.RuleId,alert.LogicRevision,alert.ResourceKey,alert.Status,reason},CanonicalJson.Options)});
    }
}
