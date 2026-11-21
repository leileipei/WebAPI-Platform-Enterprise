using System.Collections.Frozen;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using WebApi.Contracts.Common;
using WebApi.Domain.CredentialSecurity;
using WebApi.Infrastructure.Releases;
using WebApi.Infrastructure.Gateway;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Persistence;

namespace WebApi.Infrastructure.CredentialSecurity;

public static class CredentialGovernanceServiceRegistration
{
    /// <summary>
    /// Registers the independently provisioned writer. The host owns the
    /// certificate lifetime and supplies a verified deployment-coverage source.
    /// </summary>
    public static IServiceCollection AddWitnessedCredentialGovernance(this IServiceCollection services,
        IndependentGovernanceWitnessOptions options, X509Certificate2 writerCertificate, bool nodeServices)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled) return services;
        ArgumentNullException.ThrowIfNull(writerCertificate);
        if (options.DeploymentEvidence is not null) RegisterDeploymentEvidence(services, () => options);
        RegisterProofReaders(services, options.RotationProofs is not null, nodeServices && options.LegacyInitializationHistory is not null);
        return Register(services, sp => new WitnessOwner(CreateClients(options, writerCertificate,
            sp.GetRequiredService<SecuritySigningSettings>())), options.RecoveryHistory is not null, nodeServices);
    }

    public static void AddConfiguredCredentialGovernance(this IServiceCollection services, IConfiguration configuration, bool nodeServices = false)
    {
        var section = configuration.GetSection("CredentialGovernance");
        services.TryAddSingleton(GovernanceDeliverySettings.Read(section["Enabled"]));
        if (!bool.TryParse(section["Enabled"], out var enabled) || !enabled) return;
        var environments = new HashSet<Guid>();
        foreach (var entry in section.GetSection("Environments").GetChildren())
            if (!Guid.TryParse(entry.Value, out var id) || id == Guid.Empty || !environments.Add(id))
                throw new InvalidOperationException("Credential governance requires a valid, unique environment allowlist.");
        services.TryAddSingleton(new PublishSettings(
            AckTimeoutSeconds: int.Parse(configuration["Publish:AckTimeoutSeconds"] ?? "120", System.Globalization.CultureInfo.InvariantCulture),
            GovernanceSnapshot24Environments: environments.ToFrozenSet()));
        // Capture a single boot configuration for the journal, all signed sources and signing rules.
        // Reloading one provider independently could mix different journal generations in one process.
        var captured = ReadConfiguredOptions(section);
        var certificateFile = section["WriterCertificateFile"];
        services.TryAddSingleton(SecuritySigningSettings.Read(configuration));
        IndependentGovernanceWitnessOptions ReadOptions() => captured ?? throw Unavailable();
        if (section.GetSection("DeploymentEvidence").Exists()) RegisterDeploymentEvidence(services, ReadOptions);
        RegisterProofReaders(services, section.GetSection("RotationProofs").Exists(),
            nodeServices && section.GetSection("LegacyInitializationHistory").Exists());
        Register(services, sp =>
        {
            X509Certificate2? ownedCertificate = null;
            try
            {
                var options = ReadOptions();
                ownedCertificate = ReadCertificate(certificateFile);
                return new WitnessOwner(CreateClients(options, ownedCertificate, sp.GetRequiredService<SecuritySigningSettings>()), ownedCertificate);
            }
            catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException
                or CryptographicException or ArgumentException or NotSupportedException)
            {
                ownedCertificate?.Dispose();
                throw Unavailable();
            }
            catch
            {
                ownedCertificate?.Dispose();
                throw;
            }
        }, section.GetSection("RecoveryHistory").Exists(), nodeServices);
    }

    private static IndependentGovernanceWitnessOptions? ReadConfiguredOptions(IConfigurationSection section)
    {
        try { return section.Get<IndependentGovernanceWitnessOptions>(); }
        catch (Exception error) when (error is InvalidOperationException or FormatException or ArgumentException) { return null; }
    }

    private static IServiceCollection Register(IServiceCollection services, Func<IServiceProvider, WitnessOwner> create,
        bool historyConfigured, bool nodeServices)
    {
        services.TryAddSingleton(new GovernanceDeliverySettings(true));
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.TryAddSingleton(sp => SecuritySigningSettings.Read(sp.GetRequiredService<IConfiguration>()));
        services.TryAddSingleton(create);
        services.TryAddScoped<IGovernanceRecoveryContinuity>(sp => new ConfiguredRecoveryContinuity(sp, historyConfigured));
        if (nodeServices)
        {
            RegisterNodeServices(services);
            services.TryAddScoped(sp => new PostgresRevocationReceiptEvidence(() =>
            {
                var ingress = sp.GetService<ICredentialRunningIngressWitness>() ?? throw Unavailable();
                var clients = sp.GetRequiredService<WitnessOwner>().Clients;
                var history = sp.GetService<IGovernanceEnvironmentHistoryWitness>() ?? clients.RecoveryHistoryWitness ?? throw Unavailable();
                return new(clients.JournalId, clients.JournalWitness, history, ingress, sp.GetService<ICredentialArchivedIngressWitness>(),
                    sp.GetService<ICredentialRetiredTargetWitness>());
            }));
        }
        services.TryAddScoped<PostgresGovernanceJournalEnrollment>(sp =>
        {
            var clients = sp.GetRequiredService<WitnessOwner>().Clients;
            return new(ConnectionString(sp), clients.JournalId, clients.JournalWitness);
        });
        services.TryAddScoped<PostgresCredentialRevocationService>(sp =>
        {
            var coverage = sp.GetService<ICredentialRevocationCoverage>() ?? throw Unavailable();
            var clients = sp.GetRequiredService<WitnessOwner>().Clients;
            return new(ConnectionString(sp), clients.JournalId, clients.JournalWitness, coverage, sp.GetService<ICredentialArchivedIngressWitness>());
        });
        services.TryAddScoped<PostgresFormalRotationPreparation>(sp =>
        {
            var coverage = sp.GetService<ICredentialRevocationCoverage>() ?? throw Unavailable();
            var clients = sp.GetRequiredService<WitnessOwner>().Clients;
            return new(ConnectionString(sp), clients.JournalId, coverage, sp.GetService<IFormalRotationLeaseHorizonWitness>());
        });
        services.TryAddScoped<PostgresFormalRotationService>(sp =>
        {
            var coverage = sp.GetService<ICredentialRevocationCoverage>() ?? throw Unavailable();
            var horizon = sp.GetService<IFormalRotationLeaseHorizonWitness>() ?? throw Unavailable();
            var clients = sp.GetRequiredService<WitnessOwner>().Clients;
            return new(ConnectionString(sp), clients.JournalId, clients.FormalRotationWitness, coverage, horizon,
                sp.GetService<ICredentialArchivedIngressWitness>(), sp.GetService<IFormalRotationRequestProofWitness>());
        });
        return services;
    }

    private static void RegisterProofReaders(IServiceCollection services, bool rotationConfigured, bool legacyConfigured)
    {
        if (rotationConfigured)
            services.TryAddScoped<IFormalRotationRequestProofWitness>(sp => new ConfiguredRotationProofReader(sp));
        if (legacyConfigured)
            services.TryAddScoped<ILegacyEnvironmentHistoryReader>(sp => new LegacyEnvironmentHistoryHttpReader(() =>
                sp.GetRequiredService<WitnessOwner>().Clients.LegacyHistoryTransport ?? throw Unavailable(),
                sp.GetRequiredService<ILogger<LegacyEnvironmentHistoryHttpReader>>()));
    }

    private sealed class ConfiguredRotationProofReader(IServiceProvider services) : IFormalRotationRequestProofWitness
    {
        public Task<IReadOnlyList<FormalRotationRequestProof>> ObserveAsync(FormalRotationRequestProofScope scope, CancellationToken ct)
        {
            var witness = services.GetRequiredService<WitnessOwner>().Clients.RotationProofWitness ?? throw Unavailable();
            return witness.ObserveAsync(scope, ct);
        }
    }

    private static void RegisterDeploymentEvidence(IServiceCollection services, Func<IndependentGovernanceWitnessOptions> readOptions)
    {
        services.TryAddSingleton<GovernanceDeploymentEvidenceCache>();
        services.TryAddSingleton(sp =>
        {
            var options = readOptions();
            var (role, _) = IndependentGovernanceWitnessClients.RequireDeploymentRole(options);
            var journal = options.JournalId ?? throw Unavailable();
            GovernanceJournalProtocol.RequireJournal(journal);
            var signing = sp.GetRequiredService<SecuritySigningSettings>();
            void RequireDisjointSigner() => RequireIndependentSigningKeys(signing, IndependentGovernanceWitnessClients.PublicKeyPins(options));
            RequireDisjointSigner();
            return new GovernanceDeploymentEvidenceHttpWitness(role, journal, sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<GovernanceDeploymentEvidenceCache>(), RequireDisjointSigner);
        });
        services.TryAddSingleton<ICredentialRevocationCoverage>(sp => sp.GetRequiredService<GovernanceDeploymentEvidenceCache>());
        services.TryAddSingleton<ICredentialRunningIngressWitness>(sp => sp.GetRequiredService<GovernanceDeploymentEvidenceCache>());
        services.TryAddSingleton<ICredentialArchivedIngressWitness>(sp => sp.GetRequiredService<GovernanceDeploymentEvidenceCache>());
        services.TryAddSingleton<IFormalRotationLeaseHorizonWitness>(sp => sp.GetRequiredService<GovernanceDeploymentEvidenceCache>());
        services.TryAddSingleton<ICredentialRetiredTargetWitness>(sp => sp.GetRequiredService<GovernanceDeploymentEvidenceCache>());
        services.AddHostedService<GovernanceDeploymentEvidenceRefreshWorker>();
    }

    private static void RegisterNodeServices(IServiceCollection services)
    {
        services.TryAddSingleton(sp => sp.GetRequiredService<WitnessOwner>().Clients.LeaseEvidenceSources);
        services.TryAddScoped<SecurityLedgerReader>();
        services.TryAddScoped<CurrentSecurityProofService>();
        services.TryAddScoped<SecurityAckService>();
        services.TryAddScoped<SecurityLeaseIssuer>(sp =>
        {
            var db = sp.GetRequiredService<WebApiDbContext>();
            var identities = sp.GetRequiredService<NodeIdentityService>();
            var ledger = sp.GetRequiredService<SecurityLedgerReader>();
            var signing = sp.GetRequiredService<SecuritySigningSettings>();
            return new(db, identities, ledger, signing,
                sp.GetRequiredService<IGovernanceRecoveryContinuity>(), sp.GetRequiredService<WitnessOwner>().Clients.LeaseReservationWitness,
                sp.GetRequiredService<SecurityLeaseEvidenceSources>(), sp.GetRequiredService<TimeProvider>());
        });
    }

    private sealed class ConfiguredRecoveryContinuity(IServiceProvider services, bool historyConfigured) : IGovernanceRecoveryContinuity
    {
        public Task<GovernanceContinuity> RequireAsync(WebApiDbContext db, Guid environmentId, CancellationToken ct = default)
        {
            var history = services.GetService<IGovernanceEnvironmentHistoryWitness>();
            if (history is null && !historyConfigured) throw Unavailable();
            var clients = services.GetRequiredService<WitnessOwner>().Clients;
            history ??= clients.RecoveryHistoryWitness ?? throw Unavailable();
            return new PostgresGovernanceRecoveryContinuity(clients.JournalId, clients.JournalWitness, history).RequireAsync(db, environmentId, ct);
        }

        private static InvalidDataException Unavailable() => new("credential_governance_recovery_continuity_unavailable");
    }

    private static IndependentGovernanceWitnessClients CreateClients(IndependentGovernanceWitnessOptions options,
        X509Certificate2 certificate, SecuritySigningSettings signing)
    {
        try
        {
            RequireIndependentSigningKeys(signing, IndependentGovernanceWitnessClients.PublicKeyPins(options));
            return IndependentGovernanceWitnessClients.Create(options, certificate) ?? throw Unavailable();
        }
        catch (ArgumentException)
        {
            throw Unavailable();
        }
    }

    private static void RequireIndependentSigningKeys(SecuritySigningSettings signing, IReadOnlySet<string> witnessKeys)
    {
        var leaseKeys = !string.IsNullOrWhiteSpace(signing.KeyId) && !string.IsNullOrWhiteSpace(signing.PrivateKeyFile)
            ? signing.LoadTrustedKeys().SubjectPublicKeys
            : signing.TrustedPublicKeys;
        if (leaseKeys?.Values.Any(Overlaps) == true) throw Unavailable();

        bool Overlaps(string encoded)
        {
            try { return witnessKeys.Contains(Convert.ToBase64String(Convert.FromBase64String(encoded))); }
            catch (FormatException) { return false; }
        }
    }

    private static ApiException Unavailable() =>
        new(503, "credential_governance_unavailable", "独立见证或受控入口证据不可用。");

    private static string ConnectionString(IServiceProvider services) =>
        services.GetRequiredService<WebApiDbContext>().Database.GetConnectionString()
        ?? throw Unavailable();

    internal static X509Certificate2 ReadCertificate(string? path)
    {
        if (OperatingSystem.IsWindows()) throw Unavailable();
        if (path is null || !Path.IsPathFullyQualified(path) || !(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()))
            throw Unavailable();
        var file = new FileInfo(path);
        if (!file.Exists || file.LinkTarget is not null) throw Unavailable();
        for (var directory = file.Directory; directory is not null; directory = directory.Parent)
            if (directory.LinkTarget is not null) throw Unavailable();

        // Open without following a replaced leaf, and avoid blocking on a non-regular file.
        var descriptor = Open(path, OperatingSystem.IsMacOS() ? 0x104 : 0x20800);
        if (descriptor < 0) throw Unavailable();
        using var handle = new SafeFileHandle(new IntPtr(descriptor), ownsHandle: true);
        var mode = File.GetUnixFileMode(handle);
        if ((mode & UnixFileMode.UserRead) == 0 || (mode & ~(UnixFileMode.UserRead | UnixFileMode.UserWrite)) != 0)
            throw Unavailable();
        using var stream = new FileStream(handle, FileAccess.Read);
        if (!stream.CanSeek || stream.Length is < 1 or > 65536) throw Unavailable();
        var bytes = new byte[(int)stream.Length];
        try
        {
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1) throw Unavailable();
            return X509CertificateLoader.LoadPkcs12(bytes, ReadOnlySpan<char>.Empty, X509KeyStorageFlags.EphemeralKeySet);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    private sealed class WitnessOwner(IndependentGovernanceWitnessClients clients, X509Certificate2? ownedCertificate = null) : IDisposable
    {
        public IndependentGovernanceWitnessClients Clients { get; } = clients;

        public void Dispose()
        {
            Clients.Dispose();
            ownedCertificate?.Dispose();
        }
    }
}
