using Microsoft.AspNetCore.Connections.Features;
using WebApi.Contracts.Runtime;
using WebApi.Gateway.Policies;
using Yarp.ReverseProxy.Forwarder;

namespace WebApi.Gateway.Security;

internal readonly record struct FormalRotationLease(Guid EnvironmentId, Guid NodeId, Guid InstanceId,
    Guid SecurityGeneration, long SecurityEpoch, string SecurityHeadHash, Guid LeaseId);

internal sealed record FormalRotationAdmission(FormalRotationLease Lease, Guid CredentialId, Guid ApplicationId,
    Guid RouteId, string Method, SnapshotEnvelope Envelope, DateTimeOffset AdmittedAt, CancellationToken AdmissionAborted);

internal sealed class FormalRotationRequestProofMiddleware(RequestDelegate next, IFormalRotationRequestProofSink sink,
    TimeProvider clock, ILogger<FormalRotationRequestProofMiddleware> log)
{
    public async Task InvokeAsync(HttpContext context)
    {
        // Keep the server token as well as the token replaced by the route timeout middleware.
        var clientAborted = context.RequestAborted;
        var connectionClosed = context.Features.Get<IConnectionLifetimeFeature>()?.ConnectionClosed ?? default;
        FormalRotationAdmission? completed = null;
        context.Response.OnCompleted(RecordCompleted);

        await next(context);
        if (TrafficExecutionContext.From(context) is not { FormalRotationAdmission: { } admission } execution ||
            Interrupted(admission) || !Succeeded() || execution.TransformFailure is not null || execution.BodyTimeout)
            return;

        var attempts = execution.Attempts;
        if (attempts.Any(attempt => attempt.Outcome != "Completed") ||
            attempts.Count == 0 && execution.CacheDisposition != "Hit")
            return;

        // Kestrel can call OnCompleted even after a failure. CompleteAsync checks
        // the length and flushes remaining headers, body and trailers first.
        await context.Response.CompleteAsync();
        completed = admission;

        bool Interrupted(FormalRotationAdmission receipt) =>
            clientAborted.IsCancellationRequested || connectionClosed.IsCancellationRequested ||
            receipt.AdmissionAborted.IsCancellationRequested || context.RequestAborted.IsCancellationRequested;

        bool Succeeded() => context.Response.StatusCode is >= 200 and <= 399 &&
            context.Features.Get<IForwarderErrorFeature>() is null;

        Task RecordCompleted()
        {
            if (completed is not { } receipt || Interrupted(receipt) || !context.Response.HasStarted || !Succeeded())
                return Task.CompletedTask;

            try
            {
                var completedAt = clock.GetUtcNow();
                if (completedAt >= receipt.AdmittedAt && !Interrupted(receipt))
                {
                    var lease = receipt.Lease;
                    var snapshot = receipt.Envelope;
                    sink.TryRecord(new(lease.EnvironmentId, lease.NodeId, lease.InstanceId, receipt.CredentialId,
                        receipt.ApplicationId, receipt.RouteId, receipt.Method, snapshot.ReleaseId, snapshot.ConfigVersion,
                        snapshot.DeploymentSequence, snapshot.PayloadHash, lease.SecurityGeneration, lease.SecurityEpoch,
                        lease.SecurityHeadHash, lease.LeaseId, receipt.AdmittedAt, completedAt, context.Response.StatusCode));
                }
            }
            catch (Exception error)
            {
                log.LogDebug("Could not submit a formal rotation request proof ({ErrorType})", error.GetType().Name);
            }
            return Task.CompletedTask;
        }
    }
}
