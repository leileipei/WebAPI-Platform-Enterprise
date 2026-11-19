using WebApi.Gateway.Configuration;

namespace WebApi.Gateway.Security;

/// <summary>
/// Supplies this process's active security state for a routing generation.
/// Registering a provider requires security admission for every authenticated route,
/// including routes selected from compatible snapshots.
/// </summary>
public interface IGovernanceAdmissionStateProvider
{
    /// <summary>
    /// Returns state qualified by the current independent trust evidence, or null when
    /// that evidence is missing or stale. This lookup reads local state and performs no I/O.
    /// </summary>
    SecurityStateStore? GetActiveState(RuntimeGeneration generation);
}

internal static class GovernanceReadiness
{
    internal static bool CanUse(RuntimeGeneration generation, IGovernanceAdmissionStateProvider? provider)
    {
        if (provider is null) return generation.Snapshot.SchemaVersion != "2.4";
        generation.PrepareSecurityAdmission();
        return provider.GetActiveState(generation) is { } state && state.CanUseSnapshot(generation);
    }
}
