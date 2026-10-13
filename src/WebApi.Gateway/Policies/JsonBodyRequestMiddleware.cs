using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Timeouts;
using WebApi.Domain.Policies;
using WebApi.Infrastructure.Policies;
namespace WebApi.Gateway.Policies;
public sealed record BodyTransformFailure(int Status,string Reason,int? OperationIndex=null);
public sealed class JsonBodyRequestMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context,BodyTransformBudget budget,GatewayBodyTransformSettings settings)
    {
        var execution=TrafficExecutionContext.From(context)??throw new InvalidOperationException("Routing policy context unavailable.");
        var original=context.Request.Body;MemoryStream? replay=null;byte[]? encoded=null,decoded=null;
        try
        {
            var binding=(execution.Route.PolicyBindings??[]).SingleOrDefault(b=>execution.Generation.Transforms.ContainsKey(b.PolicyId));
            if(binding is null){await next(context);return;}
            var configuration=execution.Generation.Transforms[binding.PolicyId];
            var selected=configuration.RequestBody?.Methods.Contains(context.Request.Method,StringComparer.Ordinal)==true;
            if(!selected&&configuration.RequestBody is not null)execution.TransformBypassReason="body_method_not_selected";
            if(selected||configuration.ResponseBody is not null)
            {
                if(StreamUnsupported(context.Request)){await Reject(new(415,"transform_body_stream_unsupported"));return;}
                if(configuration.ResponseBody is not null&&new[]{"Range","If-Range","If-Match","If-None-Match","If-Modified-Since","If-Unmodified-Since"}.Any(context.Request.Headers.ContainsKey))
                {await Reject(new(400,"transform_body_condition_unsupported"));return;}
            }
            if(!selected){await next(context);return;}
            if(context.Request.Headers.ContainsKey("Signature")||context.Request.Headers.ContainsKey("Signature-Input")){await Reject(new(415,"transform_body_signature_unsupported"));return;}
            if(!JsonMedia(context.Request.ContentType)){await Reject(new(415,"transform_body_media_unsupported"));return;}
            var body=configuration.RequestBody!;var limit=Math.Min(body.MaxBodyBytes,settings.MaxBodyBytes);
            if(context.Request.ContentLength>limit){await Reject(new(413,"transform_body_too_large"));return;}
            if(!budget.TryAcquire(out var lease)){await Reject(new(503,"transform_body_busy"));return;}
            execution.BodyLease=lease;
            var routeTimeout=context.Features.Get<IHttpRequestTimeoutFeature>()?.RequestTimeoutToken??CancellationToken.None;
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted,routeTimeout);
            if(!routeTimeout.CanBeCanceled)deadline.CancelAfter(execution.Route.TimeoutMs);
            JsonBodyReadResult read;
            try{read=await BoundedJsonBodyCodec.ReadAsync(original,context.Request.Headers.ContentEncoding.Count==0?null:context.Request.Headers.ContentEncoding.ToString(),limit,deadline.Token);}
            catch(OperationCanceledException)when(routeTimeout.IsCancellationRequested)
            {execution.BodyTimeout=true;throw;}
            catch(OperationCanceledException)when(!context.RequestAborted.IsCancellationRequested&&!routeTimeout.IsCancellationRequested)
            {execution.BodyTimeout=true;context.Response.StatusCode=504;await context.Response.WriteAsJsonAsync(new{code="gateway_timeout",traceId=context.TraceIdentifier});return;}
            encoded=read.Encoded;decoded=read.Utf8;
            if(read.Reason is not null){await Reject(new(Status(read.Reason),read.Reason));return;}
            if(context.Features.Get<IHttpRequestTrailersFeature>() is {Available:true} trailers&&(trailers.Trailers.ContainsKey("Signature")||trailers.Trailers.ContainsKey("Signature-Input")))
            {await Reject(new(415,"transform_body_signature_unsupported"));return;}
            var mapped=JsonBodyMapper.Apply(decoded!,execution.Generation.RequestBodyMappings[binding.PolicyId],limit,deadline.Token);
            if(mapped.Reason is not null){await Reject(new(Status(mapped.Reason),mapped.Reason,mapped.OperationIndex));return;}
            execution.TransformedRequestBody=mapped.Utf8;
            replay=new MemoryStream(encoded!,false);context.Request.Body=replay;execution.OriginalRequestBody=replay;
            // BodyDetection, original length and media remain unchanged for retry eligibility and original-request semantics.
            await next(context);
        }
        finally
        {
            context.Request.Body=original;execution.ForwardingBodyStream?.Dispose();execution.ForwardingBodyStream=null;execution.OriginalRequestBody=null;replay?.Dispose();
            if(execution.TransformedRequestBody is { } mapped)CryptographicOperations.ZeroMemory(mapped);
            execution.TransformedRequestBody=null;
            if(execution.TransformedResponseBody is { } mappedResponse)CryptographicOperations.ZeroMemory(mappedResponse);
            execution.TransformedResponseBody=null;
            if(decoded is not null)CryptographicOperations.ZeroMemory(decoded);
            if(encoded is not null&&!ReferenceEquals(encoded,decoded))CryptographicOperations.ZeroMemory(encoded);
            execution.BodyLease?.Dispose();execution.BodyLease=null;
        }
        async Task Reject(BodyTransformFailure failure)
        {
            execution.TransformFailure=failure;execution.TransformRejectionReason=failure.Reason;
            context.Response.StatusCode=failure.Status;
            await context.Response.WriteAsJsonAsync(new{code="transform_rejected",reason=failure.Reason,operationIndex=failure.OperationIndex,traceId=context.TraceIdentifier});
        }
    }
    internal static bool JsonMedia(string? value)
    {
        if(!MediaTypeHeaderValue.TryParse(value,out var media)||media.MediaType is not { } type)return false;
        if(!(type.Equals("application/json",StringComparison.OrdinalIgnoreCase)||type.StartsWith("application/",StringComparison.OrdinalIgnoreCase)&&type.Length>"application/+json".Length&&type.EndsWith("+json",StringComparison.OrdinalIgnoreCase)&&!type.Contains('*')))return false;
        if(media.Parameters.Count(p=>p.Name.Equals("charset",StringComparison.OrdinalIgnoreCase))>1)return false;
        return string.IsNullOrEmpty(media.CharSet)||media.CharSet.Trim('"').Equals("utf-8",StringComparison.OrdinalIgnoreCase);
    }
    private static bool StreamUnsupported(HttpRequest request)=>request.Headers.ContainsKey("Upgrade")
        ||request.Headers.Connection.ToString().Split(',').Any(v=>v.Trim().Equals("upgrade",StringComparison.OrdinalIgnoreCase))
        ||request.ContentType?.StartsWith("application/grpc",StringComparison.OrdinalIgnoreCase)==true
        ||request.Headers.Accept.ToString().Split(',').Any(v=>v.Split(';')[0].Trim().Equals("text/event-stream",StringComparison.OrdinalIgnoreCase));
    private static int Status(string reason)=>reason switch{"transform_body_too_large"=>413,"transform_body_encoding_unsupported"=>415,"transform_json_operation_failed"=>422,_=>400};
}
