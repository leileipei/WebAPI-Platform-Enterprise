using WebApi.Infrastructure.Persistence;

namespace WebApi.Infrastructure.Governance;

/// <summary>Reads independently verified deployment, recovery and node evidence for an environment.</summary>
public interface ILegacyEnvironmentHistoryReader
{
    /// <summary>Returns a held stop-write fence only when the evidence can be read within it.</summary>
    Task<ILegacyEnvironmentHistoryFence?> OpenAsync(Guid environmentId, CancellationToken ct);
}

public interface ILegacyEnvironmentHistoryFence : IAsyncDisposable
{
    LegacyEnvironmentHistoryEvidence Evidence { get; }

    /// <summary>A true result guarantees the verified writers remain stopped until this fence is released.</summary>
    Task<bool> IsHeldAsync(CancellationToken ct);
}

public sealed record LegacyEnvironmentSource(Guid EnvironmentId, Guid ProjectId, Guid OrganizationId,
    MigrationDatabaseIdentity Database, string Migration, string SchemaHash, string HistoryHash, DateTimeOffset CapturedAt);

public enum LegacyNodeHistoryState
{
    Unknown,
    StoppedWithoutGovernanceHistory,
    GovernanceHistoryObserved
}

public sealed record LegacyEnvironmentPublication(Guid? ReleaseId, long ConfigVersion, long DeploymentSequence,
    long SnapshotSize, string? SnapshotHash)
{
    public static LegacyEnvironmentPublication NeverPublished { get; } = new(null, 0, 0, 0, null);
}

public sealed record LegacyEnvironmentNodeHistory(Guid NodeId, string InstanceId, string StorageIdentityHash,
    LegacyNodeHistoryState HistoryState, long ConfigVersion, long DeploymentSequence, string? SnapshotHash);

/// <summary>
/// The reader must verify its deployment and recovery inventories back to creation, including prior writers,
/// restored copies, replaced nodes and persistent state. Hashes bind those reviewed inventories and the stop-write scope.
/// </summary>
public sealed record LegacyEnvironmentHistoryEvidence(LegacyEnvironmentSource Source, string DeploymentManifestHash,
    string RecoveryInventoryHash, string WriterFenceHash, bool KnownMainlineDeploymentsOnly, bool RecoveryHistoryComplete,
    bool LegacyWritersStopped, LegacyEnvironmentPublication Publication, IReadOnlyList<LegacyEnvironmentNodeHistory> Nodes);
