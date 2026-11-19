using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Governance;
using WebApi.Infrastructure.CredentialSecurity;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;

namespace WebApi.Integration.Tests;

public sealed class EnvironmentInheritanceTransactionTests
{
    [Fact]
    public async Task FailedInheritanceRollsBackTheHttpEnvironmentCreationAndAudit()
    {
        await using var scenario = new DeploymentScenario();
        await scenario.InitializeAsync();
        var api = scenario.Api;
        var original = await CommitRevocationAsync(scenario);
        await using (var fault = api.Context())
        {
            await fault.Database.ExecuteSqlRawAsync("""
                CREATE SEQUENCE public.test_environment_inheritance_faults;
                CREATE FUNCTION public.test_reject_environment_inheritance() RETURNS trigger
                    LANGUAGE plpgsql SET search_path = pg_catalog AS $$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM public.environments
                                   WHERE id=NEW.environment_id AND code='FAIL-PROJECTION') THEN
                        RETURN NEW;
                    END IF;
                    -- Fail only after the transactional create and its required projection become observable.
                    IF NOT EXISTS (SELECT 1 FROM public.environment_security_initializations
                                   WHERE environment_id=NEW.environment_id AND state='FreshNonStrict')
                        OR NOT EXISTS (SELECT 1 FROM public.environment_security_states
                                       WHERE environment_id=NEW.environment_id)
                        OR NOT EXISTS (SELECT 1 FROM public.audit_logs WHERE action='environment.create'
                                       AND organization_id=NEW.organization_id AND project_id=NEW.project_id)
                        OR NOT EXISTS (SELECT 1 FROM public.security_events WHERE environment_id=NEW.environment_id
                                       AND operation_id=NEW.operation_id AND event_type='CredentialRevocationInherited') THEN
                        RAISE EXCEPTION 'fixture_expected_create_and_projection_before_failure';
                    END IF;
                    PERFORM pg_catalog.nextval('public.test_environment_inheritance_faults');
                    RAISE EXCEPTION 'fixture_inheritance_projection_failure' USING ERRCODE='23514';
                END;
                $$;
                CREATE TRIGGER test_reject_environment_inheritance AFTER INSERT ON public.credential_revocations
                    FOR EACH ROW EXECUTE FUNCTION public.test_reject_environment_inheritance();
                """);
        }

        using var response = await api.WriteAsync(HttpMethod.Post, $"/api/v1/projects/{api.Project.Id}/environments",
            new CreateEnvironmentRequest("FAIL-PROJECTION", "Rollback fixture"));
        await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "invalid_reference");

