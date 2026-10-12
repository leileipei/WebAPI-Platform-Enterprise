using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Releases;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Releases;

internal static class FrozenContractSource
{
    public static void Capture(WebApiDbContext db,ReleaseRecord release,FrozenReleaseCandidate candidate)
    {
        if(db.Database.CurrentTransaction is null||release.ReleaseType!="publish"||release.ToConfigVersion<=0||candidate.EnvironmentId!=release.EnvironmentId)
            throw new InvalidOperationException("Frozen contract capture requires the compiled publish transaction.");
        for(var i=0;i<candidate.Versions.Count;i++)
        {
            var version=candidate.Versions[i];
            db.Add(new ReleaseFrozenContract{ReleaseId=release.Id,Ordinal=i+1,EnvironmentId=release.EnvironmentId,ConfigVersion=release.ToConfigVersion,ApiId=version.Api.Id,ApiVersionId=version.Version.Id,FrozenJson=Encoding.UTF8.GetString(CanonicalJson.Serialize(version))});
        }
    }
    public static async Task<FrozenApiVersion?> ReadAsync(WebApiDbContext db,Guid environmentId,Guid apiId,Guid versionId,long running,bool includeBody,CancellationToken ct)
    {
        // OFFSET 0 keeps the PK eligibility check correlated. Flattening it into a
        // join can underestimate the correlated version keys and scan/sort every
        // historical contract before LIMIT. This retains early exit with no global
        // planner settings and still excludes invalidated or mismatched origins.
        var origins=db.Set<ReleaseFrozenContract>().FromSqlInterpolated($"""
            SELECT c.* FROM release_frozen_contracts c
            WHERE c.environment_id={environmentId} AND c.api_id={apiId}
              AND c.api_version_id={versionId} AND c.config_version<={running}
              AND EXISTS (
                SELECT 1 FROM release_records r
                WHERE r.id=c.release_id AND r.environment_id=c.environment_id
                  AND r.to_config_version=c.config_version AND r.release_type='publish'
                  AND r.candidate_bytes IS NOT NULL AND r.approval_policy IS NOT NULL
                OFFSET 0)
            ORDER BY c.config_version DESC,c.release_id,c.ordinal
            LIMIT 1
            """).AsNoTracking();
        if(!includeBody)
        {
            if(!await origins.AnyAsync(ct))throw Missing();
            return null;
        }
        var json=await origins.Select(c=>c.FrozenJson).FirstOrDefaultAsync(ct)??throw Missing();
        return JsonSerializer.Deserialize<FrozenApiVersion>(json,CanonicalJson.Options)??throw Missing();
    }
    private static ApiException Missing()=>new(422,"historical_contract_missing","运行版本的冻结契约缺失。");
}
