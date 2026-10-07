using WebApi.Infrastructure.Security;
namespace WebApi.ControlPlane.Security;
public static class PersistentDataProtection
{
    public static void Configure(IServiceCollection services,IConfiguration configuration)
    {
        PersistentProtectionConfiguration.Configure(services,configuration);
    }
}
