namespace WebApi.Infrastructure.Observability;
public sealed record ExpectedObservationNode(Guid EnvironmentId,string NodeName);
public sealed record ObservationDestination(Guid Id,Guid ClusterId,Guid EnvironmentId,string Name,bool Enabled);
public sealed record ObservationApplication(string Name, IReadOnlySet<Guid>? SharedEnvironmentIds);
public sealed record TrustedObservationScope(Guid OrganizationId,Guid ProjectId,IReadOnlyList<Guid> EnvironmentIds,
    IReadOnlyList<ExpectedObservationNode> ExpectedNodes,IReadOnlyDictionary<Guid,string> ApiNames,
    IReadOnlyDictionary<Guid,ObservationApplication> Applications,IReadOnlyDictionary<Guid,ObservationDestination> Destinations,IReadOnlyDictionary<Guid,IReadOnlySet<Guid>>? PolicyIdsByEnvironment=null)
{
    internal DestinationMembership? Membership { get; init; }

    internal bool ContainsApplication(Guid environmentId, Guid applicationId) =>
        Applications.TryGetValue(applicationId, out var app) &&
        (app.SharedEnvironmentIds is null || app.SharedEnvironmentIds.Contains(environmentId));

    internal bool RetainsAccessTo(TrustedObservationScope previous)
    {
        if (!EnvironmentIds.SequenceEqual(previous.EnvironmentIds)) return false;
        foreach (var (id, app) in previous.Applications)
        {
            if (!Applications.TryGetValue(id, out var current)) return false;
            if (current.SharedEnvironmentIds is { } allowed &&
                !allowed.IsSupersetOf(app.SharedEnvironmentIds?.AsEnumerable() ?? previous.EnvironmentIds)) return false;
        }
        return true;
    }
}