        await using var check = api.Context();
        // Sequence increments survive rollback and confirm this fixture caused the failure.
        Assert.True(await check.Database.SqlQueryRaw<bool>("""
            SELECT is_called AS "Value" FROM public.test_environment_inheritance_faults
            """).SingleAsync());
        Assert.Equal(api.Environment.Id, await check.Set<EnvironmentRecord>().Select(row => row.Id).SingleAsync());
        Assert.Equal(api.Environment.Id, await check.Set<EnvironmentSecurityInitialization>().Select(row => row.EnvironmentId).SingleAsync());
        Assert.Equal(api.Environment.Id, await check.Set<EnvironmentSecurityState>().Select(row => row.EnvironmentId).SingleAsync());
        Assert.Equal(original.OperationId, await check.Set<SecurityOperation>().Select(row => row.Id).SingleAsync());
        Assert.False(await check.Set<SecurityOutboxMessage>().AnyAsync());
        Assert.False(await check.Set<AuditLog>().AnyAsync(row => row.Action == "environment.create" || row.Action == "credential.revoke.inherit"));
        var ledger = await new SecurityLedgerReader(check).ReadCompleteAsync(api.Environment.Id);
        Assert.Equal(original.Head, Assert.Single(ledger.Events).EventHash);
        Assert.Equal(original.OperationId, Assert.Single(ledger.Revocations).OperationId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ParentReactivationCannotReopenAnAffectedChildBeforeItsArchiveTransition(bool project)
    {
        await using var scenario = new DeploymentScenario();
        await scenario.InitializeAsync();
        var api = scenario.Api;
        var published = await scenario.PublishAsync();
        var desired = await scenario.DesiredAsync(published);
        var original = await CommitRevocationAsync(scenario);
        var id = project ? api.Project.Id : api.Organization.Id;
        var path = $"/api/v1/{(project ? "projects" : "organizations")}/{id}";
        var action = project ? "project.update" : "organization.update";
        var enabled = new SaveResourceRequest(project ? api.Project.Code : api.Organization.Code,
            project ? api.Project.Name : api.Organization.Name);
        using var disabled = await api.WriteAsync(HttpMethod.Put, path, enabled with { Status = "Disabled" },
            RevisionTag.Format(project ? api.Project.Revision : api.Organization.Revision));
        disabled.EnsureSuccessStatusCode();
        var disabledTag = disabled.Headers.ETag!.ToString();

        using var rejected = await api.WriteAsync(HttpMethod.Put, path, enabled, disabledTag);
        await AssertProblemAsync(rejected, HttpStatusCode.Conflict, "credential_revocation_archive_unverified");

        EnvironmentRecord child;
        await using (var check = api.Context())
        {
            var parent = project
                ? await check.Set<Project>().Where(row => row.Id == id).Select(row => new { row.Status, row.Revision }).SingleAsync()
                : await check.Set<Organization>().Where(row => row.Id == id).Select(row => new { row.Status, row.Revision }).SingleAsync();
            Assert.Equal("Disabled", parent.Status);
            Assert.Equal(disabledTag, RevisionTag.Format(parent.Revision));
            Assert.Equal(1, await check.Set<AuditLog>().CountAsync(row => row.Action == action && row.ResourceId == id.ToString()));
            child = await check.Set<EnvironmentRecord>().AsNoTracking().SingleAsync(row => row.Id == api.Environment.Id);
            Assert.Equal("Active", child.Status);
            Assert.Equal(desired.Envelope.ConfigVersion, child.DesiredConfigVersion);
            Assert.Equal(desired.Envelope.DeploymentSequence, child.DeploymentSequence);
        }

        var childPath = $"/api/v1/environments/{child.Id}";
        var childDisabled = new UpdateEnvironmentRequest(child.Code, child.Name, "Disabled", child.IsProduction, child.SortOrder, child.ReleasePolicyId);
        using var stopChild = await api.WriteAsync(HttpMethod.Put, childPath, childDisabled, RevisionTag.Format(child.Revision));
        stopChild.EnsureSuccessStatusCode();
        using var startParent = await api.WriteAsync(HttpMethod.Put, path, enabled, disabledTag);
        startParent.EnsureSuccessStatusCode();
        using var startChild = await api.WriteAsync(HttpMethod.Put, childPath, childDisabled with { Status = "Active" }, stopChild.Headers.ETag!.ToString());
        await AssertProblemAsync(startChild, HttpStatusCode.Conflict, "credential_revocation_archive_unverified");

        await using var final = api.Context();
        var stopped = await final.Set<EnvironmentRecord>().AsNoTracking().SingleAsync(row => row.Id == child.Id);
        Assert.Equal("Disabled", stopped.Status);
        Assert.Equal(stopChild.Headers.ETag!.ToString(), RevisionTag.Format(stopped.Revision));
        Assert.Equal(desired.Envelope.ConfigVersion, stopped.DesiredConfigVersion);
        Assert.Equal(desired.Envelope.DeploymentSequence, stopped.DeploymentSequence);
        Assert.Equal(desired.Payload, (await scenario.DesiredAsync(published)).Payload);
        Assert.False(await final.Set<AuditLog>().AnyAsync(row => row.Action == "environment.archive.reactivate"));
        var ledger = await new SecurityLedgerReader(final).ReadCompleteAsync(child.Id);
        Assert.Equal(original.Head, Assert.Single(ledger.Events).EventHash);
        Assert.Equal(original.OperationId, Assert.Single(ledger.Revocations).OperationId);
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(code, problem.GetProperty("code").GetString());
    }

    private static async Task<(Guid OperationId, string Head)> CommitRevocationAsync(DeploymentScenario scenario)
    {
        var api = scenario.Api;
        var organizationId = api.Organization.Id;
        var projectId = api.Project.Id;
        var environmentId = api.Environment.Id;
        await using var db = api.Context();
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SELECT pg_catalog.pg_advisory_xact_lock(8901202)");
        var at = await db.Database.SqlQueryRaw<DateTimeOffset>("SELECT date_trunc('second', clock_timestamp()) AS \"Value\"").SingleAsync();
        var credential = await db.Set<ApplicationCredential>().SingleAsync(row => row.ApplicationId == api.Application.Id);
        Assert.Equal("LegacyGlobal", credential.BindingMode);
        credential.Status = "Revoked";
        credential.RevokedAt = at;
        credential.Revision++;
        if (!await db.Set<EnvironmentSecurityInitialization>().AnyAsync(row => row.EnvironmentId == environmentId))
        {
            db.Add(new EnvironmentSecurityInitialization { EnvironmentId = environmentId, ProjectId = projectId,
                OrganizationId = organizationId, State = "FreshNonStrict", FreshProofId = Guid.NewGuid() });
            await db.SaveChangesAsync();
        }
        var state = new EnvironmentSecurityState { EnvironmentId = environmentId, ProjectId = projectId,
            OrganizationId = organizationId, UpdatedAt = at };
        db.Add(state);
        await db.SaveChangesAsync();

        var operation = new SecurityOperation { OrganizationId = organizationId, ApplicationId = credential.ApplicationId,
            CredentialId = credential.Id, RequestedBy = api.User.Id, Reason = "Isolated fixture", CommittedAt = at };
        var item = new SecurityEvent { EnvironmentId = environmentId, OrganizationId = organizationId, ProjectId = projectId,
            SecurityGeneration = state.SecurityGeneration, Epoch = 1, OperationId = operation.Id, CredentialId = credential.Id,
            PreviousHash = state.HeadHash, OccurredAt = at };
        item.EventHash = SecurityEventHash.Compute(item);
        db.AddRange(operation, item, new CredentialRevocation { EnvironmentId = environmentId, OrganizationId = organizationId,
            ProjectId = projectId, CredentialId = credential.Id, ApplicationId = credential.ApplicationId,
            SecurityGeneration = state.SecurityGeneration, Epoch = item.Epoch, OperationId = operation.Id, RevokedAt = at });
        state.Epoch = item.Epoch;
        state.HeadHash = item.EventHash;
        state.Revision++;
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return (operation.Id, item.EventHash);
    }
}
