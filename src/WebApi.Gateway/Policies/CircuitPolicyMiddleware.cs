using System.Globalization;
using Yarp.ReverseProxy.Model;
namespace WebApi.Gateway.Policies;
public sealed class CircuitPolicyMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var execution=TrafficExecutionContext.From(context)??throw new InvalidOperationException("Authorized traffic context is unavailable.");
        var circuit=(execution.Route.PolicyBindings??[]).Select(b=>execution.Generation.PoliciesById[b.PolicyId]).SingleOrDefault(p=>p.Type=="circuit_breaker");
        if(circuit is null){await next(context);return;}
        var state=execution.Generation.CircuitLeases[execution.Route.Id].State;var admission=state.TryEnter();
        if(!admission.Allowed){execution.Record(circuit,"OpenRejected","circuit_open");context.Response.StatusCode=503;if(admission.RetryAfterSeconds>0)context.Response.Headers.RetryAfter=admission.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);await context.Response.WriteAsJsonAsync(new{code="circuit_open",traceId=context.TraceIdentifier});return;}
        execution.CircuitState=state;execution.CircuitAdmission=admission;execution.Record(circuit,admission.Probe?"HalfOpenProbe":"Allowed");
        try{await next(context);}finally{var forwarded=context.GetReverseProxyFeature().ProxiedDestination is not null;state.Complete(admission,CircuitOutcomeClassifier.Classify(context,execution.Generation.CircuitConfigurations[circuit.Id],forwarded));}
    }
}
