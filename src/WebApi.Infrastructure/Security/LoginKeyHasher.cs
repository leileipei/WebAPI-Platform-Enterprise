using System.Net;
using System.Security.Cryptography;
using System.Text;
using WebApi.Contracts.Security;
namespace WebApi.Infrastructure.Security;
public sealed class LoginKeyHasher
{
    private readonly byte[] secret;
    public LoginKeyHasher(byte[] secret)
    {if(secret.Length!=32)throw new ArgumentException("Login HMAC secret must contain 32 bytes.",nameof(secret));this.secret=secret.ToArray();}
    public LoginIdentityKeys Keys(IPAddress? ip,string username)
    {
        if(ip?.IsIPv4MappedToIPv6==true)ip=ip.MapToIPv4();
        return new(Hash("ip",ip?.ToString()??"unknown"),Hash("account",username),Hash("audit-account",username));
    }
    private string Hash(string purpose,string value)=>Convert.ToHexStringLower(HMACSHA256.HashData(secret,Encoding.UTF8.GetBytes(purpose+"\0"+value)));
}
