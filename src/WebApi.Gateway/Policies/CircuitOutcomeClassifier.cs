using Microsoft.AspNetCore.Http.Timeouts;
using WebApi.Contracts.Policies;
using WebApi.Domain.Policies;
using Yarp.ReverseProxy.Forwarder;
namespace WebApi.Gateway.Policies;
public static class CircuitOutcomeClassifier
{
    public static CircuitOutcome Classify(HttpContext context,CircuitBreakerConfiguration config,bool forwarded)
    {
        if(!forwarded) return CircuitOutcome.Cancelled;
        var error=context.Features.Get<IForwarderErrorFeature>();
        if(context.Features.Get<IHttpRequestTimeoutFeature>()?.RequestTimeoutToken.IsCancellationRequested==true||error?.Error.ToString()=="RequestTimedOut") return config.CountTimeouts?CircuitOutcome.Failure:CircuitOutcome.Neutral;
        if(context.RequestAborted.IsCancellationRequested) return CircuitOutcome.Cancelled;
        if(error is not null) return config.CountConnectionFailures?CircuitOutcome.Failure:CircuitOutcome.Neutral;
        if(config.FailureStatusCodes.Contains(context.Response.StatusCode)) return CircuitOutcome.Failure;
        return context.Response.StatusCode is >=200 and <400?CircuitOutcome.Success:CircuitOutcome.Neutral;
    }
}
