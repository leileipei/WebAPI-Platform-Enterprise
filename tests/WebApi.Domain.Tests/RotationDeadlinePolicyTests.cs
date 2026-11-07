using WebApi.Domain.Applications;
using Xunit;

namespace WebApi.Domain.Tests;

public sealed class RotationDeadlinePolicyTests
{
    private static readonly DateTimeOffset Deadline = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid AbortOperation = new("6ee4632d-5885-4fd5-a6d8-e380c22dd157");
    private static readonly Guid OldOperation = new("981af173-ff12-4306-92b3-3e2bb173e515");
    private static readonly Guid Environment = new("5c896d89-0a8f-437e-9e19-0f0c1926066e");
    private static readonly Guid OldCredential = new("8d339f15-1712-454d-ae4c-d690892a54cf");
    private static readonly Guid NewCredential = new("5f7d573a-47c9-4920-b601-486f04cc47e1");
    private static readonly Guid Rotation = new("e1b67950-776a-4a72-97ff-b281db0f9044");
    private static readonly Guid Organization = new("fc46975f-35f5-4b77-ae19-ff1d9f9a1094");
    private static readonly Guid Project = new("43f88f19-4740-4813-9f02-bd814a41975e");
    private static readonly Guid Application = new("50249f11-ecff-4dd2-828e-a88b0463b693");
    private static RotationAutomationState Formal(string status = "Verified") => new(
        AutoRevokeConfirmed: true, BindingMode: "EnvironmentBound", Status: status, RotationMode: "Formal",
        OverlapUntil: Deadline.AddHours(-2), HardDeadline: Deadline,
        OldImmediateRevocationCommitted: false, NewImmediateRevocationCommitted: false,
        OrganizationId: Organization, ProjectId: Project, ApplicationId: Application, EnvironmentId: Environment, RotationId: Rotation, OldCredentialId: OldCredential, NewCredentialId: NewCredential, NewCredentialEnvironmentId: Environment,
        OldCredentialEnvironmentId: Environment, ConfirmedEnvironmentId: Environment, ConfirmedCredentialId: OldCredential,
        ConfirmedRotationId: Rotation, ConfirmedOrganizationId: Organization, ConfirmedProjectId: Project, ConfirmedApplicationId: Application, ConfirmedNewCredentialId: NewCredential, ConfirmedOverlapUntil: Deadline.AddHours(-2), ConfirmedHardDeadline: Deadline, ScopeEvidenceVerified: true, FormalConfirmationExists: true, ConfirmationEvidenceVerified: true,
        LastVerifiedDatabaseTime: Deadline.AddHours(-3), NoPendingOldRevocationVerified: true);

    [Theory]
    [InlineData("Prepared")]
    [InlineData("AwaitingActivation")]
    [InlineData("Overlap")]
    [InlineData("Verified")]
    [InlineData("AttentionRequired")]
    public void PassedOverlapDoesNotAutomaticallyRevokeBeforeDeadline(string status)
        => Assert.Equal(RotationAutomationDecision.None, RotationDeadlinePolicy.Decide(Formal(status), Deadline.AddSeconds(-1)));

    [Theory]
    [InlineData("Prepared")]
    [InlineData("Overlap")]
    [InlineData("Verified")]
    [InlineData("AttentionRequired")]
    [InlineData("Aborting")]
    [InlineData("Aborted")]
    public void DueDeadlineRequestsOldRevocationEvenWithoutNewKeyVerification(string status)
        => Assert.Equal(RotationAutomationDecision.RevokeOldAtDeadline, RotationDeadlinePolicy.Decide(Formal(status), Deadline));

