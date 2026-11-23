using WebApi.Contracts.Security;
namespace WebApi.Domain.Security;
public static class ScopeMatcher
{
    public static bool Matches(ScopeRef granted, ScopeRef actual) =>
        granted.OrganizationId == actual.OrganizationId &&
        (granted.ProjectId is null || granted.ProjectId == actual.ProjectId) &&
        (granted.EnvironmentId is null || granted.EnvironmentId == actual.EnvironmentId);
    // Stored only for the lifetime of an organization administrator appointment.
    public const string OrganizationAdministratorWrite = "org_admin_write";
    public static bool CanWrite(string accessMode) => accessMode is "read_write" or OrganizationAdministratorWrite;
}
