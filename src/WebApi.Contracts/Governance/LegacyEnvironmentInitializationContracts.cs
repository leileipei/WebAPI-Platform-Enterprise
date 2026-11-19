namespace WebApi.Contracts.Governance;

public sealed record ConfirmLegacyEnvironmentInitialization(string PreviewHash, string Reason);

public sealed record LegacyEnvironmentInitializationPreview(Guid EnvironmentId, long EnvironmentRevision, string State,
    string StatusCode, string? PreviewHash = null, string? EvidenceHash = null, string? DeploymentManifestHash = null,
    string? SourceMigration = null, int? ReviewedNodeInstances = null, Guid? ReleaseId = null, long? ConfigVersion = null,
    string? SnapshotHash = null);
