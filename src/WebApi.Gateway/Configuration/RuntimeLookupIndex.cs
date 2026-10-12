using System.Collections;
using WebApi.Contracts.Policies;
using WebApi.Contracts.Runtime;
namespace WebApi.Gateway.Configuration;
public sealed record RuntimeCredentialEntry(RuntimeApplication Application, RuntimeCredential Credential);
public sealed record RuntimeAuthenticationEntry(AuthenticationConfiguration? Configuration, RuntimePolicy? Policy);
public sealed class RuntimeLookupIndex
{
    public IReadOnlyDictionary<Guid,RuntimeRoute> Routes {get;}
    public IReadOnlyDictionary<Guid,RuntimeCluster> Clusters {get;}
    public IReadOnlyDictionary<Guid,RuntimeApplication> Applications {get;}
    public IReadOnlyDictionary<string,RuntimeCredentialEntry> Credentials {get;}
    public IReadOnlyDictionary<Guid,RuntimeAuthenticationEntry> RouteAuthentication {get;}
    public IReadOnlyDictionary<(Guid PolicyId,string ClaimValue),Guid> JwtApplications {get;}
    public IReadOnlyDictionary<(Guid ApplicationId,Guid ApiId),IReadOnlyList<RuntimePermission>> PermissionWindows {get;}
    private RuntimeLookupIndex(RuntimeSnapshot frozen,IReadOnlyDictionary<Guid,AuthenticationConfiguration> authentication)
    {
        Validate(frozen,authentication); // Temporary validation sets are not captured by any view.
        Routes=Scan(frozen.Routes,r=>r.Id);Clusters=Scan(frozen.Clusters,c=>c.Id);Applications=Scan(frozen.Applications,a=>a.Id);
        Credentials=new RuntimeLinearLookupMap<string,RuntimeCredentialEntry>(key=>{
            foreach(var app in frozen.Applications)foreach(var credential in app.Credentials)
                if(StringComparer.Ordinal.Equals(credential.AccessKey,key))return(true,new(app,credential));
            return(false,null!);
        },()=>CredentialEntries(frozen),()=>frozen.Applications.Sum(a=>a.Credentials.Count));
        RouteAuthentication=new RuntimeLinearLookupMap<Guid,RuntimeAuthenticationEntry>(key=>{
            foreach(var route in frozen.Routes)if(route.Id==key)return(true,ResolveAuthentication(route,frozen,authentication));
            return(false,null!);
        },()=>frozen.Routes.Select(r=>new KeyValuePair<Guid,RuntimeAuthenticationEntry>(r.Id,ResolveAuthentication(r,frozen,authentication))),()=>frozen.Routes.Count);
        JwtApplications=new RuntimeLinearLookupMap<(Guid PolicyId,string ClaimValue),Guid>(key=>{
            foreach(var policy in frozen.Policies){if(policy.Id!=key.PolicyId)continue;
                if(!authentication.TryGetValue(policy.Id,out var config)||config.Mode!=AuthenticationMode.JWT)return(false,default);
                foreach(var mapping in config.Jwt!.ApplicationMappings)if(StringComparer.Ordinal.Equals(mapping.ClaimValue,key.ClaimValue))return(true,mapping.ApplicationId);
                return(false,default);
            }return(false,default);
        },()=>JwtEntries(frozen,authentication),()=>authentication.Values.Where(c=>c.Mode==AuthenticationMode.JWT).Sum(c=>c.Jwt!.ApplicationMappings.Count));
        PermissionWindows=new RuntimeLinearLookupMap<(Guid ApplicationId,Guid ApiId),IReadOnlyList<RuntimePermission>>(key=>{
            foreach(var app in frozen.Applications){if(app.Id!=key.ApplicationId)continue;
                foreach(var permission in app.Permissions)if(permission.ApiId==key.ApiId)return(true,new PermissionView(app,key.ApiId));
                return(false,null!);
            }return(false,null!);
        },()=>PermissionEntries(frozen),()=>frozen.Applications.Sum(a=>a.Permissions.Select(p=>p.ApiId).Distinct().Count()));
    }
    public static RuntimeLookupIndex Build(RuntimeSnapshot frozen,IReadOnlyDictionary<Guid,AuthenticationConfiguration> authentication)=>new(frozen,authentication);
    private static RuntimeLinearLookupMap<TKey,TValue> Scan<TKey,TValue>(IReadOnlyList<TValue> values,Func<TValue,TKey> key) where TKey:notnull=>new(wanted=>{
        foreach(var value in values)if(EqualityComparer<TKey>.Default.Equals(key(value),wanted))return(true,value);return(false,default!);
    },()=>values.Select(value=>new KeyValuePair<TKey,TValue>(key(value),value)),()=>values.Count);
    private static IEnumerable<KeyValuePair<string,RuntimeCredentialEntry>> CredentialEntries(RuntimeSnapshot snapshot)
    {
        foreach(var app in snapshot.Applications)foreach(var credential in app.Credentials)yield return new(credential.AccessKey,new(app,credential));
    }
    private static RuntimeAuthenticationEntry ResolveAuthentication(RuntimeRoute route,RuntimeSnapshot snapshot,IReadOnlyDictionary<Guid,AuthenticationConfiguration> authentication)
    {
        foreach(var binding in route.PolicyBindings??[])if(authentication.TryGetValue(binding.PolicyId,out var config))return new(config,snapshot.Policies.First(p=>p.Id==binding.PolicyId));return new(null,null);
    }
    private static IEnumerable<KeyValuePair<(Guid PolicyId,string ClaimValue),Guid>> JwtEntries(RuntimeSnapshot snapshot,IReadOnlyDictionary<Guid,AuthenticationConfiguration> authentication)
    {
        foreach(var policy in snapshot.Policies)if(authentication.TryGetValue(policy.Id,out var config)&&config.Mode==AuthenticationMode.JWT)
            foreach(var mapping in config.Jwt!.ApplicationMappings)yield return new((policy.Id,mapping.ClaimValue),mapping.ApplicationId);
    }
    private static IEnumerable<KeyValuePair<(Guid ApplicationId,Guid ApiId),IReadOnlyList<RuntimePermission>>> PermissionEntries(RuntimeSnapshot snapshot)
    {
        foreach(var app in snapshot.Applications){var emitted=new HashSet<Guid>();foreach(var permission in app.Permissions)if(emitted.Add(permission.ApiId))yield return new((app.Id,permission.ApiId),new PermissionView(app,permission.ApiId));}
    }
    private static HashSet<TKey> Unique<TValue,TKey>(IEnumerable<TValue> values,Func<TValue,TKey> key,IEqualityComparer<TKey>? comparer=null) where TKey:notnull
    {
        var keys=new HashSet<TKey>(comparer);foreach(var value in values)if(!keys.Add(key(value)))throw new InvalidOperationException("Runtime lookup key is duplicated.");return keys;
    }
    private static void Validate(RuntimeSnapshot snapshot,IReadOnlyDictionary<Guid,AuthenticationConfiguration> authentication)
    {
        Unique(snapshot.Routes,r=>r.Id);var clusters=Unique(snapshot.Clusters,c=>c.Id);var apps=Unique(snapshot.Applications,a=>a.Id);Unique(snapshot.Policies,p=>p.Id);
        var policies=snapshot.Policies.ToDictionary(p=>p.Id);
        if(authentication.Any(pair=>pair.Value is null||!policies.TryGetValue(pair.Key,out var policy)||policy.Type!="authentication")||policies.Values.Any(p=>p.Type=="authentication"&&!authentication.ContainsKey(p.Id)))throw new InvalidOperationException("Authentication configuration must match the generation policies.");
        foreach(var route in snapshot.Routes){if(!clusters.Contains(route.ClusterId))throw new InvalidOperationException("Route cluster is missing.");var found=false;
            foreach(var binding in route.PolicyBindings??[]){if(!policies.ContainsKey(binding.PolicyId))throw new InvalidOperationException("Bound policy is missing.");if(!authentication.ContainsKey(binding.PolicyId))continue;if(found)throw new InvalidOperationException("Route has more than one authentication binding.");found=true;}
        }
        Unique(snapshot.Applications.SelectMany(a=>a.Credentials),c=>c.AccessKey,StringComparer.Ordinal);
        var jwtKeys=new HashSet<(Guid,string)>();foreach(var (policyId,config) in authentication){if(config.Mode!=AuthenticationMode.JWT)continue;var jwt=config.Jwt??throw new InvalidOperationException("JWT configuration is missing.");foreach(var mapping in jwt.ApplicationMappings){if(!apps.Contains(mapping.ApplicationId))throw new InvalidOperationException("JWT application is missing.");if(!jwtKeys.Add((policyId,mapping.ClaimValue)))throw new InvalidOperationException("JWT mapping key is duplicated.");}}
        var apiIds=snapshot.Routes.Select(r=>r.ApiId).ToHashSet();foreach(var app in snapshot.Applications)foreach(var permission in app.Permissions)if(!apiIds.Contains(permission.ApiId))throw new InvalidOperationException("Granted API is missing.");
    }
    private sealed class PermissionView(RuntimeApplication app,Guid apiId):IReadOnlyList<RuntimePermission>,IList<RuntimePermission>
    {
        public int Count=>app.Permissions.Count(p=>p.ApiId==apiId);public bool IsReadOnly=>true;
        public RuntimePermission this[int index]{get{if(index<0)throw new ArgumentOutOfRangeException(nameof(index));var current=0;foreach(var p in app.Permissions)if(p.ApiId==apiId&&current++==index)return p;throw new ArgumentOutOfRangeException(nameof(index));}set=>throw new NotSupportedException();}
        public IEnumerator<RuntimePermission> GetEnumerator(){foreach(var p in app.Permissions)if(p.ApiId==apiId)yield return p;}IEnumerator IEnumerable.GetEnumerator()=>GetEnumerator();
        public int IndexOf(RuntimePermission item){var index=0;foreach(var p in this){if(EqualityComparer<RuntimePermission>.Default.Equals(p,item))return index;index++;}return -1;}
        public bool Contains(RuntimePermission item)=>IndexOf(item)>=0;
        public void CopyTo(RuntimePermission[] array,int arrayIndex){ArgumentNullException.ThrowIfNull(array);if(arrayIndex<0||arrayIndex>array.Length)throw new ArgumentOutOfRangeException(nameof(arrayIndex));if(array.Length-arrayIndex<Count)throw new ArgumentException("Insufficient destination space.",nameof(array));foreach(var p in this)array[arrayIndex++]=p;}
        public void Add(RuntimePermission item)=>throw new NotSupportedException();public void Clear()=>throw new NotSupportedException();public void Insert(int index,RuntimePermission item)=>throw new NotSupportedException();public bool Remove(RuntimePermission item)=>throw new NotSupportedException();public void RemoveAt(int index)=>throw new NotSupportedException();
    }
}
