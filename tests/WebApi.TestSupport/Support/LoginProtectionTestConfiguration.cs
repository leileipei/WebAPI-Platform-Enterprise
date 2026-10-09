using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
namespace WebApi.Integration.Tests.Support;
public static class LoginProtectionTestConfiguration
{
    public static void Apply(WebApplicationBuilder builder,string directory,string deploymentId)
    {
        Directory.CreateDirectory(directory);
        var file=Path.Combine(directory,"login-protection-hmac");
        if(!File.Exists(file))
        {
            var options=new FileStreamOptions{Mode=FileMode.CreateNew,Access=FileAccess.Write};
            if(!OperatingSystem.IsWindows())options.UnixCreateMode=UnixFileMode.UserRead|UnixFileMode.UserWrite;
            using var stream=new FileStream(file,options);
            using var writer=new StreamWriter(stream);writer.Write(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        }
        builder.Configuration["Authentication:LoginProtection:HmacSecretFile"]=file;
        builder.Configuration["Authentication:LoginProtection:RedisPrefix"]="test-login-"+deploymentId;
        builder.Configuration["Redis:Connection"]=Environment.GetEnvironmentVariable("WEBAPI_TEST_REDIS")??"redis:6379";
    }
}
