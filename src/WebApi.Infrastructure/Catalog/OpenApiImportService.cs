using WebApi.Contracts.Common;
using WebApi.Contracts.Catalog;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Commands;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Contracts;
namespace WebApi.Infrastructure.Catalog;
public sealed class OpenApiImportService(ImportBatchWriter writer,ScopeResolver scopes,AuditedCommandExecutor commands,IdempotentCommandExecutor idempotency,CommandRequestContext requestContext,ImportSourcePolicyService policies)
{
    internal static ImportPreviewResponse Describe(OpenApiOperationParser parser)
    {
        var items=new List<ImportOperationDto>();foreach(var(id,method,path)in parser.Operations()){
            try{var op=parser.Parse(id);items.Add(new(id,method,path,op.Summary,op.SuggestedCode,true,op.Warnings,op.Parameters.Count,op.Schemas.Count));}
            catch(ApiException e){items.Add(new(id,method,path,id,"",false,[e.Code+": "+e.Message],0,0));}
        }
        return new(items,["固定来源包保留引用和原文；导入只写草稿和默认API Key路由，提交再次验证权限、目标和路由冲突。"]);
    }
    public async Task<ImportPreviewResponse> PreviewAsync(ImportPreviewRequest request,ActorContext actor,CancellationToken ct=default)
    {
        await writer.ScopeAsync(request,actor,ct);var policy=await policies.CurrentAsync(request.ProjectId,ct);var bundle=OpenApiOperationParser.Read(request.Source,policy.Limits);return Describe(new(bundle,policy.Limits));
    }
    public async Task<ImportCommitResponse> CommitAsync(ImportCommitRequest request,ActorContext actor,CancellationToken ct=default)
    {
        var scope=await scopes.EnvironmentAsync(request.Input.EnvironmentId,ct);return await commands.ExecuteAsync(actor,scope,"openapi.import",async(_,token)=>{
            await writer.RequireTargetsAsync(request.Targets,request.Input,actor,token);
            return await idempotency.ExecuteAsync(new(actor.UserId,scope,"openapi.import",requestContext.IdempotencyKey),CanonicalJson.Serialize(request),async inner=>{var policy=await policies.CurrentAsync(request.Input.ProjectId,inner);return await writer.WriteAsync(OpenApiOperationParser.Read(request.Input.Source,policy.Limits),request.Input,request.Targets,actor,inner);},token);
        },ct);
    }
}
