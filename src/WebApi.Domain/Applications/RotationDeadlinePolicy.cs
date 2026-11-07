namespace WebApi.Domain.Applications;

// Inputs are facts independently verified in the caller's transaction. In
// particular, committed immediate revocations are ledger facts, not ordinary
// working-configuration status. This policy does not authenticate evidence,
// issue leases, write the ledger or operate a worker.
public sealed record RotationAutomationState(
    bool AutoRevokeConfirmed,
    string BindingMode,
    string Status,
    DateTimeOffset OverlapUntil,
    DateTimeOffset HardDeadline,
    bool OldImmediateRevocationCommitted,
    bool NewImmediateRevocationCommitted,
    string RotationMode = "",
    Guid OrganizationId = default,
    Guid ProjectId = default,
    Guid ApplicationId = default,
    Guid EnvironmentId = default,
    Guid RotationId = default,
    Guid OldCredentialId = default,
    Guid NewCredentialId = default,
    Guid OldCredentialEnvironmentId = default,
    Guid NewCredentialEnvironmentId = default,
    Guid ConfirmedEnvironmentId = default,
    Guid ConfirmedCredentialId = default,
    Guid ConfirmedNewCredentialId = default,
    Guid ConfirmedRotationId = default,
    Guid ConfirmedOrganizationId = default,
    Guid ConfirmedProjectId = default,
    Guid ConfirmedApplicationId = default,
    DateTimeOffset? ConfirmedOverlapUntil = null,
    DateTimeOffset? ConfirmedHardDeadline = null,
    bool ScopeEvidenceVerified = false,
    bool FormalConfirmationExists = false,
    bool ConfirmationEvidenceVerified = false,
    DateTimeOffset? LastVerifiedDatabaseTime = null,
    Guid? OldRevocationOperationId = null,
    Guid? OldConfirmedOperationId = null,
    Guid? OldOperationRotationId = null,
    Guid? OldOperationEnvironmentId = null,
    Guid? OldOperationCredentialId = null,
    string? OldRevocationStatus = null,
    Guid? AbortOperationId = null,
    Guid? AbortConfirmedOperationId = null,
    Guid? AbortEvidenceRotationId = null,
    Guid? AbortEvidenceEnvironmentId = null,
    Guid? AbortEvidenceCredentialId = null,
    string? AbortCurrentStatus = null,
    bool AbortCompletionRecorded = false,
    string? AbortCompletionEvidenceHash = null,
    DateTimeOffset? AbortStartedAt = null,
    DateTimeOffset? AbortConfirmedAt = null,
    bool NewCredentialVerifiedForCurrentTargets = false,
    bool NewCredentialUsable = false,
    bool NoPendingNewRevocationVerified = false,
    bool NoPendingOldRevocationVerified = false);

public enum RotationAutomationDecision
{
    None,
    // The caller must idempotently ensure the ledger fact, not blindly append.
    RevokeOldAtDeadline,
    EnsureOldRevocationAtDeadlineAndRequireAttention,
    EnsureOldRevocationAtDeadlineAndProtectionRequired,
    AwaitConfirmation,
    MarkCompleted,
    MarkAborted,
    RequiresAttention,
    // The eventual worker/issuer must block affected protected traffic until
    // independent scope/time proof has been restored.
    ProtectionRequired
}