    [Fact]
    public void OrdinaryRotationNeverStartsAutomaticRevocation()
    {
        var ordinary = Formal() with { RotationMode = "Ordinary", AutoRevokeConfirmed = false, FormalConfirmationExists = false, ConfirmationEvidenceVerified = false, ConfirmedHardDeadline = null, ConfirmedOverlapUntil = null, ConfirmedEnvironmentId = Guid.Empty, ConfirmedCredentialId = Guid.Empty, ConfirmedNewCredentialId = Guid.Empty, ConfirmedRotationId = Guid.Empty, ConfirmedOrganizationId = Guid.Empty, ConfirmedProjectId = Guid.Empty, ConfirmedApplicationId = Guid.Empty };
        Assert.Equal(RotationAutomationDecision.None, RotationDeadlinePolicy.Decide(ordinary, Deadline.AddDays(1)));
        Assert.Equal(RotationAutomationDecision.ProtectionRequired, RotationDeadlinePolicy.Decide(ordinary with { FormalConfirmationExists = true }, Deadline));
        Assert.Equal(RotationAutomationDecision.ProtectionRequired, RotationDeadlinePolicy.Decide(Formal() with { AutoRevokeConfirmed = false }, Deadline));
        Assert.Equal(RotationAutomationDecision.ProtectionRequired, RotationDeadlinePolicy.Decide(Formal() with { RotationMode = "Ordinary" }, Deadline));
    }

    [Fact]
    public void FullyConfirmedAbortCancelsDeadlineButPendingAbortDoesNot()
    {
        var aborted = Formal("Aborted") with { NewImmediateRevocationCommitted = true, AbortCurrentStatus = "FullyApplied", AbortOperationId = AbortOperation, AbortConfirmedOperationId = AbortOperation, AbortCompletionRecorded = true, AbortEvidenceRotationId = Rotation, AbortEvidenceEnvironmentId = Environment, AbortEvidenceCredentialId = NewCredential, AbortCompletionEvidenceHash = new string('a',64), AbortStartedAt = Deadline.AddMinutes(-1), AbortConfirmedAt = Deadline.AddSeconds(-1) };
        Assert.Equal(RotationAutomationDecision.None, RotationDeadlinePolicy.Decide(aborted, Deadline.AddSeconds(1)));
        var pending = aborted with { Status = "Aborting", AbortCurrentStatus = "Partial", AbortCompletionRecorded = false };
        Assert.Equal(RotationAutomationDecision.AwaitConfirmation, RotationDeadlinePolicy.Decide(pending, Deadline.AddSeconds(-1)));
        Assert.Equal(RotationAutomationDecision.RevokeOldAtDeadline, RotationDeadlinePolicy.Decide(pending, Deadline));
    }

    [Theory]
    [InlineData(-1, RotationAutomationDecision.MarkAborted)]
    [InlineData(0, RotationAutomationDecision.RevokeOldAtDeadline)]
    [InlineData(1, RotationAutomationDecision.RevokeOldAtDeadline)]
    public void AbortMustBeFullyConfirmedBeforeTheDeadline(int offsetSeconds, RotationAutomationDecision expected)
    {
        var aborting = Formal("Aborting") with { NewImmediateRevocationCommitted = true, AbortCurrentStatus = "FullyApplied", AbortOperationId = AbortOperation, AbortConfirmedOperationId = AbortOperation, AbortCompletionRecorded = true, AbortEvidenceRotationId = Rotation, AbortEvidenceEnvironmentId = Environment, AbortEvidenceCredentialId = NewCredential, AbortCompletionEvidenceHash = new string('a',64), AbortStartedAt = Deadline.AddMinutes(-1), AbortConfirmedAt = Deadline.AddSeconds(offsetSeconds) };
        Assert.Equal(expected, RotationDeadlinePolicy.Decide(aborting, Deadline.AddSeconds(1)));
    }

