using System.Security.Cryptography;
using WebApi.Contracts.Applications;
using WebApi.Contracts.Common;
using WebApi.Contracts.Security;
using WebApi.Domain.CredentialSecurity;
using WebApi.Infrastructure.Persistence;

namespace WebApi.Infrastructure.CredentialSecurity;

public sealed record CredentialRevocationNode(Guid NodeId, Guid InstanceId, Guid LeaseId, string LeasePayloadHash,
    string CheckpointHash, DateTimeOffset FreshUntil, DateTimeOffset LastLeaseUntil);
public sealed record CredentialRevocationCoverageScope(Guid OrganizationId, Guid ProjectId, Guid EnvironmentId,
    Guid SecurityGeneration, long Epoch, string HeadHash, Guid ReleaseId, string SnapshotHash, long DeploymentSequence, long ConfigVersion,
    IReadOnlyList<CredentialRevocationNode> Nodes);

// A deployment authority must verify the exact current 2.4 publication,
// every current instance's protected admission and isolation of old entrances.
// Registry metadata, or a valid time signature alone, cannot provide this proof.
public interface ICredentialRevocationCoverage
{
    // The caller holds the database writer fence. Consult already verified
    // evidence in local state. This operation must not perform I/O.
    bool IsVerified(CredentialRevocationCoverageScope scope);
}

public sealed class PostgresCredentialRevocationService
{
    private readonly string connectionString;
    private readonly GovernanceJournalId journal;
    private readonly IGovernanceJournalWitness witness;
    private readonly ICredentialRevocationCoverage coverage;

    public PostgresCredentialRevocationService(string connectionString, GovernanceJournalId journal,
        IGovernanceJournalWitness witness, ICredentialRevocationCoverage coverage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        GovernanceJournalProtocol.RequireJournal(journal);
        this.connectionString = connectionString;
        this.journal = journal;
        this.witness = witness ?? throw new ArgumentNullException(nameof(witness));
        this.coverage = coverage ?? throw new ArgumentNullException(nameof(coverage));
    }

    public async Task<RevocationOperationDto> RevokeAsync(Guid credentialId, RevokeCredentialRequest request,
        string? ifMatch, CommandRequestContext command, ActorContext actor, CancellationToken ct = default,
        Func<WebApiDbContext, CancellationToken, Task>? requireSession = null)
    {
        var reason = request.Reason?.Trim();
        if (reason is not { Length: >= 1 and <= 1000 } || reason.Any(char.IsControl))
            throw new ApiException(422, "revocation_reason_required", "请输入有效的撤销理由。");
        if (command.IdempotencyKey is not { Length: >= 1 and <= 128 } ||
            command.IdempotencyKey.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '.' or ':' or '-')))
            throw new ApiException(422, "idempotency_required", "该命令需要有效的 Idempotency-Key。");

        var operationId = new Guid(SHA256.HashData(SecuritySignedEncoding.Unsigned("webapi.credential-revoke.operation.v1",
            new { journal, actor.UserId, credentialId, command.IdempotencyKey })).AsSpan(0, 16));
        var requestHash = Convert.ToHexStringLower(SHA256.HashData(CanonicalJson.Serialize(
            new { credentialId, request.ExpectedEnvironmentId, ifMatch, reason })));
        var mutation = new CredentialRevocationMutation(actor, coverage, requireSession);
        var store = new PostgresGovernanceCommitStore(connectionString, mutation);
        var coordinator = new GovernanceCommitCoordinator(journal, store, witness);

        Task<GovernanceWriteIntent> PrepareAsync() => store.InspectAsync(journal, operationId, async (db, local, saved, token) =>
        {
            if (requireSession is not null) await requireSession(db, token);
            var resource = await mutation.RequireScopeAsync(db, credentialId, request.ExpectedEnvironmentId, token);
            var observed = await witness.ObserveAsync(journal, operationId, token);
            var prior = saved ?? (observed.Pending?.OperationId == operationId ? observed.Pending : null);
            if (prior is not null)
            {
                if (CredentialRevocationIntent.Read(prior).RequestHash != requestHash) throw Conflict();
                return prior;
            }
            if (local.AwaitingConfirmation is not null || observed.Pending is not null ||
                observed.OperationConfirmation is not null || local.Position != observed.Confirmed)
                throw new ApiException(503, "credential_governance_recovery_required", "安全账本正在等待恢复或独立确认。");
            RevisionTag.Require(ifMatch, resource.Credential.Revision);
            return (await mutation.PlanAsync(db, resource, reason, requestHash, token)).Create(operationId, local.Position);
        }, ct);

        var intent = await PrepareAsync();
        var result = await coordinator.ExecuteAsync(intent, ct);
        if (result == GovernanceCommitResult.Conflict)
        {
            // Concurrent callers can have captured the same command before
            // either prepared it. Replay only an exact, now-durable intent.
            var current = await PrepareAsync();
            if (current == intent || current.Previous != intent.Previous) throw Conflict();
            intent = current;
            result = await coordinator.ExecuteAsync(intent, ct);
            if (result == GovernanceCommitResult.Conflict) throw Conflict();
        }
        return CredentialRevocationIntent.Read(intent).Receipt(intent.OperationId, result == GovernanceCommitResult.Replayed);
    }

    public Task RecoverAsync(ActorContext actor, CancellationToken ct = default,
        Func<WebApiDbContext, CancellationToken, Task>? requireSession = null) =>
        new GovernanceCommitCoordinator(journal, new PostgresGovernanceCommitStore(connectionString,
            new CredentialRevocationMutation(actor, coverage, requireSession)), witness).RecoverAsync(ct);

    private static ApiException Conflict() => new(409, "credential_revocation_conflict", "撤销请求与已记录的请求或当前状态冲突。");
}
