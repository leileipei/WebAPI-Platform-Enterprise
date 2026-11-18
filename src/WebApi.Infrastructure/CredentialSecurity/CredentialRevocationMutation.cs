using System.Buffers.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Applications;
using WebApi.Contracts.Common;
using WebApi.Contracts.Security;
using WebApi.Domain.CredentialSecurity;
using WebApi.Infrastructure.Gateway;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Releases;
using WebApi.Infrastructure.Security;

namespace WebApi.Infrastructure.CredentialSecurity;

internal sealed record CredentialRevocationIntent(Guid CredentialId, Guid ApplicationId, Guid ActorId, long CredentialRevision,
    long ApplicationRevision, long ApplicationAuthorizationRevision, long EnvironmentAuthorizationRevision,
    string Reason, string RequestHash, DateTimeOffset EventTime,
    CredentialRevocationCoverageScope Coverage, Guid[] Challenges)
{
    private static readonly JsonSerializerOptions Strict = new(CanonicalJson.Options)
    {
        PropertyNameCaseInsensitive = false,
        NumberHandling = JsonNumberHandling.Strict,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16
    };

    public GovernanceWriteIntent Create(Guid operationId, GovernanceJournalPosition previous) =>
        new(operationId, previous, Base64Url.EncodeToString(CanonicalJson.Serialize(this)));

    public static CredentialRevocationIntent Read(GovernanceWriteIntent intent)
    {
        try
        {
            var bytes = Base64Url.DecodeFromChars(intent.RecoveryMaterial);
            var plan = JsonSerializer.Deserialize<CredentialRevocationIntent>(bytes, Strict);
            if (plan is null || !bytes.AsSpan().SequenceEqual(CanonicalJson.Serialize(plan)) || !plan.Valid()) throw Invalid();
            return plan;
        }
        catch (Exception error) when (error is FormatException or JsonException) { throw Invalid(); }
    }

    private bool Valid() => CredentialId != Guid.Empty && ApplicationId != Guid.Empty && ActorId != Guid.Empty &&
        CredentialRevision > 0 && ApplicationRevision > 0 && ApplicationAuthorizationRevision > 0 && EnvironmentAuthorizationRevision > 0 &&
        Reason is { Length: >= 1 and <= 1000 } && !Reason.Any(char.IsControl) && SecuritySignedEncoding.HexHash(RequestHash) &&
        EventTime > DateTimeOffset.MinValue && EventTime.Offset == TimeSpan.Zero && EventTime.Ticks % 10 == 0 &&
        Coverage is { Nodes: { Count: >= 2 } } && Coverage.OrganizationId != Guid.Empty && Coverage.ProjectId != Guid.Empty &&
        Coverage.EnvironmentId != Guid.Empty && Coverage.SecurityGeneration != Guid.Empty && Coverage.Epoch is >= 0 and < long.MaxValue &&
        SecuritySignedEncoding.HexHash(Coverage.HeadHash) && Coverage.ReleaseId != Guid.Empty && SecuritySignedEncoding.HexHash(Coverage.SnapshotHash) &&
        Coverage.DeploymentSequence > 0 && Coverage.ConfigVersion > 0 &&
        Coverage.Nodes.All(x => x is not null && x.NodeId != Guid.Empty && x.InstanceId != Guid.Empty &&
            x.LeaseId != Guid.Empty && SecuritySignedEncoding.HexHash(x.LeasePayloadHash) && SecuritySignedEncoding.HexHash(x.CheckpointHash) &&
            x.FreshUntil > EventTime && x.LastLeaseUntil >= x.FreshUntil && x.FreshUntil.Offset == TimeSpan.Zero && x.LastLeaseUntil.Offset == TimeSpan.Zero) &&
        Coverage.Nodes.Select(x => x.NodeId).SequenceEqual(Coverage.Nodes.Select(x => x.NodeId).Distinct().Order()) &&
        Challenges is not null && Challenges.Length == Coverage.Nodes.Count &&
        Challenges.All(x => x != Guid.Empty) && Challenges.Distinct().Count() == Challenges.Length;

    public SecurityEvent Event(Guid id)
    {
        var fact = new SecurityEvent { OperationId = id, OrganizationId = Coverage.OrganizationId, ProjectId = Coverage.ProjectId,
            EnvironmentId = Coverage.EnvironmentId, SecurityGeneration = Coverage.SecurityGeneration, Epoch = checked(Coverage.Epoch + 1),
            CredentialId = CredentialId, OccurredAt = EventTime, PreviousHash = Coverage.HeadHash };
        fact.EventHash = SecurityEventHash.Compute(fact);
        return fact;
    }

    public RevocationOperationDto Receipt(Guid id, bool replayed)
    {
        var fact = Event(id);
        return new(id, CredentialId, EventTime, 1, "Unconfirmed", $"/api/v1/credential-revocations/{id}",
            [new(fact.EnvironmentId, fact.SecurityGeneration, fact.Epoch, fact.EventHash)],
            Coverage.Nodes.Select(n => new RevocationTargetDto(fact.EnvironmentId, n.NodeId, n.InstanceId, "Pending", LastLeaseUntil: n.LastLeaseUntil)).ToArray(), replayed);
    }

    private static InvalidDataException Invalid() => new("credential_revocation_intent_invalid");
}

