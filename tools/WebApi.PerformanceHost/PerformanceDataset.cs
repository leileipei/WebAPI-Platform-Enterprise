using System.Security.Cryptography;
using System.Text;
using WebApi.Contracts.Runtime;
using WebApi.Contracts.Policies;
using WebApi.Domain.Policies;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Security;
namespace WebApi.PerformanceHost;
public sealed record DatasetSpec(int Scale,int Seed,DateTimeOffset WindowStart,DateTimeOffset WindowEnd);
public sealed record DatasetManifest(int Scale,int Seed,IReadOnlyDictionary<string,long> Counts,string BusinessFingerprint,IReadOnlyDictionary<string,string> SnapshotHashes,bool Validated);
public static partial class PerformanceDataset
{
    public static Guid ResourceId(int seed,string kind,int index)=>new(SHA256.HashData(Encoding.UTF8.GetBytes($"{seed}:{kind}:{index}")).AsSpan(0,16));
    public static RuntimeSnapshot BuildSnapshot(DatasetSpec spec,AuthenticationMode mode,string algorithm,PerformanceJwtMaterial keys)
    {
        if(spec.Scale is not (100 or 1000 or 10000)||spec.WindowEnd<=spec.WindowStart)throw new ArgumentException("Invalid performance scale or window.");
        var snapshot=SnapshotLowerBound(spec);
        var apps=snapshot.Applications.Select((a,i)=>a with {Credentials=a.Credentials.Select((c,j)=>c with {AccessKey=$"ak_{i*2+j:D5}",Hash=ApiKeySecret.Hash(keys.CredentialSecret(i*2+j))}).ToArray()}).ToArray();
        if(mode!=AuthenticationMode.JWT)return snapshot with {Applications=apps,Routes=snapshot.Routes.Select(r=>r with {RequireApiKey=mode==AuthenticationMode.ApiKey}).ToArray()};
        var policies=apps.Chunk(128).Select((group,i)=>{
            var mappings=group.Select((a,j)=>new JwtApplicationMapping($"app{i*128+j:D5}",a.Id)).ToArray();
            var config=keys.Configuration(algorithm,mappings);var source=ResourceId(spec.Seed,"authentication-"+algorithm,i);
            return new RuntimePolicy(RuntimePolicyIdentity.Create(source,1,"authentication",config),"authentication",config,source,1);
        }).ToArray();
        var routes=snapshot.Routes.Select((r,i)=>r with {RequireApiKey=false,AuthenticationMode=AuthenticationMode.JWT,PolicyBindings=new[]{new RuntimePolicyBinding(policies[(i%apps.Length)/128].Id,0)}}).ToArray();
        return snapshot with {SchemaVersion="2.2",Applications=apps,Routes=routes,Policies=policies};
    }
    // A genuine, minimally sized ordinary wire snapshot checks feasibility before seeding a database.
    public static RuntimeSnapshot SnapshotLowerBound(DatasetSpec spec)
    {
        Guid Id(string kind,int i)=>ResourceId(spec.Seed,kind,i);
        var env=Id("environment",0);var clusterCount=spec.Scale/10;var applicationCount=spec.Scale==10000?5000:spec.Scale;
        var clusters=Enumerable.Range(0,clusterCount).Select(i=>new RuntimeCluster(Id("cluster",i),env,"RoundRobin",false,"/",30,
            [new(Id("destination",i*2),"http://backend:8080/",1),new(Id("destination",i*2+1),"http://backend:8080/",1)])).ToArray();
        var routes=Enumerable.Range(0,spec.Scale).Select(i=>new RuntimeRoute(Id("route",i),Id("api",i%applicationCount),Id("version",i%applicationCount),clusters[i%clusterCount].Id,$"/r/{i}",["GET"],0,30000,true)).ToArray();
        var hash=ApiKeySecret.Hash("private-fixture-secret");
        var apps=Enumerable.Range(0,applicationCount).Select(i=>new RuntimeApplication(Id("app",i),"Active",
            Enumerable.Range(0,2).Select(j=>new RuntimeCredential(Id("credential",i*2+j),"k"+(i*2+j),hash,"Active",spec.WindowStart,spec.WindowEnd)).ToArray(),
            Enumerable.Range(0,8).Select(j=>new RuntimePermission(Id("api",(i+j)%applicationCount),spec.WindowStart,null)).ToArray())).ToArray();
        return new("2.0",env,1,spec.WindowStart,routes,clusters,[],apps);
    }
}
