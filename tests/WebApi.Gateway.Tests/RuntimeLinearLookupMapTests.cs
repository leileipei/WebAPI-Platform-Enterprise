using System.Collections;
using WebApi.Contracts.Policies;
using WebApi.Contracts.Runtime;
using WebApi.Gateway.Configuration;
using Xunit;
namespace WebApi.Gateway.Tests;
public sealed class RuntimeLinearLookupMapTests
{
    private static IReadOnlyDictionary<string,int> Map(Func<string,(bool Found,int Value)> find,Func<IEnumerable<KeyValuePair<string,int>>> enumerate,Func<int> count)
    {
        var type=typeof(RuntimeLookupIndex).Assembly.GetType("WebApi.Gateway.Configuration.RuntimeLinearLookupMap`2");Assert.NotNull(type);
        return Assert.IsAssignableFrom<IReadOnlyDictionary<string,int>>(Activator.CreateInstance(type.MakeGenericType(typeof(string),typeof(int)),find,enumerate,count));
    }
    [Theory]
    [InlineData(0,1)] [InlineData(5,6)] [InlineData(9,10)]
    public void LookupVisitsOnlyUntilMatchAndNeverCaches(int position,int expected)
    {
        var values=Enumerable.Range(0,10).Select(i=>new KeyValuePair<string,int>("key"+i,i)).ToArray();var visited=0;
        (bool,int) Find(string key){foreach(var entry in values){visited++;if(StringComparer.Ordinal.Equals(entry.Key,key))return (true,entry.Value);}return(false,default);}
        var map=Map(Find,()=>values,()=>values.Length);
        for(var attempt=0;attempt<3;attempt++){visited=0;Assert.True(map.TryGetValue("key"+position,out var actual));Assert.Equal(position,actual);Assert.Equal(expected,visited);}
        visited=0;Assert.False(map.ContainsKey("KEY"+position));Assert.Equal(10,visited);
        Assert.Throws<KeyNotFoundException>(()=>map["missing"]);Assert.Throws<ArgumentNullException>(()=>map.ContainsKey(null!));
        Assert.Equal(10,map.Count);Assert.Equal(values.Select(i=>i.Key),map.Keys);Assert.Equal(values.Select(i=>i.Value),map.Values);Assert.Equal(values,map.ToArray());
    }
    [Fact]
    public void ValidationRejectsEveryCurrentFault()
    {
        var current=new RuntimeLookupIndexTests();current.DuplicateKeysAndMissingReferencesReject();current.JwtMappingNamespacesAreDistinctAndOrdinal();
        var snapshot=Snapshot();var policy=snapshot.Policies[0];
        Assert.Throws<InvalidOperationException>(()=>RuntimeLookupIndex.Build(snapshot, new Dictionary<Guid,AuthenticationConfiguration>{[policy.Id]=new(AuthenticationMode.JWT)}));
        Assert.Throws<InvalidOperationException>(()=>RuntimeLookupIndex.Build(snapshot with {Policies=[policy with {Type="timeout"}]},Authentication(snapshot)));
        Assert.Throws<InvalidOperationException>(()=>RuntimeLookupIndex.Build(snapshot with {Routes=[snapshot.Routes[0] with {PolicyBindings=[new(policy.Id,0),new(policy.Id,1)]}]},Authentication(snapshot)));
        Assert.Throws<InvalidOperationException>(()=>RuntimeLookupIndex.Build(snapshot,new Dictionary<Guid,AuthenticationConfiguration>{[policy.Id]=null!}));
    }
    [Fact]
    public void WindowsRemainImmutableAndUnfiltered()
    {
        var snapshot=Snapshot();var app=snapshot.Applications[0];var now=DateTimeOffset.UtcNow;var api=snapshot.Routes[0].ApiId;
        var permissions=new List<RuntimePermission>{new(api,now.AddDays(-2),now.AddDays(-1)),new(api,now.AddDays(1),null),new(api,now.AddDays(-1),now.AddDays(2))};
        snapshot=snapshot with {Applications=[app with {Permissions=permissions}]};var index=RuntimeLookupIndex.Build(snapshot,Authentication(snapshot));
        Assert.All(new object[]{index.Routes,index.Clusters,index.Applications,index.Credentials,index.RouteAuthentication,index.JwtApplications,index.PermissionWindows},v=>Assert.StartsWith("RuntimeLinearLookupMap",v.GetType().Name));
        var view=index.PermissionWindows[(app.Id,api)];Assert.Equal(permissions,view);Assert.Equal(3,view.Count);Assert.Equal(permissions[1],view[1]);Assert.Throws<ArgumentOutOfRangeException>(()=>view[-1]);Assert.Throws<ArgumentOutOfRangeException>(()=>view[3]);
        var list=Assert.IsAssignableFrom<IList<RuntimePermission>>(view);Assert.True(list.IsReadOnly);Assert.Throws<NotSupportedException>(()=>list.Clear());Assert.Throws<NotSupportedException>(()=>list[0]=permissions[1]);Assert.Throws<NotSupportedException>(()=>list.Add(permissions[0]));
        var original=new RuntimeLookupIndexTests();original.MutatingInputCollectionsCannotChangeFrozenGenerationOrIndexes();original.RetiredGenerationCollectsAfterDoubleDisposeAndDuplicateSequenceRejects();
    }
    [Fact]
    public void AuthenticationBuildsValueOnlyForMatchedRoute()
    {
        var snapshot=Snapshot();var counted=new CountingBindings(snapshot.Routes[0].PolicyBindings!);var other=snapshot.Routes[0] with {Id=Guid.NewGuid(),PolicyBindings=counted};
        snapshot=snapshot with {Routes=[other,snapshot.Routes[0]]};var index=RuntimeLookupIndex.Build(snapshot,Authentication(snapshot));counted.Visits=0;
        Assert.NotNull(index.RouteAuthentication[snapshot.Routes[1].Id].Configuration);Assert.Equal(0,counted.Visits);
        Assert.NotNull(index.RouteAuthentication[other.Id].Configuration);Assert.True(counted.Visits>0);
    }
    private sealed class CountingBindings(IReadOnlyList<RuntimePolicyBinding> values):IReadOnlyList<RuntimePolicyBinding>
    {
        public int Visits;public int Count=>values.Count;public RuntimePolicyBinding this[int index]=>values[index];
        public IEnumerator<RuntimePolicyBinding> GetEnumerator(){foreach(var value in values){Visits++;yield return value;}}IEnumerator IEnumerable.GetEnumerator()=>GetEnumerator();
    }
    private static IReadOnlyDictionary<Guid,AuthenticationConfiguration> Authentication(RuntimeSnapshot snapshot)=>new Dictionary<Guid,AuthenticationConfiguration>{[snapshot.Policies[0].Id]=new(AuthenticationMode.ApiKey)};
    private static RuntimeSnapshot Snapshot()
    {
        var env=Guid.NewGuid();var api=Guid.NewGuid();var cluster=Guid.NewGuid();var policy=Guid.NewGuid();var now=DateTimeOffset.UtcNow;
        return new("2.2",env,1,now,[new(Guid.NewGuid(),api,Guid.NewGuid(),cluster,"/test",["GET"],0,30000,true,[new(policy,0)])],[new(cluster,env,"RoundRobin",false,"/health",30,[])],[new(policy,"authentication","{\"mode\":\"ApiKey\"}",Guid.NewGuid(),1)],[new(Guid.NewGuid(),"Active",[],[new(api,now,null)])]);
    }
}
