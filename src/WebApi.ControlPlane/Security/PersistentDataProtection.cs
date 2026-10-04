using Microsoft.AspNetCore.DataProtection;
namespace WebApi.ControlPlane.Security;
public static class PersistentDataProtection
{
    public static void Configure(IServiceCollection services,IConfiguration configuration)
    {
        var directory=configuration["DataProtection:KeysDirectory"];var name=configuration["DataProtection:ApplicationName"];
        if(directory is null&&name is null)return;
        if(string.IsNullOrWhiteSpace(directory)||!Path.IsPathFullyQualified(directory)||string.IsNullOrWhiteSpace(name))throw new InvalidOperationException("Persistent Data Protection requires an absolute keys directory and application name.");
        services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(directory)).SetApplicationName(name);
    }
}
