using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using WebApi.Contracts.Common;
using WebApi.Contracts.CredentialUsage;
using WebApi.ControlPlane.Security;
using WebApi.Domain.CredentialUsage;
using WebApi.Infrastructure.CredentialUsage;
using WebApi.Infrastructure.Gateway;
namespace WebApi.ControlPlane.Gateway;

public static class CredentialUsageEndpoints
{
    public static void MapCredentialUsage(this WebApplication app)
    {
        var group=app.MapGroup("/internal/v1/nodes/{id:guid}/credential-usage").AllowAnonymous();
        group.MapPost("/collectors",(Guid id,HttpContext context,NodeIdentityService identities,CredentialUsageRetentionOptions options,[FromServices] UsageCollectorRegistry registry)
            =>ExecuteAsync(id,context,identities,options,(identity,ct)=>registry.ReceiveAsync(identity,readCt=>ReadAsync<UsageCollectorRequest>(context,readCt),ct)));
        group.MapPost("/batches",(Guid id,HttpContext context,NodeIdentityService identities,CredentialUsageRetentionOptions options,CredentialUsageIngestService service)
            =>ExecuteAsync(id,context,identities,options,(identity,ct)=>service.ReceiveAsync(identity,readCt=>ReadAsync<UsageBatchRequest>(context,readCt),ct)));
    }
    private static async Task<IResult> ExecuteAsync(Guid id,HttpContext context,NodeIdentityService identities,CredentialUsageRetentionOptions options,Func<NodeIdentity,CancellationToken,Task<UsageReceipt>> operation)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);timeout.CancelAfter(TimeSpan.FromSeconds(options.IngestTimeoutSeconds));
        try{return Results.Ok(await operation(await AuthenticateAsync(id,context,identities,timeout.Token),timeout.Token));}
        catch(UsageSequenceException error){return Results.Problem(statusCode:error.Status,title:error.Message,extensions:new Dictionary<string,object?>{{"code",error.Code},{"nextExpectedSequence",error.NextExpectedSequence},{"traceId",context.TraceIdentifier}});}
        catch(OperationCanceledException) when(!context.RequestAborted.IsCancellationRequested){throw new ApiException(503,"usage_ingest_timeout","统计接收超过处理预算。");}
    }
    private static Task<NodeIdentity> AuthenticateAsync(Guid id,HttpContext context,NodeIdentityService identities,CancellationToken ct)
    {
        context.Response.Headers.CacheControl="no-store";var bearer=context.Request.Headers.Authorization.ToString();
        if(!bearer.StartsWith("Bearer ",StringComparison.Ordinal)||bearer.Length>1031)throw NodeIdentityService.Denied();
        return identities.AuthenticateAsync(id,bearer[7..],ct);
    }
    private static async Task<T> ReadAsync<T>(HttpContext context,CancellationToken ct)
    {
        const int maximum=UsageBatchRules.MaximumBytes;
        static ApiException Large()=>new(413,"usage_batch_too_large","统计请求超过256KiB限制。");
        if(context.Request.ContentLength>maximum)throw Large();
        using var buffer=new MemoryStream();var bytes=new byte[8192];
        while(true){var read=await context.Request.Body.ReadAsync(bytes,ct);if(read==0)break;if(buffer.Length+read>maximum)throw Large();buffer.Write(bytes,0,read);}
        try
        {
            using var json=JsonDocument.Parse(buffer.GetBuffer().AsMemory(0,checked((int)buffer.Length)),new JsonDocumentOptions{MaxDepth=8});
            if(json.RootElement.ValueKind!=JsonValueKind.Object)throw new JsonException();
            Unique(json.RootElement);return json.RootElement.Deserialize<T>(UsageProtocolJson.Options)??throw new JsonException();
        }
        catch(JsonException){throw new ApiException(422,"invalid_usage_request","统计1.0请求结构不合法。");}
    }
    private static void Unique(JsonElement value)
    {
        if(value.ValueKind==JsonValueKind.Object){var fields=new HashSet<string>(StringComparer.Ordinal);foreach(var property in value.EnumerateObject()){if(!fields.Add(property.Name))throw new JsonException();Unique(property.Value);}}
        else if(value.ValueKind==JsonValueKind.Array)foreach(var item in value.EnumerateArray())Unique(item);
    }
}
