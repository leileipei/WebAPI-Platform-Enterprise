using Microsoft.EntityFrameworkCore;
using WebApi.Infrastructure.Persistence;

namespace WebApi.Infrastructure.Observability;

internal static class ObservationApplicationQuery
{
    private sealed record ApplicationRow(Guid Id, string Name, Guid? EnvironmentId);

    internal static async Task<IReadOnlyDictionary<Guid, ObservationApplication>> ReadAsync(
        WebApiDbContext db, Guid organizationId, Guid projectId, Guid[] environmentIds, CancellationToken ct)
    {
        // The gateway records verified identity before checking the requested API/method, including on a 403.
        // Only project ownership or a scoped grant/runtime identity establishes where that app can be observed.
        var rows = await db.Database.SqlQuery<ApplicationRow>($"""
            WITH shared AS (
                SELECT id, name FROM applications WHERE organization_id = {organizationId} AND project_id IS NULL
            ), environments_in_scope AS (
                SELECT id FROM environments WHERE project_id = {projectId} AND id = ANY({environmentIds})
            )
            SELECT id AS "Id", name AS "Name", NULL::uuid AS "EnvironmentId"
            FROM applications WHERE organization_id = {organizationId} AND project_id = {projectId}
            UNION ALL
            SELECT a.id, a.name, e.id
            FROM shared a
            JOIN application_api_permissions g ON g.application_id = a.id
            JOIN environments_in_scope e ON e.id = g.environment_id
            WHERE g.organization_id = {organizationId} AND g.project_id = {projectId}
              AND g.valid_from <= now() AND (g.expires_at IS NULL OR g.expires_at > now())
            UNION
            SELECT a.id, a.name, e.id
            FROM environments_in_scope e
            JOIN gateway_config_versions v ON v.environment_id = e.id
            JOIN gateway_config_snapshots s ON s.config_version_id = v.id AND s.payload->>'environmentId' = e.id::text
            CROSS JOIN LATERAL jsonb_array_elements(CASE WHEN jsonb_typeof(s.payload->'applications') = 'array'
                THEN s.payload->'applications' ELSE '[]'::jsonb END) entry
            JOIN shared a ON a.id::text = entry->>'id'
            WHERE (entry->>'environmentId' IS NULL OR entry->>'environmentId' = e.id::text)
            """).ToListAsync(ct);

        return rows.GroupBy(row => row.Id).ToDictionary(group => group.Key, group =>
        {
            var first = group.First();
            return new ObservationApplication(first.Name, first.EnvironmentId is null ? null :
                group.Select(row => row.EnvironmentId!.Value).ToHashSet());
        });
    }
}
