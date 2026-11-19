using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Win32.SafeHandles;
using WebApi.Infrastructure.CredentialSecurity;
using WebApi.Infrastructure.Persistence;

namespace WebApi.ControlPlane.Applications;

public static class CredentialGovernanceRegistration
{
    /// <summary>
    /// Registers the independently provisioned writer. The host owns the
    /// certificate lifetime and supplies a verified deployment-coverage source.
    /// </summary>
    public static IServiceCollection AddWitnessedCredentialGovernance(this IServiceCollection services,
        IndependentGovernanceWitnessOptions options, X509Certificate2 writerCertificate)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled) return services;
        ArgumentNullException.ThrowIfNull(writerCertificate);
        return Register(services, () => new WitnessOwner(CreateClients(options, writerCertificate)));
    }

    internal static void AddConfiguredWitness(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection("CredentialGovernance");
        if (!bool.TryParse(section["Enabled"], out var enabled) || !enabled) return;
        Register(services, () =>
        {
            X509Certificate2? ownedCertificate = null;
            try
            {
                var options = section.Get<IndependentGovernanceWitnessOptions>() ?? throw CredentialGovernanceEndpoints.Unavailable();
                ownedCertificate = ReadCertificate(section["WriterCertificateFile"]);
                return new WitnessOwner(CreateClients(options, ownedCertificate), ownedCertificate);
            }
            catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException
                or CryptographicException or ArgumentException or NotSupportedException)
            {
                ownedCertificate?.Dispose();
                throw CredentialGovernanceEndpoints.Unavailable();
            }
            catch
            {
                ownedCertificate?.Dispose();
                throw;
            }
        });
    }

    private static IServiceCollection Register(IServiceCollection services, Func<WitnessOwner> create)
    {
        services.TryAddSingleton<WitnessOwner>(_ => create());
        services.TryAddScoped<PostgresGovernanceJournalEnrollment>(sp =>
        {
            var clients = sp.GetRequiredService<WitnessOwner>().Clients;
            return new(ConnectionString(sp), clients.JournalId, clients.JournalWitness);
        });
        services.TryAddScoped<PostgresCredentialRevocationService>(sp =>
        {
            var coverage = sp.GetService<ICredentialRevocationCoverage>() ?? throw CredentialGovernanceEndpoints.Unavailable();
            var clients = sp.GetRequiredService<WitnessOwner>().Clients;
            return new(ConnectionString(sp), clients.JournalId, clients.JournalWitness, coverage, sp.GetService<ICredentialArchivedIngressWitness>());
        });
        services.TryAddScoped<PostgresFormalRotationPreparation>(sp =>
        {
            var coverage = sp.GetService<ICredentialRevocationCoverage>() ?? throw CredentialGovernanceEndpoints.Unavailable();
            var clients = sp.GetRequiredService<WitnessOwner>().Clients;
            return new(ConnectionString(sp), clients.JournalId, coverage, sp.GetService<IFormalRotationLeaseHorizonWitness>());
        });
        return services;
    }

    private static IndependentGovernanceWitnessClients CreateClients(IndependentGovernanceWitnessOptions options,
        X509Certificate2 certificate)
    {
        try
        {
            return IndependentGovernanceWitnessClients.Create(options, certificate)
                ?? throw CredentialGovernanceEndpoints.Unavailable();
        }
        catch (ArgumentException)
        {
            throw CredentialGovernanceEndpoints.Unavailable();
        }
    }

    private static string ConnectionString(IServiceProvider services) =>
        services.GetRequiredService<WebApiDbContext>().Database.GetConnectionString()
        ?? throw CredentialGovernanceEndpoints.Unavailable();

    private static X509Certificate2 ReadCertificate(string? path)
    {
        if (OperatingSystem.IsWindows()) throw CredentialGovernanceEndpoints.Unavailable();
        if (path is null || !Path.IsPathFullyQualified(path) || !(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()))
            throw CredentialGovernanceEndpoints.Unavailable();
        var file = new FileInfo(path);
        if (!file.Exists || file.LinkTarget is not null) throw CredentialGovernanceEndpoints.Unavailable();
        for (var directory = file.Directory; directory is not null; directory = directory.Parent)
            if (directory.LinkTarget is not null) throw CredentialGovernanceEndpoints.Unavailable();

        // Open without following a replaced leaf, and avoid blocking on a non-regular file.
        var descriptor = Open(path, OperatingSystem.IsMacOS() ? 0x104 : 0x20800);
        if (descriptor < 0) throw CredentialGovernanceEndpoints.Unavailable();
        using var handle = new SafeFileHandle(new IntPtr(descriptor), ownsHandle: true);
        var mode = File.GetUnixFileMode(handle);
        if ((mode & UnixFileMode.UserRead) == 0 || (mode & ~(UnixFileMode.UserRead | UnixFileMode.UserWrite)) != 0)
            throw CredentialGovernanceEndpoints.Unavailable();
        using var stream = new FileStream(handle, FileAccess.Read);
        if (!stream.CanSeek || stream.Length is < 1 or > 65536) throw CredentialGovernanceEndpoints.Unavailable();
        var bytes = new byte[(int)stream.Length];
        try
        {
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1) throw CredentialGovernanceEndpoints.Unavailable();
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
