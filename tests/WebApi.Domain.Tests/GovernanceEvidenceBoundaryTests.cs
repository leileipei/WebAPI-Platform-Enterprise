using WebApi.Contracts.Common;
using WebApi.Infrastructure.CredentialSecurity;
using WebApi.Infrastructure.Gateway;
using Xunit;

namespace WebApi.Domain.Tests;

public sealed class GovernanceEvidenceBoundaryTests
{
    [Fact]
    public void UntrustedGenesisFixtureIsAbsentFromTheProductionAssembly()
    {
        Assert.DoesNotContain(typeof(SecurityLeaseIssuer).Assembly.GetTypes(), type => type.Name == "UntrustedGenesisLeaseMaterialBuilder");
    }

    [Fact]
    public async Task IssuerWithoutIndependentEvidenceRejectsBeforeSigningOrDatabaseAccess()
    {
        // Deliberately unavailable dependencies prove that the public entry point
        // cannot fall through to local signing, database time or a local ledger.
        var issuer = new SecurityLeaseIssuer(null!, null!, null!, new SecuritySigningSettings(null, null));
        var error = await Assert.ThrowsAsync<ApiException>(() =>
            issuer.IssueAsync(new NodeIdentity(Guid.NewGuid(), Guid.NewGuid(), "test"), Guid.NewGuid()));

        Assert.Equal(503, error.Status);
        Assert.Equal("security_evidence_unavailable", error.Code);
    }
}
