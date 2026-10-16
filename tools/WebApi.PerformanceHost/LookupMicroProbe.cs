using WebApi.Contracts.Runtime;
using WebApi.Contracts.Policies;
using WebApi.Domain.Policies;
using WebApi.Gateway.Configuration;
using System.Diagnostics;
using System.Collections.Frozen;
using System.Linq.Expressions;
using System.Security.Cryptography;
namespace WebApi.PerformanceHost;
public sealed record LookupMeasurement(string Mode,string Operation,string Position,int Iterations,long ElapsedTicks,long AllocatedBytes,int Hits,string Backend="legacy-array");
public static class LookupMicroProbe
{
    public static string ClassifyContainers(IReadOnlyList<object> containers)
    {
        if(containers.Count!=7)throw new InvalidDataException("Seven actual runtime containers required.");
        (Type Key,Type Value)[] expected=[(typeof(Guid),typeof(RuntimeRoute)),(typeof(Guid),typeof(RuntimeCluster)),(typeof(Guid),typeof(RuntimeApplication)),(typeof(string),typeof(RuntimeCredentialEntry)),(typeof(Guid),typeof(RuntimeAuthenticationEntry)),(typeof((Guid,string)),typeof(Guid)),(typeof((Guid,Guid)),typeof(IReadOnlyList<RuntimePermission>))];
        string? backend=null;
        for(var index=0;index<containers.Count;index++){
            var type=containers[index]?.GetType()??throw new InvalidDataException("Missing runtime container.");Type? dictionary=null;string current;
            if(type.IsGenericType&&type.GetGenericTypeDefinition().FullName=="WebApi.Gateway.Configuration.RuntimeLinearLookupMap`2"&&type.Assembly==typeof(RuntimeLookupIndex).Assembly){dictionary=type;current="runtime-linear-map-v1";}
            else{for(var baseType=type;baseType is not null;baseType=baseType.BaseType)if(baseType.IsGenericType&&baseType.GetGenericTypeDefinition()==typeof(FrozenDictionary<,>)){dictionary=baseType;break;}current="runtime-frozen-dictionary-v1";}
            if(dictionary is null||!dictionary.GenericTypeArguments.SequenceEqual(new[]{expected[index].Key,expected[index].Value})||backend is not null&&backend!=current)throw new InvalidDataException("Unknown, mismatched or mixed runtime containers.");backend=current;
        }
        return backend!;
    }
    public static IReadOnlyList<LookupMeasurement> Measure(RuntimeSnapshot snapshot,byte[] payload,int iterations=10000)
    {
        if(iterations is <1 or >100000)throw new ArgumentOutOfRangeException(nameof(iterations));
        var generation=new RuntimeGeneration(new(Guid.NewGuid(),1,snapshot.ConfigVersion,Convert.ToHexStringLower(SHA256.HashData(payload)),payload.Length),payload,snapshot);
        var frozen=generation.Snapshot;var indexes=typeof(RuntimeGeneration).GetProperty("Indexes")?.GetValue(generation);
        var backend=indexes is null?"legacy-array":ClassifyContainers(new[]{"Routes","Clusters","Applications","Credentials","RouteAuthentication","JwtApplications","PermissionWindows"}.Select(name=>indexes.GetType().GetProperty(name)?.GetValue(indexes)??throw new InvalidDataException("Missing runtime container.")).ToArray());
        var mode=backend=="runtime-frozen-dictionary-v1"?"indexed":"scan";var results=new List<LookupMeasurement>();
        Func<Guid,RuntimeRoute>? indexedRoute=null;Func<Guid,RuntimeCluster>? indexedCluster=null;Func<Guid,RuntimeApplication>? indexedApp=null;
        Func<string,RuntimeCredential>? indexedCredential=null;Func<Guid,AuthenticationConfiguration?>? indexedAuthentication=null;
        Func<(Guid,string),Guid>? indexedMapping=null;Func<(Guid,Guid),IReadOnlyList<RuntimePermission>>? indexedWindows=null;
        // Reflection/expression compilation happens once, before warmup/timing.
        if(indexes is not null) {
            indexedRoute=Getter<Guid,RuntimeRoute>(indexes,"Routes");indexedCluster=Getter<Guid,RuntimeCluster>(indexes,"Clusters");indexedApp=Getter<Guid,RuntimeApplication>(indexes,"Applications");
            indexedCredential=Getter<string,RuntimeCredential>(indexes,"Credentials","Credential");indexedAuthentication=Getter<Guid,AuthenticationConfiguration?>(indexes,"RouteAuthentication","Configuration");
            indexedMapping=Getter<(Guid,string),Guid>(indexes,"JwtApplications");indexedWindows=Getter<(Guid,Guid),IReadOnlyList<RuntimePermission>>(indexes,"PermissionWindows");
        }
        foreach(var position in new[]{"first","middle","last"}) {
            int At(int count)=>position=="first"?0:position=="middle"?count/2:count-1;
            var route=frozen.Routes[At(frozen.Routes.Count)];var cluster=frozen.Clusters[At(frozen.Clusters.Count)];var app=frozen.Applications[At(frozen.Applications.Count)];var credential=app.Credentials[0];
            var appRoute=frozen.Routes.First(r=>r.ApiId==app.Permissions[0].ApiId);
            var policyId=(appRoute.PolicyBindings??[]).Select(b=>b.PolicyId).Single(id=>generation.Authentication.ContainsKey(id));
            var authentication=generation.Authentication[policyId];var mapping=authentication.Jwt!.ApplicationMappings.Single(m=>m.ApplicationId==app.Id);
            var time=credential.ValidFrom.AddMinutes(1);var apiId=app.Permissions[0].ApiId;
            void Run(string operation,Func<bool> lookup) {
                for(var i=0;i<1000;i++)if(!lookup())throw new InvalidOperationException("Micro lookup warmup failed.");
                var allocated=GC.GetAllocatedBytesForCurrentThread();var start=Stopwatch.GetTimestamp();var hits=0;
                for(var i=0;i<iterations;i++)if(lookup())hits++;
                var elapsed=Stopwatch.GetElapsedTime(start).Ticks;var delta=GC.GetAllocatedBytesForCurrentThread()-allocated;
                results.Add(new(mode,operation,position,iterations,elapsed,delta,hits,backend));
            }
            Run("route",indexedRoute is null?()=>frozen.Routes.Single(r=>r.Id==route.Id)!=null:()=>indexedRoute(route.Id)!=null);
            Run("cluster",indexedCluster is null?()=>frozen.Clusters.Single(c=>c.Id==cluster.Id)!=null:()=>indexedCluster(cluster.Id)!=null);
            Run("application",indexedApp is null?()=>frozen.Applications.Single(a=>a.Id==app.Id)!=null:()=>indexedApp(app.Id)!=null);
            Run("credential",indexedCredential is null?()=>frozen.Applications.Single(a=>a.Credentials.Any(k=>k.AccessKey==credential.AccessKey)).Credentials.Single(k=>k.AccessKey==credential.AccessKey)!=null:()=>indexedCredential(credential.AccessKey)!=null);
            Run("authentication",indexedAuthentication is null?()=>(appRoute.PolicyBindings??[]).Where(b=>generation.Authentication.ContainsKey(b.PolicyId)).Select(b=>generation.Authentication[b.PolicyId]).SingleOrDefault()!=null:()=>indexedAuthentication(appRoute.Id)!=null);
            Run("jwt_mapping",indexedMapping is null?()=>authentication.Jwt.ApplicationMappings.Single(m=>string.Equals(m.ClaimValue,mapping.ClaimValue,StringComparison.Ordinal)).ApplicationId==app.Id:()=>indexedMapping((policyId,mapping.ClaimValue))==app.Id);
            Run("authorization",indexedWindows is null?()=>app.Permissions.Any(p=>p.ApiId==apiId&&time>=p.ValidFrom&&(p.ExpiresAt is null||time<p.ExpiresAt)):()=>indexedWindows((app.Id,apiId)).Any(p=>time>=p.ValidFrom&&(p.ExpiresAt is null||time<p.ExpiresAt)));
        }
        GC.KeepAlive(generation);return results;
    }
    private static Func<TKey,TValue> Getter<TKey,TValue>(object indexes,string name,string? leaf=null) {
        var property=indexes.GetType().GetProperty(name)??throw new InvalidOperationException("Candidate index interface is incompatible.");var dictionary=property.GetValue(indexes)!;
        var key=Expression.Parameter(typeof(TKey),"key");Expression value=Expression.Property(Expression.Constant(dictionary,property.PropertyType),"Item",key);
        if(leaf is not null)value=Expression.Property(value,leaf);
        return Expression.Lambda<Func<TKey,TValue>>(Expression.Convert(value,typeof(TValue)),key).Compile();
    }
}
