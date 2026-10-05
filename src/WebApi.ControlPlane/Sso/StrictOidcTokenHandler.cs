using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using WebApi.Infrastructure.Sso;
namespace WebApi.ControlPlane.Sso;
// All cryptographic validation is delegated to Microsoft's token handler.
public sealed class StrictOidcTokenHandler(IOidcMetadataClient metadata,string issuer,IHttpContextAccessor accessor):JsonWebTokenHandler
{
    public override async Task<TokenValidationResult> ValidateTokenAsync(string token,TokenValidationParameters parameters)
    {
        try
        {
            var parsed=new JsonWebToken(token);
            if(parsed.TryGetHeaderValue<string>("jku",out _)||parsed.TryGetHeaderValue<string>("x5u",out _))
                return Invalid();
            var strict=parameters.Clone();strict.RequireSignedTokens=true;strict.ConfigurationManager=null;
            var ct=accessor.HttpContext?.RequestAborted??CancellationToken.None;
            strict.IssuerSigningKeys=(await metadata.GetAsync(issuer,false,ct)).Configuration.SigningKeys;
            var result=await base.ValidateTokenAsync(token,strict);
            if(result.Exception is not SecurityTokenSignatureKeyNotFoundException)return result;
            strict.IssuerSigningKeys=(await metadata.GetAsync(issuer,true,ct)).Configuration.SigningKeys;
            return await base.ValidateTokenAsync(token,strict);
        }
        catch(Exception exception) when(exception is ArgumentException or WebApi.Contracts.Common.ApiException)
        {return Invalid();}
    }
    private static TokenValidationResult Invalid()=>new(){IsValid=false,Exception=new SecurityTokenException("OIDC token validation failed.")};
}
