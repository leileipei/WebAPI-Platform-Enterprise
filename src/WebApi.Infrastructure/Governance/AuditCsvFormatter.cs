using System.Globalization;
using WebApi.Contracts.Governance;
using WebApi.Infrastructure.Observability;
namespace WebApi.Infrastructure.Governance;
public static class AuditCsvFormatter
{
    public const string Header="id,createdAt,actorId,action,resourceType,resourceId,organizationId,projectId,environmentId,traceId\r\n";
    public static string Row(AuditDto value)=>string.Join(',',new string?[]{value.Id.ToString(CultureInfo.InvariantCulture),value.CreatedAt.ToString("O",CultureInfo.InvariantCulture),value.UserId?.ToString(),value.Action,value.ResourceType,value.ResourceId,value.OrganizationId?.ToString(),value.ProjectId?.ToString(),value.EnvironmentId?.ToString(),value.TraceId}.Select(CsvLogExporter.Cell))+"\r\n";
}
