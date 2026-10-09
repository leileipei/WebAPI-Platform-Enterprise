namespace WebApi.Contracts.Security;
public sealed record LoginRateLimits(int IpMaxAttempts,int IpWindowSeconds,int AccountMaxAttempts,int AccountWindowSeconds);
public sealed record LoginIdentityKeys(string Ip,string Account,string AuditAccount);
public sealed record LoginRateRequest(LoginIdentityKeys Identity,LoginRateLimits Limits);
public enum LoginRateDecisionKind { Allowed, Limited, Unavailable }
public sealed record LoginRateDecision(LoginRateDecisionKind Kind,int RetryAfterSeconds=0);
public interface ILoginRateStore
{
    ValueTask<LoginRateDecision> TryAcquireAsync(LoginRateRequest request,CancellationToken ct);
}
