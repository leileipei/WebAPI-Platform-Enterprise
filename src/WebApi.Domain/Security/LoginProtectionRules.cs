using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using WebApi.Contracts.Security;
namespace WebApi.Domain.Security;
public static class LoginProtectionRules
{
    public static void Validate(LoginRateLimits limits)
    {
        if(limits.IpMaxAttempts is <1 or >10000 || limits.AccountMaxAttempts is <1 or >10000 || limits.IpWindowSeconds is <1 or >3600 || limits.AccountWindowSeconds is <1 or >3600)
            throw new ArgumentOutOfRangeException(nameof(limits));
    }
    public static string Fingerprint(LoginRateLimits limits)
    {
        Validate(limits);
        var value=string.Create(CultureInfo.InvariantCulture,$"{limits.IpMaxAttempts}:{limits.IpWindowSeconds}:{limits.AccountMaxAttempts}:{limits.AccountWindowSeconds}");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }
}
