using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Releases;
using WebApi.Contracts.Runtime;
using WebApi.Contracts.Catalog;
using WebApi.Contracts.Security;
using WebApi.Domain.Runtime;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Catalog;
namespace WebApi.Infrastructure.Releases;
public sealed record HistoricalArtifact(RuntimeSnapshot Snapshot,byte[] Payload,string Hash,long Size);
public sealed class HistoricalSnapshotService(WebApiDbContext db,ScopeResolver scopes)
{
    public async Task<HistoricalArtifact> ReadAsync(Guid envId,long version,CancellationToken ct=default)
    {
        var row=await(from v in db.Set<GatewayConfigVersion>().AsNoTracking() join s in db.Set<GatewayConfigSnapshot>().AsNoTracking() on v.Id equals s.ConfigVersionId where v.EnvironmentId==envId&&v.VersionNo==version select new {v.SnapshotHash,s.PayloadBytes,s.SizeBytes}).SingleOrDefaultAsync(ct)??throw ScopeResolver.Missing();
        if(row.PayloadBytes.LongLength!=row.SizeBytes||Convert.ToHexStringLower(SHA256.HashData(row.PayloadBytes))!=row.SnapshotHash) throw new ApiException(422,"corrupt_snapshot","历史快照完整性校验失败。");var snapshot=SnapshotValidator.ParsePayload(row.PayloadBytes,envId);if(snapshot.ConfigVersion!=version) throw new ApiException(422,"corrupt_snapshot","历史版本标识不一致。");return new(snapshot,row.PayloadBytes,row.SnapshotHash!,row.SizeBytes);
    }

    internal async Task<HistoricalArtifact> ReadGovernedRollbackSourceAsync(Guid environmentId, long version,
        SnapshotCompiler compiler, CancellationToken ct)
    {
        var artifact = await ReadAsync(environmentId, version, ct);
        if (artifact.Snapshot.SchemaVersion != "2.4" ||
            !artifact.Payload.AsSpan().SequenceEqual(CanonicalJson.Serialize(artifact.Snapshot)) ||
            !await db.Set<GatewayConfigVersion>().AsNoTracking().AnyAsync(row => row.EnvironmentId == environmentId &&
                row.VersionNo == version && row.Status == "Published" && row.PublishedAt != null, ct))
            throw GovernedRollbackCandidate.HistoryUnavailable();

        var origins = await db.Set<ReleaseRecord>().AsNoTracking().Where(row => row.EnvironmentId == environmentId &&
            row.ToConfigVersion == version && row.DeploymentSequence > 0 &&
            (row.ReleaseType == "publish" || row.ReleaseType == "rollback") &&
            (row.Status == "Succeeded" || row.Status == "RolledBack") &&
            row.ApprovalPolicy != null && row.CandidateBytes != null).Take(2).ToArrayAsync(ct);
        if (origins.Length != 1) throw GovernedRollbackCandidate.HistoryUnavailable();
        var origin = origins[0];
        FrozenReleaseCandidate? candidate;
        try { candidate = JsonSerializer.Deserialize<FrozenReleaseCandidate>(origin.CandidateBytes!, CanonicalJson.Options); }
        catch (JsonException) { throw GovernedRollbackCandidate.HistoryUnavailable(); }
        if (candidate?.Governance is not { Targets: { Count: >= 2 } } governance ||
            candidate.EnvironmentId != environmentId || candidate.BaselineConfigVersion != origin.BaselineConfigVersion ||
            origin.ReleaseType == "rollback" && candidate.GovernedRollback is null)
            throw GovernedRollbackCandidate.HistoryUnavailable();

        var targets = await db.Set<ReleaseTarget>().AsNoTracking().Where(row => row.ReleaseId == origin.Id)
            .OrderBy(row => row.NodeId).Select(row => new { row.NodeId, row.InstanceId }).ToArrayAsync(ct);
        var acks = await db.Set<GatewayAck>().AsNoTracking().Where(row => row.ReleaseId == origin.Id && row.Success &&
            row.ConfigVersion == version && row.DeploymentSequence == origin.DeploymentSequence && row.PayloadHash == artifact.Hash)
            .OrderBy(row => row.NodeId).Select(row => new { row.NodeId, row.InstanceId }).ToArrayAsync(ct);
        if (!targets.SequenceEqual(acks) ||
            !governance.Targets.Select(row => (row.NodeId, row.InstanceId)).SequenceEqual(targets.Select(row => (row.NodeId, row.InstanceId))))
            throw GovernedRollbackCandidate.HistoryUnavailable();

        var baseline = origin.BaselineConfigVersion > 0
            ? (await ReadAsync(environmentId, origin.BaselineConfigVersion, ct)).Snapshot
            : new RuntimeSnapshot("2.0", environmentId, 0, artifact.Snapshot.GeneratedAt, [], [], [], []);
        var rebuilt = compiler.Compile(candidate, baseline, version, artifact.Snapshot.GeneratedAt);
        if (rebuilt.Hash != artifact.Hash || !rebuilt.Payload.Span.SequenceEqual(artifact.Payload))
            throw GovernedRollbackCandidate.HistoryUnavailable();
        return artifact;
    }

