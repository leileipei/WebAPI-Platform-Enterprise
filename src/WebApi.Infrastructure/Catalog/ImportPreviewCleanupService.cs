using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Catalog;
public sealed class ImportPreviewCleanupService(WebApiDbContext db,TimeProvider clock)
{
    public async Task<int> RunAsync(int batchSize,CancellationToken ct)
    {
        if(batchSize is <1 or >1000)throw new ArgumentOutOfRangeException(nameof(batchSize));
        await using var tx=await db.Database.BeginTransactionAsync(ct);await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(8901202)",ct);var now=clock.GetUtcNow();
        var rows=await db.Set<ApiImportPreview>().Where(x=>(x.Status!="Committed"&&(x.ExpiresAt<=now||x.Status=="Revoked"||x.Status=="Expired"))||x.Status=="Committed"&&x.ExpiresAt<=now&&x.BundleJson!=null||x.ReceiptExpiresAt!=null&&x.ReceiptExpiresAt<=now).OrderBy(x=>x.ExpiresAt).ThenBy(x=>x.Id).Take(batchSize).ToArrayAsync(ct);
        foreach(var row in rows){
            if(row.Status!="Committed"){db.Remove(row);continue;}
            row.BundleJson=null;row.PreviewJson=null;row.Revision++;
            if(row.CommittedTargetsJson is string json){var metadata=JsonSerializer.Deserialize<ImportCommittedMetadata>(json,CanonicalJson.Options)??throw new InvalidOperationException("Invalid stored commit metadata.");var scope=$"{row.OrganizationId}:{row.ProjectId}:{row.EnvironmentId}";
                if(!await db.Set<IdempotencyRecord>().AnyAsync(x=>x.ActorId==row.ActorId&&x.ScopeKey==scope&&x.Operation=="openapi.import-session"&&x.Key==metadata.ReceiptKey,ct))db.Remove(row);
            }
        }
        await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return rows.Length;
    }
}
