using System.Text;
using WebApi.Contracts.Common;
using WebApi.Contracts.Policies;
using WebApi.Contracts.Security;
using WebApi.Domain.Policies;
using WebApi.Infrastructure.Governance;

namespace WebApi.Infrastructure.Policies;

public sealed class TransformPreviewService(PolicyAccess access, GatewayPolicyDeploymentRules deployment)
{
    public async Task<TransformPreviewDto> PreviewAsync(TransformPreviewRequest request, ActorContext actor, CancellationToken ct)
    {
        var scope = await access.ResolveAsync(request.Scope, ct);
        if (!await access.CanReadAsync(scope, actor, ct)) throw ScopeResolver.Missing();
        var config = PolicyConfigurationValidator.ParseTransform(request.Config);
        deployment.Validate("transform", request.Config);
        IReadOnlyList<JsonBodyOperation> operations; int maxBytes;
        if (request.Direction == "Request" && config.RequestBody is {} input)
        { operations = input.Operations; maxBytes = input.MaxBodyBytes; }
        else if (request.Direction == "Response" && config.ResponseBody is {} output)
        { operations = output.Operations; maxBytes = output.MaxBodyBytes; }
        else throw new ApiException(422, "invalid_request", "预览方向不合法或该方向未启用正文映射。");
        ct.ThrowIfCancellationRequested();
        if (Encoding.UTF8.GetByteCount(request.SampleJson) > maxBytes) throw new TransformPreviewException(413, "transform_body_too_large");
        var mapping = JsonBodyMapper.Compile(operations);
        var result = JsonBodyMapper.Apply(Encoding.UTF8.GetBytes(request.SampleJson), mapping, maxBytes, ct);
        if (result.Reason is {} reason)
        {
            var status = reason switch { "transform_body_too_large" => 413, "transform_json_invalid" => 400, _ => 422 };
            throw new TransformPreviewException(status, reason, result.OperationIndex);
        }
        return new(Encoding.UTF8.GetString(result.Utf8!), mapping.OperationCount);
    }
}
public sealed class TransformPreviewException(int status, string reason, int? operationIndex = null)
    : Exception("JSON正文映射预览未完成。")
{
    public int Status { get; } = status;
    public string Code => "transform_rejected";
    public string Reason { get; } = reason;
    public int? OperationIndex { get; } = operationIndex;
}
