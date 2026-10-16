using WebApi.Contracts.CredentialUsage;
namespace WebApi.Gateway.CredentialUsage;
public interface ICredentialUsageRecorder
{
    bool TryRecord(UsageObservation observation);
}
