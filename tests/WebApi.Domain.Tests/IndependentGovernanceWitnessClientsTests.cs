using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using WebApi.Domain.CredentialSecurity;
using WebApi.Infrastructure.CredentialSecurity;
using Xunit;

namespace WebApi.Domain.Tests;

public sealed class IndependentGovernanceWitnessClientsTests
{
    [Fact]
    public void AbsentAndDisabledConfigurationDoNotCreateClients()
    {
        Assert.Null(IndependentGovernanceWitnessClients.Create());
        Assert.Null(IndependentGovernanceWitnessClients.Create(new()));
        Assert.Null(IndependentGovernanceWitnessClients.Create(new() { JournalId = new(Guid.NewGuid(), Guid.Empty) }));
    }

    [Theory]
    [InlineData("time")]
    [InlineData("history")]
    [InlineData("write")]
    [InlineData("journal")]
    [InlineData("authority")]
    [InlineData("generation")]
    public void IncompleteEnabledConfigurationIsRejected(string field)
    {
        using var fixture = new Fixture();
        var options = field switch
        {
            "time" => fixture.Options with { TimeEvidence = null },
            "history" => fixture.Options with { HistoryEvidence = null },
            "write" => fixture.Options with { GovernanceJournal = null },
            "journal" => fixture.Options with { JournalId = null },
            "authority" => fixture.Options with { JournalId = fixture.Journal with { AuthorityId = Guid.Empty } },
            "generation" => fixture.Options with { JournalId = fixture.Journal with { Generation = Guid.Empty } },
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };

        Assert.Throws<ArgumentException>(() => IndependentGovernanceWitnessClients.Create(options));
    }

    [Theory]
    [InlineData("/history")]
    [InlineData("http://witness.example.test/history")]
    [InlineData("https://identity@witness.example.test/history")]
    [InlineData("https://witness.example.test/history?parameter=value")]
    [InlineData("https://witness.example.test/history#fragment")]
    public void RejectsAWriteEndpointThatIsNotFixedHttps(string endpoint)
    {
        using var fixture = new Fixture();
        var options = fixture.Options with
        {
            GovernanceJournal = fixture.Options.GovernanceJournal! with { Endpoint = new Uri(endpoint, UriKind.RelativeOrAbsolute) }
        };

        Assert.Throws<ArgumentException>(() => IndependentGovernanceWitnessClients.Create(options));
    }

    [Fact]
    public void RejectsAnEndpointThatCannotDistinguishTheTwoReadEvidenceRequests()
    {
        using var fixture = new Fixture();
        var options = fixture.Options with
        {
            TimeEvidence = fixture.Options.TimeEvidence! with { Endpoint = new Uri("https://WITNESS.EXAMPLE.TEST:443/history") }
        };

        Assert.Throws<ArgumentException>(() => IndependentGovernanceWitnessClients.Create(options));
    }

