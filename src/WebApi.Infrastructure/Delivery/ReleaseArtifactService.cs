using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Catalog;
using WebApi.Contracts.Common;
using WebApi.Contracts.Releases;
using WebApi.Contracts.Security;
using WebApi.Domain.Delivery;
using WebApi.Infrastructure.Commands;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Policies;
using WebApi.Infrastructure.Releases;
using WebApi.Infrastructure.Security;

namespace WebApi.Infrastructure.Delivery;

public sealed class ReleaseArtifactService(WebApiDbContext db, ScopeResolver scopes, AuthorizationService auth,
    AuditedCommandExecutor commands, IdempotentCommandExecutor idempotency, CommandRequestContext requestContext,
    RunningDeploymentReader running, HistoricalSnapshotService history, SnapshotCompiler compiler, PolicyAccess policyAccess)
{
    public async Task<ReleaseArtifactDto> CreateAsync(Guid sourceReleaseId, ActorContext actor, CancellationToken ct)
    {
        var initial = await db.Set<ReleaseRecord>().AsNoTracking().SingleOrDefaultAsync(r => r.Id == sourceReleaseId, ct) ?? throw ScopeResolver.Missing();
        var scope = await scopes.EnvironmentAsync(initial.EnvironmentId, ct);
        return await commands.ExecuteAsync(actor, scope, "release.artifact.create", async (_, token) =>
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM environments WHERE id={initial.EnvironmentId} FOR UPDATE", token);
            await auth.RequireAsync(actor, "release.create", new("environment", initial.EnvironmentId, scope), token);
            var source = await RequireSourceAsync(sourceReleaseId, actor, token);
            var bytes = ReleaseArtifactCanonicalizer.Serialize(source.Content);
            var hash = ReleaseArtifactCanonicalizer.Hash(source.Content);
            return await idempotency.ExecuteAsync(new(actor.UserId, scope, "release.artifact.create", requestContext.IdempotencyKey), CanonicalJson.Serialize(new { sourceReleaseId }), async inner =>
            {
                var row = await db.Set<ReleaseArtifact>().SingleOrDefaultAsync(a => a.SourceReleaseId == sourceReleaseId && a.ArtifactHash == hash, inner);
                if (row is null)
                {
                    row = new() { OrganizationId = scope.OrganizationId, ProjectId = scope.ProjectId!.Value, SourceEnvironmentId = initial.EnvironmentId, SourceReleaseId = sourceReleaseId, CanonicalContent = Encoding.UTF8.GetString(bytes), ArtifactHash = hash, SourceSnapshotHash = source.Deployment.Snapshot.Hash, CreatedBy = actor.UserId };
                    db.Add(row);
                }
                else if (row.SourceSnapshotHash != source.Deployment.Snapshot.Hash) throw Invalid();
                return View(row);
            }, token);
        }, ct);
    }

    public async Task<ReleaseArtifactDto> GetAsync(Guid id, ActorContext actor, CancellationToken ct)
    {
        var row = await db.Set<ReleaseArtifact>().AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, ct) ?? throw ScopeResolver.Missing();
        var scope = await scopes.EnvironmentAsync(row.SourceEnvironmentId, ct);
        var dto = View(row);
        if (scope.OrganizationId != row.OrganizationId || scope.ProjectId != row.ProjectId) throw ScopeResolver.Missing();
        await RequireVisibilityAsync(dto.Content, row.SourceReleaseId, scope, actor, ct);
        return dto;
    }

    public async Task<(RunningDeployment Deployment, ArtifactContent Content)> RequireSourceAsync(Guid releaseId, ActorContext actor, CancellationToken ct)
    {
        var release = await db.Set<ReleaseRecord>().AsNoTracking().SingleOrDefaultAsync(r => r.Id == releaseId, ct) ?? throw ScopeResolver.Missing();
        var scope = await scopes.EnvironmentAsync(release.EnvironmentId, ct);
        if (!await auth.CanAsync(actor, "release.read", new("release", releaseId, scope), ct)) throw ScopeResolver.Missing();
        var deployment = await running.ReadAsync(release.EnvironmentId, releaseId, ct);
        if (deployment.Environment.IsProduction) throw new ApiException(422, "invalid_artifact_source", "制品来源需为非生产环境。");
        FrozenReleaseCandidate candidate;
        try { candidate = JsonSerializer.Deserialize<FrozenReleaseCandidate>(release.CandidateBytes ?? [], CanonicalJson.Options) ?? throw Invalid(); }
        catch (JsonException) { throw Invalid(); }
        if (candidate.EnvironmentId != release.EnvironmentId || candidate.OrganizationId != scope.OrganizationId || candidate.ProjectId != scope.ProjectId || candidate.VersionIds.Count == 0) throw Invalid();
        var baseline = candidate.BaselineConfigVersion > 0 ? (await history.ReadAsync(release.EnvironmentId, candidate.BaselineConfigVersion, ct)).Snapshot : new WebApi.Contracts.Runtime.RuntimeSnapshot("2.0", release.EnvironmentId, 0, deployment.Snapshot.Snapshot.GeneratedAt, [], [], [], []);
        // Recovery reuses an existing exact snapshot: validate its original publish candidate.
        if (release.ReleaseType is "retry" or "rollback")
        {
            var origin = await db.Set<ReleaseRecord>().AsNoTracking().SingleOrDefaultAsync(r => r.EnvironmentId == release.EnvironmentId && r.ReleaseType == "publish" && r.ToConfigVersion == release.ToConfigVersion, ct) ?? throw Invalid();
            try { candidate = JsonSerializer.Deserialize<FrozenReleaseCandidate>(origin.CandidateBytes ?? [], CanonicalJson.Options) ?? throw Invalid(); } catch (JsonException) { throw Invalid(); }
            baseline = candidate.BaselineConfigVersion > 0 ? (await history.ReadAsync(release.EnvironmentId, candidate.BaselineConfigVersion, ct)).Snapshot : baseline with { ConfigVersion = 0 };
        }
        if (compiler.Compile(candidate, baseline, release.ToConfigVersion, deployment.Snapshot.Snapshot.GeneratedAt).Hash != deployment.Snapshot.Hash) throw Invalid();
        var apiContracts = new List<ArtifactApiContract>();
        foreach (var frozen in candidate.Versions)
        {
            var version = await db.Set<ApiVersion>().AsNoTracking().SingleOrDefaultAsync(v => v.Id == frozen.Version.Id, ct) ?? throw Invalid();
            if (version.ApiId != frozen.Api.Id || version.Revision != frozen.Version.Revision || version.Version != frozen.Version.Version || version.SealedAt is null) throw Invalid();
            var parameters = await db.Set<ApiParameter>().AsNoTracking().Where(p => p.ApiVersionId == version.Id).Select(p => new ParameterDto(p.Id, p.ApiVersionId, p.Location, p.Name, p.DataType, p.Required, p.Schema, p.Description, p.ExampleJson)).ToArrayAsync(ct);
            var schemas = await db.Set<ApiSchema>().AsNoTracking().Where(s => s.ApiVersionId == version.Id).Select(s => new SchemaDto(s.Id, s.ApiVersionId, s.SchemaType, s.Name, s.StatusCode, s.ContentType, s.SchemaJson, s.SchemaHash, s.ExampleJson)).ToArrayAsync(ct);
            if (!CanonicalJson.Serialize(parameters.OrderBy(p => p.Id)).AsSpan().SequenceEqual(CanonicalJson.Serialize(frozen.Parameters.OrderBy(p => p.Id))) || !CanonicalJson.Serialize(schemas.OrderBy(s => s.Id)).AsSpan().SequenceEqual(CanonicalJson.Serialize(frozen.Schemas.OrderBy(s => s.Id)))) throw Invalid();
            apiContracts.Add(new(frozen.Api.Id, version.Id, frozen.Version.Version, frozen.Version.Revision, frozen.Parameters, frozen.Schemas));
        }
        var routes = candidate.Routes.Where(r => r.Enabled).Select(r =>
        {
            var version = candidate.Versions.Single(v => v.Version.Id == r.ApiVersionId);
            var runtime = deployment.Snapshot.Snapshot.Routes.SingleOrDefault(actual => actual.Id == r.Id && actual.ApiVersionId == r.ApiVersionId) ?? throw Invalid();
            var templates = candidate.Bindings.Where(b => b.RouteId == r.Id).Select(b =>
            {
                var policy = candidate.Policies.Single(p => p.Id == b.PolicyId);
                return policy.Enabled ? ArtifactPolicyTemplates.Split(policy.Type, policy.Config) with { Priority = b.Priority } : null;
            }).Where(p => p is not null).Cast<ArtifactPolicyTemplate>().ToArray();
            var mode = runtime.AuthenticationMode == WebApi.Contracts.Policies.AuthenticationMode.JWT ? "JWT" : runtime.RequireApiKey ? "ApiKey" : "Anonymous";
            return new ArtifactRoute("", version.Api.Id, version.Version.Id, r.Path, r.Methods, r.Priority, true, null, templates, mode);
        }).ToArray();
        var content = ReleaseArtifactCanonicalizer.Normalize(new(apiContracts, routes));
        await RequireVisibilityAsync(content, releaseId, scope, actor, ct);
        foreach (var policy in candidate.Policies.Where(p => candidate.Bindings.Any(b => b.PolicyId == p.Id) && p.Enabled))
        {
            var actual = await db.Set<Policy>().AsNoTracking().SingleOrDefaultAsync(p => p.Id == (policy.SourcePolicyId ?? policy.Id), ct);
            if (actual is null || actual.OrganizationId != scope.OrganizationId || actual.ProjectId is Guid project && project != scope.ProjectId || !await policyAccess.CanReadAsync(new(actual.OrganizationId, actual.ProjectId), actor, ct)) throw ScopeResolver.Missing();
        }
        return (deployment, content);
    }

    internal async Task RequireVisibilityAsync(ArtifactContent content, Guid releaseId, ScopeRef scope, ActorContext actor, CancellationToken ct)
    {
        if (!await auth.CanAsync(actor, "release.read", new("release", releaseId, scope), ct) || !await auth.CanAsync(actor, "environment.read", new("environment", scope.EnvironmentId!.Value, scope), ct) || !await auth.CanAsync(actor, "route.read", new("environment", scope.EnvironmentId.Value, scope), ct)) throw ScopeResolver.Missing();
        foreach (var contract in content.Apis)
        {
            var actual = await scopes.ApiAsync(contract.ApiId, ct);
            if (actual.OrganizationId != scope.OrganizationId || actual.ProjectId != scope.ProjectId || !await db.Set<ApiVersion>().AnyAsync(v => v.Id == contract.VersionId && v.ApiId == contract.ApiId, ct)) throw ScopeResolver.Missing();
            foreach (var permission in new[] { "api.read", "api.version.read", "api.schema.read" }) if (!await auth.CanAsync(actor, permission, new("api", contract.ApiId, scope), ct)) throw ScopeResolver.Missing();
        }
        if (content.Routes.Any(r => r.Policies.Count > 0) && !await policyAccess.CanReadAsync(scope, actor, ct)) throw ScopeResolver.Missing();
    }

    private static ReleaseArtifactDto View(ReleaseArtifact row)
    {
        try
        {
            var content = JsonSerializer.Deserialize<ArtifactContent>(row.CanonicalContent, CanonicalJson.Options) ?? throw Invalid();
            if (ReleaseArtifactCanonicalizer.Hash(content) != row.ArtifactHash) throw Invalid();
            return new(row.Id, row.OrganizationId, row.ProjectId, row.SourceEnvironmentId, row.SourceReleaseId, row.ArtifactHash, row.SourceSnapshotHash, row.CreatedBy, row.CreatedAt, content);
        }
        catch (JsonException) { throw Invalid(); }
    }
    private static ApiException Invalid() => new(422, "corrupt_release_artifact_source", "来源冻结契约或快照完整性校验失败。");
}
