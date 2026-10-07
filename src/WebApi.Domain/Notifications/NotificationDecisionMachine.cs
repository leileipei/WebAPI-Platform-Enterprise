using WebApi.Contracts.Notifications;
namespace WebApi.Domain.Notifications;
public static class NotificationDecisionMachine
{
    public static RetryDecision NextAttemptAt(NotificationRetryPolicy policy,int attemptNo,DateTimeOffset now,DateTimeOffset expires,TimeSpan? retryAfter,double jitter)
    {
        NotificationPolicyValidator.ValidateRetry(policy);
        if(attemptNo<1||!double.IsFinite(jitter)||jitter is <0 or >.2||retryAfter<TimeSpan.Zero)throw new ArgumentException("重试输入不合法。");
        if(expires<=now)return new(null,DeliveryStatus.Expired,"DeadlineExceeded");
        if(attemptNo>=policy.MaxAttempts)return new(null,DeliveryStatus.Failed,"AttemptsExhausted");
        if(retryAfter>TimeSpan.FromSeconds(3600))return new(null,DeliveryStatus.Failed,"RetryAfterExceedsBudget");
        var exponential=Math.Min(policy.MaxDelaySeconds,policy.BaseDelaySeconds*Math.Pow(2,attemptNo-1));var delay=TimeSpan.FromSeconds(Math.Min(policy.MaxDelaySeconds,exponential*(1+jitter)));
        if(retryAfter is TimeSpan requested&&requested>delay)delay=requested;
        if(delay>=expires-now)return new(null,DeliveryStatus.Expired,"DeadlineExceeded");
        return new(now+delay,DeliveryStatus.RetryScheduled,null);
    }
}
