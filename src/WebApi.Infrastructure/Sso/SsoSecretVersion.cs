using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Sso;
// Only a protected digest is persisted. All instances use the same private DP key ring.
public sealed class SsoSecretVersion(IDataProtectionProvider protection)
{
    private IDataProtector Protector(SsoProvider provider)=>protection.CreateProtector("WebApi.Sso.SecretVersion.v1",provider.Id.ToString("N"),provider.AuthRevision.ToString(CultureInfo.InvariantCulture));
    private static byte[] Digest(string secret)=>SHA256.HashData(Encoding.UTF8.GetBytes(secret));
    public void Pin(SsoProvider provider,string secret)=>provider.ProtectedSecretFingerprint=Convert.ToBase64String(Protector(provider).Protect(Digest(secret)));
    public bool Matches(SsoProvider provider,string secret)
    {
        if(string.IsNullOrEmpty(provider.ProtectedSecretFingerprint))return false;
        try{return CryptographicOperations.FixedTimeEquals(Protector(provider).Unprotect(Convert.FromBase64String(provider.ProtectedSecretFingerprint)),Digest(secret));}
        catch(Exception error) when(error is CryptographicException or FormatException){return false;}
    }
}
