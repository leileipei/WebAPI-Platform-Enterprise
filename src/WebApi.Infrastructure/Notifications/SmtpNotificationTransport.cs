using MailKit.Security;
using MimeKit;
using WebApi.Contracts.Common;
using WebApi.Domain.Notifications;
using WebApi.Contracts.Notifications;
namespace WebApi.Infrastructure.Notifications;
public sealed class SmtpNotificationTransport(NotificationDeploymentSettings settings,INotificationAddressPolicy addresses):INotificationTransport
{
    public async Task<TransportResult> SendAsync(NotificationSendEnvelope envelope,CancellationToken ct=default)
    {
        using var budget=CancellationTokenSource.CreateLinkedTokenSource(ct);budget.CancelAfter(TimeSpan.FromSeconds(settings.SendTimeoutSeconds));var token=budget.Token;var sending=false;
        try
        {
            NotificationTransportConnection.Validate(envelope);settings.RequireAllowedRecipient(envelope.Target!);
            var security=envelope.Security switch {"StartTlsRequired"=>SecureSocketOptions.StartTls,"TlsOnConnect"=>SecureSocketOptions.SslOnConnect,_=>throw new ArgumentException("Invalid notification TLS mode.")};
            var from=NotificationPolicyValidator.NormalizeEmail(envelope.From!);var target=NotificationPolicyValidator.NormalizeEmail(envelope.Target!);
            if(envelope.Secret.Username is null||envelope.Secret.Password is null)throw new ArgumentException("Invalid notification secret.");
            var trust=NotificationTransportConnection.FixtureTrust(settings);
            var validated=await addresses.ResolveAsync(NotificationChannel.Email,envelope.Host,envelope.Port,null,token);
            using var socket=await NotificationTransportConnection.ConnectAsync(validated,envelope.Port,token);
            using var client=new MailKit.Net.Smtp.SmtpClient();client.Timeout=settings.SendTimeoutSeconds*1000;
            client.SslProtocols=System.Security.Authentication.SslProtocols.Tls12|System.Security.Authentication.SslProtocols.Tls13;
            if(trust is not null)client.ServerCertificateValidationCallback=trust;
            await client.ConnectAsync(socket,envelope.Host,envelope.Port,security,token);await client.AuthenticateAsync(envelope.Secret.Username,envelope.Secret.Password,token);
            using var content=new MemoryStream(envelope.Body,false);
            var message=new MimeMessage{MessageId=envelope.DeliveryId.ToString("D")+"@webapi.invalid",Subject="WebAPI notification",Body=new MimePart("application","json"){Content=new MimeContent(content),ContentTransferEncoding=ContentEncoding.Base64}};
            message.From.Add(MailboxAddress.Parse(from));message.To.Add(MailboxAddress.Parse(target));sending=true;await client.SendAsync(message,token);
            // DATA acceptance is authoritative; a failed QUIT cannot revoke it.
            try {await client.DisconnectAsync(true,token);}catch(Exception error)when(error is IOException or MailKit.ProtocolException or System.Net.Sockets.SocketException or OperationCanceledException){}
            return new(DeliveryOutcome.Accepted,"SmtpAccepted",250);
        }
        catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
        catch(OperationCanceledException){return new(sending?DeliveryOutcome.OutcomeUnknown:DeliveryOutcome.TransientFailure,"SendTimeout");}
        catch(MailKit.Net.Smtp.SmtpCommandException error)
        {var status=(int)error.StatusCode;return new(status is >=400 and <=499?DeliveryOutcome.TransientFailure:DeliveryOutcome.PermanentFailure,status is >=400 and <=499?"SmtpTemporaryFailure":"SmtpPermanentFailure",status);}
        catch(Exception error)when(error is SslHandshakeException or MailKit.Security.AuthenticationException or System.Security.Authentication.AuthenticationException or NotSupportedException)
        {return new(DeliveryOutcome.PermanentFailure,"TlsOrAuthenticationRejected");}
        catch(ApiException){return new(DeliveryOutcome.PermanentFailure,"AddressOrConfigurationRejected");}
        catch(Exception error)when(error is ArgumentException or FormatException){return new(DeliveryOutcome.PermanentFailure,"ConfigurationRejected");}
        catch(Exception error)when(error is IOException or MailKit.ProtocolException or System.Net.Sockets.SocketException)
        {return new(sending?DeliveryOutcome.OutcomeUnknown:DeliveryOutcome.TransientFailure,sending?"SmtpOutcomeUnknown":"NetworkFailure");}
    }
}
