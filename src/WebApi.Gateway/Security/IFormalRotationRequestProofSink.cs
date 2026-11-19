using WebApi.Domain.CredentialSecurity;

namespace WebApi.Gateway.Security;

/// <summary>Optional trusted gateway service for completed API-key requests.</summary>
public interface IFormalRotationRequestProofSink
{
    /// <summary>Transfers a proof to the sink without blocking or performing network or disk I/O on the request path.</summary>
    /// <returns>Whether the sink accepted the proof for delivery.</returns>
    bool TryRecord(FormalRotationRequestProof proof);
}