    [Fact]
    public void ExistingOldRevocationWaitsForItsOwnFullConfirmationWithoutRepeating()
    {
        var pending = Formal("Completing") with { OldImmediateRevocationCommitted = true, OldRevocationStatus = "Partial", OldRevocationOperationId = OldOperation, OldOperationRotationId = Rotation, OldOperationEnvironmentId = Environment, OldOperationCredentialId = OldCredential, OldConfirmedOperationId = OldOperation, NewCredentialVerifiedForCurrentTargets = true, NewCredentialUsable = true, NoPendingNewRevocationVerified = true };
        Assert.Equal(RotationAutomationDecision.AwaitConfirmation, RotationDeadlinePolicy.Decide(pending, Deadline));
        Assert.Equal(RotationAutomationDecision.MarkCompleted, RotationDeadlinePolicy.Decide(pending with { OldRevocationStatus = "FullyApplied" }, Deadline));
        Assert.Equal(RotationAutomationDecision.ProtectionRequired, RotationDeadlinePolicy.Decide(pending with { OldRevocationStatus = "FullyApplied", OldConfirmedOperationId = AbortOperation }, Deadline));
        Assert.Equal(RotationAutomationDecision.ProtectionRequired, RotationDeadlinePolicy.Decide(pending with { OldRevocationOperationId = null }, Deadline));
        Assert.Equal(RotationAutomationDecision.ProtectionRequired, RotationDeadlinePolicy.Decide(pending with { OldOperationCredentialId = NewCredential }, Deadline));
    }

    [Fact]
    public void ConfirmedOldRevocationDoesNotClaimSuccessfulRotationWhenNewKeyIsUnusableOrUnverified()
    {
        var oldApplied = Formal("Completing") with { OldImmediateRevocationCommitted = true, OldRevocationStatus = "FullyApplied", OldRevocationOperationId = OldOperation, OldOperationRotationId = Rotation, OldOperationEnvironmentId = Environment, OldOperationCredentialId = OldCredential, OldConfirmedOperationId = OldOperation };
        Assert.Equal(RotationAutomationDecision.RequiresAttention, RotationDeadlinePolicy.Decide(oldApplied, Deadline));
        Assert.Equal(RotationAutomationDecision.RequiresAttention, RotationDeadlinePolicy.Decide(oldApplied with { NewImmediateRevocationCommitted = true, NewCredentialVerifiedForCurrentTargets = true, NewCredentialUsable = true }, Deadline));
        Assert.Equal(RotationAutomationDecision.RequiresAttention, RotationDeadlinePolicy.Decide(oldApplied with { NewCredentialVerifiedForCurrentTargets = true, NewCredentialUsable = false }, Deadline));
    }
    [Fact]
    public void InFlightAbortCannotBeReportedAsCompletedAfterTheDeadline()
    {
        var crossing = Formal("Aborting") with { OldImmediateRevocationCommitted = true, OldRevocationStatus = "FullyApplied", OldRevocationOperationId = OldOperation, OldOperationRotationId = Rotation, OldOperationEnvironmentId = Environment, OldOperationCredentialId = OldCredential, OldConfirmedOperationId = OldOperation, AbortOperationId = AbortOperation, AbortCurrentStatus = "Partial", NewCredentialVerifiedForCurrentTargets = true, NewCredentialUsable = true };
        Assert.Equal(RotationAutomationDecision.RequiresAttention, RotationDeadlinePolicy.Decide(crossing, Deadline));
        Assert.Equal(RotationAutomationDecision.RequiresAttention, RotationDeadlinePolicy.Decide(crossing with { Status = "Completing" }, Deadline));
        Assert.Equal(RotationAutomationDecision.ProtectionRequired, RotationDeadlinePolicy.Decide(crossing with { OldOperationEnvironmentId = Guid.NewGuid() }, Deadline));
    }
    [Fact]
    public void ContradictoryProgressOrBothRevocationsRequireAttention()
    {
        var applied = Formal("Completing") with { OldImmediateRevocationCommitted = true, OldRevocationStatus = "FullyApplied", OldRevocationOperationId = OldOperation, OldOperationRotationId = Rotation, OldOperationEnvironmentId = Environment, OldOperationCredentialId = OldCredential, OldConfirmedOperationId = OldOperation, NewCredentialVerifiedForCurrentTargets = true, NewCredentialUsable = true, NoPendingNewRevocationVerified = true };
        Assert.Equal(RotationAutomationDecision.RequiresAttention, RotationDeadlinePolicy.Decide(applied with { Status = "Prepared" }, Deadline));
        Assert.Equal(RotationAutomationDecision.RequiresAttention, RotationDeadlinePolicy.Decide(applied with { Status = "Verified" }, Deadline));
        Assert.Equal(RotationAutomationDecision.RequiresAttention, RotationDeadlinePolicy.Decide(applied with { Status = "AttentionRequired" }, Deadline));
        Assert.Equal(RotationAutomationDecision.RequiresAttention, RotationDeadlinePolicy.Decide(applied with { NewImmediateRevocationCommitted = true, OldRevocationStatus = "Partial" }, Deadline));
    }

