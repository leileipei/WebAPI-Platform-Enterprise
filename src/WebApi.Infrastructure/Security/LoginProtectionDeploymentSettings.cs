using Microsoft.Extensions.Configuration;
namespace WebApi.Infrastructure.Security;
public sealed record LoginProtectionDeploymentSettings(string RedisConnection,string RedisPrefix,byte[] HmacSecret)
{
    public static LoginProtectionDeploymentSettings Read(IConfiguration configuration)
    {
        var prefix=configuration["Authentication:LoginProtection:RedisPrefix"];
        var file=configuration["Authentication:LoginProtection:HmacSecretFile"];
        var connection=configuration["Redis:Connection"];
        if(string.IsNullOrWhiteSpace(connection)||prefix is null||prefix.Length is <1 or >128||prefix.Any(c=>!char.IsAsciiLetterOrDigit(c)&&c is not '-' and not '_' and not ':')||string.IsNullOrWhiteSpace(file))
            throw new InvalidOperationException("Login protection configuration is incomplete.");
        try
        {
            var info=new FileInfo(file);
            if(!info.Exists||info.LinkTarget is not null||info.Length>256)throw new InvalidDataException();
            if(!OperatingSystem.IsWindows())
            {
                var mode=File.GetUnixFileMode(file);
                if((mode & (UnixFileMode.GroupRead|UnixFileMode.GroupWrite|UnixFileMode.GroupExecute|UnixFileMode.OtherRead|UnixFileMode.OtherWrite|UnixFileMode.OtherExecute|UnixFileMode.UserExecute))!=0 || (mode&UnixFileMode.UserRead)==0)
                    throw new InvalidDataException();
            }
            var bytes=Convert.FromBase64String(File.ReadAllText(file).Trim());
            if(bytes.Length!=32)throw new InvalidDataException();
            return new(connection,prefix,bytes);
        }
        catch(Exception e) when(e is IOException or InvalidDataException or UnauthorizedAccessException or FormatException)
        {throw new InvalidOperationException("Login protection secret is invalid or inaccessible.");}
    }
}
