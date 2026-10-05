using WebApi.Contracts.Sso;
namespace WebApi.Infrastructure.Sso;
public sealed record SsoProviderSnapshot(Guid Id,Guid? OrganizationId,string Issuer,string ClientId,string SecretRef,IReadOnlyList<string> Scopes,SsoClaimMapping ClaimMapping,long Revision,long AuthRevision);
public sealed record PendingSsoLogin(Guid AttemptId,SsoProviderSnapshot Provider,string ReturnPath,DateTimeOffset ExpiresAt);
// Internal protocol adapter input; never exposed as an HTTP request contract.
public sealed record ValidatedOidcIdentity
{
    internal ValidatedOidcIdentity(string issuer,string subject,IReadOnlyDictionary<string,string> claims)
    {
        Issuer=issuer;Subject=subject;Claims=new System.Collections.ObjectModel.ReadOnlyDictionary<string,string>(claims.ToDictionary(x=>x.Key,x=>x.Value,StringComparer.Ordinal));
    }
    public string Issuer {get;}
    public string Subject {get;}
    public IReadOnlyDictionary<string,string> Claims {get;}
}
public sealed record SsoSessionGrant(Guid UserId,string SecurityStamp,Guid ProviderId,long AuthRevision,Guid BindingId,int TtlMinutes,string ReturnPath);
