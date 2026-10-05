using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.Extensions.Options;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using WebApi.Contracts.Common;
namespace WebApi.Infrastructure.Sso;
public sealed record OidcMetadataSnapshot(OpenIdConnectConfiguration Configuration,DateTimeOffset FetchedAt,IReadOnlyList<string> SigningAlgorithms,bool CodeSupported,bool PkceS256Supported,bool ClientSecretPostSupported);
public interface IOidcMetadataClient {Task<OidcMetadataSnapshot> GetAsync(string issuer,bool forceRefresh=false,CancellationToken ct=default);}
public sealed class OidcMetadataClient(IOptions<SsoOptions> options,IOidcAddressPolicy policy,TimeProvider clock,HttpClient client):IOidcMetadataClient,IDisposable
{
    private readonly Dictionary<string,OidcMetadataSnapshot> _cache=new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate=new(1,1);
    public async Task<OidcMetadataSnapshot> GetAsync(string issuer,bool forceRefresh=false,CancellationToken ct=default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if(!forceRefresh&&_cache.TryGetValue(issuer,out var cached)&&clock.GetUtcNow()-cached.FetchedAt<TimeSpan.FromMinutes(5))return cached;
            if(!Uri.TryCreate(issuer,UriKind.Absolute,out var issuerUri)||issuerUri.Query.Length!=0||issuerUri.Fragment.Length!=0)throw Invalid();
            var document=await GetDocumentAsync(new Uri(issuer.TrimEnd('/')+"/.well-known/openid-configuration"),ct);
            using var json=JsonDocument.Parse(document);
            var root=json.RootElement;
            if(root.ValueKind!=JsonValueKind.Object||!root.TryGetProperty("issuer",out var actual)||actual.GetString()!=issuer)throw Invalid();
            var configuration=OpenIdConnectConfiguration.Create(Encoding.UTF8.GetString(document));
            foreach(var endpoint in new[]{configuration.AuthorizationEndpoint,configuration.TokenEndpoint,configuration.JwksUri})
            {
                if(!Uri.TryCreate(endpoint,UriKind.Absolute,out var endpointUri))throw Invalid();
                await policy.ResolveAllowedAsync(endpointUri,ct);
            }
            var advertised=Values(root,"id_token_signing_alg_values_supported");
            var algorithms=advertised.Intersect(options.Value.AllowedSigningAlgorithms,StringComparer.Ordinal)
                .Intersect(new[]{"RS256","PS256","ES256"},StringComparer.Ordinal).Distinct().ToArray();
            if(algorithms.Length==0)throw Invalid();
            var keysDocument=await GetDocumentAsync(new Uri(configuration.JwksUri),ct);
            using var keysJson=JsonDocument.Parse(keysDocument);
            if(!keysJson.RootElement.TryGetProperty("keys",out var entries)||entries.ValueKind!=JsonValueKind.Array||entries.GetArrayLength() is <1 or >32)throw Invalid();
            foreach(var key in entries.EnumerateArray())
                if(key.ValueKind!=JsonValueKind.Object||new[]{"d","p","q","dp","dq","qi","oth","k"}.Any(p=>key.TryGetProperty(p,out _)))throw Invalid();
            var keySet=new JsonWebKeySet(Encoding.UTF8.GetString(keysDocument));
            foreach(var key in keySet.Keys)
            {
                if(key.Kty is not ("RSA" or "EC")||!string.IsNullOrEmpty(key.Use)&&key.Use!="sig"||
                   !string.IsNullOrEmpty(key.Alg)&&!algorithms.Contains(key.Alg,StringComparer.Ordinal))continue;
                if(key.Kty=="RSA"&&(string.IsNullOrEmpty(key.N)||string.IsNullOrEmpty(key.E)))throw Invalid();
                if(key.Kty=="EC"&&(key.Crv!="P-256"||string.IsNullOrEmpty(key.X)||string.IsNullOrEmpty(key.Y)))throw Invalid();
                configuration.SigningKeys.Add(key);
            }
            if(configuration.SigningKeys.Count==0)throw Invalid();
            configuration.JsonWebKeySet=keySet;
            var snapshot=new OidcMetadataSnapshot(configuration,clock.GetUtcNow(),algorithms,
                Values(root,"response_types_supported").Contains("code"),
                Values(root,"code_challenge_methods_supported").Contains("S256"),
                Values(root,"token_endpoint_auth_methods_supported").Contains("client_secret_post"));
            if(_cache.Count>=128&&!_cache.ContainsKey(issuer))_cache.Remove(_cache.MinBy(entry=>entry.Value.FetchedAt).Key);
            _cache[issuer]=snapshot;return snapshot;
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
        catch(ApiException){throw;}
        catch(Exception ex) when(ex is JsonException or ArgumentException or InvalidOperationException or HttpRequestException)
        {throw Invalid();}
        finally{_gate.Release();}
    }
    private async Task<byte[]> GetDocumentAsync(Uri uri,CancellationToken ct)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await policy.ResolveAllowedAsync(uri,timeout.Token);
            using var response=await client.GetAsync(uri,HttpCompletionOption.ResponseHeadersRead,timeout.Token);
            if(!response.IsSuccessStatusCode)throw Invalid();
            return await SsoBoundedResponse.ReadAsync(response,256*1024,timeout.Token);
        }
        catch(OperationCanceledException) when(!ct.IsCancellationRequested){throw new ApiException(422,"sso_endpoint_timeout","身份服务请求超时。");}
    }
    private static string[] Values(JsonElement root,string name)
    {
        if(!root.TryGetProperty(name,out var value)||value.ValueKind!=JsonValueKind.Array)return [];
        return value.EnumerateArray().Select(item=>item.GetString()??"").ToArray();
    }
    private static ApiException Invalid()=>new(422,"sso_metadata_invalid","身份服务元数据未通过验证。");
    public void Dispose(){client.Dispose();_gate.Dispose();}
}
