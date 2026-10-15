using System.Net;
using System.Security.Cryptography;
using System.Text;
using WebApi.Contracts.Security;
using WebApi.Contracts.Common;
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
    public LoginIdentityKeys PasswordChangeKeys(IPAddress? ip,Guid userId)
    {
        if(ip?.IsIPv4MappedToIPv6==true)ip=ip.MapToIPv4();
        return new(Hash("password-change/ip",ip?.ToString()??"unknown"),Hash("password-change/account",userId.ToString("N")),Hash("password-change/audit-account",userId.ToString("N")));
    }
    public string PasswordResetFingerprint(Guid actorId,Guid targetId,long expectedRevision,string password)
        =>Hash("password-reset/idempotency",Encoding.UTF8.GetString(CanonicalJson.Serialize(new {Action="user.password.reset",ActorId=actorId,TargetId=targetId,ExpectedRevision=expectedRevision,Password=password})));
    private string Hash(string purpose,string value)=>Convert.ToHexStringLower(HMACSHA256.HashData(secret,Encoding.UTF8.GetBytes(purpose+"\0"+value)));
}
