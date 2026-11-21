using WebApi.Contracts.Runtime;

namespace WebApi.Contracts.Applications;

public sealed record PermissionMatrixPage(Guid ApplicationId, Guid ProjectId, DateTimeOffset EvaluatedAt,
    IReadOnlyList<MatrixEnvironment> Environments, IReadOnlyList<MatrixApi> Items, int Total, int Page, int PageSize);
public sealed record MatrixEnvironment(Guid Id, string Code, string Name, bool IsProduction, bool CanManage,
    bool CanGrantLegacy, bool CanGrantRouteMethods);
public sealed record MatrixApi(Guid Id, string Code, string Name, IReadOnlyList<MatrixGrant> Grants);
public sealed record MatrixGrant(Guid Id, Guid EnvironmentId, string Mode, DateTimeOffset ValidFrom, DateTimeOffset? ExpiresAt,
    long Revision, string State, IReadOnlyList<RuntimeRouteMethodGrant>? RouteMethods);

public sealed record MatrixChange(Guid ApiId, Guid EnvironmentId, string Action, Guid? PermissionId = null, long? Revision = null,
    DateTimeOffset? ValidFrom = null, DateTimeOffset? ExpiresAt = null, string? Mode = null, IReadOnlyList<RuntimeRouteMethodGrant>? RouteMethods = null,
    Guid? DestinationApiId = null, Guid? DestinationEnvironmentId = null);
public sealed record MatrixPreviewRequest(Guid ProjectId, IReadOnlyList<MatrixChange> Changes);
public sealed record MatrixCommitRequest(Guid ProjectId, IReadOnlyList<MatrixChange> Changes, string PreviewToken, bool ConfirmProduction = false);
public sealed record MatrixPreview(string PreviewToken, DateTimeOffset ExpiresAt, bool RequiresProductionConfirmation, IReadOnlyList<MatrixImpact> Items);
public sealed record MatrixImpact(Guid ApiId, Guid EnvironmentId, bool IsProduction, MatrixGrantImpact? Before, MatrixGrantImpact? After, MatrixRunning Running);
public sealed record MatrixGrantImpact(string Mode, string State, DateTimeOffset ValidFrom, DateTimeOffset? ExpiresAt,
    int MethodCount, IReadOnlyList<MatrixRoute>? Methods);
public sealed record MatrixRoute(Guid RouteId, string? Path, long AuthorizationRevision, IReadOnlyList<string> Methods, bool? MatchesRoute = null);
public sealed record MatrixRunning(string Status, long? ConfigVersion = null, long? DeploymentSequence = null, MatrixGrantImpact? Grant = null);
public sealed record MatrixReceipt(int AppliedCount, string Activation);
