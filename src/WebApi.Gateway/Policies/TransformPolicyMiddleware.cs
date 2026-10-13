namespace WebApi.Gateway.Policies;

public sealed class TransformPolicyMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var execution=TrafficExecutionContext.From(context)??throw new InvalidOperationException("Routing policy context unavailable.");
        var binding=(execution.Route.PolicyBindings??[]).SingleOrDefault(b=>execution.Generation.Transforms.ContainsKey(b.PolicyId));
        if(binding is null){await next(context);return;}
        var policy=execution.Generation.PoliciesById[binding.PolicyId];
        var result=TransformPlanner.Plan(context.Request,execution.Generation.Transforms[binding.PolicyId]);
        if(result.Plan is null)
        {
            execution.Record(policy,"Rejected",result.Reason);context.Response.StatusCode=result.ErrorStatus!.Value;
            await context.Response.WriteAsJsonAsync(new{code="transform_rejected",reason=result.Reason,traceId=context.TraceIdentifier});return;
        }
        execution.TransformPlan=result.Plan;
        context.Response.OnStarting(()=>
        {
            if(!execution.UpstreamResponseAvailable&&execution.CacheDisposition!="Hit")return Task.CompletedTask;
            foreach(var name in execution.ResponseConnectionHeaders??new HashSet<string>())
            {
                // These fields describe the platform's new representation, not copied upstream headers.
                if(execution.ResponseBodyApplied&&(name.Equals("Content-Type",StringComparison.OrdinalIgnoreCase)||name.Equals("Content-Length",StringComparison.OrdinalIgnoreCase)))continue;
                context.Response.Headers.Remove(name);
            }
            foreach(var operation in result.Plan.ResponseHeaders)
            {
                if(execution.ResponseConnectionHeaders?.Contains(operation.Name)==true){execution.TransformHopProtected=true;continue;}
                context.Response.Headers.Remove(operation.Name);
                if(operation.Action==WebApi.Contracts.Policies.TransformAction.Set)context.Response.Headers[operation.Name]=operation.Value;
                execution.ResponseTransformApplied=true;
            }
            return Task.CompletedTask;
        });
        try{await next(context);if(execution.UpstreamResponseAvailable&&!context.Response.HasStarted)await context.Response.StartAsync(context.RequestAborted);}
        finally
        {
            var applied=execution.RequestTransformApplied||execution.ResponseTransformApplied;
            var configuration=execution.Generation.Transforms[binding.PolicyId];
            int? operations=configuration.RequestBody is null&&configuration.ResponseBody is null?null:
                (execution.RequestBodyApplied?execution.Generation.RequestBodyMappings[binding.PolicyId].OperationCount:0)
                +(execution.ResponseBodyApplied?execution.Generation.ResponseBodyMappings[binding.PolicyId].OperationCount:0);
            execution.Record(policy,execution.TransformRejectionReason is not null?"Rejected":applied?"Applied":"Bypass",
                execution.TransformRejectionReason??(applied?null:execution.CacheDisposition=="Hit"?"cache_hit":execution.TransformHopProtected?"hop_by_hop_protected":execution.TransformBypassReason??"no_upstream_response"),operationIndex:execution.TransformFailure?.OperationIndex,bodyOperationCount:operations);
        }
    }
}