    internal async Task RequireSealedRollbackContractsAsync(RuntimeSnapshot source, IReadOnlyList<FrozenApiVersion> current,
        CancellationToken ct)
    {
        foreach (var version in current)
        {
            var frozen = await FrozenContractSource.ReadAsync(db, source.EnvironmentId, version.Api.Id, version.Version.Id,
                source.ConfigVersion, includeBody: true, ct);
            if (frozen is null || version.Version.SealedAt is null ||
                (version.Version with { Status = frozen.Version.Status, SealedAt = frozen.Version.SealedAt }) != frozen.Version ||
                !CanonicalJson.Serialize(version.Parameters.OrderBy(item => item.Id)).AsSpan()
                    .SequenceEqual(CanonicalJson.Serialize(frozen.Parameters.OrderBy(item => item.Id))) ||
                !CanonicalJson.Serialize(version.Schemas.OrderBy(item => item.Id)).AsSpan()
                    .SequenceEqual(CanonicalJson.Serialize(frozen.Schemas.OrderBy(item => item.Id))))
                throw GovernedRollbackCandidate.HistoryUnavailable();
        }
    }
    public async Task<FrozenReleaseCandidate> CandidateAsync(Guid envId,long version,long baseline,CancellationToken ct)
    {
        var artifact=await ReadAsync(envId,version,ct);var scope=await scopes.EnvironmentAsync(envId,ct);var runtime=artifact.Snapshot;var versions=new List<FrozenApiVersion>();
        foreach(var id in runtime.Routes.Select(r=>r.ApiVersionId).Distinct().OrderBy(id=>id))
        {
            var v=await db.Set<ApiVersion>().AsNoTracking().SingleOrDefaultAsync(v=>v.Id==id&&v.SealedAt!=null,ct)??throw new ApiException(422,"historical_version_missing","历史版本的封存资料缺失。");var a=await db.Set<Api>().AsNoTracking().SingleAsync(a=>a.Id==v.ApiId,ct);if(a.OrganizationId!=scope.OrganizationId||a.ProjectId!=scope.ProjectId||runtime.Routes.Any(r=>r.ApiVersionId==id&&r.ApiId!=a.Id)) throw new ApiException(422,"foreign_snapshot_resource","历史引用不属于当前环境范围。");
            var parameters=await db.Set<ApiParameter>().AsNoTracking().Where(p=>p.ApiVersionId==id).Select(p=>new ParameterDto(p.Id,p.ApiVersionId,p.Location,p.Name,p.DataType,p.Required,p.Schema,p.Description,p.ExampleJson)).ToArrayAsync(ct);var schemas=await db.Set<ApiSchema>().AsNoTracking().Where(s=>s.ApiVersionId==id).Select(s=>new SchemaDto(s.Id,s.ApiVersionId,s.SchemaType,s.Name,s.StatusCode,s.ContentType,s.SchemaJson,s.SchemaHash,s.ExampleJson)).ToArrayAsync(ct);versions.Add(new(CatalogService.Dto(a),CatalogService.Dto(v),parameters,schemas));
        }
        // Keep historical consumer metadata and identifiers from the original compiled release.
        var origin=await db.Set<ReleaseRecord>().AsNoTracking().SingleAsync(r=>r.EnvironmentId==envId&&r.ReleaseType=="publish"&&r.ToConfigVersion==version,ct);var original=JsonSerializer.Deserialize<FrozenReleaseCandidate>(origin.CandidateBytes!,CanonicalJson.Options)!;
        var apps=original.Applications.Where(a=>runtime.Applications.Any(r=>r.Id==a.Application.Id)).Select(a=>a with {Credentials=a.Credentials.Where(k=>runtime.Applications.Single(r=>r.Id==a.Application.Id).Credentials.Any(c=>c.Id==k.Id)).ToArray(),Permissions=a.Permissions.Where(p=>runtime.Applications.Single(r=>r.Id==a.Application.Id).Permissions.Any(g=>g.ApiId==p.ApiId&&g.ValidFrom==p.ValidFrom&&g.ExpiresAt==p.ExpiresAt)).ToArray()}).ToArray();
        var routes=runtime.Routes.Select(r=>new RouteDto(r.Id,r.ApiVersionId,envId,"历史路由",r.Path,r.Path,r.Methods,r.ClusterId,0,r.MatchOrder,true,r.TimeoutMs,r.RequireApiKey,1,EffectiveAuthenticationMode:r.AuthenticationMode==WebApi.Contracts.Policies.AuthenticationMode.JWT?"JWT":null)).ToArray();var clusters=runtime.Clusters.Select(c=>new ClusterDto(c.Id,scope.ProjectId!.Value,envId,"历史后端 "+(c.SourceId??c.Id).ToString()[..8],c.LoadBalancingPolicy,c.HealthCheckEnabled,c.HealthCheckPath,c.HealthCheckIntervalSec,"Active",1,c.Destinations.Select(d=>new DestinationDto(d.Id,c.Id,"历史实例",d.Address,d.Weight,true,null,1)).ToArray())).ToArray();
        return new(envId,scope.OrganizationId,scope.ProjectId!.Value,baseline,versions.Select(v=>v.Version.Id).ToArray(),versions,routes,clusters,runtime.Policies.Select(p=>new FrozenPolicy(p.Id,p.Type,p.Config,true,p.SourceRevision??1,p.SourcePolicyId)).ToArray(),runtime.Routes.SelectMany(r=>(r.PolicyBindings??[]).Select(b=>new FrozenBinding(r.Id,b.PolicyId,b.Priority))).ToArray(),apps,versions.Select(v=>new ResourceRevision("version",v.Version.Id,v.Version.Revision)).ToArray());
    }
}
