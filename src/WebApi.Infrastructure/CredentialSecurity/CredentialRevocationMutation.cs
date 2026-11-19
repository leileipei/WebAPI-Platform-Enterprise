using System.Text.Json;
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

internal sealed class CredentialRevocationMutation(ActorContext actor, ICredentialRevocationCoverage coverage,
    Func<WebApiDbContext, CancellationToken, Task>? requireSession = null) : IPostgresGovernanceMutation
{
    internal sealed record Resource(ApplicationCredential Credential, ApplicationRecord Application, EnvironmentRecord[] Environments);
    private sealed record RevocationAuditFact(Guid OperationId, long Epoch, string EventHash);

    internal Task<Resource> RequireScopeAsync(WebApiDbContext db, Guid id, Guid? expectedEnvironment, CancellationToken ct) =>
        ReadResourceAsync(db, id, expectedEnvironment, actor, null, ct);

    private async Task<Resource> ReadResourceAsync(WebApiDbContext db, Guid id, Guid? expectedEnvironment,
        ActorContext requester, CredentialRevocationIntent? recorded, CancellationToken ct)
    {
        var credential = await db.Set<ApplicationCredential>().SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new ApiException(404, "credential_not_found", "找不到凭证。");
        var app = await db.Set<ApplicationRecord>().SingleAsync(x => x.Id == credential.ApplicationId, ct);
        if (credential.BindingMode == "LegacyGlobal" && recorded is null && coverage is ICredentialRevocationHistory history)
        {
            var affected = await LegacyGlobalRevocationScope.ReadAsync(db, credential, app, expectedEnvironment, requester, history, ct);
            var environments = await db.Set<EnvironmentRecord>().Where(x => affected.Contains(x.Id)).ToArrayAsync(ct);
            if (environments.Length != affected.Count) throw LegacyGlobalRevocationScope.Unknown();
            return new(credential, app, environments.OrderBy(x => x.Id).ToArray());
        }

        var auth = new AuthorizationService(db);
        var credentialProject = credential.ProjectId ?? app.ProjectId;
        await auth.RequireAsync(requester, "credential.revoke", new("credential", id,
            new(credential.OrganizationId, credentialProject, credential.EnvironmentId)), ct);
        if (credential.BindingMode == "LegacyGlobal" && recorded is { IsGlobal: true } &&
            expectedEnvironment is null && credential.EnvironmentId is null && credential.OrganizationId == app.OrganizationId &&
            (credential.ProjectId is null || app.ProjectId is null || credential.ProjectId == app.ProjectId) &&
            recorded.ApplicationId == app.Id)
        {
            // Replay authorizes the frozen scope without rescanning history that
            // may have grown since this exact operation was witnessed.
            var environments = new List<EnvironmentRecord>();
            foreach (var frozen in recorded.Environments())
            {
                var scope = frozen.Coverage;
                if (scope.OrganizationId != credential.OrganizationId || credentialProject is Guid project && project != scope.ProjectId)
                    throw LegacyGlobalRevocationScope.Unknown();
                await auth.RequireAsync(requester, "credential.revoke", new("credential", id,
                    new(scope.OrganizationId, scope.ProjectId, scope.EnvironmentId)), ct);
                environments.Add(await db.Set<EnvironmentRecord>().SingleAsync(x => x.Id == scope.EnvironmentId && x.ProjectId == scope.ProjectId, ct));
            }
            return new(credential, app, [.. environments]);
        }

        if (recorded is { IsGlobal: true } || credential.BindingMode != "EnvironmentBound" || credential.EnvironmentId is not Guid environmentId ||
            credential.ProjectId is not Guid projectId || credential.OrganizationId != app.OrganizationId ||
            app.ProjectId is not null && app.ProjectId != projectId)
            throw LegacyGlobalRevocationScope.Unknown();
        if (expectedEnvironment != environmentId)
            throw new ApiException(409, "credential_revocation_environment_mismatch", "所选环境与凭证的绑定环境不一致。");
        var environment = await db.Set<EnvironmentRecord>().SingleAsync(x => x.Id == environmentId && x.ProjectId == projectId, ct);
        return new(credential, app, [environment]);
    }

    internal async Task<CredentialRevocationIntent> PlanAsync(WebApiDbContext db, Resource resource, string reason, string requestHash, CancellationToken ct)
    {
        var now = await SecurityDatabaseClock.ObserveAsync(db, ct);
        var scopes = await ReadVerifiedCoverageAsync(db, resource, now, BoundedWitnessHttpClient.RequestTimeout, ct);
        var environments = resource.Environments.Zip(scopes, (env, scope) => new CredentialRevocationEnvironmentIntent(
            env.AuthorizationRevision, scope, scope.Nodes.Select(_ => Guid.NewGuid()).ToArray())).ToArray();
        var first = environments[0];
        return new(resource.Credential.Id, resource.Application.Id, actor.UserId, resource.Credential.Revision,
            resource.Application.Revision, resource.Application.AuthorizationRevision, first.AuthorizationRevision,
            reason, requestHash, now, first.Coverage, first.Challenges,
            resource.Credential.BindingMode == "LegacyGlobal" ? environments[1..] : null);
    }

    public async Task AuthorizeAsync(WebApiDbContext db, GovernanceWriteIntent intent, CancellationToken ct)
    {
        if (requireSession is not null) await requireSession(db, ct);
        var plan = CredentialRevocationIntent.Read(intent);
        if (plan.ActorId != actor.UserId) throw new ApiException(403, "permission_denied", "缺少操作权限。");
        await ReadResourceAsync(db, plan.CredentialId, plan.ExpectedEnvironmentId, actor, plan, ct);
    }

    public async Task AuthorizeRecoveryAsync(WebApiDbContext db, CancellationToken ct)
    {
        if (requireSession is not null) await requireSession(db, ct);
        await new AuthorizationService(db).RequireAsync(actor, "credential.governance.recover",
            new("credential_governance", Guid.Empty, new(Guid.Empty)), ct);
    }

    public async Task ValidateNewAsync(WebApiDbContext db, GovernanceWriteIntent intent, CancellationToken ct) =>
        await ValidatePlanAsync(db, CredentialRevocationIntent.Read(intent), BoundedWitnessHttpClient.RequestTimeout, ct);

    public async Task ApplyAsync(WebApiDbContext db, GovernanceWriteIntent intent, CancellationToken ct)
    {
        var plan = CredentialRevocationIntent.Read(intent);
        var resource = await ValidatePlanAsync(db, plan, TimeSpan.Zero, ct);
        resource.Credential.Status = "Revoked";
        resource.Credential.RevokedAt ??= plan.EventTime;
        resource.Credential.Revision = checked(resource.Credential.Revision + 1);
        resource.Application.Revision = checked(resource.Application.Revision + 1);
        resource.Application.AuthorizationRevision = checked(resource.Application.AuthorizationRevision + 1);
        resource.Application.UpdatedAt = plan.EventTime;
        db.Add(new SecurityOperation { Id = intent.OperationId, OrganizationId = resource.Credential.OrganizationId, ApplicationId = plan.ApplicationId,
            CredentialId = plan.CredentialId, RequestedBy = plan.ActorId, Reason = plan.Reason, CommittedAt = plan.EventTime });
        foreach (var (environment, frozen) in resource.Environments.Zip(plan.Environments()))
            await AppendEnvironmentAsync(db, plan, intent.OperationId, environment, frozen, ct);
    }

    private static async Task AppendEnvironmentAsync(WebApiDbContext db, CredentialRevocationIntent plan, Guid operationId,
        EnvironmentRecord environment, CredentialRevocationEnvironmentIntent frozen, CancellationToken ct)
    {
        var fact = plan.Event(operationId, frozen.Coverage);
        var authority = await db.Set<EnvironmentSecurityState>().SingleAsync(x => x.EnvironmentId == fact.EnvironmentId, ct);
        authority.Epoch = fact.Epoch;
        authority.HeadHash = fact.EventHash;
        authority.Revision = checked(authority.Revision + 1);
        authority.UpdatedAt = plan.EventTime;
        environment.AuthorizationRevision = checked(environment.AuthorizationRevision + 1);
        db.AddRange(fact,
            new CredentialRevocation { EnvironmentId = fact.EnvironmentId, OrganizationId = fact.OrganizationId, ProjectId = fact.ProjectId,
                CredentialId = plan.CredentialId, ApplicationId = plan.ApplicationId, SecurityGeneration = fact.SecurityGeneration,
                Epoch = fact.Epoch, OperationId = operationId, RevokedAt = plan.EventTime },
            new SecurityOutboxMessage { Id = plan.OutboxId(operationId, fact.EnvironmentId), EnvironmentId = fact.EnvironmentId,
                SecurityGeneration = fact.SecurityGeneration, Epoch = fact.Epoch, OperationId = operationId, CreatedAt = plan.EventTime },
            new AuditLog { OrganizationId = fact.OrganizationId, ProjectId = fact.ProjectId, EnvironmentId = fact.EnvironmentId,
                UserId = plan.ActorId, Action = "credential.revoke", ResourceType = "credential", ResourceId = plan.CredentialId.ToString(),
                CreatedAt = plan.EventTime, AfterJson = JsonSerializer.Serialize(new RevocationAuditFact(operationId, fact.Epoch, fact.EventHash)) });
        foreach (var (node, challenge) in frozen.Coverage.Nodes.Zip(frozen.Challenges))
            db.Add(new SecurityOperationTarget { OperationId = operationId, EnvironmentId = fact.EnvironmentId, NodeId = node.NodeId,
                InstanceGuid = node.InstanceId, ChallengeNonce = challenge, CreatedAt = plan.EventTime,
                LastLeaseUntil = node.LastLeaseUntil, ProtocolSupportedAtFreeze = true });
    }

    private async Task<Resource> ValidatePlanAsync(WebApiDbContext db, CredentialRevocationIntent plan,
        TimeSpan minimumRemaining, CancellationToken ct)
    {
        var resource = await ReadResourceAsync(db, plan.CredentialId, plan.ExpectedEnvironmentId, new(plan.ActorId, actor.TraceId), null, ct);
        var frozen = plan.Environments();
        if (resource.Credential.Revision != plan.CredentialRevision || resource.Application.Id != plan.ApplicationId ||
            resource.Application.Revision != plan.ApplicationRevision || resource.Application.AuthorizationRevision != plan.ApplicationAuthorizationRevision ||
            plan.IsGlobal != (resource.Credential.BindingMode == "LegacyGlobal") ||
            !resource.Environments.Select(x => (x.Id, x.AuthorizationRevision)).SequenceEqual(
                frozen.Select(x => (x.Coverage.EnvironmentId, x.AuthorizationRevision))))
            throw new ApiException(409, "credential_revocation_changed", "撤销准备后数据已变更。");
        var now = await SecurityDatabaseClock.ObserveAsync(db, ct);
        var current = await ReadVerifiedCoverageAsync(db, resource, now, minimumRemaining, ct);
        if (plan.EventTime > now || !CanonicalJson.Serialize(frozen.Select(x => x.Coverage).ToArray()).AsSpan().SequenceEqual(CanonicalJson.Serialize(current)))
            throw CoverageUnavailable();
        return resource;
    }

    private async Task<CredentialRevocationCoverageScope[]> ReadVerifiedCoverageAsync(WebApiDbContext db, Resource resource,
        DateTimeOffset now, TimeSpan minimumRemaining, CancellationToken ct)
    {
        var scopes = new List<CredentialRevocationCoverageScope>(resource.Environments.Length);
        if (await db.Set<SecurityOperation>().AnyAsync(o => o.Kind == "CredentialRevoke" && o.CredentialId == resource.Credential.Id, ct))
            throw new ApiException(409, "credential_revocation_changed", "已有安全撤销或账本范围不一致。");
        foreach (var environment in resource.Environments)
        {
            var scope = await ReadCoverageAsync(db, resource.Credential, environment, now, ct);
            if (!coverage.IsVerified(scope)) throw CoverageUnavailable();
            scopes.Add(scope);
        }
        var finalSample = await SecurityDatabaseClock.ObserveAsync(db, ct);
        // Once prepared, the independent intent cannot be dropped on timeout.
        // Check before prepare that the evidence outlives its transport budget.
        if (finalSample < now || scopes.Any(scope => scope.Nodes.Any(node => node.FreshUntil - finalSample <= minimumRemaining)))
            throw CoverageUnavailable();
        return [.. scopes];
    }

    private static async Task<CredentialRevocationCoverageScope> ReadCoverageAsync(WebApiDbContext db, ApplicationCredential credential,
        EnvironmentRecord env, DateTimeOffset now, CancellationToken ct)
    {
        SecurityLedgerMaterial ledger;
        try { ledger = await new SecurityLedgerReader(db).ReadCompleteLockedAsync(env.Id, ct); }
        catch (InvalidOperationException) { throw CoverageUnavailable(); }
        if (ledger.OrganizationId != credential.OrganizationId || ledger.ProjectId != env.ProjectId ||
            ledger.Revocations.Any(r => r.CredentialId == credential.Id))
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
            !Guid.TryParse(n.InstanceId, out var instance) || instance == Guid.Empty ||
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
        var environments = plan.Environments();
        var operationId = intent.OperationId;
        if (await db.Set<SecurityEvent>().CountAsync(x => x.OperationId == operationId, ct) != environments.Length ||
            !await db.Set<SecurityOperation>().AnyAsync(x => x.Id == operationId && x.Kind == "CredentialRevoke" &&
                x.OrganizationId == plan.Coverage.OrganizationId && x.CredentialId == plan.CredentialId && x.CommittedAt == plan.EventTime &&
                x.ApplicationId == plan.ApplicationId && x.RequestedBy == plan.ActorId && x.Reason == plan.Reason, ct) ||
            !await db.Set<ApplicationCredential>().AnyAsync(x => x.Id == plan.CredentialId && x.Revision > plan.CredentialRevision &&
                x.BindingMode == (plan.IsGlobal ? "LegacyGlobal" : "EnvironmentBound"), ct) ||
            !await db.Set<ApplicationRecord>().AnyAsync(x => x.Id == plan.ApplicationId && x.Revision > plan.ApplicationRevision &&
                x.AuthorizationRevision > plan.ApplicationAuthorizationRevision, ct)) return false;

        var targets = await db.Set<SecurityOperationTarget>().AsNoTracking().Where(x => x.OperationId == operationId).ToArrayAsync(ct);
        foreach (var frozen in environments)
        {
            var fact = plan.Event(operationId, frozen.Coverage);
            var ledger = await new SecurityLedgerReader(db).ReadCompleteLockedAsync(fact.EnvironmentId, ct);
            if (ledger.Events.SingleOrDefault(x => x.OperationId == operationId)?.EventHash != fact.EventHash ||
                !await db.Set<SecurityOutboxMessage>().AnyAsync(x => x.Id == plan.OutboxId(operationId, fact.EnvironmentId) &&
                    x.EnvironmentId == fact.EnvironmentId && x.OperationId == operationId && x.SecurityGeneration == fact.SecurityGeneration &&
                    x.Epoch == fact.Epoch && x.CreatedAt == fact.OccurredAt, ct) ||
                !await db.Set<EnvironmentRecord>().AnyAsync(x => x.Id == fact.EnvironmentId && x.AuthorizationRevision > frozen.AuthorizationRevision, ct)) return false;

            var audit = await db.Set<AuditLog>().Where(x => x.Action == "credential.revoke" && x.UserId == plan.ActorId &&
                x.EnvironmentId == fact.EnvironmentId && x.OrganizationId == fact.OrganizationId && x.ProjectId == fact.ProjectId &&
                x.ResourceType == "credential" && x.ResourceId == plan.CredentialId.ToString() && x.CreatedAt == fact.OccurredAt && x.AfterJson != null)
                .Select(x => x.AfterJson!).ToArrayAsync(ct);
            if (!audit.Any(json => JsonSerializer.Deserialize<RevocationAuditFact>(json) == new RevocationAuditFact(operationId, fact.Epoch, fact.EventHash)) ||
                !frozen.Coverage.Nodes.Zip(frozen.Challenges).All(pair => targets.Any(t => t.NodeId == pair.First.NodeId &&
                    t.InstanceGuid == pair.First.InstanceId && t.EnvironmentId == fact.EnvironmentId && t.ChallengeNonce == pair.Second &&
                    t.CreatedAt == fact.OccurredAt && t.ProtocolSupportedAtFreeze && t.LastLeaseUntil >= pair.First.LastLeaseUntil))) return false;
        }
        return true;
    }

    private static ApiException CoverageUnavailable() => new(409, "credential_revocation_coverage_unknown", "全部入口的治理安全状态尚未核验。");
}
