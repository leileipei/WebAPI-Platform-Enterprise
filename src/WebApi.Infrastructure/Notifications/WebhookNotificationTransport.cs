using System.Globalization;
using System.Net.Sockets;
using WebApi.Contracts.Notifications;
using WebApi.Domain.Notifications;
using WebApi.Contracts.Common;
namespace WebApi.Infrastructure.Notifications;
public sealed class WebhookNotificationTransport(NotificationDeploymentSettings settings,INotificationAddressPolicy addresses,TimeProvider clock):INotificationTransport
{
    private int responseBytes;
    internal int LastResponseBytes=>Volatile.Read(ref responseBytes);
    public async Task<TransportResult> SendAsync(NotificationSendEnvelope envelope,CancellationToken ct=default)
    {
        using var budget=CancellationTokenSource.CreateLinkedTokenSource(ct);budget.CancelAfter(TimeSpan.FromSeconds(settings.SendTimeoutSeconds));var token=budget.Token;var connected=false;var connections=0;Volatile.Write(ref responseBytes,0);
        try
        {
            NotificationTransportConnection.Validate(envelope);if(envelope.Secret.Key is not {Length:>=32 and <=64})throw new ArgumentException("Invalid notification secret.");
            var trust=NotificationTransportConnection.FixtureTrust(settings);
            var validated=await addresses.ResolveAsync(NotificationChannel.Webhook,envelope.Host,envelope.Port,envelope.Url,token);
            using var handler=new SocketsHttpHandler{UseProxy=false,UseCookies=false,AllowAutoRedirect=false,AutomaticDecompression=System.Net.DecompressionMethods.None,MaxResponseHeadersLength=16,ConnectTimeout=TimeSpan.FromSeconds(settings.SendTimeoutSeconds),ConnectCallback=async(_,cancel)=>{
                // New handler per attempt and one connection: no hidden request retry.
                if(Interlocked.Increment(ref connections)!=1)throw new IOException("Notification connection already attempted.");
                var socket=await NotificationTransportConnection.ConnectAsync(validated,envelope.Port,cancel);connected=true;return new NetworkStream(socket,true);
            }};
            if(trust is not null)handler.SslOptions.RemoteCertificateValidationCallback=trust;
            handler.SslOptions.EnabledSslProtocols=System.Security.Authentication.SslProtocols.Tls12|System.Security.Authentication.SslProtocols.Tls13;
            using var http=new HttpClient(handler){Timeout=Timeout.InfiniteTimeSpan};using var request=new HttpRequestMessage(HttpMethod.Post,envelope.Url){Content=new ByteArrayContent(envelope.Body),Version=System.Net.HttpVersion.Version11,VersionPolicy=HttpVersionPolicy.RequestVersionExact};request.Content.Headers.ContentType=new("application/json");
            var timestamp=clock.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            request.Headers.Add("X-WebAPI-Delivery-Id",envelope.DeliveryId.ToString("D"));request.Headers.Add("X-WebAPI-Timestamp",timestamp);request.Headers.Add("X-WebAPI-Signature",WebhookSignature.Sign(envelope.Secret.Key,timestamp,envelope.DeliveryId,envelope.Body));
            using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,token);var status=(int)response.StatusCode;
            var retryAfter=RetryAfter(response,clock.GetUtcNow());var known=new TransportResult(status is >=200 and <=299?DeliveryOutcome.Accepted:status is 408 or 429 or >=500 and <=599?DeliveryOutcome.TransientFailure:DeliveryOutcome.PermanentFailure,status is >=200 and <=299?"WebhookAccepted":status is 408 or 429 or >=500 and <=599?"WebhookTemporaryFailure":"WebhookPermanentFailure",status,retryAfter);
            // Discard bounded bytes. Headers already give the protocol result;
            // a stalled response body cannot erase a known acceptance/rejection.
            try
            {await using var stream=await response.Content.ReadAsStreamAsync(token);var buffer=new byte[settings.MaxResponseBytes];var total=0;while(total<buffer.Length){var count=await stream.ReadAsync(buffer.AsMemory(total,buffer.Length-total),token);if(count==0)break;total+=count;}Volatile.Write(ref responseBytes,total);}
            catch(Exception error)when(error is OperationCanceledException or IOException or HttpRequestException){}
            return known;
        }
        catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
        catch(OperationCanceledException){return new(connected?DeliveryOutcome.OutcomeUnknown:DeliveryOutcome.TransientFailure,"SendTimeout");}
        catch(HttpRequestException error)when(error.HttpRequestError==HttpRequestError.SecureConnectionError||error.InnerException is System.Security.Authentication.AuthenticationException)
        {return new(DeliveryOutcome.PermanentFailure,"TlsRejected");}
        catch(ApiException){return new(DeliveryOutcome.PermanentFailure,"AddressOrConfigurationRejected");}
        catch(Exception error)when(error is ArgumentException or FormatException){return new(DeliveryOutcome.PermanentFailure,"ConfigurationRejected");}
        catch(Exception error)when(error is HttpRequestException or IOException or System.Net.Sockets.SocketException)
        {return new(connected?DeliveryOutcome.OutcomeUnknown:DeliveryOutcome.TransientFailure,connected?"WebhookOutcomeUnknown":"NetworkFailure");}
    }
    private static TimeSpan? RetryAfter(HttpResponseMessage response,DateTimeOffset now)
    {
        if(!response.Headers.TryGetValues("Retry-After",out var fields))return null;var values=fields.Take(2).ToArray();if(values.Length!=1)return null;var raw=values[0].Trim();
        if(raw.Length>0&&raw.All(char.IsAsciiDigit))
        {if(!long.TryParse(raw,NumberStyles.None,CultureInfo.InvariantCulture,out var seconds)||seconds>TimeSpan.MaxValue.TotalSeconds)return TimeSpan.MaxValue;return TimeSpan.FromSeconds(seconds);}
        if(response.Headers.RetryAfter?.Date is {} date)return date>now?date-now:TimeSpan.Zero;
        return null;
    }
}
