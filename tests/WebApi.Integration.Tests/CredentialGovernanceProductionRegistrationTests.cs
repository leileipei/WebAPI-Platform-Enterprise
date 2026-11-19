using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using WebApi.Contracts.Common;
using WebApi.ControlPlane;
using WebApi.ControlPlane.Applications;
using WebApi.Domain.CredentialSecurity;
using WebApi.Gateway;
using WebApi.Gateway.Security;
using WebApi.Infrastructure.CredentialSecurity;
using WebApi.Infrastructure.Releases;
using WebApi.Infrastructure.Governance;
using WebApi.Integration.Tests.Support;
using WebApi.Worker;
using Xunit;

namespace WebApi.Integration.Tests;

public sealed class CredentialGovernanceProductionRegistrationTests
{
    [Fact]
    public async Task ControlPlaneAndWorkerUseConfiguredEvidenceAndExplicitReleaseEnvironments()
    {
        await using var fixture = new ApiFixture();
        var directory = Directory.CreateDirectory(fixture.LoginProtectionDirectory);
        using var pins = new FixturePins();
        var certificate = pins.WriteWriterCertificate(directory.FullName);
        void Configure(IConfiguration config)
        {
            pins.ConfigureEvidence(config);
            config["CredentialGovernance:WriterCertificateFile"] = certificate;
            config["CredentialGovernance:JournalId:AuthorityId"] = pins.Authority.ToString();
            config["CredentialGovernance:JournalId:Generation"] = pins.Generation.ToString();
            config["CredentialGovernance:Environments:0"] = fixture.Environment.Id.ToString();
        }

        await fixture.InitializeAsync(builder => Configure(builder.Configuration));
        using var controlPlaneScope = fixture.Services();
        using var worker = WorkerApp.Build(["--environment", "Development"], builder =>
        {
            builder.Configuration["ConnectionStrings:WebApi"] = fixture.Database.ConnectionString;
            builder.Logging.ClearProviders();
            Configure(builder.Configuration);
        });
        await using var workerScope = worker.Services.CreateAsyncScope();

        Assert.NotNull(controlPlaneScope.ServiceProvider.GetService<SecurityLeaseEvidenceSources>());
        Assert.NotNull(controlPlaneScope.ServiceProvider.GetService<IGovernanceRecoveryContinuity>());
        Assert.NotNull(workerScope.ServiceProvider.GetService<IGovernanceRecoveryContinuity>());
        foreach (var services in new[] { controlPlaneScope.ServiceProvider, workerScope.ServiceProvider })
        {
            var settings = services.GetRequiredService<PublishSettings>();
            Assert.True(settings.EnablesGovernanceSnapshot24(fixture.Environment.Id));
            Assert.False(settings.EnablesGovernanceSnapshot24(Guid.NewGuid()));
        }
        Assert.Contains(worker.Services.GetServices<IHostedService>(),
            service => service.GetType().Name == "FormalRotationDeadlineWorker");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfiguredProofAdaptersShareTheFrozenOptionsAndRejectTheActiveLeaseSigner(bool conflict)
    {
        using var pins = new FixturePins();
        var directory = Directory.CreateTempSubdirectory("webapi-governance-registration-");
        try
        {
            var certificate = pins.WriteWriterCertificate(directory.FullName);
            var privateKey = Path.Combine(directory.FullName, "lease.pem");
            File.WriteAllText(privateKey, pins.Lease.ExportPkcs8PrivateKeyPem());
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(privateKey, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            void Configure(IConfiguration config)
            {
                pins.ConfigureEvidence(config);
                config["ConnectionStrings:WebApi"] = "Host=unused;Database=unused;Username=unused;Password=fixture";
                config["CredentialGovernance:WriterCertificateFile"] = certificate;
                config["CredentialGovernance:JournalId:AuthorityId"] = pins.Authority.ToString();
                config["CredentialGovernance:JournalId:Generation"] = pins.Generation.ToString();
                FixturePins.SetRole(config, "DeploymentEvidence", "deployment", conflict ? pins.Lease : pins.Deployment);
                FixturePins.SetRole(config, "RotationProofs", "rotation", pins.Rotation);
                FixturePins.SetRole(config, "LegacyInitializationHistory", "legacy", pins.Legacy);
                config["Security:Signing:KeyId"] = "current-lease";
                config["Security:Signing:PrivateKeyFile"] = privateKey;
            }

            IConfiguration? cpConfiguration = null;
            await using var control = ControlPlaneApp.Build(["--environment", "Development"], builder =>
            {
                Configure(builder.Configuration);
                cpConfiguration = builder.Configuration;
                builder.Logging.ClearProviders();
            });
            IConfiguration? workerConfiguration = null;
            using var worker = WorkerApp.Build(["--environment", "Development"], builder =>
            {
                Configure(builder.Configuration);
                workerConfiguration = builder.Configuration;
                builder.Logging.ClearProviders();
            });

            // Reloads after boot must not let the background feed and the
            // lazily constructed journal client pin different authorities.
            foreach (var config in new[] { cpConfiguration!, workerConfiguration! })
            {
                config["CredentialGovernance:JournalId:Generation"] = Guid.Empty.ToString();
                config["CredentialGovernance:DeploymentEvidence:Endpoint"] = "http://invalid.example.test";
            }
            foreach (var host in new[] { control.Services, worker.Services })
            {
                await using var scope = host.CreateAsyncScope();
                var services = scope.ServiceProvider;
                if (conflict)
                {
                    Assert.Equal("credential_governance_unavailable", Assert.Throws<ApiException>(() =>
                        services.GetRequiredService<GovernanceDeploymentEvidenceHttpWitness>()).Code);
                    Assert.Equal("credential_governance_unavailable", Assert.Throws<ApiException>(() =>
                        services.GetRequiredService<PostgresGovernanceJournalEnrollment>()).Code);
                    continue;
                }
                Assert.NotNull(services.GetRequiredService<GovernanceDeploymentEvidenceHttpWitness>());
                Assert.NotNull(services.GetRequiredService<ICredentialRunningIngressWitness>());
                Assert.NotNull(services.GetRequiredService<ICredentialArchivedIngressWitness>());
                Assert.NotNull(services.GetRequiredService<IFormalRotationLeaseHorizonWitness>());
                Assert.NotNull(services.GetRequiredService<IFormalRotationRequestProofWitness>());
                Assert.NotNull(services.GetRequiredService<PostgresGovernanceJournalEnrollment>());
            }
            await using var controlScope = control.Services.CreateAsyncScope();
            Assert.NotNull(controlScope.ServiceProvider.GetRequiredService<ILegacyEnvironmentHistoryReader>());
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public async Task EmbeddedControlPlaneCanKeepItsExplicitIndependentSourceInjection()
    {
        using var pins = new FixturePins();
        using var explicitEvidence = new IndependentEvidenceHttpClient(new("https://clock.example.test/source"),
            new("https://history.example.test/source"));
        var trust = new IndependentEvidenceTrust("clock", "clock-key", FixturePins.PublicKey(pins.Time), "history",
            "history-key", FixturePins.PublicKey(pins.History));
        var sources = new SecurityLeaseEvidenceSources(explicitEvidence, explicitEvidence, trust);
        var directory = Directory.CreateTempSubdirectory("webapi-governance-embedded-");
        try
        {
            using var certificate = X509CertificateLoader.LoadPkcs12FromFile(pins.WriteWriterCertificate(directory.FullName), null,
                X509KeyStorageFlags.EphemeralKeySet);
            var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
            pins.ConfigureEvidence(config);
            config["CredentialGovernance:JournalId:AuthorityId"] = pins.Authority.ToString();
            config["CredentialGovernance:JournalId:Generation"] = pins.Generation.ToString();
            var options = config.GetSection("CredentialGovernance").Get<IndependentGovernanceWitnessOptions>()!;
            await using var control = ControlPlaneApp.Build(["--environment", "Development"], builder =>
            {
                builder.Configuration["ConnectionStrings:WebApi"] = "Host=unused;Database=unused;Username=unused;Password=fixture";
                builder.Services.AddSingleton(sources);
                builder.Services.AddWitnessedCredentialGovernance(options, certificate);
                builder.Logging.ClearProviders();
            });
            using var scope = control.Services.CreateScope();
            Assert.Same(sources, scope.ServiceProvider.GetRequiredService<SecurityLeaseEvidenceSources>());
            Assert.NotNull(scope.ServiceProvider.GetRequiredService<PostgresGovernanceJournalEnrollment>());
        }
        finally { directory.Delete(recursive: true); }
    }

    [Theory]
    [InlineData("enabled")]
    [InlineData("with-rotation-proof-delivery")]
    [InlineData("proof-certificate-reuses-time-key")]
    [InlineData("proof-certificate-reuses-history-key")]
    [InlineData("proof-certificate-reuses-lease-key")]
    [InlineData("disabled")]
    [InlineData("wrong-key")]
    [InlineData("invalid-endpoint")]
    public async Task GatewayConfigurationRequiresDistinctPinnedAuthoritiesAndHttps(string scenario)
    {
        using var pins = new FixturePins();
        var reusedKey = scenario switch
        {
            "proof-certificate-reuses-time-key" => pins.Time,
            "proof-certificate-reuses-history-key" => pins.History,
            "proof-certificate-reuses-lease-key" => pins.Lease,
            _ => null
        };
        var withProofs = scenario == "with-rotation-proof-delivery" || reusedKey is not null;
        void Configure(IConfiguration config)
        {
            config["Gateway:EnvironmentId"] = Guid.NewGuid().ToString();
            config["Gateway:NodeName"] = "governance-fixture";
            config["Gateway:SecretFile"] = "/fixture/secret";
            config["Gateway:LkgDirectory"] = "/fixture/lkg";
            config["Gateway:ControlPlaneUrl"] = "https://control.example.test";
            config["Gateway:AutomaticUpdates"] = "false";
            if (scenario == "disabled") return;
            pins.ConfigureEvidence(config);
            config["CredentialGovernance:LeaseVerificationKeys:lease-key"] =
                FixturePins.PublicKey(scenario == "wrong-key" ? pins.Time : pins.Lease);
            if (scenario == "invalid-endpoint")
                config["CredentialGovernance:HistoryEvidence:Endpoint"] = "http://history.example.test/observe";
        }

        if (scenario is "wrong-key" or "invalid-endpoint")
        {
            Assert.Throws<InvalidOperationException>(() => GatewayApp.Build([], builder => Configure(builder.Configuration)));
            return;
        }

        var directory = Directory.CreateTempSubdirectory("webapi-governance-gateway-");
        try
        {
            var secretFile = Path.Combine(directory.FullName, "node-secret");
            await File.WriteAllTextAsync(secretFile, new string('x', 64));
            await using var gateway = GatewayApp.Build([], builder =>
            {
                Configure(builder.Configuration);
                builder.Configuration["Gateway:SecretFile"] = secretFile;
                if (withProofs)
                {
                    FixturePins.SetRole(builder.Configuration, "RotationProofs", "rotation", pins.Rotation);
                    builder.Configuration["CredentialGovernance:JournalId:AuthorityId"] = pins.Authority.ToString();
                    builder.Configuration["CredentialGovernance:JournalId:Generation"] = pins.Generation.ToString();
                    builder.Configuration["CredentialGovernance:RotationProofCertificateFile"] = pins.WriteWriterCertificate(directory.FullName, reusedKey);
                }
            });
            var provider = gateway.Services.GetService<IGovernanceAdmissionStateProvider>();
            if (scenario == "disabled") Assert.Null(provider);
            else Assert.NotNull(provider);
            var proofs = gateway.Services.GetService<IFormalRotationRequestProofSink>();
            if (withProofs)
            {
                Assert.NotNull(proofs);
                if (reusedKey is null)
                    Assert.NotNull(gateway.Services.GetRequiredService<FormalRotationProofHttpWitness>());
                else
                    Assert.Throws<ArgumentException>(() => gateway.Services.GetRequiredService<FormalRotationProofHttpWitness>());
            }
            else Assert.Null(proofs);
        }
        finally { directory.Delete(recursive: true); }
    }

    private sealed class FixturePins : IDisposable
    {
        public Guid Authority { get; } = Guid.NewGuid();
        public Guid Generation { get; } = Guid.NewGuid();
        public ECDsa Time { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public ECDsa History { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public ECDsa Lease { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public ECDsa Deployment { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public ECDsa Rotation { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public ECDsa Legacy { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        public string WriteWriterCertificate(string directory, ECDsa? reuse = null)
        {
            using var newKey = reuse is null ? ECDsa.Create(ECCurve.NamedCurves.nistP256) : null;
            var request = new CertificateRequest("CN=isolated writer", reuse ?? newKey!, HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new("1.3.6.1.5.5.7.3.2") }, critical: true));
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
            var file = Path.Combine(directory, "writer.pfx");
            File.WriteAllBytes(file, certificate.Export(X509ContentType.Pkcs12));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return file;
        }

        public void ConfigureEvidence(IConfiguration config)
        {
            config["CredentialGovernance:Enabled"] = "true";
            foreach (var (role, name, key) in new[]
                {
                    ("TimeEvidence", "clock", Time), ("HistoryEvidence", "history", History),
                    ("GovernanceJournal", "journal", History), ("RecoveryHistory", "recovery", History)
                })
                SetRole(config, role, name, key);
        }

        public static void SetRole(IConfiguration config, string role, string name, ECDsa key)
        {
            var section = $"CredentialGovernance:{role}";
            config[$"{section}:Endpoint"] = $"https://{name}.example.test/observe";
            config[$"{section}:SourceId"] = name;
            config[$"{section}:KeyId"] = $"{name}-key";
            config[$"{section}:SubjectPublicKeyInfoBase64"] = PublicKey(key);
        }

        public static string PublicKey(ECDsa key) => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());

        public void Dispose()
        {
            Time.Dispose();
            History.Dispose();
            Lease.Dispose();
            Deployment.Dispose();
            Rotation.Dispose();
            Legacy.Dispose();
        }
    }
}
