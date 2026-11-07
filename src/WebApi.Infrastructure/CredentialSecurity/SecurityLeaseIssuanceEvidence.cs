using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Security;
using WebApi.Domain.CredentialSecurity;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;

namespace WebApi.Infrastructure.CredentialSecurity;

public static class SecurityLeaseIssuanceEvidence
{
    public static GatewaySecurityLeaseIssuance Create(SecurityNodeLeasePayload payload, Guid organizationId, Guid projectId) => new()
    {
        LeaseId = payload.LeaseId, NodeId = payload.NodeId, InstanceId = payload.InstanceId,
        EnvironmentId = payload.EnvironmentId, OrganizationId = organizationId, ProjectId = projectId,
        SecurityGeneration = payload.SecurityGeneration, Epoch = payload.Epoch, CheckpointHash = payload.CheckpointHash,
        PayloadHash = SecuritySignedEncoding.LeaseHash(payload), IssuedAt = payload.IssuedAt,
        FreshUntil = payload.FreshUntil, SigningKeyId = payload.KeyId
    };

    public static Task<bool> ContainsAsync(WebApiDbContext db, SecurityNodeLeasePayload payload, Guid organizationId, Guid projectId, CancellationToken ct)
    {
        var hash = SecuritySignedEncoding.LeaseHash(payload);
        return db.Set<GatewaySecurityLeaseIssuance>().AsNoTracking().AnyAsync(x => x.LeaseId == payload.LeaseId &&
            x.NodeId == payload.NodeId && x.InstanceId == payload.InstanceId && x.EnvironmentId == payload.EnvironmentId &&
            x.OrganizationId == organizationId && x.ProjectId == projectId && x.SecurityGeneration == payload.SecurityGeneration &&
            x.Epoch == payload.Epoch && x.CheckpointHash == payload.CheckpointHash && x.PayloadHash == hash &&
            x.IssuedAt == payload.IssuedAt && x.FreshUntil == payload.FreshUntil && x.SigningKeyId == payload.KeyId, ct);
    }
}
