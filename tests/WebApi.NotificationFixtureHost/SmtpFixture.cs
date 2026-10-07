using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using MimeKit;
namespace WebApi.NotificationFixtureHost;

public sealed class SmtpFixtureOptions
{
    internal X509Certificate2 Certificate {get;}
    internal string Username {get;}
    internal string Password {get;}
    internal int Port {get;}
    internal IPAddress ListenAddress {get;}
    internal bool TlsOnConnect {get;}
    internal bool AdvertiseStartTls {get;}
    public SmtpFixtureOptions(X509Certificate2 certificate,string username,string password,int port=0,bool tlsOnConnect=false,bool advertiseStartTls=true,IPAddress? listenAddress=null)
    {Certificate=certificate;Username=username;Password=password;Port=port;TlsOnConnect=tlsOnConnect;AdvertiseStartTls=advertiseStartTls;ListenAddress=listenAddress??IPAddress.Loopback;}
}
public sealed record SmtpFixtureReceipt(Guid SessionId,Guid? DeliveryId,string? MessageId,int RecipientCount,bool Tls,bool Authenticated,bool Accepted,int? ReplyCode,string? BodyHash,int CcCount,int BccCount,DateTimeOffset ObservedAt);
public sealed class SmtpFixture(SmtpFixtureOptions options):IHostedService,IDisposable
{
    private readonly ConcurrentQueue<SmtpFixtureReceipt> receipts=new();
    private readonly ConcurrentDictionary<Guid,TcpClient> clients=new();
    private readonly ConcurrentDictionary<Guid,Task> sessions=new();
    private readonly CancellationTokenSource shutdown=new();
    private readonly TcpListener listener=new(options.ListenAddress,options.Port);
    private Task? accepting;
    private int disposed;
    private string mode="Accept";
    public int Port=>((IPEndPoint)listener.LocalEndpoint).Port;
    public IReadOnlyList<SmtpFixtureReceipt> Receipts=>receipts.ToArray();
    public int ActiveSessions=>clients.Count;
    public void SetMode(string value)
    {
        if(value is not("Accept" or "Temporary" or "Permanent" or "AcceptThenDisconnect" or "AcceptThenQuitDisconnect" or "Delay"))throw new ArgumentException("Invalid SMTP fixture mode.");
        Volatile.Write(ref mode,value);
    }
    public Task StartAsync(CancellationToken ct)
    {listener.Start();accepting=AcceptAsync(shutdown.Token);return Task.CompletedTask;}
    private async Task AcceptAsync(CancellationToken ct)
    {
        try
        {
            while(!ct.IsCancellationRequested)
            {
                var client=await listener.AcceptTcpClientAsync(ct);var id=Guid.NewGuid();clients[id]=client;
                var task=HandleAsync(id,client,ct);sessions[id]=task;
                _=task.ContinueWith(_=>sessions.TryRemove(id,out var ignored),CancellationToken.None,TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
            }
        }
        catch(Exception error)when(error is OperationCanceledException or SocketException or ObjectDisposedException){ }
    }
    private async Task HandleAsync(Guid id,TcpClient client,CancellationToken ct)
    {
        Stream stream=client.GetStream();StreamReader? reader=null;StreamWriter? writer=null;var tls=false;var authenticated=false;var recipientCount=0;var accepted=false;Guid? delivery=null;string? messageId=null,bodyHash=null;int? replyCode=null;var cc=0;var bcc=0;
        var selectedMode=Volatile.Read(ref mode);
        void Readers()
        {reader=new(stream,Encoding.UTF8,false,4096,true);writer=new(stream,new UTF8Encoding(false),4096,true){NewLine="\r\n",AutoFlush=true};}
        async Task Secure()
        {
            var secure=new SslStream(stream,false);await secure.AuthenticateAsServerAsync(new SslServerAuthenticationOptions{ServerCertificate=options.Certificate,EnabledSslProtocols=SslProtocols.Tls12|SslProtocols.Tls13,ClientCertificateRequired=false},ct);stream=secure;tls=true;
        }
        async Task Reply(string text)=>await writer!.WriteLineAsync(text.AsMemory(),ct);
        try
        {
            if(options.TlsOnConnect)await Secure();Readers();await Reply("220 fixture.local ESMTP");
            while(!ct.IsCancellationRequested)
            {
                var line=await reader!.ReadLineAsync(ct);if(line is null)break;if(line.Length>32768)throw new IOException("Fixture command too large.");
                if(line.StartsWith("EHLO ",StringComparison.OrdinalIgnoreCase)||line.StartsWith("HELO ",StringComparison.OrdinalIgnoreCase))
                {
                    await Reply("250-fixture.local");if(!tls&&options.AdvertiseStartTls&&!options.TlsOnConnect)await Reply("250-STARTTLS");await Reply("250 AUTH PLAIN");
                }
                else if(line.Equals("STARTTLS",StringComparison.OrdinalIgnoreCase)&&options.AdvertiseStartTls&&!tls)
                {await Reply("220 Ready for TLS");reader.Dispose();writer!.Dispose();await Secure();Readers();}
                else if(line.StartsWith("AUTH PLAIN",StringComparison.OrdinalIgnoreCase))
                {
                    var encoded=line.Length>11?line[11..].Trim():"";if(encoded.Length==0){await Reply("334 ");encoded=await reader.ReadLineAsync(ct)??"";}
                    var fields=Encoding.UTF8.GetString(Convert.FromBase64String(encoded)).Split('\0');
                    authenticated=tls&&fields.Length==3&&Equal(fields[1],options.Username)&&Equal(fields[2],options.Password);await Reply(authenticated?"235 2.7.0 Authenticated":"535 5.7.0 Authentication rejected");
                }
                else if(line.StartsWith("MAIL FROM:",StringComparison.OrdinalIgnoreCase))
                {recipientCount=0;await Reply(authenticated?"250 2.1.0 Sender accepted":"530 5.7.0 Authentication required");}
                else if(line.StartsWith("RCPT TO:",StringComparison.OrdinalIgnoreCase))
                {if(authenticated){recipientCount++;await Reply("250 2.1.5 Recipient accepted");}else await Reply("530 5.7.0 Authentication required");}
                else if(line.Equals("DATA",StringComparison.OrdinalIgnoreCase)&&authenticated&&recipientCount>0)
                {
                    await Reply("354 End with dot");var data=new StringBuilder();
                    while(true){var part=await reader.ReadLineAsync(ct)??throw new IOException("Fixture data disconnected.");if(part==".")break;if(part.StartsWith("..",StringComparison.Ordinal))part=part[1..];data.Append(part).Append("\r\n");if(data.Length>32768)throw new IOException("Fixture data too large.");}
                    await using var raw=new MemoryStream(Encoding.UTF8.GetBytes(data.ToString()));var mime=await MimeMessage.LoadAsync(raw,ct);messageId=mime.MessageId;cc=mime.Cc.Count;bcc=mime.Bcc.Count;
                    if(Guid.TryParse(messageId?.Split('@')[0],out var parsed))delivery=parsed;
                    if(mime.Body is MimePart {Content:{} content}){await using var decoded=new MemoryStream();await content.DecodeToAsync(decoded,ct);bodyHash=Convert.ToHexStringLower(SHA256.HashData(decoded.ToArray()));}
                    if(selectedMode=="Delay")await Task.Delay(TimeSpan.FromSeconds(15),ct);
                    replyCode=selectedMode switch{"Temporary"=>451,"Permanent"=>550,_=>250};accepted=replyCode==250;
                    receipts.Enqueue(new(id,delivery,messageId,recipientCount,tls,authenticated,accepted,selectedMode=="AcceptThenDisconnect"?null:replyCode,bodyHash,cc,bcc,DateTimeOffset.UtcNow));
                    if(selectedMode=="AcceptThenDisconnect")break;
                    await Reply(replyCode switch{451=>"451 4.3.0 Temporary fixture failure",550=>"550 5.0.0 Permanent fixture failure",_=>"250 2.0.0 Accepted"});
                }
                else if(line.Equals("QUIT",StringComparison.OrdinalIgnoreCase))
                {if(selectedMode!="AcceptThenQuitDisconnect")await Reply("221 Bye");break;}
                else if(line.Equals("RSET",StringComparison.OrdinalIgnoreCase)){recipientCount=0;await Reply("250 Reset");}
                else await Reply("500 Unsupported fixture command");
            }
        }
        catch(Exception error)when(error is IOException or AuthenticationException or OperationCanceledException or SocketException or FormatException or ObjectDisposedException){ }
        finally
        {
            if(delivery is null)receipts.Enqueue(new(id,null,null,recipientCount,tls,authenticated,false,replyCode,null,0,0,DateTimeOffset.UtcNow));
            try{reader?.Dispose();writer?.Dispose();await stream.DisposeAsync();}catch(Exception error)when(error is IOException or ObjectDisposedException){ }
            client.Dispose();clients.TryRemove(id,out _);
        }
    }
    private static bool Equal(string left,string right)=>CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left),Encoding.UTF8.GetBytes(right));
    public async Task StopAsync(CancellationToken ct)
    {shutdown.Cancel();listener.Stop();foreach(var client in clients.Values)client.Dispose();if(accepting is not null)await accepting;await Task.WhenAll(sessions.Values).WaitAsync(ct);}
    public void Dispose(){if(Interlocked.Exchange(ref disposed,1)!=0)return;shutdown.Cancel();listener.Stop();foreach(var client in clients.Values)client.Dispose();shutdown.Dispose();}
}
