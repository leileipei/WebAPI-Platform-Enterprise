using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Catalog;
using WebApi.Contracts.Common;
using WebApi.Contracts.OpenApi;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Commands;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Security;
namespace WebApi.Infrastructure.Catalog;
public sealed class ImportSourcePolicyService(WebApiDbContext db, ScopeResolver scopes, AuthorizationService auth, AuditedCommandExecutor commands,
    IdempotentCommandExecutor idempotency, CommandRequestContext requestContext, ImportSourceSettings settings, TimeProvider clock)
{
    public async Task<ImportSourcePolicyDto> GetAsync(Guid projectId, ActorContext actor, CancellationToken ct)
    {
        var scope = await scopes.ProjectAsync(projectId, ct);
        if (!await auth.CanAsync(actor, "project.read", new("project", projectId, scope), ct)) throw ScopeResolver.Missing();
        return await CurrentAsync(projectId,ct);
    }
    internal async Task<ImportSourcePolicyDto> CurrentAsync(Guid projectId,CancellationToken ct) => View(projectId,await db.Set<ProjectImportSourcePolicy>().AsNoTracking().SingleOrDefaultAsync(x=>x.ProjectId==projectId,ct));
    public async Task<CommandResult<ImportSourcePolicyDto>> SaveAsync(Guid projectId, SaveImportSourcePolicyRequest request, string? tag, ActorContext actor, CancellationToken ct)
    {
        var scope = await scopes.ProjectAsync(projectId, ct);
        return await commands.ExecuteAsync(actor, scope, "project.import-source-policy.save", async (_, token) => {
            await auth.RequireAsync(actor, "project.write", new("project", projectId, scope), token);
            Validate(request);
            var normalized = request with { Allowances = request.Allowances.Select(new ImportAddressPolicy(settings).NormalizeAllowance).ToArray() };
            return await idempotency.ExecuteAsync(new(actor.UserId, scope, "project.import-source-policy.save", requestContext.IdempotencyKey), CanonicalJson.Serialize(new { projectId, request = normalized, tag }), async commandToken => {
                var row = await db.Set<ProjectImportSourcePolicy>().SingleOrDefaultAsync(x => x.ProjectId == projectId, commandToken);
                RevisionTag.Require(tag, row?.Revision ?? 0);
                if (row is null) { row = new() { ProjectId = projectId }; db.Add(row); } else row.Revision++;
                row.RulesJson = JsonSerializer.Serialize(normalized, CanonicalJson.Options); row.UpdatedBy = actor.UserId; row.UpdatedAt = clock.GetUtcNow();
                return new CommandResult<ImportSourcePolicyDto>(View(projectId, row), RevisionTag.Format(row.Revision));
            }, token);
        }, ct);
    }
    private void Validate(SaveImportSourcePolicyRequest request)
    {
        if (request.Allowances.Count > 32) throw ImportAddressPolicy.Rejected();
        var policy = new ImportAddressPolicy(settings);
        foreach (var allowance in request.Allowances) policy.ValidateAllowance(allowance);
        RequireLimits(request.Limits, settings.Limits);
        RequireLimits(settings.Limits, new());
    }
    internal static void RequireLimits(ContractLimits value, ContractLimits cap)
    {
        foreach (var property in typeof(ContractLimits).GetProperties()) {
            var requested = (int)property.GetValue(value)!; var maximum = (int)property.GetValue(cap)!;
            if (requested < 1 || requested > maximum) throw new ApiException(422, "invalid_contract_limits", "项目契约预算必须为正数，且不能超过部署上限。");
        }
    }
    private ImportSourcePolicyDto View(Guid projectId, ProjectImportSourcePolicy? row)
    {
        if (row is null) return new(projectId, 0, [], settings.Limits);
        var stored = JsonSerializer.Deserialize<SaveImportSourcePolicyRequest>(row.RulesJson, CanonicalJson.Options) ?? throw new InvalidOperationException("Invalid stored import source policy.");
        // Deployment tightening always applies to existing project rules.
        var limits = new ContractLimits(Math.Min(stored.Limits.MaxDocumentBytes, settings.Limits.MaxDocumentBytes), Math.Min(stored.Limits.MaxBundleBytes, settings.Limits.MaxBundleBytes), Math.Min(stored.Limits.MaxResources, settings.Limits.MaxResources), Math.Min(stored.Limits.MaxDepth, settings.Limits.MaxDepth), Math.Min(stored.Limits.MaxNodes, settings.Limits.MaxNodes), Math.Min(stored.Limits.MaxOperations, settings.Limits.MaxOperations), Math.Min(stored.Limits.MaxExampleBytes, settings.Limits.MaxExampleBytes), Math.Min(stored.Limits.MaxDiagnostics, settings.Limits.MaxDiagnostics), Math.Min(stored.Limits.MaxComparisonSideBytes, settings.Limits.MaxComparisonSideBytes), Math.Min(stored.Limits.MaxComparisonPairBytes, settings.Limits.MaxComparisonPairBytes), Math.Min(stored.Limits.MaxFindings, settings.Limits.MaxFindings));
        return new(projectId, row.Revision, stored.Allowances, limits);
    }
}
