using System.Security.Cryptography;
using System.Text;
using System.Globalization;
namespace WebApi.Domain.Notifications;
public static class WebhookSignature
{
    public static string Sign(ReadOnlySpan<byte> key,string timestamp,Guid deliveryId,ReadOnlySpan<byte> body)
    {
        if(key.Length is <32 or >64||deliveryId==Guid.Empty||body.Length>16384||string.IsNullOrEmpty(timestamp)||timestamp.Length>20||timestamp.Any(c=>c is <'0' or >'9')||!long.TryParse(timestamp,NumberStyles.None,CultureInfo.InvariantCulture,out _))throw new ArgumentException("Webhook 签名输入不合法。");
        var prefix=Encoding.UTF8.GetBytes(timestamp+"."+deliveryId.ToString("D")+".");var input=new byte[prefix.Length+body.Length];prefix.CopyTo(input,0);body.CopyTo(input.AsSpan(prefix.Length));
        return Convert.ToHexString(HMACSHA256.HashData(key,input)).ToLowerInvariant();
    }
}
