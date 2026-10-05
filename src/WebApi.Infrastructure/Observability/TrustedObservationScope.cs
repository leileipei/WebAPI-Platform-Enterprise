namespace WebApi.Infrastructure.Observability;
public sealed record ExpectedObservationNode(Guid EnvironmentId,string NodeName);
public sealed record ObservationDestination(Guid Id,Guid ClusterId,Guid EnvironmentId,string Name,bool Enabled);
public sealed record TrustedObservationScope(Guid OrganizationId,Guid ProjectId,IReadOnlyList<Guid> EnvironmentIds,
    IReadOnlyList<ExpectedObservationNode> ExpectedNodes,IReadOnlyDictionary<Guid,string> ApiNames,
    IReadOnlyDictionary<Guid,string> ApplicationNames,IReadOnlyDictionary<Guid,ObservationDestination> Destinations,IReadOnlyDictionary<Guid,IReadOnlySet<Guid>>? PolicyIdsByEnvironment=null);