internal sealed class CredentialRevocationMutation(ActorContext actor, ICredentialRevocationCoverage coverage,
    Func<WebApiDbContext, CancellationToken, Task>? requireSession = null) : IPostgresGovernanceMutation
{
    internal sealed record Resource(ApplicationCredential Credential, ApplicationRecord Application, EnvironmentRecord Environment);
    private sealed record RevocationAuditFact(Guid OperationId, long Epoch, string EventHash);

    internal async Task<Resource> RequireScopeAsync(WebApiDbContext db, Guid id, Guid? expectedEnvironment, CancellationToken ct)
    {
        var resource = await ReadResourceAsync(db, id, actor, ct, expectedEnvironment, coverage as ICredentialRevocationHistory);
        if (expectedEnvironment != resource.Environment.Id)
            throw new ApiException(409, "credential_revocation_environment_mismatch", "所选环境与凭证的绑定环境不一致。");
        return resource;
    }

    private static async Task<Resource> ReadResourceAsync(WebApiDbContext db, Guid id, ActorContext requester, CancellationToken ct,
        Guid? expectedEnvironment = null, ICredentialRevocationHistory? history = null)
    {
        var credential = await db.Set<ApplicationCredential>().SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new ApiException(404, "credential_not_found", "找不到凭证。");
        var app = await db.Set<ApplicationRecord>().SingleAsync(x => x.Id == credential.ApplicationId, ct);
        if (credential.BindingMode == "LegacyGlobal" && history is not null)
        {
            await LegacyGlobalRevocationScope.ReadAsync(db, credential, app, expectedEnvironment, requester, history, ct);
            // Complete historical scope is only one prerequisite. Multi-environment
            // commit and future inheritance are required before this action can write.
            throw LegacyGlobalRevocationScope.Unknown();
        }
        var auth = new AuthorizationService(db);
        await auth.RequireAsync(requester, "credential.revoke", new("credential", id,
            new(credential.OrganizationId, credential.ProjectId ?? app.ProjectId, credential.EnvironmentId)), ct);
        if (credential.BindingMode != "EnvironmentBound" || credential.EnvironmentId is not Guid environmentId ||
            credential.ProjectId is not Guid projectId || credential.OrganizationId != app.OrganizationId ||
            app.ProjectId is not null && app.ProjectId != projectId)
            throw new ApiException(409, "credential_revocation_scope_unknown", "凭证的即时撤销范围尚未核验。");
        var environment = await db.Set<EnvironmentRecord>().SingleAsync(x => x.Id == environmentId && x.ProjectId == projectId, ct);
        return new(credential, app, environment);
    }

    internal async Task<CredentialRevocationIntent> PlanAsync(WebApiDbContext db, Resource resource, string reason, string requestHash, CancellationToken ct)
    {
        var now = await SecurityDatabaseClock.ObserveAsync(db, ct);
        var scope = await ReadCoverageAsync(db, resource, now, ct);
        return new(resource.Credential.Id, resource.Application.Id, actor.UserId, resource.Credential.Revision,
            resource.Application.Revision, resource.Application.AuthorizationRevision, resource.Environment.AuthorizationRevision,
            reason, requestHash, now, scope,
            scope.Nodes.Select(_ => Guid.NewGuid()).ToArray());
    }

    public async Task AuthorizeAsync(WebApiDbContext db, GovernanceWriteIntent intent, CancellationToken ct)
    {
        if (requireSession is not null) await requireSession(db, ct);
        var plan = CredentialRevocationIntent.Read(intent);
        if (plan.ActorId != actor.UserId) throw new ApiException(403, "permission_denied", "缺少操作权限。");
        await RequireScopeAsync(db, plan.CredentialId, plan.Coverage.EnvironmentId, ct);
    }

    public async Task AuthorizeRecoveryAsync(WebApiDbContext db, CancellationToken ct)
    {
        if (requireSession is not null) await requireSession(db, ct);
        await new AuthorizationService(db).RequireAsync(actor, "credential.governance.recover",
            new("credential_governance", Guid.Empty, new(Guid.Empty)), ct);
    }

    public async Task ValidateNewAsync(WebApiDbContext db, GovernanceWriteIntent intent, CancellationToken ct) =>
        await ValidatePlanAsync(db, CredentialRevocationIntent.Read(intent), ct);

    public async Task ApplyAsync(WebApiDbContext db, GovernanceWriteIntent intent, CancellationToken ct)
    {
        var plan = CredentialRevocationIntent.Read(intent);
        var resource = await ValidatePlanAsync(db, plan, ct);
        var fact = plan.Event(intent.OperationId);
        var authority = await db.Set<EnvironmentSecurityState>().SingleAsync(x => x.EnvironmentId == fact.EnvironmentId, ct);
        authority.Epoch = fact.Epoch;
        authority.HeadHash = fact.EventHash;
        authority.Revision = checked(authority.Revision + 1);
        authority.UpdatedAt = plan.EventTime;
        resource.Credential.Status = "Revoked";
        resource.Credential.RevokedAt ??= plan.EventTime;
        resource.Credential.Revision = checked(resource.Credential.Revision + 1);
        resource.Application.Revision = checked(resource.Application.Revision + 1);
        resource.Application.AuthorizationRevision = checked(resource.Application.AuthorizationRevision + 1);
        resource.Application.UpdatedAt = plan.EventTime;
        resource.Environment.AuthorizationRevision = checked(resource.Environment.AuthorizationRevision + 1);
        db.AddRange(new SecurityOperation { Id = fact.OperationId, OrganizationId = fact.OrganizationId, ApplicationId = plan.ApplicationId,
                CredentialId = plan.CredentialId, RequestedBy = plan.ActorId, Reason = plan.Reason, CommittedAt = plan.EventTime },
            fact,
            new CredentialRevocation { EnvironmentId = fact.EnvironmentId, OrganizationId = fact.OrganizationId, ProjectId = fact.ProjectId,
                CredentialId = plan.CredentialId, ApplicationId = plan.ApplicationId, SecurityGeneration = fact.SecurityGeneration,
                Epoch = fact.Epoch, OperationId = fact.OperationId, RevokedAt = plan.EventTime },
            new SecurityOutboxMessage { Id = fact.OperationId, EnvironmentId = fact.EnvironmentId, SecurityGeneration = fact.SecurityGeneration,
                Epoch = fact.Epoch, OperationId = fact.OperationId, CreatedAt = plan.EventTime },
            new AuditLog { OrganizationId = fact.OrganizationId, ProjectId = fact.ProjectId, EnvironmentId = fact.EnvironmentId,
                UserId = plan.ActorId, Action = "credential.revoke", ResourceType = "credential", ResourceId = plan.CredentialId.ToString(),
                CreatedAt = plan.EventTime, AfterJson = JsonSerializer.Serialize(new RevocationAuditFact(fact.OperationId, fact.Epoch, fact.EventHash)) });
        foreach (var (node, index) in plan.Coverage.Nodes.Select((node, index) => (node, index)))
            db.Add(new SecurityOperationTarget { OperationId = fact.OperationId, EnvironmentId = fact.EnvironmentId, NodeId = node.NodeId,
                InstanceGuid = node.InstanceId, ChallengeNonce = plan.Challenges[index], CreatedAt = plan.EventTime,
                LastLeaseUntil = node.LastLeaseUntil, ProtocolSupportedAtFreeze = true });
    }

    private async Task<Resource> ValidatePlanAsync(WebApiDbContext db, CredentialRevocationIntent plan, CancellationToken ct)
    {
        var resource = await ReadResourceAsync(db, plan.CredentialId, new(plan.ActorId, actor.TraceId), ct);
        if (resource.Credential.Revision != plan.CredentialRevision || resource.Application.Id != plan.ApplicationId ||
            resource.Application.Revision != plan.ApplicationRevision || resource.Application.AuthorizationRevision != plan.ApplicationAuthorizationRevision ||
            resource.Environment.AuthorizationRevision != plan.EnvironmentAuthorizationRevision)
            throw new ApiException(409, "credential_revocation_changed", "撤销准备后数据已变更。");
        var now = await SecurityDatabaseClock.ObserveAsync(db, ct);
        var current = await ReadCoverageAsync(db, resource, now, ct);
        if (plan.EventTime > now || !CanonicalJson.Serialize(plan.Coverage).AsSpan().SequenceEqual(CanonicalJson.Serialize(current)) ||
            !coverage.IsVerified(current))
            throw CoverageUnavailable();
        return resource;
    }

    private static async Task<CredentialRevocationCoverageScope> ReadCoverageAsync(WebApiDbContext db, Resource resource,
        DateTimeOffset now, CancellationToken ct)
    {
        var env = resource.Environment;
        var ledger = await new SecurityLedgerReader(db).ReadCompleteLockedAsync(env.Id, ct);
        if (ledger.OrganizationId != resource.Credential.OrganizationId || ledger.ProjectId != resource.Credential.ProjectId ||
            ledger.Revocations.Any(r => r.CredentialId == resource.Credential.Id) ||
            await db.Set<SecurityOperation>().AnyAsync(o => o.Kind == "CredentialRevoke" && o.CredentialId == resource.Credential.Id, ct))
            throw new ApiException(409, "credential_revocation_changed", "已有安全撤销或账本范围不一致。");
        var deployment = await new RunningDeploymentReader(db, new HistoricalSnapshotService(db, new ScopeResolver(db))).ReadAsync(env.Id, null, ct);
        var published = deployment.Snapshot.Snapshot;
        if (published.SchemaVersion != "2.4" || published.RequiredSecurityGeneration != ledger.SecurityGeneration ||
            published.RequiredSecurityEpoch is not long publishedEpoch || publishedEpoch > ledger.Epoch ||
            published.MinimumSnapshotSchema != ledger.MinimumSnapshotSchema || published.RequireEnvironmentCredentials != ledger.RequireEnvironmentCredentials)
            throw CoverageUnavailable();
        var nodes = await db.Set<GatewayNode>().AsNoTracking().Where(x => x.EnvironmentId == env.Id && x.Enabled).ToArrayAsync(ct);
        var grace = TimeSpan.FromSeconds(new PublishSettings().HeartbeatGraceSeconds);
        if (env.DesiredConfigVersion is not > 0 || env.DeploymentSequence <= 0 || nodes.Length < 2 || nodes.Any(n =>
            n.Status != "Ready" || n.CurrentConfigVersion != env.DesiredConfigVersion || n.CurrentDeploymentSequence != env.DeploymentSequence ||
            !SecuritySignedEncoding.HexHash(n.IdentityHash) || n.LastHeartbeatAt is not DateTimeOffset heartbeat || heartbeat > now || now - heartbeat > grace))
            throw CoverageUnavailable();
        SnapshotSchemaCapabilities.RequireSupported(nodes, "2.4");
        var proven = await (from receipt in db.Set<GatewaySecurityCurrentReceipt>().AsNoTracking()
                            join lease in db.Set<GatewaySecurityLease>().AsNoTracking() on receipt.LeaseId equals lease.LeaseId
                            join issuance in db.Set<GatewaySecurityLeaseIssuance>().AsNoTracking() on receipt.LeaseId equals issuance.LeaseId
                            where receipt.EnvironmentId == env.Id && receipt.ProjectId == ledger.ProjectId && receipt.OrganizationId == ledger.OrganizationId &&
                                receipt.SecurityGeneration == ledger.SecurityGeneration && receipt.Epoch == ledger.Epoch && receipt.HeadHash == ledger.HeadHash &&
                                receipt.ReceivedAt <= now && receipt.FreshUntil > now &&
                                lease.NodeId == receipt.NodeId && lease.InstanceId == receipt.InstanceId && lease.EnvironmentId == receipt.EnvironmentId &&
                                lease.ProjectId == receipt.ProjectId && lease.OrganizationId == receipt.OrganizationId &&
                                lease.SecurityGeneration == receipt.SecurityGeneration && lease.Epoch == receipt.Epoch &&
                                lease.CheckpointHash == receipt.CheckpointHash && lease.FreshUntil == receipt.FreshUntil &&
                                issuance.NodeId == lease.NodeId && issuance.InstanceId == lease.InstanceId && issuance.EnvironmentId == lease.EnvironmentId &&
                                issuance.ProjectId == lease.ProjectId && issuance.OrganizationId == lease.OrganizationId &&
                                issuance.SecurityGeneration == lease.SecurityGeneration && issuance.Epoch == lease.Epoch && issuance.CheckpointHash == lease.CheckpointHash &&
                                issuance.SigningKeyId == lease.SigningKeyId && issuance.IssuedAt == lease.IssuedAt && issuance.FreshUntil == lease.FreshUntil &&
                                issuance.IssuedAt <= receipt.ReceivedAt &&
                                !db.Set<GatewaySecurityLeaseExpiryObservation>().Any(x => x.LeaseId == receipt.LeaseId)
                            select new CredentialRevocationNode(receipt.NodeId, receipt.InstanceId, receipt.LeaseId, issuance.PayloadHash,
                                receipt.CheckpointHash, receipt.FreshUntil, lease.LastIssuedLeaseUntil)).ToArrayAsync(ct);
        var targets = nodes.OrderBy(n => n.Id).Select(n =>
        {
            var instance = Guid.Parse(n.InstanceId);
            return proven.SingleOrDefault(l => l.NodeId == n.Id && l.InstanceId == instance) ?? throw CoverageUnavailable();
        }).ToArray();
        return new(ledger.OrganizationId, ledger.ProjectId, env.Id, ledger.SecurityGeneration, ledger.Epoch, ledger.HeadHash,
            deployment.Release.Id, deployment.Snapshot.Hash, env.DeploymentSequence, env.DesiredConfigVersion.Value, targets);
    }

    public async Task<bool> VerifyAsync(WebApiDbContext db, GovernanceWriteIntent intent, CancellationToken ct)
    {
        var plan = CredentialRevocationIntent.Read(intent);
        var fact = plan.Event(intent.OperationId);
        var ledger = await new SecurityLedgerReader(db).ReadCompleteLockedAsync(fact.EnvironmentId, ct);
        if (ledger.Events.SingleOrDefault(x => x.OperationId == fact.OperationId)?.EventHash != fact.EventHash ||
            await db.Set<SecurityEvent>().CountAsync(x => x.OperationId == fact.OperationId, ct) != 1 ||
            !await db.Set<SecurityOperation>().AnyAsync(x => x.Id == fact.OperationId && x.Kind == "CredentialRevoke" &&
                x.ApplicationId == plan.ApplicationId && x.RequestedBy == plan.ActorId && x.Reason == plan.Reason, ct) ||
            !await db.Set<SecurityOutboxMessage>().AnyAsync(x => x.Id == fact.OperationId && x.EnvironmentId == fact.EnvironmentId &&
                x.OperationId == fact.OperationId && x.SecurityGeneration == fact.SecurityGeneration && x.Epoch == fact.Epoch && x.CreatedAt == fact.OccurredAt, ct) ||
            !await db.Set<ApplicationCredential>().AnyAsync(x => x.Id == plan.CredentialId && x.Revision > plan.CredentialRevision, ct) ||
            !await db.Set<ApplicationRecord>().AnyAsync(x => x.Id == plan.ApplicationId && x.Revision > plan.ApplicationRevision &&
                x.AuthorizationRevision > plan.ApplicationAuthorizationRevision, ct) ||
            !await db.Set<EnvironmentRecord>().AnyAsync(x => x.Id == fact.EnvironmentId && x.AuthorizationRevision > plan.EnvironmentAuthorizationRevision, ct)) return false;
        var audit = await db.Set<AuditLog>().Where(x => x.Action == "credential.revoke" && x.UserId == plan.ActorId &&
            x.EnvironmentId == fact.EnvironmentId && x.OrganizationId == fact.OrganizationId && x.ProjectId == fact.ProjectId &&
            x.ResourceType == "credential" && x.ResourceId == plan.CredentialId.ToString() && x.CreatedAt == fact.OccurredAt && x.AfterJson != null)
            .Select(x => x.AfterJson!).ToArrayAsync(ct);
        if (!audit.Any(json => JsonSerializer.Deserialize<RevocationAuditFact>(json) == new RevocationAuditFact(fact.OperationId, fact.Epoch, fact.EventHash))) return false;
        var targets = await db.Set<SecurityOperationTarget>().AsNoTracking().Where(x => x.OperationId == fact.OperationId).ToArrayAsync(ct);
        return plan.Coverage.Nodes.Select((n, index) => targets.Any(t => t.NodeId == n.NodeId && t.InstanceGuid == n.InstanceId &&
            t.EnvironmentId == fact.EnvironmentId && t.ChallengeNonce == plan.Challenges[index] && t.CreatedAt == fact.OccurredAt &&
            t.ProtocolSupportedAtFreeze && t.LastLeaseUntil >= n.LastLeaseUntil)).All(v => v);
    }

    private static ApiException CoverageUnavailable() => new(409, "credential_revocation_coverage_unknown", "全部入口的治理安全状态尚未核验。");
}
