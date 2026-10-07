using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using WebApi.Contracts.Notifications;
namespace WebApi.Infrastructure.Notifications;

public interface INotificationTransport
{
    Task<TransportResult> SendAsync(NotificationSendEnvelope envelope,CancellationToken ct=default);
}
public sealed class NotificationTransport(SmtpNotificationTransport smtp,WebhookNotificationTransport webhook):INotificationTransport
{public Task<TransportResult> SendAsync(NotificationSendEnvelope envelope,CancellationToken ct=default)=>envelope.Url is null?smtp.SendAsync(envelope,ct):webhook.SendAsync(envelope,ct);}
// No serializable public properties: only the dispatcher builds this frozen input.
public sealed class NotificationSendEnvelope
{
    internal Guid DeliveryId {get;}
    internal byte[] Body {get;}
    internal string Host {get;}
    internal int Port {get;}
    internal string? Security {get;}
    internal string? From {get;}
    internal string? Target {get;}
    internal Uri? Url {get;}
    internal NotificationSecret Secret {get;}
    internal NotificationSendEnvelope(Guid deliveryId,byte[] body,string host,int port,NotificationSecret secret,string? security=null,string? from=null,string? target=null,Uri? url=null)
    {DeliveryId=deliveryId;Body=body.ToArray();Host=host;Port=port;Secret=secret;Security=security;From=from;Target=target;Url=url;}
}
internal static class NotificationTransportConnection
{
    internal static void Validate(NotificationSendEnvelope envelope)
    {if(envelope.DeliveryId==Guid.Empty||envelope.Body.Length is <1 or >16384||string.IsNullOrWhiteSpace(envelope.Host)||envelope.Port is <1 or >65535)throw new ArgumentException("Invalid notification envelope.");}
    internal static async Task<Socket> ConnectAsync(IReadOnlyList<IPAddress> addresses,int port,CancellationToken ct)
    {
        var socket=new Socket(addresses[0].AddressFamily,SocketType.Stream,ProtocolType.Tcp);
        try {await socket.ConnectAsync(new IPEndPoint(addresses[0],port),ct);return socket;}
        catch {socket.Dispose();throw;}
    }
    internal static RemoteCertificateValidationCallback? FixtureTrust(NotificationDeploymentSettings settings)
    {
        if(settings.FixtureRootCertificate is null)return null;
        return (_,certificate,_,errors)=>{
            if(certificate is null||(errors&(SslPolicyErrors.RemoteCertificateNameMismatch|SslPolicyErrors.RemoteCertificateNotAvailable))!=0)return false;
            using var root=X509CertificateLoader.LoadCertificate(settings.FixtureRootCertificate);using var leaf=X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());using var chain=new X509Chain();chain.ChainPolicy.TrustMode=X509ChainTrustMode.CustomRootTrust;chain.ChainPolicy.CustomTrustStore.Add(root);chain.ChainPolicy.ApplicationPolicy.Add(new System.Security.Cryptography.Oid("1.3.6.1.5.5.7.3.1"));chain.ChainPolicy.RevocationMode=X509RevocationMode.NoCheck;return chain.Build(leaf);
        };
    }
}
