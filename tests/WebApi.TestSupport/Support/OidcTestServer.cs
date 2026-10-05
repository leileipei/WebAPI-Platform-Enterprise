using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
namespace WebApi.Integration.Tests.Support;
// Synthetic protocol fixtures verify rejection rules, not real enterprise integration.
public sealed class OidcTestServer:IAsyncDisposable
{
    private readonly RSA key=RSA.Create(2048);
    private readonly RSA otherKey=RSA.Create(2048);
    private readonly Dictionary<string,(string Client,string Nonce,string Challenge)> codes=new();
    private WebApplication app=null!;
    public string Origin {get;private set;}="";
    public string Issuer=>Origin+"/realm";
    public string Secret {get;}=Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
    public string Failure {get;set;}="";
    public string PublicKid {get;set;}="fixture";
    public int Redemptions {get;private set;}
    public int JwksRequests {get;private set;}
    public bool PkceValidated {get;private set;}
    public bool ClientSecretPostValidated {get;private set;}
    public bool DelayToken {get;set;}
    public TaskCompletionSource TokenStarted {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource TokenRelease {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async Task InitializeAsync()
    {
        var builder=WebApplication.CreateBuilder(["--environment","Development"]);builder.WebHost.UseUrls("http://127.0.0.1:0");builder.Logging.ClearProviders();
        app=builder.Build();
        app.MapGet("/realm/.well-known/openid-configuration",()=>Results.Json(new{issuer=Issuer,authorization_endpoint=Origin+"/authorize",token_endpoint=Origin+"/token",jwks_uri=Origin+"/keys",
            response_types_supported=new[]{"code"},code_challenge_methods_supported=new[]{"S256"},token_endpoint_auth_methods_supported=new[]{"client_secret_post"},id_token_signing_alg_values_supported=new[]{"RS256"}}));
        app.MapGet("/keys",()=>{JwksRequests++;var parameters=key.ExportParameters(false);return Results.Json(new{keys=new[]{new{kty="RSA",kid=PublicKid,use="sig",alg="RS256",n=Base64UrlEncoder.Encode(parameters.Modulus!),e=Base64UrlEncoder.Encode(parameters.Exponent!)}}});});
        app.MapGet("/authorize",(HttpContext context)=>{
            var query=context.Request.Query;
            if(query["response_type"]!="code"||query["response_mode"]!="query"||query["code_challenge_method"]!="S256")return Results.BadRequest();
            var code=Guid.NewGuid().ToString("N");lock(codes)codes[code]=(query["client_id"].ToString(),query["nonce"].ToString(),query["code_challenge"].ToString());
            var state=query["state"].ToString();if(Failure=="state")state+="invalid";
            if(Failure=="cancelled")return Results.Redirect(query["redirect_uri"]+"?error=access_denied&state="+Uri.EscapeDataString(state));
            return Results.Redirect(query["redirect_uri"]+"?code="+Uri.EscapeDataString(code)+"&state="+Uri.EscapeDataString(state));
        });
        app.MapPost("/token",async(HttpContext context)=>{
            var form=await context.Request.ReadFormAsync();(string Client,string Nonce,string Challenge) stored;
            lock(codes){if(!codes.Remove(form["code"].ToString(),out stored))return Results.BadRequest();}
            Redemptions++;var challenge=Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"].ToString())));
            PkceValidated=challenge==stored.Challenge;ClientSecretPostValidated=form["client_secret"]==Secret&&form["client_id"]==stored.Client;
            if(!PkceValidated||!ClientSecretPostValidated)return Results.BadRequest();
            if(DelayToken){TokenStarted.TrySetResult();await TokenRelease.Task.WaitAsync(context.RequestAborted);}
            var now=DateTime.UtcNow;var signingKey=new RsaSecurityKey(Failure=="signature"?otherKey:key){KeyId=Failure=="unknownKid"?"unknown":Failure=="rotation"?"rotated":"fixture"};
            SigningCredentials? signing=Failure=="none"?null:Failure=="HS256"?new SigningCredentials(new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(64)),SecurityAlgorithms.HmacSha256):new(signingKey,SecurityAlgorithms.RsaSha256);
            var claims=new Dictionary<string,object>{{"sub","SyntheticSubject"},{"nonce",Failure=="nonce"?"invalid":stored.Nonce},{"name","身份源显示名"},{"email","fixture@example.test"},{"roles",new[]{"PlatformAdmin"}}};
            if(Failure=="azp")claims["azp"]="wrong-client";
            if(Failure=="mappingType")claims["name"]=new[]{"not-a-string"};
            var descriptor=new SecurityTokenDescriptor{Issuer=Failure=="issuer"?Origin+"/other":Issuer,Audience=Failure=="aud"?"wrong-audience":stored.Client,Claims=claims,IssuedAt=now.AddMinutes(-2),
                NotBefore=now.AddMinutes(-2),Expires=Failure=="expired"?now.AddMinutes(-1):now.AddMinutes(5),SigningCredentials=signing};
            if(Failure is "jku" or "x5u")descriptor.AdditionalHeaderClaims=new Dictionary<string,object>{{Failure,"http://169.254.169.254/keys"}};
            var token=new JsonWebTokenHandler().CreateToken(descriptor);
            return Results.Json(new{access_token="synthetic-opaque-token",token_type="Bearer",expires_in=300,id_token=token});
        });
        await app.StartAsync();var address=app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        Origin=new UriBuilder(address){Host="localhost"}.Uri.GetLeftPart(UriPartial.Authority);
    }
    public async ValueTask DisposeAsync(){if(app is not null){await app.StopAsync();await app.DisposeAsync();}key.Dispose();otherKey.Dispose();}
}
