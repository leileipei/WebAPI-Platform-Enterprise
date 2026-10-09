using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Releases;
using WebApi.Contracts.Security;
using WebApi.Domain.Releases;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Security;

namespace WebApi.Infrastructure.Releases;

public sealed class ApprovalEligibilityService(WebApiDbContext db, AuthorizationService auth)
{
    public async Task<ApprovalEligibility> EvaluateAsync(ReleaseRecord release, ScopeRef scope, ActorContext actor, CancellationToken ct)
    {
        try { await auth.RequireAsync(actor, "approval.act", new("release", release.Id, scope), ct); }
        catch (ApiException e) when (e.Status is 403 or 409) { return new(false, null, e.Code); }
        if (release.Status != "WaitingApproval") return new(false, null, "invalid_release_state");
        var step = await CurrentStepAsync(release.Id, ct);
        if (release.RequestedBy == actor.UserId) return new(false, step, "self_approval");
        if(await IsPipelineRunCreatorAsync(release,actor.UserId,ct))return new(false,step,"pipeline_run_creator_approval");
        if (await HasUsedSeatAsync(release.Id, actor.UserId, ct)) return new(false, step, "independent_approval_required");
        var canAct = await QueryActionableReleaseIds(actor, [release.EnvironmentId]).AnyAsync(id => id == release.Id, ct);
        return new(canAct, step, canAct ? null : "approval_role_required");
    }

    // Called by the command executor while the governance lock is held. Read permission
    // belongs to the inbox/detail visibility layer, not to the existing action contract.
    public async Task<ApprovalTask> RequireTaskAsync(ReleaseRecord release, ScopeRef scope, ActorContext actor, CancellationToken ct)
    {
        await auth.RequireAsync(actor, "approval.act", new("release", release.Id, scope), ct);
        ReleaseStateMachine.Require(release.Status, "WaitingApproval");
        if (release.RequestedBy == actor.UserId) throw new ApiException(403, "self_approval", "申请人不能审批自己的申请。");
        if(await IsPipelineRunCreatorAsync(release,actor.UserId,ct))throw new ApiException(403,"pipeline_run_creator_approval","运行创建人不能通过委托办理审批自己的生产交付。");
        if (await HasUsedSeatAsync(release.Id, actor.UserId, ct)) throw new ApiException(403, "independent_approval_required", "同一人不能再次占用审批步骤。");
        if (!await QueryActionableReleaseIds(actor, [release.EnvironmentId]).AnyAsync(id => id == release.Id, ct))
            throw new ApiException(403, "approval_role_required", "用户不属于当前审批角色。");
        return await db.Set<ApprovalTask>().Where(t => t.ReleaseId == release.Id && t.Status == "Pending")
            .OrderBy(t => t.StepOrder).ThenBy(t => t.Id).FirstAsync(ct);
    }

    private Task<bool> IsPipelineRunCreatorAsync(ReleaseRecord release,Guid user,CancellationToken ct)=>
        (from p in db.Set<ReleasePromotion>() join stage in db.Set<ReleasePipelineRunStage>() on p.PipelineRunStageId equals (Guid?)stage.Id join run in db.Set<ReleasePipelineRun>() on stage.RunId equals run.Id join environment in db.Set<EnvironmentRecord>() on release.EnvironmentId equals environment.Id where p.Id==release.PromotionId&&p.TargetReleaseId==release.Id&&p.GateOrigin=="PipelineRunStage"&&environment.IsProduction&&run.CreatedBy==user select run.Id).AnyAsync(ct);
    private Task<bool> HasUsedSeatAsync(Guid releaseId, Guid actorId, CancellationToken ct) =>
        db.Set<ApprovalTask>().AnyAsync(t => t.ReleaseId == releaseId && t.Status == "Approved" && t.AssigneeUserId == actorId, ct);

    private Task<int?> CurrentStepAsync(Guid releaseId, CancellationToken ct) =>
        db.Set<ApprovalTask>().Where(t => t.ReleaseId == releaseId && t.Status == "Pending").MinAsync(t => (int?)t.StepOrder, ct);