    [Fact]
    public void UnboundDefaultOrContradictoryAbortConfirmationCannotCancelTheDeadline()
    {
        var aborting = Formal("Aborting") with { NewImmediateRevocationCommitted = true, AbortCurrentStatus = "FullyApplied", AbortOperationId = AbortOperation, AbortConfirmedOperationId = AbortOperation, AbortCompletionRecorded = true, AbortEvidenceRotationId = Rotation, AbortEvidenceEnvironmentId = Environment, AbortEvidenceCredentialId = NewCredential, AbortCompletionEvidenceHash = new string('a',64), AbortStartedAt = Deadline.AddMinutes(-1), AbortConfirmedAt = Deadline.AddSeconds(-1) };
        var expected = RotationAutomationDecision.ProtectionRequired;
        Assert.Equal(expected, RotationDeadlinePolicy.Decide(aborting with { AbortConfirmedAt = DateTimeOffset.MinValue }, Deadline));
        Assert.Equal(expected, RotationDeadlinePolicy.Decide(aborting with { AbortStartedAt = null }, Deadline));
        Assert.Equal(expected, RotationDeadlinePolicy.Decide(aborting with { AbortStartedAt = Deadline }, Deadline));
        Assert.Equal(expected, RotationDeadlinePolicy.Decide(aborting with { AbortConfirmedOperationId = Guid.NewGuid() }, Deadline));
        Assert.Equal(expected, RotationDeadlinePolicy.Decide(aborting with { AbortOperationId = Guid.Empty, AbortConfirmedOperationId = Guid.Empty }, Deadline));
        Assert.Equal(expected, RotationDeadlinePolicy.Decide(aborting with { AbortEvidenceRotationId = Guid.NewGuid() }, Deadline));
        Assert.Equal(expected, RotationDeadlinePolicy.Decide(aborting with { AbortEvidenceEnvironmentId = Guid.NewGuid() }, Deadline));
        Assert.Equal(expected, RotationDeadlinePolicy.Decide(aborting with { AbortEvidenceCredentialId = OldCredential }, Deadline));
        Assert.Equal(RotationAutomationDecision.RevokeOldAtDeadline, RotationDeadlinePolicy.Decide(aborting with { AbortCompletionRecorded = false }, Deadline));
        Assert.Equal(expected, RotationDeadlinePolicy.Decide(aborting with { AbortCompletionEvidenceHash = "invalid" }, Deadline));
    }

