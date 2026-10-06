using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Comparisons;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Catalog;
using WebApi.Infrastructure.Commands;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Security;
namespace WebApi.Infrastructure.Comparisons;
public sealed class VersionComparisonService(WebApiDbContext db,AuthorizationService auth,ScopeResolver scopes,CatalogService catalog,AuditedCommandExecutor commands,IdempotentCommandExecutor idempotency,CommandRequestContext requestContext,ContractComparisonEngine engine,ComparisonCursorCodec cursors)
{
    public async Task<ScopeRef> RequireReadAsync(Guid apiId,ActorContext actor,CancellationToken ct)
    {var scope=await scopes.ApiAsync(apiId,ct);foreach(var code in new[]{"api.read","api.version.read","api.schema.read"})if(!await auth.CanAsync(actor,code,new("api",apiId,scope),ct))throw ScopeResolver.Missing();return scope;}
    public async Task<ComparisonInput> ReadSnapshotAsync(Guid apiId,Guid fromVersionId,Guid toVersionId,ActorContext actor,CancellationToken ct)
    {
        await RequireReadAsync(apiId,actor,ct);var a=await catalog.VersionAsync(fromVersionId,actor,ct);var b=await catalog.VersionAsync(toVersionId,actor,ct);
        if(a.ApiId!=apiId||b.ApiId!=apiId||a.Id==b.Id)throw new ApiException(422,"invalid_comparison_pair","必须选择同一 API 的两个不同版本。");
        var ap=await catalog.ParametersAsync(a.Id,actor,ct);var ass=await catalog.SchemasAsync(a.Id,actor,ct);var bp=await catalog.ParametersAsync(b.Id,actor,ct);var bs=await catalog.SchemasAsync(b.Id,actor,ct);return new(new(a,ap,ass),new(b,bp,bs));
    }
    public async Task<VersionComparisonView> CreateAsync(Guid apiId,CreateVersionComparisonRequest request,ActorContext actor,CancellationToken ct)
    {
        var scope=await RequireReadAsync(apiId,actor,ct);
        return await commands.ExecuteAsync(actor,scope,"comparison.created",async(_,token)=>{
            var actual=await RequireReadAsync(apiId,actor,token);if(actual!=scope)throw ScopeResolver.Missing();
            return await idempotency.ExecuteAsync(new(actor.UserId,scope,"comparison.created",requestContext.IdempotencyKey),CanonicalJson.Serialize(new {apiId,request}),async inner=>{
                var input=await ReadSnapshotAsync(apiId,request.FromVersionId,request.ToVersionId,actor,inner);
                if(input.From.Version.Revision!=request.ExpectedFromRevision||input.To.Version.Revision!=request.ExpectedToRevision)throw new ApiException(412,"stale_comparison_revision","版本已变化，请刷新后重新比较。");
                var report=engine.Compare(input,new(),inner);var reportBytes=ContractNormalizer.CanonicalBytes(report);
                var entity=new ApiVersionComparison {OrganizationId=scope.OrganizationId,ProjectId=scope.ProjectId!.Value,ApiId=apiId,FromVersionId=request.FromVersionId,ToVersionId=request.ToVersionId,FromRevision=input.From.Version.Revision,ToRevision=input.To.Version.Revision,FromVersion=input.From.Version.Version,ToVersion=input.To.Version.Version,EngineVersion=report.EngineVersion,InputFingerprint=report.InputFingerprint,ReportHash=ContractNormalizer.Hash(reportBytes),Coverage=report.Coverage,CountsJson=JsonSerializer.Serialize(report.Counts,CanonicalJson.Options),InputBytes=CanonicalJson.Serialize(input),ReportBytes=reportBytes,CreatedBy=actor.UserId};db.Add(entity);
                return View(entity,input,report,"Current",null);
            },token,ContractNormalizer.JsonOptions);
        },ct);
    }
    public async Task<ApiVersionComparison> ReadEntityAsync(Guid id,ActorContext actor,CancellationToken ct)
    {
        var entity=await db.Set<ApiVersionComparison>().AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,ct)??throw ScopeResolver.Missing();var scope=await RequireReadAsync(entity.ApiId,actor,ct);if(scope.OrganizationId!=entity.OrganizationId||scope.ProjectId!=entity.ProjectId)throw ScopeResolver.Missing();return entity;
    }
    public static ComparisonReport Report(ApiVersionComparison entity)
    {if(ContractNormalizer.Hash(entity.ReportBytes)!=entity.ReportHash)throw new ApiException(409,"comparison_corrupted","比较证据完整性校验失败。");return JsonSerializer.Deserialize<ComparisonReport>(entity.ReportBytes,ContractNormalizer.JsonOptions)??throw new ApiException(409,"comparison_corrupted","比较证据无法读取。");}
    public async Task<string> FreshnessAsync(ApiVersionComparison entity,ActorContext actor,CancellationToken ct)
    {
        if(entity.EngineVersion!=ContractComparisonEngine.EngineVersion)return "Stale";
        try{return ContractNormalizer.Fingerprint(await ReadSnapshotAsync(entity.ApiId,entity.FromVersionId,entity.ToVersionId,actor,ct),entity.EngineVersion)==entity.InputFingerprint?"Current":"Stale";}
        catch(ApiException e) when(e.Status==404){return "Missing";}
        catch(ApiException e) when(e.Status==422){return "Stale";}
    }
    public async Task<VersionComparisonView> GetAsync(Guid comparisonId,ActorContext actor,CancellationToken ct)
    {
        var owned=db.Database.CurrentTransaction is null?await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead,ct):null;
        try{var e=await ReadEntityAsync(comparisonId,actor,ct);var input=JsonSerializer.Deserialize<ComparisonInput>(e.InputBytes,CanonicalJson.Options)??throw new ApiException(409,"comparison_corrupted","输入快照无法读取。");var latest=await db.Set<ApiVersionRiskReview>().AsNoTracking().Where(x=>x.ComparisonId==e.Id).OrderByDescending(x=>x.CreatedAt).ThenByDescending(x=>x.Id).FirstOrDefaultAsync(ct);var result=View(e,input,Report(e),await FreshnessAsync(e,actor,ct),latest is null?null:ReviewDto(latest,e.EngineVersion));if(owned is not null)await owned.CommitAsync(ct);return result;}
        finally{if(owned is not null)await owned.DisposeAsync();}
    }
    private static VersionComparisonView View(ApiVersionComparison e,ComparisonInput input,ComparisonReport report,string freshness,RiskReviewDto? latest)
    {ComparisonVersion Version(ContractVersionInput v)=>new(v.Version.Id,v.Version.Version,v.Version.Status,v.Version.ChangeType,v.Version.Revision);return new(e.Id,e.ApiId,Version(input.From),Version(input.To),e.ReportHash,report,e.CreatedAt,freshness,latest);}
    public static RiskReviewDto ReviewDto(ApiVersionRiskReview e,string engineVersion)=>new(e.Id,e.ComparisonId,e.ApiId,e.InputFingerprint,e.ReportHash,engineVersion,e.Decision,e.Comment,e.ActorId,e.CreatedAt);
    public async Task<ComparisonPage<ComparisonSummaryDto>> ListAsync(Guid apiId,string? cursor,int? limit,Guid? reviewId,ActorContext actor,CancellationToken ct)
    {
        var scope=await RequireReadAsync(apiId,actor,ct);var take=Math.Clamp(limit??20,1,50);var query=db.Set<ApiVersionComparison>().AsNoTracking().Where(x=>x.ApiId==apiId&&x.OrganizationId==scope.OrganizationId&&x.ProjectId==scope.ProjectId);
        if(cursor is not null){var key=cursors.Decode(cursor,apiId,reviewId);query=query.Where(x=>x.CreatedAt<key.CreatedAt||x.CreatedAt==key.CreatedAt&&x.Id.CompareTo(key.Id)<0);}
        if(reviewId is Guid id)query=query.Where(x=>db.Set<ApiVersionRiskReview>().Any(r=>r.Id==id&&r.ComparisonId==x.Id));
        var rows=await query.OrderByDescending(x=>x.CreatedAt).ThenByDescending(x=>x.Id).Take(take+1).Select(x=>new {x.Id,x.ApiId,x.FromVersionId,x.ToVersionId,x.FromVersion,x.ToVersion,x.Coverage,x.CountsJson,x.EngineVersion,x.CreatedAt}).ToArrayAsync(ct);
        var items=rows.Take(take).Select(x=>new ComparisonSummaryDto(x.Id,x.ApiId,x.FromVersionId,x.ToVersionId,x.FromVersion,x.ToVersion,x.Coverage,JsonSerializer.Deserialize<ComparisonCounts>(x.CountsJson,CanonicalJson.Options)!,x.EngineVersion,x.CreatedAt)).ToArray();var last=items.LastOrDefault();return new(items,rows.Length>take&&last is not null?cursors.Encode(apiId,reviewId,last.CreatedAt,last.Id):null);
    }
    public async Task<ComparisonExport> ExportAsync(Guid id,string format,ActorContext actor,CancellationToken ct)=>ComparisonExporter.Export(await GetAsync(id,actor,ct),format);
}
