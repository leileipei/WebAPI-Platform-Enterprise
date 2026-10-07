using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using MailKit.Security;
using MimeKit;
using WebApi.Contracts.Notifications;
using WebApi.Domain.Notifications;
using WebApi.Integration.Tests.Support;
using Xunit;
namespace WebApi.Integration.Tests;
// Establish fixture health with real TLS/auth before product protocol assertions.
public sealed class NotificationFixtureTests
{
    private static RemoteCertificateValidationCallback Trust(string rootPath)
    {
        return (_,certificate,_,errors)=>{
            if(certificate is null||(errors&(SslPolicyErrors.RemoteCertificateNameMismatch|SslPolicyErrors.RemoteCertificateNotAvailable))!=0)return false;
            using var root=X509Certificate2.CreateFromPem(File.ReadAllText(rootPath));using var leaf=X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());using var chain=new X509Chain();chain.ChainPolicy.TrustMode=X509ChainTrustMode.CustomRootTrust;chain.ChainPolicy.CustomTrustStore.Add(root);chain.ChainPolicy.RevocationMode=X509RevocationMode.NoCheck;return chain.Build(leaf);
        };
    }
    [Fact] public async Task ActualTlsAuthAndIndependentSignedWebhookFixtureAreOperational()
    {
        await using var fixture=new NotificationTransportFixture();await fixture.InitializeAsync();var id=Guid.NewGuid();var body=NotificationPayload.Test(new Uri("https://console.fixture.test/"),DateTimeOffset.UtcNow);var smtpSecret=await fixture.SecretAsync(NotificationChannel.Email);
        using(var socket=new Socket(AddressFamily.InterNetwork,SocketType.Stream,ProtocolType.Tcp))
        using(var client=new MailKit.Net.Smtp.SmtpClient())
        {
            await socket.ConnectAsync(IPAddress.Loopback,fixture.Smtp.Port);client.ServerCertificateValidationCallback=Trust(fixture.RootCertificatePath);await client.ConnectAsync(socket,"smtp.fixture.test",fixture.Smtp.Port,SecureSocketOptions.StartTls);await client.AuthenticateAsync(smtpSecret.Username!,smtpSecret.Password!);
            var message=new MimeMessage{MessageId=id.ToString("D")+"@webapi.invalid",Subject="Fixture protocol probe",Body=new MimePart("application","json"){Content=new MimeContent(new MemoryStream(body)),ContentTransferEncoding=ContentEncoding.Base64}};message.From.Add(MailboxAddress.Parse("fixture.sender@example.test"));message.To.Add(MailboxAddress.Parse("fixture.recipient@example.test"));await client.SendAsync(message);await client.DisconnectAsync(true);
        }
        var receipt=Assert.Single(fixture.Smtp.Receipts,x=>x.DeliveryId==id);Assert.True(receipt.Tls);Assert.True(receipt.Authenticated);Assert.True(receipt.Accepted);Assert.Equal(1,receipt.RecipientCount);Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(body)),receipt.BodyHash);
        var secret=await fixture.SecretAsync(NotificationChannel.Webhook);var timestamp=DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);var prefix=Encoding.UTF8.GetBytes(timestamp+"."+id.ToString("D")+".");var signed=new byte[prefix.Length+body.Length];prefix.CopyTo(signed,0);body.CopyTo(signed,prefix.Length);
        using var handler=new SocketsHttpHandler{UseProxy=false,UseCookies=false,AllowAutoRedirect=false,SslOptions=new(){RemoteCertificateValidationCallback=Trust(fixture.RootCertificatePath)},ConnectCallback=async(_,ct)=>{var socket=new Socket(AddressFamily.InterNetwork,SocketType.Stream,ProtocolType.Tcp);try{await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback,fixture.WebhookUrl.Port),ct);return new NetworkStream(socket,true);}catch{socket.Dispose();throw;}}};using var http=new HttpClient(handler);using var request=new HttpRequestMessage(HttpMethod.Post,fixture.WebhookUrl){Content=new ByteArrayContent(body)};request.Headers.Add("X-WebAPI-Delivery-Id",id.ToString("D"));request.Headers.Add("X-WebAPI-Timestamp",timestamp);request.Headers.Add("X-WebAPI-Signature",Convert.ToHexStringLower(HMACSHA256.HashData(secret.Key!,signed)));using var response=await http.SendAsync(request);Assert.Equal(HttpStatusCode.Accepted,response.StatusCode);
        var webhook=Assert.Single(fixture.Webhook.Receipts);Assert.True(webhook.Tls);Assert.True(webhook.SignatureValid);Assert.True(webhook.RemoteAccepted);Assert.Equal(receipt.BodyHash,webhook.BodyHash);Assert.Equal(1,fixture.Webhook.BusinessAcceptCount);
    }
}
