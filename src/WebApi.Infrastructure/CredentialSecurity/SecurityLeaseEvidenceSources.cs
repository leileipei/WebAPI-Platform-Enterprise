using WebApi.Domain.CredentialSecurity;

namespace WebApi.Infrastructure.CredentialSecurity;

/// <summary>Explicit host composition for the two authenticated issuance challenges.</summary>
public sealed record SecurityLeaseEvidenceSources(IIndependentTimeEvidenceSource Time,
    IIndependentSecurityHistoryWitness History, IndependentEvidenceTrust Trust);
