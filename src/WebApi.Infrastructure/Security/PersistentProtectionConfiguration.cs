using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
namespace WebApi.Infrastructure.Security;
public static class PersistentProtectionConfiguration
{
    public static void Configure(IServiceCollection services,IConfiguration configuration,bool readOnly=false)
    {
        var directory=configuration["DataProtection:KeysDirectory"];var name=configuration["DataProtection:ApplicationName"];
        if(directory is null&&name is null){if(readOnly)services.AddDataProtection().DisableAutomaticKeyGeneration();return;}
        if(string.IsNullOrWhiteSpace(directory)||!Path.IsPathFullyQualified(directory)||string.IsNullOrWhiteSpace(name))throw new InvalidOperationException("Persistent Data Protection requires an absolute keys directory and application name.");
        var protection=services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(directory)).SetApplicationName(name);
        if(readOnly)protection.DisableAutomaticKeyGeneration();
    }
}
