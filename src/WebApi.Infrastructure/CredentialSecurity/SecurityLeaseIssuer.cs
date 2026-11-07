using WebApi.Contracts.Common;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Gateway;
using WebApi.Infrastructure.Persistence;

namespace WebApi.Infrastructure.CredentialSecurity;

// No public issuance path may use database time or the database's own ledger
// as independent evidence. This entry point remains closed until the external
// witness, recovery protocol and gateway admission are integrated and reviewed.
public sealed class SecurityLeaseIssuer
{
    public SecurityLeaseIssuer(WebApiDbContext db, NodeIdentityService identities, SecurityLedgerReader reader, SecuritySigningSettings signing) { }

    public Task<SecurityNodeMaterial> IssueAsync(NodeIdentity identity, Guid instanceId, CancellationToken ct = default) =>
        Task.FromException<SecurityNodeMaterial>(new ApiException(503, "security_evidence_unavailable", "独立安全证明尚不可用。"));
}
