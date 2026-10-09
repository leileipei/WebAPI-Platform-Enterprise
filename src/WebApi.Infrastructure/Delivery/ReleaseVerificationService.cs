using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Releases;
using WebApi.Contracts.Security;
using WebApi.Domain.Delivery;
using WebApi.Infrastructure.Commands;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Security;

namespace WebApi.Infrastructure.Delivery;

public sealed class ReleaseVerificationService(WebApiDbContext db, ReleaseArtifactService artifacts, VerificationReportStore reports,
    ScopeResolver scopes, AuthorizationService auth, AuditedCommandExecutor commands, IdempotentCommandExecutor idempotency, CommandRequestContext requestContext)
{
    private static readonly string[] sourceTypes = ["InterfaceFunction", "Integration", "ContractCompatibility"];
    public async Task<ReleaseVerificationDto> RecordSourceAsync(Guid artifactId, RecordVerificationRequest request, ActorContext actor, CancellationToken ct)
    {
        var artifact = await artifacts.GetAsync(artifactId, actor, ct);
        var scope = await scopes.EnvironmentAsync(artifact.SourceEnvironmentId, ct);
        return await commands.ExecuteAsync(actor, scope, "release.verification.source", async (_, token) =>
        {
            await auth.RequireAsync(actor, "release.test.record", new("environment", artifact.SourceEnvironmentId, scope), token);
            Validate(request, sourceTypes);
            return await idempotency.ExecuteAsync(new(actor.UserId, scope, "release.verification.source", requestContext.IdempotencyKey), CanonicalJson.Serialize(new { artifactId, request }), async inner =>
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM environments WHERE id={artifact.SourceEnvironmentId} FOR UPDATE", inner);
                // Recheck visibility inside the governance transaction, then bind every runtime fact on the server.
                var actual = await artifacts.GetAsync(artifactId, actor, inner);
                var source = await artifacts.RequireSourceAsync(actual.SourceReleaseId, actor, inner);
                if (source.Deployment.Release.CompletedAt is not DateTimeOffset completedAt || request.FinishedAt < completedAt) throw new ApiException(422, "verification_predates_deployment", "来源测试完成时间不能早于本次全节点确认。");
                if (source.Deployment.Snapshot.Hash != actual.SourceSnapshotHash || ReleaseArtifactCanonicalizer.Hash(source.Content) != actual.ArtifactHash) throw new ApiException(409, "artifact_source_changed", "来源运行内容已变化，请重新生成制品。");
                var policy = await db.Set<ProjectDeliveryPolicy>().AsNoTracking().SingleOrDefaultAsync(p => p.ProjectId == artifact.ProjectId, inner);
                if (policy is not null && policy.SourceEnvironmentId != artifact.SourceEnvironmentId) throw new ApiException(422, "delivery_source_mismatch", "制品不属于当前项目连接的来源环境。");
                VerificationReport? report = null;
                if (request.ReportId is Guid reportId)
                {
                    report = await reports.GetMetadataAsync(reportId, actor, inner);
                    if (report.ArtifactId != artifactId || report.PromotionId is not null || report.EnvironmentId != artifact.SourceEnvironmentId) throw ScopeResolver.Missing();
                }
                var environment = source.Deployment.Environment;
                var row = new ReleaseVerification
                {
                    OrganizationId = artifact.OrganizationId, ProjectId = artifact.ProjectId, ArtifactId = artifactId,
                    ReleaseId = source.Deployment.Release.Id, EnvironmentId = environment.Id,
                    ConfigVersion = source.Deployment.Release.ToConfigVersion, DeploymentSequence = source.Deployment.Release.DeploymentSequence!.Value,
                    SnapshotHash = source.Deployment.Snapshot.Hash, AccessAddressRevision = environment.AccessAddressRevision,
                    AccessContextJson = System.Text.Encoding.UTF8.GetString(CanonicalJson.Serialize(new { environmentId = environment.Id, accessAddressRevision = environment.AccessAddressRevision, publicOrigin = environment.GatewayPublicUrl, basePath = environment.BasePath })),
                    PolicyRevision = policy?.Revision ?? 0, Phase = "SourceTest", Type = request.Type, Result = request.Result, IsManual = true,
                    ReportId = report?.Id, ReportHash = report?.Sha256, Comment = request.Comment ?? "",
                    StartedAt = request.StartedAt.ToUniversalTime(), FinishedAt = request.FinishedAt.ToUniversalTime(),
                    ExpiresAt = request.FinishedAt.ToUniversalTime().AddMinutes(policy?.VerificationValidityMinutes ?? 1440), CreatedBy = actor.UserId
                };
                db.Add(row); return View(row);
            }, token);
        }, ct);
    }
    public async Task<IReadOnlyList<ReleaseVerificationDto>> ListSourceAsync(Guid artifactId, ActorContext actor, CancellationToken ct)
    {
        await artifacts.GetAsync(artifactId, actor, ct);
        var rows = await db.Set<ReleaseVerification>().AsNoTracking().Where(v => v.ArtifactId == artifactId && v.Phase == "SourceTest").OrderByDescending(v => v.CreatedAt).ThenBy(v => v.Id).Take(100).ToArrayAsync(ct);
        return rows.Select(View).ToArray();
    }
    internal static void Validate(RecordVerificationRequest request, IReadOnlyCollection<string> allowed)
    {
        if (!allowed.Contains(request.Type) || request.Result is not ("Passed" or "Failed") || request.StartedAt > request.FinishedAt || request.FinishedAt > DateTimeOffset.UtcNow || request.FinishedAt > DateTimeOffset.MaxValue.AddDays(-8) || (request.Comment?.Length ?? 0) > 4000) throw new ApiException(422, "invalid_verification", "验证类型、结果、起止时间或说明不合法。");
    }
    public static ReleaseVerificationDto View(ReleaseVerification row) => new(row.Id, row.ArtifactId, row.PromotionId, row.ReleaseId, row.EnvironmentId, row.ConfigVersion, row.DeploymentSequence, row.SnapshotHash, row.AccessAddressRevision, row.PolicyRevision, row.Phase, row.Type, row.Result, row.IsManual, row.ReportId, row.ReportHash, row.Comment, row.StartedAt, row.FinishedAt, row.ExpiresAt, row.CreatedBy, row.CreatedAt);
}
