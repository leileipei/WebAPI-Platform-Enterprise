using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Security;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Connections.Features;
namespace WebApi.NotificationFixtureHost;
public sealed class WebhookFixtureOptions
{
    internal byte[] Key {get;}
    public WebhookFixtureOptions(byte[] key){if(key.Length is <32 or >64)throw new ArgumentException("Invalid fixture key.");Key=key.ToArray();}
}
public sealed record WebhookFixtureReceipt(Guid? DeliveryId,string Timestamp,bool SignatureValid,bool Tls,string? TlsProtocol,string BodyHash,int BodyBytes,int ReplyCode,bool RemoteAccepted,bool Duplicate,DateTimeOffset ObservedAt);
public sealed class WebhookFixture(WebhookFixtureOptions options,TimeProvider clock,FixtureJournal? journal=null)
{
    private readonly ConcurrentQueue<WebhookFixtureReceipt> receipts=new(journal?.Webhook??[]);
    private readonly ConcurrentDictionary<Guid,byte> accepted=new((journal?.Webhook??[]).Where(r=>r.RemoteAccepted&&r.DeliveryId is not null).Select(r=>r.DeliveryId!.Value).Distinct().Select(id=>new KeyValuePair<Guid,byte>(id,0)));
    private string mode="Accept";
    private string retryAfter="1";
    public IReadOnlyList<WebhookFixtureReceipt> Receipts=>receipts.ToArray();
    public int BusinessAcceptCount=>accepted.Count;
    public void SetMode(string value,string retryAfterValue="1")
    {
        if(value is not("Accept" or "Temporary" or "Permanent" or "RateLimited" or "Redirect" or "Disconnect" or "Delay" or "Large"))throw new ArgumentException("Invalid Webhook fixture mode.");
        if(retryAfterValue.Length>1024||retryAfterValue.Any(char.IsControl))throw new ArgumentException("Invalid fixture Retry-After.");
        Volatile.Write(ref retryAfter,retryAfterValue);Volatile.Write(ref mode,value);
    }
    public async Task ReceiveAsync(HttpContext context,bool redirectTarget=false)
    {
        var ct=context.RequestAborted;await using var data=new MemoryStream();var buffer=new byte[4096];
        while(true){var count=await context.Request.Body.ReadAsync(buffer,ct);if(count==0)break;if(data.Length+count>16384){context.Response.StatusCode=413;return;}await data.WriteAsync(buffer.AsMemory(0,count),ct);}
        var body=data.ToArray();var idText=context.Request.Headers["X-WebAPI-Delivery-Id"].ToString();var timestamp=context.Request.Headers["X-WebAPI-Timestamp"].ToString();var signature=context.Request.Headers["X-WebAPI-Signature"].ToString();Guid? id=Guid.TryParseExact(idText,"D",out var parsed)?parsed:null;
        var valid=context.Request.Headers["X-WebAPI-Delivery-Id"].Count==1&&context.Request.Headers["X-WebAPI-Timestamp"].Count==1&&context.Request.Headers["X-WebAPI-Signature"].Count==1&&id is not null&&id!=Guid.Empty&&Verify(id.Value,timestamp,signature,body);
        var selected=redirectTarget?"Accept":Volatile.Read(ref mode);var code=!valid?401:selected switch{"Temporary"=>500,"Permanent"=>400,"RateLimited"=>429,"Redirect"=>307,_=>202};var remoteAccepted=valid&&code==202;var duplicate=remoteAccepted&&!accepted.TryAdd(id!.Value,0);
        Record(new(id,valid?timestamp:"Invalid",valid,context.Request.IsHttps,context.Features.Get<ITlsHandshakeFeature>()?.Protocol.ToString(),Convert.ToHexStringLower(SHA256.HashData(body)),body.Length,code,remoteAccepted,duplicate,clock.GetUtcNow()));
        if(valid&&selected=="Disconnect"){context.Abort();return;}
        if(valid&&selected=="Delay")await Task.Delay(TimeSpan.FromSeconds(15),ct);
        context.Response.StatusCode=code;if(code==429)context.Response.Headers.RetryAfter=Volatile.Read(ref retryAfter);
        if(code==307)context.Response.Headers.Location="/redirect-target";
        await context.Response.WriteAsync(selected=="Large"?new string('x',65536):"fixture receipt",ct);
    }
    private void Record(WebhookFixtureReceipt receipt){journal?.Append(receipt);receipts.Enqueue(receipt);}
    // Independent receiver implementation; never calls the product signature helper.
    private bool Verify(Guid id,string timestamp,string signature,byte[] body)
    {
        if(timestamp.Length is <1 or >20||!timestamp.All(char.IsAsciiDigit)||!long.TryParse(timestamp,NumberStyles.None,CultureInfo.InvariantCulture,out var seconds)||Math.Abs((double)seconds-clock.GetUtcNow().ToUnixTimeSeconds())>300||signature.Length!=64||signature.Any(c=>!char.IsAsciiDigit(c)&&c is not(>='a' and <='f')))return false;
        var prefix=Encoding.UTF8.GetBytes(timestamp+"."+id.ToString("D")+".");var input=new byte[prefix.Length+body.Length];prefix.CopyTo(input,0);body.CopyTo(input,prefix.Length);
        return CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(options.Key,input),Convert.FromHexString(signature));
    }
}
