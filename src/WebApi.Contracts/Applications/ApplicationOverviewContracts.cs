using WebApi.Contracts.CredentialUsage;
namespace WebApi.Contracts.Applications;
public sealed record ApplicationSummaryDto(int VisibleActiveApiCount,int AvailableCredentials,int TotalCredentials,string Activation="WorkingConfiguration");
public sealed record ApplicationOverviewDto(ApplicationDto Application,ApplicationSummaryDto Summary,CredentialUsagePage Usage);
