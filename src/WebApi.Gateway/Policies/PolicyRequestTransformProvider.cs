using WebApi.Contracts.Policies;
using Yarp.ReverseProxy.Transforms;
using Yarp.ReverseProxy.Transforms.Builder;
using Yarp.ReverseProxy.Forwarder;

namespace WebApi.Gateway.Policies;

public sealed class PolicyRequestTransformProvider : ITransformProvider
{
    public void ValidateRoute(TransformRouteValidationContext context) { }
    public void ValidateCluster(TransformClusterValidationContext context) { }
    public void Apply(TransformBuilderContext context)=>Configure(context);
    public void Configure(TransformBuilderContext context)
    {
        context.AddRequestTransform(async transform=>
        {
            var execution=TrafficExecutionContext.From(transform.HttpContext);
            if(execution?.TransformPlan is not { } plan)return;
            transform.ProxyRequest.RequestUri=RequestUtilities.MakeDestinationAddress(transform.DestinationPrefix,plan.Path,plan.Query);
            var dynamicHeaders=ConnectionHeaders(transform.HttpContext.Request.Headers.Connection.ToString());
            foreach(var name in dynamicHeaders){RemovePresent(transform.ProxyRequest.Headers,name);if(transform.ProxyRequest.Content is { } content)RemovePresent(content.Headers,name);}
            if(execution.TransformedRequestBody is { } body)
            {
                var content=transform.ProxyRequest.Content??throw new InvalidOperationException("YARP body content unavailable.");
                foreach(var name in RepresentationHeaders){RemovePresent(transform.ProxyRequest.Headers,name);RemovePresent(content.Headers,name);}
                content.Headers.ContentType=new System.Net.Http.Headers.MediaTypeHeaderValue("application/json"){CharSet="utf-8"};content.Headers.ContentLength=body.Length;
                execution.ForwardingBodyStream=new MemoryStream(body,false);
                transform.HttpContext.Request.Body=execution.ForwardingBodyStream;
                execution.RequestBodyApplied=true;execution.RequestTransformApplied=true;
            }
            var applied=plan.RewritePath||plan.RewriteQuery;
            foreach(var operation in plan.RequestHeaders)
            {
                if(dynamicHeaders.Contains(operation.Name)){execution.TransformHopProtected=true;continue;}
                RemovePresent(transform.ProxyRequest.Headers,operation.Name);
                if(transform.ProxyRequest.Content is { } content)RemovePresent(content.Headers,operation.Name);
                // YARP dispatches content headers to the existing content container and uses
                // its own EmptyHttpContent for bodyless requests; never replace a streamed body.
                if(operation.Action==TransformAction.Set)await new RequestHeaderValueTransform(operation.Name,operation.Value!,append:false).ApplyAsync(transform);
                applied=true;
            }
            execution.RequestTransformApplied|=applied;
        });
        context.AddResponseTransform(transform=>
        {
            var execution=TrafficExecutionContext.From(transform.HttpContext);
            if(execution?.OriginalRequestBody is { } original)transform.HttpContext.Request.Body=original;
            if(execution?.TransformPlan is null||transform.ProxyResponse is null)return ValueTask.CompletedTask;
            execution.UpstreamResponseAvailable=true;
            execution.ResponseConnectionHeaders=ConnectionHeaders(string.Join(",",transform.ProxyResponse.Headers.Connection));
            return ValueTask.CompletedTask;
        });
    }
    private static void RemovePresent(System.Net.Http.Headers.HttpHeaders headers,string name)
    {if(headers.NonValidated.Any(h=>h.Key.Equals(name,StringComparison.OrdinalIgnoreCase)))headers.Remove(name);}
    internal static readonly HashSet<string> RepresentationHeaders=new(StringComparer.OrdinalIgnoreCase){"Content-Length","Content-Type","Content-Encoding","Content-MD5","Digest","Content-Digest","Repr-Digest","ETag","Last-Modified","Trailer"};
    internal static HashSet<string> ConnectionHeaders(string value)=>value.Split(',',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
}
