using WebApi.Contracts.Policies;
namespace WebApi.Gateway.Policies;
public sealed record RateLimitRequest(Guid EnvironmentId,Guid RuntimePolicyId,Guid RouteId,Guid? ApplicationId,RateLimitConfiguration Configuration);
public enum RateLimitDecisionKind {Allowed,Exceeded,StoreUnavailable}
public sealed record RateLimitDecision(RateLimitDecisionKind Kind,int RetryAfterSeconds=0);
public interface IRateLimitStore {ValueTask<RateLimitDecision> TakeAsync(RateLimitRequest request,CancellationToken ct);}
// Boundary to the external Redis command: the caller imposes one budget and never redispatches.
public interface ITokenBucketExecutor {Task<long[]> TakeAsync(string key,RateLimitConfiguration configuration,CancellationToken ct);}
