using System.Security.Cryptography;
using WebApi.Contracts.Common;
using WebApi.Contracts.Policies;
using WebApi.Domain.Runtime;
namespace WebApi.PerformanceHost;
public sealed record GatewayFixtureManifest(DatasetSpec Spec,IReadOnlyDictionary<string,long> Counts,IReadOnlyDictionary<string,string> SnapshotHashes,IReadOnlyDictionary<string,long> SnapshotBytes,string Fingerprint);
public static class PerformanceFixtureFiles
{
    public static async Task<GatewayFixtureManifest> PrepareAsync(DatasetSpec spec,string directory,CancellationToken ct=default)
    {
        if(spec.WindowEnd-spec.WindowStart>TimeSpan.FromHours(24))throw new ArgumentException("JWT fixture window must not exceed 24 hours.");
        using var keys=PerformanceJwtMaterial.LoadOrCreate(directory);
        var hashes=new Dictionary<string,string>();var sizes=new Dictionary<string,long>();
        foreach(var (profile,mode,algorithm) in new[]{("anonymous",AuthenticationMode.Anonymous,"RS256"),("apikey",AuthenticationMode.ApiKey,"RS256"),("rs256",AuthenticationMode.JWT,"RS256"),("es256",AuthenticationMode.JWT,"ES256"),("no-grant",AuthenticationMode.JWT,"RS256")}) {
            var snapshot=PerformanceDataset.BuildSnapshot(spec,mode,algorithm,keys);
            if(profile=="no-grant")snapshot=snapshot with {Applications=snapshot.Applications.Select(a=>a with {Permissions=a.Permissions.Select(p=>p with {ExpiresAt=spec.WindowStart.AddSeconds(1)}).ToArray()}).ToArray()};
            var bytes=CanonicalJson.Serialize(snapshot);SnapshotValidator.ParsePayload(bytes,snapshot.EnvironmentId);
            hashes[profile]=Convert.ToHexStringLower(SHA256.HashData(bytes));sizes[profile]=bytes.LongLength;
            await WritePrivate(Path.Combine(directory,profile+".json"),bytes,ct);
        }
        var appCount=spec.Scale==10000?5000:spec.Scale;
        var rsTokens=Enumerable.Range(0,appCount).Select(i=>keys.Token("RS256",$"app{i:D5}",spec.WindowStart,spec.WindowEnd)).ToArray();
        var esTokens=Enumerable.Range(0,appCount).Select(i=>keys.Token("ES256",$"app{i:D5}",spec.WindowStart,spec.WindowEnd)).ToArray();
        var unknown=keys.Token("RS256","unknown-app",spec.WindowStart,spec.WindowEnd);
        var requests=new Dictionary<string,IReadOnlyList<FixtureRequest>>();
        foreach(var auth in new[]{"anonymous","api-key","missing-key","rs256","es256","wrong-secret","no-mapping","no-grant"}) {
            var count=auth=="api-key"?spec.Scale*2:spec.Scale;
            requests[auth]=Enumerable.Range(0,count).Select(i=>{
                var route=auth=="api-key"?i/2:i;var app=route%appCount;var headers=new Dictionary<string,string>();
                switch(auth) {
                    case "api-key":headers["X-API-Key"]=$"ak_{app*2+i%2:D5}."+keys.CredentialSecret(app*2+i%2);break;
                    case "missing-key":headers["X-API-Key"]="ak_not_present.invalid-secret";break;
                    case "wrong-secret":headers["X-API-Key"]=$"ak_{app*2:D5}.invalid-secret";break;
                    case "rs256":case "no-grant":headers["Authorization"]="Bearer "+rsTokens[app];break;
                    case "es256":headers["Authorization"]="Bearer "+esTokens[app];break;
                    case "no-mapping":headers["Authorization"]="Bearer "+unknown;break;
                }
                return new FixtureRequest($"/r/{route}",headers,auth is "missing-key" or "wrong-secret" or "no-mapping"?401:auth=="no-grant"?403:200);
            }).ToArray();
        }
        var requestBytes=CanonicalJson.Serialize(requests);await WritePrivate(Path.Combine(directory,"requests.json"),requestBytes,ct);
        var counts=new Dictionary<string,long> { ["routes"]=spec.Scale,["clusters"]=spec.Scale/10,["applications"]=appCount,["credentials"]=appCount*2,["windows"]=appCount*8 };
        var fingerprint=Convert.ToHexStringLower(SHA256.HashData(CanonicalJson.Serialize(new {spec,counts,hashes,sizes,requestHash=Convert.ToHexStringLower(SHA256.HashData(requestBytes))})));
        var manifest=new GatewayFixtureManifest(spec,counts,hashes,sizes,fingerprint);await WritePrivate(Path.Combine(directory,"gateway-manifest.json"),CanonicalJson.Serialize(manifest),ct);return manifest;
    }
    private static async Task WritePrivate(string path,byte[] bytes,CancellationToken ct) {
        await using var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None,81920,FileOptions.Asynchronous);
        if(!OperatingSystem.IsWindows())File.SetUnixFileMode(path,UnixFileMode.UserRead|UnixFileMode.UserWrite);await file.WriteAsync(bytes,ct);
    }
    private sealed record FixtureRequest(string Path,IReadOnlyDictionary<string,string> Headers,int Expected);
}
