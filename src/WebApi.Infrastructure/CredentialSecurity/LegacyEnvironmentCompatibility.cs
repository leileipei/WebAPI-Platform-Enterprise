using Microsoft.EntityFrameworkCore;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;

namespace WebApi.Infrastructure.CredentialSecurity;

internal static class LegacyEnvironmentCompatibility
{
    internal static async Task<bool> IsVerifiedAsync(WebApiDbContext db, Guid environmentId, CancellationToken ct)
    {
        var origin = await (from initialization in db.Set<EnvironmentSecurityInitialization>().AsNoTracking()
            join environment in db.Set<EnvironmentRecord>() on initialization.EnvironmentId equals environment.Id
            join project in db.Set<Project>() on environment.ProjectId equals project.Id
            where environment.Id == environmentId && initialization.ProjectId == project.Id &&
                initialization.OrganizationId == project.OrganizationId
            select initialization).SingleOrDefaultAsync(ct);
        if (origin?.State is not ("FreshNonStrict" or "VerifiedLegacyNetwork")) return false;

        var state = await db.Set<EnvironmentSecurityState>().AsNoTracking()
            .SingleOrDefaultAsync(row => row.EnvironmentId == environmentId, ct);
        if (state is not null && (state.ProjectId != origin.ProjectId || state.OrganizationId != origin.OrganizationId ||
            state.SecurityGeneration == Guid.Empty || state.Epoch != 0 || state.HeadHash != SecurityEventHash.Genesis ||
            state.RequireEnvironmentCredentials || state.InitialRequireEnvironmentCredentials == true || state.MinimumSnapshotSchema != "2.0"))
            return false;

        if (await db.Set<SecurityEvent>().Where(row => row.EnvironmentId == environmentId).Select(row => row.EnvironmentId)
            .Concat(db.Set<CredentialRevocation>().Where(row => row.EnvironmentId == environmentId).Select(row => row.EnvironmentId))
            .AnyAsync(ct)) return false;

        if (origin.State == "FreshNonStrict") return origin.FreshProofId is Guid proofId && proofId != Guid.Empty;
        if (state is null || db.Database.CurrentTransaction is null) return false;

        var receipt = await db.Set<EnvironmentSecuritySourceReceipt>().AsNoTracking()
            .SingleOrDefaultAsync(row => row.EnvironmentId == environmentId && row.ProjectId == origin.ProjectId &&
                row.OrganizationId == origin.OrganizationId, ct);
        if (receipt is null) return false;

        var database = await MigrationReceiptIdentityCapture.CaptureAsync(db, ct);
        return database == new MigrationDatabaseIdentity(receipt.SourceClusterId, receipt.SourceDatabase, receipt.SourceDatabaseOid);
    }
}
