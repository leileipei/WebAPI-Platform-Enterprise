using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Releases;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;

namespace WebApi.Infrastructure.Releases;

internal static class ReleaseVersionPublication
{
    internal static void CaptureItems(WebApiDbContext db, ReleaseRecord release, FrozenReleaseCandidate candidate)
    {
        foreach (var entry in candidate.Versions)
        {
            var version = entry.Version;
            db.Add(new ReleaseItem
            {
                ReleaseId = release.Id, ResourceType = "version", ResourceId = version.Id, ChangeType = version.ChangeType,
                AfterJson = JsonSerializer.Serialize(new { version = version.Version, revision = version.Revision }, CanonicalJson.Options)
            });
        }
    }

    // The caller owns the deployment transaction and lock. Building has not dispatched a snapshot yet.
    internal static async Task RecordOutcomeAsync(WebApiDbContext db, ReleaseRecord release, CancellationToken ct)
    {
        var affected = db.Set<ReleaseItem>().Where(i => i.ReleaseId == release.Id && i.ResourceType == "version").Select(i => i.ResourceId);
        var versions = await (from version in db.Set<ApiVersion>()
                              join api in db.Set<Api>() on version.ApiId equals api.Id
                              join env in db.Set<EnvironmentRecord>() on api.ProjectId equals env.ProjectId
                              where env.Id == release.EnvironmentId && affected.Contains(version.Id)
                              select version).ToArrayAsync(ct);
        if (versions.Length == 0) return;

        var ids = versions.Select(v => v.Id).ToArray();
        var otherFacts = await (from item in db.Set<ReleaseItem>()
                                join other in db.Set<ReleaseRecord>() on item.ReleaseId equals other.Id
                                join env in db.Set<EnvironmentRecord>() on other.EnvironmentId equals env.Id
                                where other.Id != release.Id && other.DeploymentSequence > 0 && other.ToConfigVersion > 0
                                    && db.Set<EnvironmentRecord>().Any(current => current.Id == release.EnvironmentId && current.ProjectId == env.ProjectId)
                                    && item.ResourceType == "version" && ids.Contains(item.ResourceId)
                                    && (other.Status == "Publishing" || other.Status == "Succeeded" || other.Status == "RolledBack")
                                group other by item.ResourceId into g
                                select new { Id = g.Key, Pending = g.Any(r => r.Status == "Publishing"), Published = g.Any(r => r.Status != "Publishing") })
            .ToDictionaryAsync(fact => fact.Id, ct);
        foreach (var version in versions)
        {
            if(version.Status=="Retired")continue;
            otherFacts.TryGetValue(version.Id, out var facts);
            var published = release.Status == "Succeeded" || facts?.Published == true;
            version.Status = facts?.Pending == true ? "Publishing" : published ? "Published" : "Draft";
            if (published) version.SealedAt ??= DateTimeOffset.UtcNow;
        }
    }
}
