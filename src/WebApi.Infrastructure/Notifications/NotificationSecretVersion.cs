using Microsoft.AspNetCore.DataProtection;
using System.Security.Cryptography;
using WebApi.Contracts.Common;
namespace WebApi.Infrastructure.Notifications;
public sealed class NotificationSecretVersion(IDataProtectionProvider protection)
{
    private IDataProtector Protector(Guid profileId)=>protection.CreateProtector("WebApi.Notification.SecretVersion.v1",profileId.ToString("N"));
    private static byte[] Digest(NotificationSecret secret)=>SHA256.HashData(CanonicalJson.Serialize(new{secret.Username,secret.Password,key=secret.Key is null?null:Convert.ToBase64String(secret.Key)}));
    public string Pin(Guid profileId,NotificationSecret secret)
    {
        try{return Convert.ToBase64String(Protector(profileId).Protect(Digest(secret)));}
        catch(Exception error)when(error is CryptographicException or InvalidOperationException){throw new ApiException(503,"notification_protection_unavailable","通知持久密钥不可用。");}
    }
    public bool Matches(Guid profileId,string fingerprint,NotificationSecret secret)
    {
        if(string.IsNullOrEmpty(fingerprint))return false;
        try{return CryptographicOperations.FixedTimeEquals(Protector(profileId).Unprotect(Convert.FromBase64String(fingerprint)),Digest(secret));}
        catch(Exception error)when(error is CryptographicException or FormatException){return false;}
    }
    internal bool Verify(Guid profileId,string fingerprint,NotificationSecret secret)
    {
        try{return CryptographicOperations.FixedTimeEquals(Protector(profileId).Unprotect(Convert.FromBase64String(fingerprint)),Digest(secret));}
        catch(Exception error)when(error is CryptographicException or FormatException or InvalidOperationException){throw new ApiException(503,"notification_protection_unavailable","通知持久密钥不可用。");}
    }
}
