using System.Text;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Governance;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Settings;
namespace WebApi.Infrastructure.Governance;
public sealed record AuditExportResult(byte[] Bytes,int RowCount,bool Truncated);
public sealed class AuditExportService(WebApiDbContext db,AuditAccessQuery access,SystemSettingsReader settings)
{
    private async Task Enabled(CancellationToken ct){if(!(await settings.AuditAsync(ct)).AuditExportEnabled)throw new ApiException(403,"audit_export_disabled","平台设置已关闭审计导出。");}
    public async Task<AuditExportResult> ExportAsync(ActorContext actor,string? traceId,string? resourceId,CancellationToken ct=default)
    {
        var first=await access.AccessAsync(actor,traceId,resourceId,ct);await Enabled(ct);var upper=await first.Query.MaxAsync(x=>(long?)x.Id,ct)??0;long? cursor=null;var rows=0;var truncated=false;
        using var buffer=new MemoryStream();buffer.Write(Encoding.UTF8.GetBytes(AuditCsvFormatter.Header));
        while(true)
        {
            var current=await access.AccessAsync(actor,traceId,resourceId,ct);await Enabled(ct);if(current.ScopeFingerprint!=first.ScopeFingerprint)throw new ApiException(403,"audit_export_authority_changed","导出期间权限范围发生变化，请重新读取。");
            var page=current.Query.Where(x=>x.Id<=upper);if(cursor is long last)page=page.Where(x=>x.Id<last);
            var records=await page.OrderByDescending(x=>x.Id).Take(500).Select(x=>new AuditDto(x.Id,x.OrganizationId,x.ProjectId,x.EnvironmentId,x.UserId,x.Action,x.ResourceType,x.ResourceId,null,null,null,x.TraceId,x.CreatedAt)).ToArrayAsync(ct);
            if(records.Length==0)break;
            foreach(var record in records){var bytes=Encoding.UTF8.GetBytes(AuditCsvFormatter.Row(record));if(rows==10000||buffer.Length+bytes.Length>8*1024*1024){truncated=true;break;}buffer.Write(bytes);rows++;cursor=record.Id;}
            if(truncated)break;
        }
        await using(var tx=await db.Database.BeginTransactionAsync(ct))
        {
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(8901202)",ct);var final=await access.AccessAsync(actor,traceId,resourceId,ct);await Enabled(ct);if(final.ScopeFingerprint!=first.ScopeFingerprint)throw new ApiException(403,"audit_export_authority_changed","导出期间权限范围发生变化，请重新读取。");await tx.CommitAsync(ct);
        }
        return new(buffer.ToArray(),rows,truncated);
    }
}
