using System.Net;
using System.Security.Cryptography;
using System.Text;
namespace WebApi.Gateway.Observability;
public sealed class TelemetrySanitizer(TelemetrySettings settings)
{
    private readonly byte[] key=ReadKey(settings);
    private static byte[] ReadKey(TelemetrySettings settings)
    {
        try{var bytes=Convert.FromBase64String(File.ReadAllText(settings.IpHmacSecretFile!).Trim());if(bytes.Length<32)throw new FormatException();return bytes;}
        catch(Exception error) when(error is IOException or UnauthorizedAccessException or FormatException)
        {throw new InvalidOperationException("IP HMAC secret configuration is invalid.");}
    }
    public string Path(string? routeTemplate)=>routeTemplate is {Length:>0 and <=512}&&routeTemplate.StartsWith('/')&&!routeTemplate.Any(c=>c is '?' or '#' or '\r' or '\n')?routeTemplate:"[unmatched]";
    public static string Method(string method)=>method.Length is >0 and <=16&&method.All(c=>char.IsAsciiLetter(c))?method.ToUpperInvariant():"OTHER";
    public (string Masked,string Hmac) Ip(IPAddress address)
    {
        if(address.IsIPv4MappedToIPv6)address=address.MapToIPv4();var bytes=address.GetAddressBytes();string masked;
        if(bytes.Length==4)masked=$"{bytes[0]}.{bytes[1]}.{bytes[2]}.xxx";
        else{Array.Clear(bytes,6,bytes.Length-6);masked=new IPAddress(bytes)+"/48";}
        return(masked,Convert.ToHexStringLower(HMACSHA256.HashData(key,Encoding.UTF8.GetBytes(address.ToString()))));
    }
}