    // PostgreSQL expands the frozen policy, not the current mutable flow template.
    // This remains composable: the inbox intersects it with readable releases before
    // counting/paging; no actionable IDs are materialized in memory.
    internal IQueryable<Guid> QueryActionableReleaseIds(ActorContext actor, IReadOnlyList<Guid> visibleEnvironmentIds)
    {
        var environments = visibleEnvironmentIds.ToArray();
        string[] deliveryReadCodes = ["release.read", "environment.read", "route.read", "api.read", "api.version.read", "api.schema.read"];
        return db.Database.SqlQuery<Guid>($"""
            SELECT r.id AS "Value"
            FROM release_records r
            JOIN environments e ON e.id = r.environment_id
            JOIN projects project ON project.id = e.project_id
            JOIN organizations organization ON organization.id = project.organization_id
            WHERE r.environment_id = ANY({environments})
              AND r.status = 'WaitingApproval' AND r.requested_by <> {actor.UserId}
              AND e.status = 'Active' AND project.status = 'Active' AND organization.status = 'Active'
              AND EXISTS (SELECT 1 FROM users u WHERE u.id = {actor.UserId} AND u.status = 'Active')
              AND EXISTS (
                SELECT 1 FROM user_project_scopes grant_scope
                WHERE grant_scope.user_id = {actor.UserId}
                  AND grant_scope.organization_id = organization.id
                  AND (grant_scope.project_id IS NULL OR grant_scope.project_id = project.id)
                  AND (grant_scope.environment_id IS NULL OR grant_scope.environment_id = e.id)
                  AND grant_scope.access_mode = 'read_write')
              AND EXISTS (
                SELECT 1 FROM user_roles ur
                JOIN roles role ON role.id = ur.role_id
                JOIN role_permissions rp ON rp.role_id = role.id
                JOIN permissions permission ON permission.id = rp.permission_id
                WHERE ur.user_id = {actor.UserId} AND permission.code = 'approval.act'
                  AND (role.organization_id IS NULL OR role.organization_id = organization.id))
              AND (r.promotion_id IS NULL OR EXISTS (
                SELECT 1 FROM release_promotions promotion
                JOIN release_artifacts artifact ON artifact.id = promotion.artifact_id
                JOIN environments source ON source.id = promotion.source_environment_id
                JOIN projects source_project ON source_project.id = source.project_id
                JOIN organizations source_org ON source_org.id = source_project.organization_id
                WHERE promotion.id = r.promotion_id
                  AND source.status = 'Active' AND source_project.status = 'Active' AND source_org.status = 'Active'
                  AND EXISTS (SELECT 1 FROM user_project_scopes source_grant
                    WHERE source_grant.user_id = {actor.UserId} AND source_grant.organization_id = source_org.id
                      AND (source_grant.project_id IS NULL OR source_grant.project_id = source_project.id)
                      AND (source_grant.environment_id IS NULL OR source_grant.environment_id = source.id))
                  AND (SELECT COUNT(DISTINCT permission.code) FROM user_roles ur
                    JOIN roles role ON role.id = ur.role_id
                    JOIN role_permissions rp ON rp.role_id = role.id
                    JOIN permissions permission ON permission.id = rp.permission_id
                    WHERE ur.user_id = {actor.UserId} AND permission.code = ANY({deliveryReadCodes})
                      AND (role.organization_id IS NULL OR role.organization_id = source_org.id)) = 6
                  AND (NOT jsonb_path_exists(artifact.canonical_content, '$.routes[*].policies[*]') OR EXISTS (
                    SELECT 1 FROM user_roles ur JOIN roles role ON role.id = ur.role_id
                    JOIN role_permissions rp ON rp.role_id = role.id JOIN permissions permission ON permission.id = rp.permission_id
                    WHERE ur.user_id = {actor.UserId} AND permission.code = 'policy.read'
                      AND (role.organization_id IS NULL OR role.organization_id = source_org.id)))))
              AND NOT EXISTS (
                SELECT 1 FROM release_promotions pipeline_promotion
                JOIN release_pipeline_run_stages pipeline_stage ON pipeline_stage.id = pipeline_promotion.pipeline_run_stage_id
                JOIN release_pipeline_runs pipeline_run ON pipeline_run.id = pipeline_stage.run_id
                WHERE pipeline_promotion.id = r.promotion_id AND pipeline_promotion.gate_origin = 'PipelineRunStage'
                  AND e.is_production AND pipeline_run.created_by = {actor.UserId})
              AND NOT EXISTS (
                SELECT 1 FROM approval_tasks used_seat WHERE used_seat.release_id = r.id
                  AND used_seat.status = 'Approved' AND used_seat.assignee_user_id = {actor.UserId})
              AND EXISTS (
                SELECT 1 FROM jsonb_array_elements(COALESCE(r.approval_policy, '[]'::jsonb)) frozen_rule
                WHERE (frozen_rule->>'stepOrder')::int = (
                  SELECT MIN(seat.step_order) FROM approval_tasks seat
                  WHERE seat.release_id = r.id AND seat.status = 'Pending')
                AND EXISTS (
                  SELECT 1 FROM user_roles ur JOIN roles role ON role.id = ur.role_id
                  WHERE ur.user_id = {actor.UserId}
                    AND (role.organization_id IS NULL OR role.organization_id = organization.id)
                    AND (role.code = frozen_rule->>'roleCode'
                      OR (role.code = 'PlatformAdmin' AND role.is_system AND role.organization_id IS NULL))))
            """);
    }
}
