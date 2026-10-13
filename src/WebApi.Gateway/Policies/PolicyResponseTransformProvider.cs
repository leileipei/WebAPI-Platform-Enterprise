using System.Security.Cryptography;
using WebApi.Domain.Policies;
using WebApi.Contracts.Common;
using WebApi.Infrastructure.Policies;
using Microsoft.AspNetCore.Http.Timeouts;
using Yarp.ReverseProxy.Transforms;
using Yarp.ReverseProxy.Transforms.Builder;
namespace WebApi.Gateway.Policies;
public sealed class PolicyResponseTransformProvider(BodyTransformBudget budget,GatewayBodyTransformSettings settings) : ITransformProvider
{
    public void ValidateRoute(TransformRouteValidationContext context) { }
    public void ValidateCluster(TransformClusterValidationContext context) { }
    public void Apply(TransformBuilderContext context)=>Configure(context);
    public void Configure(TransformBuilderContext context)=>context.AddResponseTransform(async transform=>
    {
        var http=transform.HttpContext;var execution=TrafficExecutionContext.From(http);var response=transform.ProxyResponse;
        if(execution?.TransformPlan is null||response is null)return;
        var binding=(execution.Route.PolicyBindings??[]).Single(b=>execution.Generation.Transforms.ContainsKey(b.PolicyId));
        var body=execution.Generation.Transforms[binding.PolicyId].ResponseBody;
        if(body is null)return;
        if(!body.StatusCodes.Contains((int)response.StatusCode)){execution.TransformBypassReason="body_status_not_selected";return;}
        if(response.Headers.NonValidated.Any(h=>h.Key.Equals("Signature",StringComparison.OrdinalIgnoreCase)||h.Key.Equals("Signature-Input",StringComparison.OrdinalIgnoreCase)))
        {await Reject(new(502,"transform_body_signature_unsupported"));return;}
        if(response.Headers.NonValidated.Any(h=>h.Key.Equals("Trailer",StringComparison.OrdinalIgnoreCase))||response.TrailingHeaders.Any())
        {await Reject(new(502,"transform_body_trailer_unsupported"));return;}
        if(HttpMethods.IsHead(http.Request.Method))
        {
            RemoveRepresentation(response,http,keepMedia:true);transform.SuppressResponseBody=true;execution.TransformBypassReason="head_response";return;
        }
        var media=response.Content.Headers.ContentType?.ToString();
        if(media?.StartsWith("text/event-stream",StringComparison.OrdinalIgnoreCase)==true||media?.StartsWith("application/grpc",StringComparison.OrdinalIgnoreCase)==true)
        {await Reject(new(502,"transform_body_stream_unsupported"));return;}
        if(!JsonBodyRequestMiddleware.JsonMedia(media)){await Reject(new(502,"transform_body_media_unsupported"));return;}
        var limit=Math.Min(body.MaxBodyBytes,settings.MaxBodyBytes);
        if(response.Content.Headers.ContentLength>limit){await Reject(new(502,"transform_body_too_large"));return;}
        if(execution.BodyLease is null)
        {if(!budget.TryAcquire(out var lease)){await Reject(new(503,"transform_body_busy"));return;}execution.BodyLease=lease;}
        byte[]? encoded=null,decoded=null;
        var routeTimeout=http.Features.Get<IHttpRequestTimeoutFeature>()?.RequestTimeoutToken??CancellationToken.None;
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted,routeTimeout,transform.CancellationToken);
        if(!routeTimeout.CanBeCanceled)deadline.CancelAfter(execution.Route.TimeoutMs);
        try
        {
            await using var source=await response.Content.ReadAsStreamAsync(deadline.Token);
            var read=await BoundedJsonBodyCodec.ReadAsync(source,response.Content.Headers.ContentEncoding.Count==0?null:string.Join(",",response.Content.Headers.ContentEncoding),limit,deadline.Token);
            encoded=read.Encoded;decoded=read.Utf8;
            if(read.Reason is not null){await Reject(new(502,read.Reason));return;}
            if(response.TrailingHeaders.Any()){await Reject(new(502,"transform_body_trailer_unsupported"));return;}
            var mapped=JsonBodyMapper.Apply(decoded!,execution.Generation.ResponseBodyMappings[binding.PolicyId],limit,deadline.Token);
            if(mapped.Reason is not null){await Reject(new(502,mapped.Reason,mapped.OperationIndex));return;}
            var old=response.Content;var content=new ByteArrayContent(mapped.Utf8!);
            foreach(var header in old.Headers)if(!PolicyRequestTransformProvider.RepresentationHeaders.Contains(header.Key))content.Headers.TryAddWithoutValidation(header.Key,header.Value);
            RemoveRepresentation(response,http);
            content.Headers.ContentType=new System.Net.Http.Headers.MediaTypeHeaderValue("application/json"){CharSet="utf-8"};content.Headers.ContentLength=mapped.Utf8!.Length;
            response.Content=content;old.Dispose();
            http.Response.ContentType="application/json; charset=utf-8";http.Response.ContentLength=mapped.Utf8.Length;
            execution.TransformedResponseBody=mapped.Utf8;execution.ResponseBodyApplied=true;execution.ResponseTransformApplied=true;
        }
        catch(OperationCanceledException)when(routeTimeout.IsCancellationRequested)
        {
            // The endpoint timeout feature is removed before the outer telemetry middleware runs.
            // Preserve the cause, and let the existing timeout pipeline emit its response.
            execution.BodyTimeout=true;throw;
        }
        catch(OperationCanceledException)when(!http.RequestAborted.IsCancellationRequested)
        {
            execution.BodyTimeout=true;execution.UpstreamResponseAvailable=false;transform.SuppressResponseBody=true;
            http.Response.Clear();http.Response.StatusCode=504;await http.Response.WriteAsJsonAsync(new{code="gateway_timeout",traceId=http.TraceIdentifier},http.RequestAborted);
        }
        finally
        {
            if(decoded is not null)CryptographicOperations.ZeroMemory(decoded);
            if(encoded is not null&&!ReferenceEquals(encoded,decoded))CryptographicOperations.ZeroMemory(encoded);
        }
        async Task Reject(BodyTransformFailure failure)
        {
            execution.TransformFailure=failure;execution.TransformRejectionReason=failure.Reason;execution.UpstreamResponseAvailable=false;
            transform.SuppressResponseBody=true;http.Response.Clear();http.Response.StatusCode=failure.Status;
            await http.Response.WriteAsJsonAsync(new{code="transform_rejected",reason=failure.Reason,operationIndex=failure.OperationIndex,traceId=http.TraceIdentifier},http.RequestAborted);
        }
    });
    private static void RemoveRepresentation(HttpResponseMessage response,HttpContext http,bool keepMedia=false)
    {
        foreach(var name in PolicyRequestTransformProvider.RepresentationHeaders)
        {
            if(keepMedia&&name.Equals("Content-Type",StringComparison.OrdinalIgnoreCase))continue;
            http.Response.Headers.Remove(name);
            if(response.Headers.NonValidated.Any(h=>h.Key.Equals(name,StringComparison.OrdinalIgnoreCase)))response.Headers.Remove(name);
            if(response.Content.Headers.NonValidated.Any(h=>h.Key.Equals(name,StringComparison.OrdinalIgnoreCase)))response.Content.Headers.Remove(name);
        }
    }
}
