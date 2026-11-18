using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Contracts.Applications;
using WebApi.Contracts.Common;
using WebApi.Domain.CredentialSecurity;
using WebApi.Infrastructure.CredentialSecurity;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;

namespace WebApi.Integration.Tests;

public sealed class CredentialGovernanceEndpointTests
{
    [Fact]
    public async Task ExecutableResolvesProvisionedWitnessAndKeepsWritesUnavailableWithoutIngressProof()
    {
        await using var fixture = new ApiFixture();
        Directory.CreateDirectory(fixture.LoginProtectionDirectory);
        var certificateFile = Path.Combine(fixture.LoginProtectionDirectory, "writer.pfx");
        using var writerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var certificateRequest = new CertificateRequest("CN=isolated writer", writerKey, HashAlgorithmName.SHA256);
        certificateRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.2") }, critical: true));
        using var certificate = certificateRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        await File.WriteAllBytesAsync(certificateFile, certificate.Export(X509ContentType.Pkcs12));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(certificateFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        using var timeKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var historyKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await fixture.InitializeAsync(builder =>
        {
            const string prefix = "CredentialGovernance:";
            builder.Configuration[prefix + "Enabled"] = "true";
            builder.Configuration[prefix + "WriterCertificateFile"] = certificateFile;
            builder.Configuration[prefix + "JournalId:AuthorityId"] = Guid.NewGuid().ToString();
            builder.Configuration[prefix + "JournalId:Generation"] = Guid.NewGuid().ToString();
            foreach (var (role, name, key) in new[] { ("TimeEvidence", "clock", timeKey), ("HistoryEvidence", "history", historyKey),
                         ("GovernanceJournal", "journal", historyKey) })
            {
                builder.Configuration[prefix + role + ":Endpoint"] = $"https://{name}.example.test/authority";
                builder.Configuration[prefix + role + ":SourceId"] = name;
                builder.Configuration[prefix + role + ":KeyId"] = name + "-key";
                builder.Configuration[prefix + role + ":SubjectPublicKeyInfoBase64"] = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
            }
        });

        using var services = fixture.Services();
        Assert.NotNull(services.ServiceProvider.GetRequiredService<PostgresGovernanceJournalEnrollment>());
        var error = Assert.Throws<ApiException>(() => services.ServiceProvider.GetRequiredService<PostgresCredentialRevocationService>());
        Assert.Equal("credential_governance_unavailable", error.Code);
    }

    [Fact]
    public async Task ImmediateRevocationChecksDedicatedPermissionAndScopeBeforeUnavailableWitness()
    {
        await using var fixture = new ApiFixture();
        await fixture.InitializeAsync();
        await fixture.SeedConsumerAsync();
        using var login = await fixture.LoginAsync();
        login.EnsureSuccessStatusCode();
        var credential = await CreateCredentialAsync(fixture);
        var path = $"/api/v1/credentials/{credential.Id}/revoke";
        var request = new RevokeCredentialRequest("compromised");

        using var denied = await fixture.WriteAsync(HttpMethod.Post, path, request, $"\"{credential.Revision}\"");
        await RequireProblemAsync(denied, HttpStatusCode.Forbidden, "permission_denied");
        await AddPermissionAsync(fixture, "credential.revoke");
        await SetScopeModeAsync(fixture, "read");
        using var readOnly = await fixture.WriteAsync(HttpMethod.Post, path, request, $"\"{credential.Revision}\"");
        await RequireProblemAsync(readOnly, HttpStatusCode.Forbidden, "scope_denied");
        await SetScopeModeAsync(fixture, "read_write");
        using var noProof = await fixture.WriteAsync(HttpMethod.Post, path, request, $"\"{credential.Revision}\"");
        await RequireProblemAsync(noProof, HttpStatusCode.ServiceUnavailable, "credential_governance_unavailable");
        Assert.True(noProof.Headers.CacheControl?.NoStore);

        await using var db = fixture.Context();
        var record = await db.Set<ApplicationCredential>().SingleAsync(x => x.Id == credential.Id);
        Assert.Equal(credential.Revision, record.Revision);
        Assert.NotEqual("Revoked", record.Status);
        Assert.Empty(await db.Set<SecurityOperation>().ToArrayAsync());
    }

    [Fact]
    public async Task PublicRevocationDoesNotAcceptFreeTextAsExternalRecoveryMaterial()
    {
        await using var fixture = new ApiFixture();
        await fixture.InitializeAsync();
        await fixture.SeedConsumerAsync();
        using var login = await fixture.LoginAsync();
        login.EnsureSuccessStatusCode();
        var credential = await CreateCredentialAsync(fixture);
        await AddPermissionAsync(fixture, "credential.revoke");

        using var invalid = await fixture.WriteAsync(HttpMethod.Post, $"/api/v1/credentials/{credential.Id}/revoke",
            new RevokeCredentialRequest("arbitrary text can contain a pasted secret"), $"\"{credential.Revision}\"");
        await RequireProblemAsync(invalid, HttpStatusCode.UnprocessableEntity, "revocation_reason_invalid");
        Assert.DoesNotContain("pasted secret", await invalid.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task EnrollmentRequiresPlatformRecoveryPermissionAndReverifiesAnExactReplay()
    {
        await using var fixture = new ApiFixture();
        var journal = new GovernanceJournalId(Guid.NewGuid(), Guid.NewGuid());
        var observer = new GenesisObserver(journal);
        await fixture.InitializeAsync(builder => builder.Services.AddSingleton(
            new PostgresGovernanceJournalEnrollment(fixture.Database.ConnectionString, journal, observer)));
        await fixture.SeedScopeAsync(platformAdmin: true);
        using var login = await fixture.LoginAsync();
        login.EnsureSuccessStatusCode();
        const string path = "/api/v1/credential-governance/journal/enroll";
        using var denied = await fixture.WriteAsync(HttpMethod.Post, path);
        await RequireProblemAsync(denied, HttpStatusCode.Forbidden, "permission_denied");
        Assert.Equal(0, observer.Calls);
        await AddPermissionAsync(fixture, "credential.governance.recover");
        using var first = await fixture.WriteAsync(HttpMethod.Post, path);
        first.EnsureSuccessStatusCode();
        Assert.True((await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("created").GetBoolean());
        using var repeated = await fixture.WriteAsync(HttpMethod.Post, path);
        repeated.EnsureSuccessStatusCode();
        Assert.False((await repeated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("created").GetBoolean());
        Assert.Equal(2, observer.Calls);
    }

    [Fact]
    public async Task SessionInvalidatedWhileWaitingForWriterLockCannotEnrollOrContactWitness()
    {
        await using var fixture = new ApiFixture();
        var journal = new GovernanceJournalId(Guid.NewGuid(), Guid.NewGuid());
        var observer = new GenesisObserver(journal);
        await fixture.InitializeAsync(builder => builder.Services.AddSingleton(
            new PostgresGovernanceJournalEnrollment(fixture.Database.ConnectionString, journal, observer)));
        await fixture.SeedScopeAsync(platformAdmin: true);
        await AddPermissionAsync(fixture, "credential.governance.recover");
        using var login = await fixture.LoginAsync();
        login.EnsureSuccessStatusCode();

        await using var db = fixture.Context();
        await using var writer = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SELECT pg_catalog.pg_advisory_xact_lock(8901202)");
        var request = fixture.WriteAsync(HttpMethod.Post, "/api/v1/credential-governance/journal/enroll");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!await db.Database.SqlQueryRaw<bool>("""
            SELECT EXISTS (SELECT FROM pg_catalog.pg_locks
             WHERE locktype='advisory' AND NOT granted AND classid=0 AND objid=8901202 AND objsubid=1
              AND database=(SELECT oid FROM pg_catalog.pg_database WHERE datname=current_database())) AS "Value"
            """).SingleAsync(deadline.Token))
        {
            Assert.False(request.IsCompleted);
            await Task.Delay(20, deadline.Token);
        }
        var account = await db.Set<UserRecord>().SingleAsync(x => x.Id == fixture.User.Id);
        account.SecurityStamp = Guid.NewGuid().ToString("N");
        await db.SaveChangesAsync();
        await writer.CommitAsync();

        using var rejected = await request;
        await RequireProblemAsync(rejected, HttpStatusCode.Unauthorized, "authentication_required");
        Assert.Equal(0, observer.Calls);
    }

    private static async Task<CredentialDto> CreateCredentialAsync(ApiFixture fixture)
    {
        var now = DateTimeOffset.UtcNow;
        using var response = await fixture.WriteAsync(HttpMethod.Post, $"/api/v1/applications/{fixture.Application.Id}/credentials",
            new CredentialCreateRequest(now.AddMinutes(-1), now.AddDays(1)));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CredentialCreateResponse>())!.Credential;
    }

    private static async Task AddPermissionAsync(ApiFixture fixture, string code)
    {
        await using var db = fixture.Context();
        var role = await db.Set<UserRole>().SingleAsync(x => x.UserId == fixture.User.Id);
        var permission = await db.Set<Permission>().SingleOrDefaultAsync(x => x.Code == code);
        if (permission is null)
        {
            permission = new Permission { Code = code, Module = "credential", Name = code };
            db.Add(permission);
        }
        db.Add(new RolePermission { RoleId = role.RoleId, PermissionId = permission.Id });
        await db.SaveChangesAsync();
    }

    private static async Task SetScopeModeAsync(ApiFixture fixture, string mode)
    {
        await using var db = fixture.Context();
        var scope = await db.Set<UserProjectScope>().SingleAsync(x => x.UserId == fixture.User.Id);
        scope.AccessMode = mode;
        await db.SaveChangesAsync();
    }

    private static async Task RequireProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(code, problem.GetProperty("code").GetString());
    }

    private sealed class GenesisObserver(GovernanceJournalId journal) : IGovernanceJournalWitness
    {
        public int Calls { get; private set; }

        public Task<GovernanceWitnessState> ObserveAsync(GovernanceJournalId id, Guid? operationId, CancellationToken ct)
        {
            Assert.Equal(journal, id);
            Assert.Null(operationId);
            Calls++;
            return Task.FromResult(new GovernanceWitnessState(GovernanceJournalProtocol.Genesis, null, null));
        }

        public Task<GovernanceWitnessState> PrepareAsync(GovernanceJournalId id, GovernanceWriteIntent intent, CancellationToken ct) =>
            throw new InvalidOperationException("Enrollment must not prepare a witness write.");

        public Task<GovernanceWitnessState> ConfirmAsync(GovernanceJournalId id, GovernanceWriteIntent intent, CancellationToken ct) =>
            throw new InvalidOperationException("Enrollment must not confirm a witness write.");
    }
}
