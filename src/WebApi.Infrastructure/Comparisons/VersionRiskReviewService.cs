using System.Text;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Comparisons;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Commands;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Security;
namespace WebApi.Infrastructure.Comparisons;
public sealed class VersionRiskReviewService(WebApiDbContext db,AuthorizationService auth,VersionComparisonService comparisons,AuditedCommandExecutor commands,IdempotentCommandExecutor idempotency,CommandRequestContext requestContext,ComparisonCursorCodec cursors)
{
    public async Task<RiskReviewDto> CreateAsync(Guid comparisonId,CreateRiskReviewRequest request,ActorContext actor,CancellationToken ct)
    {
        var original=await comparisons.ReadEntityAsync(comparisonId,actor,ct);var scope=new ScopeRef(original.OrganizationId,original.ProjectId,null);
        return await commands.ExecuteAsync(actor,scope,request.Decision=="AcceptedRisk"?"comparison.risk_accepted":"comparison.reviewed",async(_,token)=>{
            var entity=await comparisons.ReadEntityAsync(comparisonId,actor,token);if(entity.OrganizationId!=scope.OrganizationId||entity.ProjectId!=scope.ProjectId)throw ScopeResolver.Missing();
            await auth.RequireAsync(actor,"api.approve",new("api",entity.ApiId,scope),token);
            return await idempotency.ExecuteAsync(new(actor.UserId,scope,"comparison.reviewed",requestContext.IdempotencyKey),CanonicalJson.Serialize(new{comparisonId,request}),async inner=>{
                if(request.ExpectedReportHash!=entity.ReportHash)throw new ApiException(412,"stale_report_hash","报告已变化，请重新读取比较。");
                var report=VersionComparisonService.Report(entity);RequireDecision(report,request.Decision,request.Comment,request.ConfirmRisk);
                await RequireCurrentAsync(entity,actor,inner);
                var review=new ApiVersionRiskReview {ComparisonId=entity.Id,OrganizationId=entity.OrganizationId,ProjectId=entity.ProjectId,ApiId=entity.ApiId,InputFingerprint=entity.InputFingerprint,ReportHash=entity.ReportHash,Decision=request.Decision,Comment=request.Comment?.Trim(),ActorId=actor.UserId};db.Add(review);return VersionComparisonService.ReviewDto(review,entity.EngineVersion);
            },token);
        },ct);
    }
    private static void RequireDecision(ComparisonReport report,string decision,string? comment,bool confirm)
    {
        var count=(comment?.Trim()??"").EnumerateRunes().Count();
        if(report.Coverage=="Invalid"||decision is not("Reviewed" or "AcceptedRisk")||count>2000)throw new ApiException(422,"invalid_risk_review","报告无效、评审决定无效或说明超过 2,000 字。");
        if(decision=="Reviewed"&&(report.Coverage!="Complete"||report.Counts.Breaking>0||report.Counts.Unknown>0))throw new ApiException(422,"risk_acceptance_required","报告含破坏性或未知风险，需要确认接受风险。");
        if(decision=="AcceptedRisk"&&(!confirm||count<10))throw new ApiException(422,"risk_confirmation_required","接受风险需要明确勾选并填写 10–2,000 字说明。");
    }
    private async Task RequireCurrentAsync(ApiVersionComparison e,ActorContext actor,CancellationToken ct)
    {if(await comparisons.FreshnessAsync(e,actor,ct)!="Current")throw new ApiException(409,"risk_review_stale","版本或比较规则已变化，请重新比较并评审。");}
    public async Task<ComparisonPage<RiskReviewDto>> ListAsync(Guid comparisonId,string? cursor,int? limit,ActorContext actor,CancellationToken ct)
    {
        var entity=await comparisons.ReadEntityAsync(comparisonId,actor,ct);var take=Math.Clamp(limit??20,1,50);var query=db.Set<ApiVersionRiskReview>().AsNoTracking().Where(x=>x.ComparisonId==entity.Id&&x.ApiId==entity.ApiId&&x.OrganizationId==entity.OrganizationId&&x.ProjectId==entity.ProjectId);
        if(cursor is not null){var key=cursors.Decode(cursor,comparisonId,null);query=query.Where(x=>x.CreatedAt<key.CreatedAt||x.CreatedAt==key.CreatedAt&&x.Id.CompareTo(key.Id)<0);}
        var rows=await query.OrderByDescending(x=>x.CreatedAt).ThenByDescending(x=>x.Id).Take(take+1).ToArrayAsync(ct);var items=rows.Take(take).Select(x=>VersionComparisonService.ReviewDto(x,entity.EngineVersion)).ToArray();var last=items.LastOrDefault();return new(items,rows.Length>take&&last is not null?cursors.Encode(comparisonId,null,last.CreatedAt,last.Id):null);
    }
    public async Task<IReadOnlyList<FrozenRiskReviewReference>> ResolveReferencesAsync(ScopeRef environmentScope,IReadOnlyList<Guid> targetVersionIds,IReadOnlyList<Guid>? reviewIds,ActorContext actor,CancellationToken ct)
    {
        if(reviewIds is null||reviewIds.Count==0)return [];
        if(reviewIds.Count>100||reviewIds.Distinct().Count()!=reviewIds.Count)throw new ApiException(422,"invalid_risk_reviews","风险评审重复或超过限制。");
        var result=new List<FrozenRiskReviewReference>();var apis=new HashSet<Guid>();
        foreach(var id in reviewIds.OrderBy(x=>x))
        {
            var r=await db.Set<ApiVersionRiskReview>().AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,ct)??throw ScopeResolver.Missing();var e=await comparisons.ReadEntityAsync(r.ComparisonId,actor,ct);
            if(e.OrganizationId!=environmentScope.OrganizationId||e.ProjectId!=environmentScope.ProjectId||r.OrganizationId!=e.OrganizationId||r.ProjectId!=e.ProjectId||r.ApiId!=e.ApiId)throw ScopeResolver.Missing();
            if(!targetVersionIds.Contains(e.ToVersionId)||!apis.Add(e.ApiId))throw new ApiException(422,"risk_review_target_mismatch","每个 API 只能关联一份评审，且必须匹配发布目标版本。");
            var report=VersionComparisonService.Report(e);
            if(report.InputFingerprint!=e.InputFingerprint||report.EngineVersion!=e.EngineVersion||r.InputFingerprint!=e.InputFingerprint||r.ReportHash!=e.ReportHash)throw new ApiException(409,"risk_review_stale","评审证据与报告不一致。");
            RequireDecision(report,r.Decision,r.Comment,true);await RequireCurrentAsync(e,actor,ct);
            var summary=new RiskReviewSummaryDto(r.Id,e.Id,e.ApiId,e.FromVersionId,e.ToVersionId,e.FromVersion,e.ToVersion,r.Decision,r.ActorId,r.CreatedAt,e.InputFingerprint,e.ReportHash,e.EngineVersion,report.Coverage,report.Counts,r.Comment);
            result.Add(new(e.ApiId,e.ToVersionId,e.Id,r.Id,e.InputFingerprint,e.ReportHash,e.EngineVersion,summary));
        }
        return result;
    }
    public async Task ValidateReferencesAsync(ScopeRef environmentScope,IReadOnlyList<Guid> targetVersionIds,IReadOnlyList<FrozenRiskReviewReference>? references,ActorContext actor,CancellationToken ct)
    {
        if(references is null||references.Count==0)return;
        var resolved=await ResolveReferencesAsync(environmentScope,targetVersionIds,references.Select(x=>x.ReviewId).ToArray(),actor,ct);
        foreach(var trusted in resolved){var stored=references.Single(x=>x.ReviewId==trusted.ReviewId);if(!ContractNormalizer.CanonicalBytes(trusted).AsSpan().SequenceEqual(ContractNormalizer.CanonicalBytes(stored)))throw new ApiException(409,"risk_review_stale","冻结评审证据完整性校验失败。");}
    }
}
