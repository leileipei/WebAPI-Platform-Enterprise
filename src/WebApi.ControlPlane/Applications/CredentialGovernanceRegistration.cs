using System.Security.Cryptography.X509Certificates;
using WebApi.Infrastructure.CredentialSecurity;

namespace WebApi.ControlPlane.Applications;

public static class CredentialGovernanceRegistration
{
    /// <summary>Registers an independently provisioned writer and node evidence sources.</summary>
    public static IServiceCollection AddWitnessedCredentialGovernance(this IServiceCollection services,
        IndependentGovernanceWitnessOptions options, X509Certificate2 writerCertificate) =>
        CredentialGovernanceServiceRegistration.AddWitnessedCredentialGovernance(services, options, writerCertificate, nodeServices: true);
}