    [Fact]
    public void ConfirmedAbortRemainsCancelledWhenCurrentTargetCoverageChanges()
    {
        var aborted = Formal("Aborted") with { NewImmediateRevocationCommitted = true, AbortCurrentStatus = "Partial", AbortOperationId = AbortOperation, AbortConfirmedOperationId = AbortOperation, AbortCompletionRecorded = true, AbortEvidenceRotationId = Rotation, AbortEvidenceEnvironmentId = Environment, AbortEvidenceCredentialId = NewCredential, AbortCompletionEvidenceHash = new string('a',64), AbortStartedAt = Deadline.AddMinutes(-1), AbortConfirmedAt = Deadline.AddSeconds(-1) };
        Assert.Equal(RotationAutomationDecision.None, RotationDeadlinePolicy.Decide(aborted, Deadline.AddHours(1)));
        Assert.Equal(RotationAutomationDecision.ProtectionRequired, RotationDeadlinePolicy.Decide(aborted with { NewImmediateRevocationCommitted = false }, Deadline.AddHours(1)));
        Assert.Equal(RotationAutomationDecision.RequiresAttention, RotationDeadlinePolicy.Decide(aborted with { Status = "AttentionRequired" }, Deadline.AddHours(1)));
        Assert.Equal(RotationAutomationDecision.RequiresAttention, RotationDeadlinePolicy.Decide(aborted with { Status = "Unknown" }, Deadline.AddHours(1)));
        Assert.Equal(RotationAutomationDecision.RequiresAttention, RotationDeadlinePolicy.Decide(aborted with { NoPendingOldRevocationVerified = false }, Deadline.AddHours(1)));
        Assert.Equal(RotationAutomationDecision.RequiresAttention, RotationDeadlinePolicy.Decide(aborted with { OldRevocationOperationId = OldOperation, OldOperationRotationId = Rotation, OldOperationEnvironmentId = Environment, OldOperationCredentialId = OldCredential }, Deadline.AddHours(1)));
        Assert.Equal(RotationAutomationDecision.None, RotationDeadlinePolicy.Decide(aborted with { OldRevocationOperationId = OldOperation, OldRevocationStatus = "Failed", OldOperationRotationId = Rotation, OldOperationEnvironmentId = Environment, OldOperationCredentialId = OldCredential }, Deadline.AddHours(1)));
        Assert.Equal(RotationAutomationDecision.ProtectionRequired, RotationDeadlinePolicy.Decide(aborted with { OldImmediateRevocationCommitted = true, OldRevocationOperationId = OldOperation, OldOperationRotationId = Rotation, OldOperationEnvironmentId = Guid.NewGuid(), OldOperationCredentialId = OldCredential }, Deadline.AddHours(1)));
    }

    [Fact]
    public void InvalidScopeOrContradictoryCompletedStateRequiresAttention()
    {
        Assert.Equal(RotationAutomationDecision.ProtectionRequired, RotationDeadlinePolicy.Decide(Formal() with { BindingMode = "LegacyGlobal" }, Deadline));
        Assert.Equal(RotationAutomationDecision.ProtectionRequired, RotationDeadlinePolicy.Decide(Formal() with { OldCredentialEnvironmentId = Guid.NewGuid() }, Deadline));
        Assert.Equal(RotationAutomationDecision.ProtectionRequired, RotationDeadlinePolicy.Decide(Formal() with { NewCredentialId = OldCredential }, Deadline));
        Assert.Equal(RotationAutomationDecision.ProtectionRequired, RotationDeadlinePolicy.Decide(Formal() with { NewCredentialEnvironmentId = Guid.NewGuid() }, Deadline));
        Assert.Equal(RotationAutomationDecision.ProtectionRequired, RotationDeadlinePolicy.Decide(Formal() with { ConfirmedRotationId = Guid.NewGuid() }, Deadline));
        Assert.Equal(RotationAutomationDecision.ProtectionRequired, RotationDeadlinePolicy.Decide(Formal() with { ConfirmedNewCredentialId = Guid.NewGuid() }, Deadline));
        Assert.Equal(RotationAutomationDecision.ProtectionRequired, RotationDeadlinePolicy.Decide(Formal() with { ConfirmedProjectId = Guid.NewGuid() }, Deadline));
        Assert.Equal(RotationAutomationDecision.ProtectionRequired, RotationDeadlinePolicy.Decide(Formal() with { ConfirmedOverlapUntil = Deadline.AddHours(-1) }, Deadline));
        Assert.Equal(RotationAutomationDecision.ProtectionRequired, RotationDeadlinePolicy.Decide(Formal() with { ConfirmedCredentialId = Guid.NewGuid() }, Deadline));
        Assert.Equal(RotationAutomationDecision.ProtectionRequired, RotationDeadlinePolicy.Decide(Formal() with { ConfirmedHardDeadline = Deadline.AddSeconds(1) }, Deadline));
        Assert.Equal(RotationAutomationDecision.ProtectionRequired, RotationDeadlinePolicy.Decide(Formal() with { ScopeEvidenceVerified = false }, Deadline));
        Assert.Equal(RotationAutomationDecision.ProtectionRequired, RotationDeadlinePolicy.Decide(Formal() with { ConfirmationEvidenceVerified = false }, Deadline));
        Assert.Equal(RotationAutomationDecision.RequiresAttention, RotationDeadlinePolicy.Decide(Formal("Completed"), Deadline.AddSeconds(-1)));
        Assert.Equal(RotationAutomationDecision.RequiresAttention, RotationDeadlinePolicy.Decide(Formal("Unknown"), Deadline.AddSeconds(-1)));
    }

