using WebApi.Contracts.Security;
namespace WebApi.Domain.Security;
public static class ScopeMatcher
{
    public static bool Matches(ScopeRef granted, ScopeRef actual) =>
        granted.OrganizationId == actual.OrganizationId &&
        (granted.ProjectId is null || granted.ProjectId == actual.ProjectId) &&
        (granted.EnvironmentId is null || granted.EnvironmentId == actual.EnvironmentId);
    public static bool CanWrite(string accessMode) => accessMode == "read_write";
}