    [Theory]
    [InlineData("source")]
    [InlineData("key-id")]
    [InlineData("base64")]
    [InlineData("whitespace")]
    [InlineData("trailing-bytes")]
    [InlineData("wrong-curve")]
    [InlineData("private-key")]
    public void RejectsInvalidRoleIdentityOrPublicKey(string invalid)
    {
        using var fixture = new Fixture();
        using var otherCurve = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var history = fixture.Options.HistoryEvidence!;
        var changed = invalid switch
        {
            "source" => history with { SourceId = "history source" },
            "key-id" => history with { KeyId = "" },
            "base64" => history with { SubjectPublicKeyInfoBase64 = "not-base64" },
            "whitespace" => history with { SubjectPublicKeyInfoBase64 = history.SubjectPublicKeyInfoBase64 + " " },
            "trailing-bytes" => history with
            {
                SubjectPublicKeyInfoBase64 = Convert.ToBase64String([.. Convert.FromBase64String(history.SubjectPublicKeyInfoBase64), 0])
            },
            "wrong-curve" => history with { SubjectPublicKeyInfoBase64 = Convert.ToBase64String(otherCurve.ExportSubjectPublicKeyInfo()) },
            "private-key" => history with { SubjectPublicKeyInfoBase64 = Convert.ToBase64String(fixture.TimeKey.ExportPkcs8PrivateKey()) },
            _ => throw new ArgumentOutOfRangeException(nameof(invalid))
        };

        Assert.Throws<ArgumentException>(() => IndependentGovernanceWitnessClients.Create(fixture.Options with { HistoryEvidence = changed }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectsReusingTheTimeSigningKeyForHistoryOrWriting(bool useForWriting)
    {
        using var fixture = new Fixture();
        var timeKey = fixture.Options.TimeEvidence!.SubjectPublicKeyInfoBase64;
        var options = useForWriting
            ? fixture.Options with { GovernanceJournal = fixture.Options.GovernanceJournal! with { SubjectPublicKeyInfoBase64 = timeKey } }
            : fixture.Options with { HistoryEvidence = fixture.Options.HistoryEvidence! with { SubjectPublicKeyInfoBase64 = timeKey } };

        Assert.Throws<ArgumentException>(() => IndependentGovernanceWitnessClients.Create(options));
    }

    [Fact]
    public void RequiresAWriterCertificateWithItsPrivateKey()
    {
        using var fixture = new Fixture();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=offline-public-fixture", key, HashAlgorithmName.SHA256);
        var now = DateTimeOffset.UtcNow;
        using var publicOnly = request.Create(request.SubjectName, X509SignatureGenerator.CreateForECDsa(key), now.AddMinutes(-1),
            now.AddMinutes(5), [1]);

        Assert.Throws<ArgumentNullException>(() => IndependentGovernanceWitnessClients.Create(fixture.Options));
        Assert.Throws<ArgumentException>(() => IndependentGovernanceWitnessClients.Create(fixture.Options, publicOnly));
    }

    [Fact]
    public void RejectsUsingAResponseSigningKeyForWriterAuthentication()
    {
        using var fixture = new Fixture();
        using var responseSigner = CreateCertificate(fixture.TimeKey);

        Assert.Throws<ArgumentException>(() => IndependentGovernanceWitnessClients.Create(fixture.Options, responseSigner));
    }

    [Fact]
    public void RejectsCertificatesWithUsageRestrictionsThatExcludeClientAuthentication()
    {
        using var fixture = new Fixture();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var serverOnly = CreateCertificate(key, eku: "1.3.6.1.5.5.7.3.1");
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=offline-encipherment-fixture", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyEncipherment, critical: true));
        var now = DateTimeOffset.UtcNow;
        using var noDigitalSignature = request.CreateSelfSigned(now.AddMinutes(-1), now.AddMinutes(5));

        Assert.True(serverOnly.HasPrivateKey);
        Assert.True(noDigitalSignature.HasPrivateKey);
        Assert.Throws<ArgumentException>("writerCertificate", () => IndependentGovernanceWitnessClients.Create(fixture.Options, serverOnly));
        Assert.Throws<ArgumentException>("writerCertificate", () => IndependentGovernanceWitnessClients.Create(fixture.Options, noDigitalSignature));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("1.3.6.1.5.5.7.3.2")]
    [InlineData("2.5.29.37.0")]
    public void CompleteConfigurationSupportsSharedHistoryRolesAndBorrowsTheWriterCertificate(string? eku)
    {
        using var fixture = new Fixture();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var writer = CreateCertificate(key, X509KeyUsageFlags.DigitalSignature, eku);

        using (var clients = IndependentGovernanceWitnessClients.Create(fixture.Options, writer))
        {
            Assert.NotNull(clients);
            Assert.Equal(fixture.Journal, clients.JournalId);
            Assert.NotNull(clients.JournalWitness);
            Assert.Null(clients.RecoveryHistoryWitness);
        }

        Assert.True(writer.HasPrivateKey);
    }

    [Fact]
    public async Task ConfiguredRecoveryHistoryUsesThePinnedJournalAndAnIndependentSigningKey()
    {
        using var fixture = new Fixture();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var writer = CreateCertificate(key);
        var role = fixture.Options.HistoryEvidence! with { Endpoint = new Uri("https://witness.example.test/recovery-history") };
        var options = fixture.Options with { RecoveryHistory = role };
        using var clients = IndependentGovernanceWitnessClients.Create(options, writer)!;
        var recoveryHistory = Assert.IsAssignableFrom<IGovernanceEnvironmentHistoryWitness>(clients.RecoveryHistoryWitness);
        var foreign = fixture.Journal with { Generation = Guid.NewGuid() };
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAsync<InvalidDataException>(() => recoveryHistory.ObserveAsync(foreign, GovernanceJournalProtocol.Genesis,
            Guid.NewGuid(), cancelled.Token));

        Assert.Throws<ArgumentException>(() => IndependentGovernanceWitnessClients.Create(options with
        {
            RecoveryHistory = role with { SubjectPublicKeyInfoBase64 = fixture.Options.TimeEvidence!.SubjectPublicKeyInfoBase64 }
        }, writer));
        Assert.Throws<ArgumentException>(() => IndependentGovernanceWitnessClients.Create(options with
        {
            RecoveryHistory = role with { SubjectPublicKeyInfoBase64 = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) }
        }, writer));
    }

    [Fact]
    public async Task JournalWitnessRejectsAnUnconfiguredJournalBeforeUsingTheTransport()
    {
        using var fixture = new Fixture();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var writer = CreateCertificate(key);
        using var clients = IndependentGovernanceWitnessClients.Create(fixture.Options, writer)!;
        var other = fixture.Journal with { Generation = Guid.NewGuid() };
        var intent = new GovernanceWriteIntent(Guid.NewGuid(), GovernanceJournalProtocol.Genesis, "YQ");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<InvalidDataException>(() => clients.JournalWitness.ObserveAsync(other, null, cancellation.Token));
        await Assert.ThrowsAsync<InvalidDataException>(() => clients.JournalWitness.PrepareAsync(other, intent, cancellation.Token));
        await Assert.ThrowsAsync<InvalidDataException>(() => clients.JournalWitness.ConfirmAsync(other, intent, cancellation.Token));
    }

    [Fact]
    public async Task FormalRotationDecisionRequiresTheConfiguredAuthorityAndGeneration()
    {
        using var fixture = new Fixture();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var writer = CreateCertificate(key);
        using var clients = IndependentGovernanceWitnessClients.Create(fixture.Options, writer)!;
        var intent = new GovernanceWriteIntent(Guid.NewGuid(), GovernanceJournalProtocol.Genesis, "YQ");
        var schedule = Guid.NewGuid();
        var environment = Guid.NewGuid();
        var deadline = new DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        foreach (var journal in new[]
        {
            fixture.Journal with { AuthorityId = Guid.NewGuid() },
            fixture.Journal with { Generation = Guid.NewGuid() }
        })
            await Assert.ThrowsAsync<InvalidDataException>(() => clients.FormalRotationWitness.PrepareFormalRotationDecisionAsync(
                journal, intent, schedule, environment, deadline, "Complete", cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => clients.FormalRotationWitness.PrepareFormalRotationDecisionAsync(
            fixture.Journal, intent, schedule, environment, deadline, "Complete", cancellation.Token));
    }

    private static X509Certificate2 CreateCertificate(ECDsa key, X509KeyUsageFlags? keyUsage = null, string? eku = null)
    {
        var request = new CertificateRequest("CN=offline-writer-fixture", key, HashAlgorithmName.SHA256);
        if (keyUsage is { } flags) request.CertificateExtensions.Add(new X509KeyUsageExtension(flags, critical: true));
        if (eku is not null)
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid(eku) }, critical: true));
        var now = DateTimeOffset.UtcNow;
        return request.CreateSelfSigned(now.AddMinutes(-1), now.AddMinutes(5));
    }

    private sealed class Fixture : IDisposable
    {
        public ECDsa TimeKey { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public GovernanceJournalId Journal { get; } = new(Guid.NewGuid(), Guid.NewGuid());
        public IndependentGovernanceWitnessOptions Options { get; }

        public Fixture()
        {
            using var historyKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var history = new IndependentWitnessRole(new Uri("https://witness.example.test/history"), "witness", "history-key",
                Convert.ToBase64String(historyKey.ExportSubjectPublicKeyInfo()));
            Options = new()
            {
                Enabled = true,
                TimeEvidence = new(new Uri("https://witness.example.test/time"), "witness", "time-key",
                    Convert.ToBase64String(TimeKey.ExportSubjectPublicKeyInfo())),
                HistoryEvidence = history,
                GovernanceJournal = history,
                JournalId = Journal
            };
        }

        public void Dispose() => TimeKey.Dispose();
    }
}