public static class RotationDeadlinePolicy
{
    public static RotationAutomationDecision Decide(RotationAutomationState state, DateTimeOffset databaseNow)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.RotationMode == "Ordinary" && !state.AutoRevokeConfirmed)
            return !state.FormalConfirmationExists && !state.ConfirmationEvidenceVerified && state.ConfirmedHardDeadline is null &&
                   state.ConfirmedOverlapUntil is null && state.ConfirmedOrganizationId == Guid.Empty && state.ConfirmedProjectId == Guid.Empty &&
                   state.ConfirmedApplicationId == Guid.Empty && state.ConfirmedEnvironmentId == Guid.Empty && state.ConfirmedCredentialId == Guid.Empty &&
                   state.ConfirmedNewCredentialId == Guid.Empty && state.ConfirmedRotationId == Guid.Empty
                ? RotationAutomationDecision.None : RotationAutomationDecision.ProtectionRequired;
        if (state.RotationMode != "Formal" || !state.AutoRevokeConfirmed || !state.FormalConfirmationExists) return RotationAutomationDecision.ProtectionRequired;
        if (state.BindingMode != "EnvironmentBound" || !state.ScopeEvidenceVerified || !state.ConfirmationEvidenceVerified ||
            state.OrganizationId == Guid.Empty || state.ProjectId == Guid.Empty || state.ApplicationId == Guid.Empty ||
            state.EnvironmentId == Guid.Empty || state.RotationId == Guid.Empty || state.OldCredentialId == Guid.Empty ||
            state.NewCredentialId == Guid.Empty || state.NewCredentialId == state.OldCredentialId ||
            state.OldCredentialEnvironmentId != state.EnvironmentId || state.NewCredentialEnvironmentId != state.EnvironmentId ||
            state.ConfirmedOrganizationId != state.OrganizationId || state.ConfirmedProjectId != state.ProjectId || state.ConfirmedApplicationId != state.ApplicationId ||
            state.ConfirmedEnvironmentId != state.EnvironmentId || state.ConfirmedRotationId != state.RotationId || state.ConfirmedNewCredentialId != state.NewCredentialId ||
            state.ConfirmedCredentialId != state.OldCredentialId || state.ConfirmedOverlapUntil != state.OverlapUntil || state.ConfirmedHardDeadline != state.HardDeadline ||
            state.HardDeadline == DateTimeOffset.MinValue || state.OverlapUntil == DateTimeOffset.MinValue ||
            state.OverlapUntil > state.HardDeadline || databaseNow == DateTimeOffset.MinValue ||
            state.LastVerifiedDatabaseTime is not DateTimeOffset last || last == DateTimeOffset.MinValue || databaseNow < last)
            return RotationAutomationDecision.ProtectionRequired;

        var oldOperationBound = state.OldRevocationOperationId is Guid oldOperationId && oldOperationId != Guid.Empty &&
            state.OldOperationRotationId == state.RotationId && state.OldOperationEnvironmentId == state.EnvironmentId &&
            state.OldOperationCredentialId == state.OldCredentialId;
        if (state.OldImmediateRevocationCommitted && !oldOperationBound) return RotationAutomationDecision.ProtectionRequired;
        if (state.OldRevocationOperationId is not null && !oldOperationBound)
        {
            if (state.AbortCompletionRecorded && state.AbortConfirmedAt < state.HardDeadline) return RotationAutomationDecision.ProtectionRequired;
            return databaseNow >= state.HardDeadline && !state.OldImmediateRevocationCommitted
                ? RotationAutomationDecision.EnsureOldRevocationAtDeadlineAndProtectionRequired
                : RotationAutomationDecision.ProtectionRequired;
        }

        // Validate a claimed immutable completion before consulting mutable
        // status. Invalid claimed evidence requires protection, never a guess.
        if (state.AbortCompletionRecorded)
        {
            var validEvidence = state.AbortOperationId is Guid abortId && abortId != Guid.Empty &&
                state.AbortConfirmedOperationId == abortId && state.AbortEvidenceRotationId == state.RotationId &&
                state.AbortEvidenceEnvironmentId == state.EnvironmentId && state.AbortEvidenceCredentialId == state.NewCredentialId &&
                ValidHash(state.AbortCompletionEvidenceHash) &&
                state.AbortStartedAt is DateTimeOffset started && started != DateTimeOffset.MinValue &&
                state.AbortConfirmedAt is DateTimeOffset confirmed && confirmed != DateTimeOffset.MinValue &&
                started <= confirmed && confirmed <= databaseNow;
            if (!validEvidence) return RotationAutomationDecision.ProtectionRequired;
            if (!state.NewImmediateRevocationCommitted)
                return databaseNow >= state.HardDeadline && state.AbortConfirmedAt >= state.HardDeadline && !state.OldImmediateRevocationCommitted
                    ? RotationAutomationDecision.EnsureOldRevocationAtDeadlineAndProtectionRequired
                    : RotationAutomationDecision.ProtectionRequired;
            if (state.AbortConfirmedAt < state.HardDeadline)
            {
                if (state.OldImmediateRevocationCommitted || !state.NoPendingOldRevocationVerified ||
                    state.OldRevocationOperationId is not null && state.OldRevocationStatus is not ("Failed" or "Cancelled")) return RotationAutomationDecision.RequiresAttention;
                return state.Status switch
                {
                    "Aborted" => RotationAutomationDecision.None,
                    "Aborting" => RotationAutomationDecision.MarkAborted,
                    _ => RotationAutomationDecision.RequiresAttention
                };
            }
        }

        var known = state.Status is "Prepared" or "AwaitingActivation" or "Overlap" or "Verified" or "AttentionRequired" or "Completing" or "Aborting" or "Completed" or "Aborted";
        if (!known)
            return databaseNow >= state.HardDeadline && !state.OldImmediateRevocationCommitted
                ? RotationAutomationDecision.EnsureOldRevocationAtDeadlineAndRequireAttention
                : RotationAutomationDecision.RequiresAttention;

        if (state.OldImmediateRevocationCommitted)
        {
            // AttentionRequired can include faults not represented here; an
            // explicit, audited recovery must clear them before completion.
            if (state.Status is not ("Completing" or "Completed"))
                return RotationAutomationDecision.RequiresAttention;
            var oldOperation = state.OldRevocationOperationId!.Value;
            if (state.NewImmediateRevocationCommitted) return RotationAutomationDecision.RequiresAttention;
            if (state.OldRevocationStatus != "FullyApplied") return RotationAutomationDecision.AwaitConfirmation;
            if (state.OldConfirmedOperationId != oldOperation) return RotationAutomationDecision.ProtectionRequired;
            if (!state.NoPendingNewRevocationVerified || !state.NewCredentialVerifiedForCurrentTargets || !state.NewCredentialUsable)
                return RotationAutomationDecision.RequiresAttention;
            return state.Status == "Completed" ? RotationAutomationDecision.None : RotationAutomationDecision.MarkCompleted;
        }

        // Contradictory states still need an idempotent deadline safeguard;
        // resolving an existing or interrupted operation belongs to the worker.
        if (state.Status == "Completed" || state.OldRevocationOperationId is not null)
            return databaseNow >= state.HardDeadline
                ? RotationAutomationDecision.EnsureOldRevocationAtDeadlineAndRequireAttention
                : RotationAutomationDecision.RequiresAttention;

        if (databaseNow >= state.HardDeadline) return RotationAutomationDecision.RevokeOldAtDeadline;
        if (state.Status is "Aborting" or "Aborted") return RotationAutomationDecision.AwaitConfirmation;
        return RotationAutomationDecision.None;
    }

    private static bool ValidHash(string? value)
        => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
