using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Catalog;
using WebApi.Contracts.Common;
using WebApi.Contracts.OpenApi;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Commands;
using WebApi.Infrastructure.Contracts;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Catalog;
internal sealed record ImportCommittedMetadata(string ReceiptKey,IReadOnlyList<ImportTarget> Targets,IReadOnlyList<ImportedOperationDto> Operations);
public sealed class ImportPreviewService(WebApiDbContext db,ImportBatchWriter writer,ImportSourcePolicyService policies,ImportSourceFetcher fetcher,ImportSourceSettings settings,
    ScopeResolver scopes,AuditedCommandExecutor commands,IdempotentCommandExecutor idempotency,CommandRequestContext requestContext,TimeProvider clock)
{
    public async Task<ImportSessionDto> CreateAsync(CreateImportPreviewRequest request,ActorContext actor,CancellationToken ct)
    {
        var input=new ImportPreviewRequest(request.ProjectId,request.EnvironmentId,request.ClusterId,"");var scope=await writer.ScopeAsync(input,actor,ct);
        var policy=await policies.CurrentAsync(request.ProjectId,ct);var source=await new ImportBundleReader(fetcher,settings).ReadAsync(request,policy,ct);
        var preview=OpenApiImportService.Describe(new(source.Bundle,policy.Limits));var root=source.Bundle.Documents.Single(x=>x.Source.LogicalUri==source.Bundle.RootUri);
        return await commands.ExecuteAsync(actor,scope,"openapi.preview.create",async(_,token)=>{
            await writer.ScopeAsync(input,actor,token);var current=await policies.CurrentAsync(request.ProjectId,token);if(current.Revision!=policy.Revision)throw Changed();
            var now=clock.GetUtcNow();var valid=db.Set<ApiImportPreview>().Where(x=>x.ProjectId==request.ProjectId&&x.Status=="Active"&&x.ExpiresAt>now);
            if(await valid.CountAsync(token)>=100||await valid.CountAsync(x=>x.ActorId==actor.UserId,token)>=5)throw new ApiException(429,"import_preview_quota","有效预览配额已满，请撤销旧预览或稍后再试。");
            var row=new ApiImportPreview{OrganizationId=scope.OrganizationId,ProjectId=request.ProjectId,EnvironmentId=request.EnvironmentId,ClusterId=request.ClusterId,ActorId=actor.UserId,SourcePolicyRevision=policy.Revision,
                BundleJson=ContractBundleCodec.Encode(source.Bundle,policy.Limits),SourceHash=ContractBundleCodec.Hash(root.Source.RawText),BundleHash=source.Bundle.Hash,SourceFormat=root.Source.Format,Dialect=root.Dialect.ToString(),CreatedAt=now,ExpiresAt=now.AddMinutes(20)};
            var dto=new ImportSessionDto(row.Id,row.SourceHash,row.BundleHash,row.Dialect,row.ExpiresAt,row.SourcePolicyRevision,preview.Operations,source.Issues,root.Source.Format);row.PreviewJson=JsonSerializer.Serialize(dto,CanonicalJson.Options);db.Add(row);return dto;
        },ct);
    }
    public async Task<ImportSessionDto> GetAsync(Guid id,ActorContext actor,CancellationToken ct)
    {
        var row=await OwnedAsync(id,actor,ct);await writer.ScopeAsync(Input(row),actor,ct);if(row.PreviewJson is null||row.BundleJson is null)throw ScopeResolver.Missing();if(row.Status!="Active"||row.ExpiresAt<=clock.GetUtcNow())throw Changed();
        return JsonSerializer.Deserialize<ImportSessionDto>(row.PreviewJson,CanonicalJson.Options)??throw Changed();
    }
    public async Task<ImportCommitResponse> CommitAsync(Guid id,CommitImportSessionRequest request,ActorContext actor,CancellationToken ct)
    {
        var initial=await OwnedAsync(id,actor,ct);var scope=await scopes.EnvironmentAsync(initial.EnvironmentId,ct);
        return await commands.ExecuteAsync(actor,scope,"openapi.import",async(_,token)=>{
            // Re-read after the shared governance transaction lock, including cleanup/revoke writers.
            var row=await db.Set<ApiImportPreview>().SingleOrDefaultAsync(x=>x.Id==id&&x.ActorId==actor.UserId,token)??throw ScopeResolver.Missing();var input=Input(row);
            await writer.RequireTargetsAsync(request.Targets,input,actor,token);
            if(row.CommittedTargetsJson is string metadataJson){var metadata=JsonSerializer.Deserialize<ImportCommittedMetadata>(metadataJson,CanonicalJson.Options)??throw Changed();
                // Replaying a receipt still requires current ownership of every persisted result.
                foreach(var op in metadata.Operations){var target=await scopes.VersionAsync(op.VersionId,token);if(target.OrganizationId!=row.OrganizationId||target.ProjectId!=row.ProjectId||!await db.Set<ApiRoute>().AnyAsync(r=>r.Id==op.RouteId&&r.ApiVersionId==op.VersionId&&r.EnvironmentId==row.EnvironmentId,token))throw ScopeResolver.Missing();}
            }
            var identity=new CommandIdentity(actor.UserId,scope,"openapi.import-session",requestContext.IdempotencyKey);var normalized=CanonicalJson.Serialize(new{id,request});
            return await idempotency.ExecuteAsync(identity,normalized,async inner=>{
                if(row.Status!="Active"||row.ExpiresAt<=clock.GetUtcNow()||row.BundleJson is null||row.BundleHash!=request.ExpectedBundleHash)throw Changed();
                var policy=await policies.CurrentAsync(row.ProjectId,inner);if(policy.Revision!=row.SourcePolicyRevision)throw Changed();
                var bundle=ContractBundleCodec.Decode(row.BundleJson,policy.Limits);ContractBundleCodec.Verify(bundle,policy.Limits);
                if(bundle.Hash!=row.BundleHash)throw Changed();var result=await writer.WriteAsync(bundle,input,request.Targets,actor,inner);
                row.Status="Committed";row.ImportId=result.ImportId;row.ExpiresAt=clock.GetUtcNow().AddMinutes(20);row.CommittedTargetsJson=JsonSerializer.Serialize(new ImportCommittedMetadata(requestContext.IdempotencyKey,request.Targets,result.Operations),CanonicalJson.Options);row.Revision++;
                // Existing receipts have no expiry. Keep minimum authorization metadata while their receipt exists.
                row.ReceiptExpiresAt=null;return result;
            },token);
        },ct);
    }
    public async Task RevokeAsync(Guid id,ActorContext actor,CancellationToken ct)
    {
        var initial=await OwnedAsync(id,actor,ct);var scope=await scopes.EnvironmentAsync(initial.EnvironmentId,ct);
        await commands.ExecuteAsync(actor,scope,"openapi.preview.revoke",async(_,token)=>{
            var row=await db.Set<ApiImportPreview>().SingleOrDefaultAsync(x=>x.Id==id&&x.ActorId==actor.UserId,token)??throw ScopeResolver.Missing();await writer.ScopeAsync(Input(row),actor,token);
            if(row.Status!="Active")throw Changed();row.Status="Revoked";row.Revision++;return true;
        },ct);
    }
    private async Task<ApiImportPreview> OwnedAsync(Guid id,ActorContext actor,CancellationToken ct)=>await db.Set<ApiImportPreview>().AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id&&x.ActorId==actor.UserId,ct)??throw ScopeResolver.Missing();
    private static ImportPreviewRequest Input(ApiImportPreview row)=>new(row.ProjectId,row.EnvironmentId,row.ClusterId,"");
    private static ApiException Changed()=>new(409,"import_preview_changed","预览已过期、撤销、提交或来源/哈希变化，请重新预览。");
}
