using Microsoft.Extensions.Logging;
using WebApi.Contracts.Common;
using WebApi.Domain.CredentialSecurity;
using WebApi.Infrastructure.CredentialSecurity;

namespace WebApi.Infrastructure.Governance;

// An open command only retains a reference to a scope already stopped by the independent maintenance authority.
public sealed record LegacyEnvironmentHistoryCommand(string Action, Guid EnvironmentId, Guid HoldId);
public sealed record LegacyEnvironmentHistoryObservation(Guid EnvironmentId, Guid HoldId, string State, string? EvidenceHash,
    LegacyEnvironmentHistoryEvidence? Evidence);

internal sealed class LegacyEnvironmentHistoryHttpReader(Func<GovernanceProofHttpTransport> resolve,
    ILogger<LegacyEnvironmentHistoryHttpReader> log) : ILegacyEnvironmentHistoryReader
{
    public const string Protocol = "legacy-environment-history";

    public async Task<ILegacyEnvironmentHistoryFence?> OpenAsync(Guid environmentId, CancellationToken ct)
    {
        if (environmentId == Guid.Empty) return null;
        var id = Guid.NewGuid();
        GovernanceProofHttpTransport? transport = null;
        var held = false;
        try
        {
            transport = resolve();
            var reply = await SendAsync(transport, "open", environmentId, id, ct);
            if (reply.State != "Held" || reply.Evidence is not { } evidence || evidence.Source?.EnvironmentId != environmentId ||
                !evidence.LegacyWritersStopped || !SecuritySignedEncoding.HexHash(evidence.WriterFenceHash) ||
                reply.EvidenceHash != EvidenceHash(evidence)) return null;
            var fence = new HeldFence(transport, log, environmentId, id, reply.EvidenceHash, evidence);
            held = true;
            return fence;
        }
        catch (Exception error) when (Unavailable(error)) { return null; }
        finally
        {
            // Release only this attempt's randomly generated hold using the same identity. If the
            // authority cannot authenticate the release, the independently held stop-write remains.
            if (transport is not null && !held) await ReleaseAsync(transport, log, environmentId, id);
        }
    }

    public static string EvidenceHash(LegacyEnvironmentHistoryEvidence evidence) =>
        GovernanceDeploymentEvidenceProtocol.ScopeHash("legacy-environment-history-evidence", evidence);

    private static async Task<LegacyEnvironmentHistoryObservation> SendAsync(GovernanceProofHttpTransport transport, string action,
        Guid environmentId, Guid holdId, CancellationToken ct)
    {
        var reply = await transport.ExchangeAsync<LegacyEnvironmentHistoryCommand, LegacyEnvironmentHistoryObservation>(Protocol,
            new(action, environmentId, holdId), ct);
        if (reply.EnvironmentId != environmentId || reply.HoldId != holdId || action != "open" && reply.Evidence is not null)
            throw new InvalidDataException("credential_governance_legacy_history_unavailable");
        return reply;
    }

    private static async Task ReleaseAsync(GovernanceProofHttpTransport transport, ILogger<LegacyEnvironmentHistoryHttpReader> log,
        Guid environmentId, Guid holdId)
    {
        try
        {
            var reply = await SendAsync(transport, "release", environmentId, holdId, CancellationToken.None);
            if (reply.State is "Released" or "Absent") return;
        }
        catch (Exception error) when (Unavailable(error)) { }
        log.LogWarning("Legacy stop-write release unknown for environment {EnvironmentId}, hold {HoldId}.", environmentId, holdId);
    }

    private static bool Unavailable(Exception error) =>
        error is IOException or InvalidDataException or InvalidOperationException or ArgumentException or ApiException;

    private sealed class HeldFence(GovernanceProofHttpTransport transport, ILogger<LegacyEnvironmentHistoryHttpReader> log,
        Guid environmentId, Guid holdId, string digest, LegacyEnvironmentHistoryEvidence evidence) : ILegacyEnvironmentHistoryFence
    {
        private int released;

        public LegacyEnvironmentHistoryEvidence Evidence { get; } = evidence;

        public async Task<bool> IsHeldAsync(CancellationToken ct)
        {
            if (Volatile.Read(ref released) != 0) return false;
            try
            {
                var reply = await SendAsync(transport, "check", environmentId, holdId, ct);
                return reply.State == "Held" && digest == reply.EvidenceHash && Volatile.Read(ref released) == 0;
            }
            catch (Exception error) when (Unavailable(error)) { return false; }
        }

        public void RetainForRecovery()
        {
            if (Interlocked.Exchange(ref released, 1) == 0)
                log.LogWarning("Legacy stop-write retained for recovery for environment {EnvironmentId}, hold {HoldId}.", environmentId, holdId);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref released, 1) == 0) await ReleaseAsync(transport, log, environmentId, holdId);
        }
    }
}