    [Fact]
    public void MissingOrRegressedDatabaseTimeRequiresProtectiveAttention()
    {
        Assert.Equal(RotationAutomationDecision.ProtectionRequired, RotationDeadlinePolicy.Decide(Formal(), DateTimeOffset.MinValue));
        Assert.Equal(RotationAutomationDecision.ProtectionRequired, RotationDeadlinePolicy.Decide(Formal(), Deadline.AddHours(-4)));
        Assert.Equal(RotationAutomationDecision.ProtectionRequired, RotationDeadlinePolicy.Decide(Formal() with { LastVerifiedDatabaseTime = null }, Deadline));
        Assert.Equal(RotationAutomationDecision.ProtectionRequired, RotationDeadlinePolicy.Decide(Formal() with { LastVerifiedDatabaseTime = Deadline.AddSeconds(10) }, Deadline.AddSeconds(-1)));
    }

    [Fact]
    public void DueDeadlineStillEnsuresRevocationForContradictoryOrInterruptedState()
    {
        var expected = RotationAutomationDecision.EnsureOldRevocationAtDeadlineAndRequireAttention;
        Assert.Equal(expected, RotationDeadlinePolicy.Decide(Formal("Completed"), Deadline));
        Assert.Equal(expected, RotationDeadlinePolicy.Decide(Formal("Unknown"), Deadline));
        Assert.Equal(expected, RotationDeadlinePolicy.Decide(Formal("Completing") with { OldRevocationOperationId = OldOperation, OldOperationRotationId = Rotation, OldOperationEnvironmentId = Environment, OldOperationCredentialId = OldCredential, OldRevocationStatus = "Partial" }, Deadline));
    }

    [Fact]
    public void LateAbortAndOldRevocationKeepDistinctOperationIdentities()
    {
        var late = Formal("Completing") with { OldImmediateRevocationCommitted = true, NewImmediateRevocationCommitted = true,
            OldRevocationOperationId = OldOperation, OldOperationRotationId = Rotation, OldOperationEnvironmentId = Environment, OldOperationCredentialId = OldCredential, OldConfirmedOperationId = OldOperation, OldRevocationStatus = "FullyApplied",
            AbortOperationId = AbortOperation, AbortConfirmedOperationId = AbortOperation, AbortCurrentStatus = "FullyApplied",
            AbortCompletionRecorded = true, AbortEvidenceRotationId = Rotation, AbortEvidenceEnvironmentId = Environment, AbortEvidenceCredentialId = NewCredential, AbortCompletionEvidenceHash = new string('a',64), AbortStartedAt = Deadline.AddMinutes(-1), AbortConfirmedAt = Deadline.AddSeconds(1) };
        Assert.Equal(RotationAutomationDecision.RequiresAttention, RotationDeadlinePolicy.Decide(late, Deadline.AddSeconds(2)));
        var incompleteLedger = late with { OldImmediateRevocationCommitted = false, NewImmediateRevocationCommitted = false };
        Assert.Equal(RotationAutomationDecision.EnsureOldRevocationAtDeadlineAndProtectionRequired, RotationDeadlinePolicy.Decide(incompleteLedger, Deadline.AddSeconds(2)));
    }
}
