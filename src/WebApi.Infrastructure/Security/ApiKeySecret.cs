using System.Security.Cryptography;
using System.Text;
namespace WebApi.Infrastructure.Security;
public sealed record ApiKeyMaterial(string AccessKey,string Secret,string Hash,string Last4);
public static class ApiKeySecret
{
    private static string Encode(byte[] bytes)=>Convert.ToBase64String(bytes).TrimEnd('=').Replace('+','-').Replace('/','_');
    public static ApiKeyMaterial Create()
    {
        var secret=Encode(RandomNumberGenerator.GetBytes(32));return new("ak_"+Encode(RandomNumberGenerator.GetBytes(16)),secret,Hash(secret),secret[^4..]);
    }
    public static string Hash(string secret)=>Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
    public static bool Verify(string expectedHash,string secret)
    {
        if(expectedHash.Length!=64||secret.Length>128) return false;
        try {return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expectedHash),SHA256.HashData(Encoding.UTF8.GetBytes(secret)));}catch(FormatException) {return false;}
    }
}
