using WebApi.Contracts.Common;
using WebApi.Contracts.Notifications;
using Microsoft.Extensions.DependencyInjection;
namespace WebApi.Infrastructure.Notifications;
public sealed class NotificationDispatcher(NotificationDeliveryStore store,INotificationTransport transport,NotificationDeploymentSettings settings,IServiceScopeFactory scopes)
{
    public async Task<bool> DispatchNextAsync(string owner,CancellationToken ct=default)
    {
        var lease=await store.TryBeginAsync(owner,ct);if(lease is null)return false;
        using var budget=CancellationTokenSource.CreateLinkedTokenSource(ct);var remaining=lease.ExpiresAt-DateTimeOffset.UtcNow;budget.CancelAfter(remaining>TimeSpan.Zero?TimeSpan.FromSeconds(Math.Min(settings.SendTimeoutSeconds,remaining.TotalSeconds)):TimeSpan.Zero);
        TransportResult result;
        try {var envelope=await store.PrepareAsync(lease,budget.Token);result=envelope is null?new(DeliveryOutcome.OutcomeUnknown,"LeaseExpired"):await transport.SendAsync(envelope,budget.Token);}
        catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
        catch(OperationCanceledException){result=new(DeliveryOutcome.OutcomeUnknown,"SendTimeout");}
        catch(ApiException error)when(error.Code=="notification_protection_unavailable"){result=new(DeliveryOutcome.TransientFailure,"ProtectionUnavailable");}
        catch(ApiException error)when(error.Code=="notification_secret_unavailable"){result=new(DeliveryOutcome.TransientFailure,"SecretUnavailable");}
        catch(ApiException){result=new(DeliveryOutcome.PermanentFailure,"ConfigurationRejected");}
        await store.CompleteAsync(lease,result,ct);
        // New scope after writeback. A crash here remains recoverable by the scan.
        using var coordination=scopes.CreateScope();await coordination.ServiceProvider.GetRequiredService<NotificationPlanner>().CoordinateNextResolvedAsync(ct);return true;
    }
}
