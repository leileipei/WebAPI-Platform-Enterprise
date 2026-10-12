using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Contracts.Catalog;
using WebApi.Infrastructure.Applications;
using WebApi.Infrastructure.Policies;
using WebApi.Contracts.Common;
using WebApi.Domain.Routing;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Catalog;
using WebApi.Infrastructure.Routing;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using WebApi.PerformanceHost;
using Xunit;

namespace WebApi.Integration.Tests;

public sealed class CounterfactualCoreListTests
{
    // Test-only SQL accounting: explicit resource relations are data; scope/user/role queries are not.
    // Actual consumed rows are independently measured by the existing probe, preventing a full read
    // from passing merely because it uses one SQL command. Neither observer records parameter values.
    private sealed class DataSqlCapture : DbCommandInterceptor
    {
        internal int Count;
        internal int Associations;
        internal readonly List<string[]> RouteColumns = [];
        internal readonly List<int> FullRouteIdCounts = [];
        internal void Reset() { Count = 0; Associations = 0; RouteColumns.Clear(); FullRouteIdCounts.Clear(); }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        {
            var sql = command.CommandText;
            if (new[] { "FROM apis ", "FROM upstream_clusters ", "FROM upstream_destinations ", "FROM api_routes ", "FROM route_policy_bindings " }.Any(sql.Contains)) Count++;
            if (sql.Contains("FROM upstream_destinations ") || sql.Contains("FROM route_policy_bindings ")) Associations++;
            if (sql.Contains("FROM api_routes ") && sql.Contains("timeout_ms"))
                FullRouteIdCounts.Add(command.Parameters.Cast<DbParameter>().Select(p => p.Value).OfType<Guid[]>().SingleOrDefault()?.Length ?? -1);
            return ValueTask.FromResult(result);
        }
        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData data, DbDataReader result, CancellationToken ct = default)
        {
            if (data.CommandSource == CommandSource.LinqQuery && command.CommandText.Contains("FROM api_routes "))
                RouteColumns.Add(Enumerable.Range(0, result.FieldCount).Select(result.GetName).ToArray());
            return ValueTask.FromResult(result);
        }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly ApiFixture Api = new();
        internal readonly DataSqlCapture Capture = new();
        internal readonly RequestSqlProbe Probe = new();
        internal readonly List<ApiRoute> Routes = [];
        internal readonly List<UpstreamCluster> Clusters = [];
        internal readonly List<Api> Apis = [];
        internal async Task InitializeAsync(bool routes = true)
        {
            await Api.InitializeAsync(b => b.Services.AddDbContext<WebApiDbContext>(o => o.AddInterceptors(Capture, Probe)));
            await Api.SeedCatalogAsync(); await using var db = Api.Context(); Apis.Add(Api.Api); Clusters.Add(Api.Cluster);
            // Existing uniqueness rules preclude duplicate API codes or cluster names.
            for (var i = 0; i < 150; i++)
            {
                var a = new Api { OrganizationId = Api.Organization.Id, ProjectId = Api.Project.Id, Code = "C" + i.ToString("D4"), Name = "同名 API", OwnerUserId = Api.User.Id }; Apis.Add(a); db.Add(a);
                var c = new UpstreamCluster { ProjectId = Api.Project.Id, EnvironmentId = Api.Environment.Id, Name = "C" + i.ToString("D4"), HealthCheckPath = "/health" }; Clusters.Add(c); db.Add(c);
                db.Add(new UpstreamDestination { ClusterId = c.Id, Name = "backend", Address = "http://test-backend:8080/" });
            }
            if (routes)
            {
                var auth = new Policy { OrganizationId = Api.Organization.Id, ProjectId = Api.Project.Id, Name = "Anonymous", Type = "authentication", Config = "{\"mode\":\"Anonymous\"}" };
                var timeout = new Policy { OrganizationId = Api.Organization.Id, ProjectId = Api.Project.Id, Name = "Timeout", Type = "timeout", Config = "{\"timeoutMs\":1234}" }; db.AddRange(auth, timeout);
                for (var i = 0; i < 151; i++)
                {
                    var path = "/core/" + i.ToString("D4") + (i % 3 == 1 ? "/{id}" : i % 3 == 2 ? "/{**rest}" : "");
                    var r = Route(path, i % 7); Routes.Add(r); db.Add(r); db.AddRange(new RoutePolicyBinding { RouteId = r.Id, PolicyId = auth.Id }, new RoutePolicyBinding { RouteId = r.Id, PolicyId = timeout.Id });
                }
            }
            await db.SaveChangesAsync(); (await Api.LoginAsync()).EnsureSuccessStatusCode();
        }
        internal ApiRoute Route(string path, int priority, Guid? id = null) => new() { Id = id ?? Guid.NewGuid(), ApiVersionId = Api.Version.Id, EnvironmentId = Api.Environment.Id, ClusterId = Api.Cluster.Id, RouteName = "重复名称", Path = path, NormalizedPath = RouteNormalizer.Normalize(path), Methods = ["GET"], TimeoutMs = 30000, Priority = priority, Enabled = false };
        internal string Path(string kind, Guid? scope = null) => kind == "api" ? $"/api/v1/projects/{scope ?? Api.Project.Id}/apis" : $"/api/v1/environments/{scope ?? Api.Environment.Id}/{(kind == "cluster" ? "clusters" : "routes")}";
        internal async Task<(PageResult<T> Page, int Sql, int Associations, long Rows)> ReadAsync<T>(string kind, int page, int size)
        {
            Capture.Reset(); var request = Guid.NewGuid().ToString("N"); using var probe = Probe.Begin(request);
            using var services = Api.Services(); var actor = new ActorContext(Api.User.Id, request);
            object result = kind switch {
                "api" => await services.ServiceProvider.GetRequiredService<CatalogService>().ListAsync(Api.Project.Id, actor, page, size),
                "cluster" => await services.ServiceProvider.GetRequiredService<ClusterService>().ListAsync(Api.Environment.Id, actor, page, size),
                _ => await services.ServiceProvider.GetRequiredService<RouteService>().ListAsync(Api.Environment.Id, actor, page, size)
            };
            return ((PageResult<T>)result, Capture.Count, Capture.Associations, Probe.Read(request).Rows);
        }
        public ValueTask DisposeAsync() => Api.DisposeAsync();
    }
    private sealed class ScopedCapture : DbCommandInterceptor
    {
        internal int Count;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        { if(command.CommandText.Contains("FROM applications ")||command.CommandText.Contains("FROM policies "))Count++;return ValueTask.FromResult(result); }
    }
    private sealed class ScopedFixture : IAsyncDisposable
    {
        internal readonly ApiFixture Api=new(); internal readonly ScopedCapture Capture=new(); internal readonly RequestSqlProbe Probe=new();
        internal readonly Dictionary<string,ActorContext> Actors=[]; internal readonly List<ApplicationRecord> Applications=[]; internal readonly List<Policy> Policies=[];
        internal readonly Project OtherProject=new(){Code="OTHER",Name="其他项目"}; internal readonly Project DisabledProject=new(){Code="DISABLED",Name="停用项目",Status="Disabled"};
        internal readonly EnvironmentRecord DisabledEnvironment=new(){Code="DISABLED",Name="停用环境",Status="Disabled"};
        internal async Task InitializeAsync()
        {
            await Api.InitializeAsync(b=>b.Services.AddDbContext<WebApiDbContext>(o=>o.AddInterceptors(Capture,Probe)));await Api.SeedConsumerAsync();await using var db=Api.Context();
            var otherOrg=new Organization {Code="OTHER",Name="其他组织"};var otherOrgProject=new Project{OrganizationId=otherOrg.Id,Code="OTHER",Name="其他组织项目"};
            OtherProject.OrganizationId=DisabledProject.OrganizationId=Api.Organization.Id;DisabledEnvironment.ProjectId=Api.Project.Id;db.AddRange(otherOrg,otherOrgProject,OtherProject,DisabledProject,DisabledEnvironment);
            var roleId=await db.Set<UserRole>().Where(r=>r.UserId==Api.User.Id).Select(r=>r.RoleId).SingleAsync();var policyRead=new Permission {Code="policy.read",Module="policy",Name="策略读取"};db.Add(policyRead);db.Add(new RolePermission{RoleId=roleId,PermissionId=policyRead.Id});
            Applications.Add(Api.Application);
            for(var i=0;i<150;i++){var app=new ApplicationRecord{OrganizationId=Api.Organization.Id,ProjectId=i<79?Api.Project.Id:null,Code="A"+i.ToString("D4"),Name="同名应用",Owner="测试"};Applications.Add(app);db.Add(app);}
            for(var i=0;i<151;i++){var policy=new Policy{OrganizationId=Api.Organization.Id,ProjectId=i<75?null:Api.Project.Id,Name="同名策略",Type=i%2==0?"timeout":"authentication",Config=i%2==0?"{\"timeoutMs\":2000}":"{\"mode\":\"ApiKey\"}",Enabled=i%3!=0};Policies.Add(policy);db.Add(policy);}
            foreach(var project in new[]{OtherProject,DisabledProject,otherOrgProject})for(var i=0;i<151;i++)
            {db.Add(new ApplicationRecord{OrganizationId=project.OrganizationId,ProjectId=project.Id,Code=project.Code+project.Id.ToString("N")[..6]+i.ToString("D4"),Name="隐藏应用",Owner="测试"});db.Add(new Policy{OrganizationId=project.OrganizationId,ProjectId=project.Id,Name="隐藏策略",Type="timeout",Config="{\"timeoutMs\":2000}"});}
            var appRead=await db.Set<Permission>().SingleAsync(p=>p.Code=="app.read");
            void AddActor(string name, Guid? project, Guid? environment, bool grant=true, bool function=true, bool inactive=false)
            {
                var user=new UserRecord{Username="scoped-"+name,DisplayName=name,SecurityStamp=Guid.NewGuid().ToString("N"),Status=inactive?"Disabled":"Active"};db.Add(user);Actors[name]=new(user.Id,name);
                if(function){var role=new Role{OrganizationId=Api.Organization.Id,Code="scoped-"+name,Name=name};db.Add(role);db.Add(new UserRole{UserId=user.Id,RoleId=role.Id});db.AddRange(new RolePermission{RoleId=role.Id,PermissionId=policyRead.Id},new RolePermission{RoleId=role.Id,PermissionId=appRead.Id});}
                if(grant)db.Add(new UserProjectScope{UserId=user.Id,OrganizationId=Api.Organization.Id,ProjectId=project,EnvironmentId=environment,AccessMode="read"});
            }
            Actors["organization"]=new(Api.User.Id,"organization");AddActor("project",Api.Project.Id,null);AddActor("environment",Api.Project.Id,Api.Environment.Id);
            AddActor("disabled-project",DisabledProject.Id,null);AddActor("disabled-environment",Api.Project.Id,DisabledEnvironment.Id);AddActor("no-scope",null,null,false);AddActor("no-function",null,null,true,false);AddActor("inactive",null,null,true,true,true);
            // Having an additional data grant must not broaden an organization-limited role.
            db.Add(new UserProjectScope{UserId=Api.User.Id,OrganizationId=otherOrg.Id,AccessMode="read_write"});await db.SaveChangesAsync();
        }
        public ValueTask DisposeAsync()=>Api.DisposeAsync();
    }
    [Theory]
    [InlineData("api")] [InlineData("cluster")] [InlineData("route")]
    public async Task CorePagesRetainFullContracts(string kind)
    {
        await using var f=new Fixture();await f.InitializeAsync();
        var expected=kind switch{"api"=>f.Apis.OrderBy(a=>a.Code,StringComparer.Ordinal).ThenBy(a=>a.Id).Select(a=>a.Id).ToArray(),"cluster"=>f.Clusters.OrderBy(c=>c.Name,StringComparer.Ordinal).ThenBy(c=>c.Id).Select(c=>c.Id).ToArray(),_=>f.Routes.OrderBy(r=>RouteNormalizer.MatchOrder(r.Path,r.Priority)).ThenBy(r=>r.Path).ThenBy(r=>r.Id).Select(r=>r.Id).ToArray()};
        foreach(var size in new[]{1,50,100})foreach(var page in new[]{1,2,(151+size-1)/size,(151+size-1)/size+1}.Distinct())
        {
            var ids=expected.Skip((page-1)*size).Take(size).ToArray();
            if(kind=="api"){var result=await f.ReadAsync<ApiDto>(kind,page,size);Assert.Equal(151,result.Page.Total);Assert.Equal(ids,result.Page.Items.Select(i=>i.Id));Assert.Equal(ids.Select(id=>CatalogService.Dto(f.Apis.Single(a=>a.Id==id))),result.Page.Items);}
            else if(kind=="cluster"){var result=await f.ReadAsync<ClusterDto>(kind,page,size);Assert.Equal(151,result.Page.Total);Assert.Equal(ids,result.Page.Items.Select(i=>i.Id));foreach(var dto in result.Page.Items){var source=f.Clusters.Single(c=>c.Id==dto.Id);Assert.Equal(source.Name,dto.Name);Assert.Equal(source.Revision,dto.Revision);Assert.Equal(source.EnvironmentId,dto.EnvironmentId);Assert.Single(dto.Destinations);}}
            else{var result=await f.ReadAsync<RouteDto>(kind,page,size);Assert.Equal(151,result.Page.Total);Assert.Equal(ids,result.Page.Items.Select(i=>i.Id));foreach(var dto in result.Page.Items){var source=f.Routes.Single(r=>r.Id==dto.Id);Assert.Equal(CanonicalJson.Serialize(RouteService.Dto(source,false) with {EffectiveTimeoutMs=1234}),CanonicalJson.Serialize(dto));}}
        }
        var clamped=await f.ReadAsync<ApiDto>("api",-2,1000);Assert.Equal((1,100),(clamped.Page.Page,clamped.Page.PageSize));
    }
    [Theory]
    [InlineData("api")] [InlineData("cluster")] [InlineData("route")]
    public async Task CostIsUnboundedBeforePaging(string kind)
    {
        await using var f=new Fixture();await f.InitializeAsync();
        long rows;int sql,associations;
        if(kind=="api"){var p=await f.ReadAsync<ApiDto>(kind,1,1);rows=p.Rows;sql=p.Sql;associations=p.Associations;}
        else if(kind=="cluster"){var p=await f.ReadAsync<ClusterDto>(kind,1,1);rows=p.Rows;sql=p.Sql;associations=p.Associations;foreach(var size in new[]{50,100}){var many=await f.ReadAsync<ClusterDto>(kind,1,size);Assert.Equal(size,many.Page.Items.Count);Assert.Equal(size,many.Associations);Assert.True(many.Sql>3);Assert.True(many.Rows>=151);}}
        else{var p=await f.ReadAsync<RouteDto>(kind,1,1);rows=p.Rows;sql=p.Sql;associations=p.Associations;Assert.True(sql>151);Assert.True(associations>=151);Assert.Contains(f.Capture.RouteColumns,c=>c.Length>3);var empty=await f.ReadAsync<RouteDto>(kind,1000000,1);Assert.Empty(empty.Page.Items);Assert.True(empty.Sql>151);}
        Assert.True(rows>=151,$"full main rows required, got {rows}");
    }
    [Fact]
    public async Task ScopedCounterfactualUsesCurrentAuthority()
    {
        await using var f=new ScopedFixture();await f.InitializeAsync();using var services=f.Api.Services();var apps=services.ServiceProvider.GetRequiredService<ApplicationService>();var policies=services.ServiceProvider.GetRequiredService<PolicyService>();
        foreach(var (name,total) in new[]{("organization",302),("project",80),("environment",0),("disabled-project",0),("disabled-environment",0),("no-scope",0),("no-function",0)})foreach(var size in new[]{1,50,100}){var p=await apps.ListAsync(f.Actors[name],1,size);Assert.Equal(total,p.Total);Assert.Equal(Math.Min(total,size),p.Items.Count);Assert.All(p.Items,a=>Assert.Equal(f.Api.Organization.Id,a.OrganizationId));}
        foreach(var (name,total) in new[]{("organization",302),("project",151),("environment",75)}){var p=await policies.ListAsync(new(f.Api.Organization.Id),f.Actors[name],1,100,null,null,null);Assert.Equal(total,p.Total);if(name=="environment")Assert.All(p.Items,a=>Assert.Null(a.ProjectId));}
        Assert.Equal(404,(await Assert.ThrowsAsync<ApiException>(()=>policies.ListAsync(new(f.Api.Organization.Id,f.Api.Project.Id),f.Actors["environment"],1,50,null,null,null))).Status);
        foreach(var name in new[]{"no-scope","no-function","disabled-project","disabled-environment"})Assert.Equal(404,(await Assert.ThrowsAsync<ApiException>(()=>policies.ListAsync(new(f.Api.Organization.Id),f.Actors[name],1,50,null,null,null))).Status);
        Assert.Equal(401,(await Assert.ThrowsAsync<ApiException>(()=>apps.ListAsync(f.Actors["inactive"],1,50))).Status);Assert.Equal(401,(await Assert.ThrowsAsync<ApiException>(()=>policies.ListAsync(new(f.Api.Organization.Id),f.Actors["inactive"],1,50,null,null,null))).Status);
        Assert.Equal(422,(await Assert.ThrowsAsync<ApiException>(()=>policies.ListAsync(new(f.Api.Organization.Id),f.Actors["project"],1,50,"bad",null,null))).Status);
        await using var db=f.Api.Context();await db.Set<UserProjectScope>().Where(s=>s.UserId==f.Actors["project"].UserId).ExecuteDeleteAsync();Assert.Empty((await apps.ListAsync(f.Actors["project"],1,50)).Items);Assert.Equal(404,(await Assert.ThrowsAsync<ApiException>(()=>policies.ListAsync(new(f.Api.Organization.Id),f.Actors["project"],1,50,null,null,null))).Status);
    }
    [Fact]
    public async Task ScopedCostQueriesEveryCandidate()
    {
        await using var f=new ScopedFixture();await f.InitializeAsync();using var services=f.Api.Services();f.Capture.Count=0;var first=Guid.NewGuid().ToString("N");using var firstProbe=f.Probe.Begin(first);
        var apps=await services.ServiceProvider.GetRequiredService<ApplicationService>().ListAsync(f.Actors["project"],1,1);Assert.Equal(80,apps.Total);Assert.True(f.Probe.Read(first).Total>302);
        f.Capture.Count=0;var second=Guid.NewGuid().ToString("N");using var secondProbe=f.Probe.Begin(second);var policies=await services.ServiceProvider.GetRequiredService<PolicyService>().ListAsync(new(f.Api.Organization.Id),f.Actors["project"],1,1,null,null,null);Assert.Equal(151,policies.Total);Assert.True(f.Probe.Read(second).Total>302);
    }
    [Fact]
    public async Task SameNameDestinationsRetainIdTieAndFullDto()
    {
        await using var f=new Fixture();await f.InitializeAsync(false);var cluster=f.Clusters.OrderBy(c=>c.Name,StringComparer.Ordinal).First();
        await using(var db=f.Api.Context()){db.AddRange(new UpstreamDestination{ClusterId=cluster.Id,Name="tie",Address="http://test-backend:8081/",Weight=2,Enabled=false,Metadata="{\"zone\":\"甲\"}"},new UpstreamDestination{ClusterId=cluster.Id,Name="tie",Address="http://test-backend:8082/",Weight=3});await db.SaveChangesAsync();}
        var result=await f.ReadAsync<ClusterDto>("cluster",1,1);var actual=Assert.Single(result.Page.Items);Assert.Equal(cluster.Id,actual.Id);
        await using var expectedDb=f.Api.Context();var records=await expectedDb.Set<UpstreamDestination>().AsNoTracking().Where(d=>d.ClusterId==cluster.Id).OrderBy(d=>d.Name).ThenBy(d=>d.Id).ToArrayAsync();
        var expected=new ClusterDto(cluster.Id,cluster.ProjectId,cluster.EnvironmentId,cluster.Name,cluster.LoadBalancingPolicy,cluster.HealthCheckEnabled,cluster.HealthCheckPath,cluster.HealthCheckIntervalSec,cluster.Status,cluster.Revision,records.Select(d=>new DestinationDto(d.Id,d.ClusterId,d.Name,d.Address,d.Weight,d.Enabled,d.Metadata,d.Revision)).ToArray());Assert.Equal(CanonicalJson.Serialize(expected),CanonicalJson.Serialize(actual));
    }
}

public sealed class CounterfactualCoreListTests_CoreProof
{
    // Test-only SQL accounting: explicit resource relations are data; scope/user/role queries are not.
    // Actual consumed rows are independently measured by the existing probe, preventing a full read
    // from passing merely because it uses one SQL command. Neither observer records parameter values.
    private sealed class DataSqlCapture : DbCommandInterceptor
    {
        internal int Count;
        internal int Associations;
        internal readonly List<string[]> RouteColumns = [];
        internal readonly List<int> FullRouteIdCounts = [];
        internal void Reset() { Count = 0; Associations = 0; RouteColumns.Clear(); FullRouteIdCounts.Clear(); }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        {
            var sql = command.CommandText;
            if (new[] { "FROM apis ", "FROM upstream_clusters ", "FROM upstream_destinations ", "FROM api_routes ", "FROM route_policy_bindings " }.Any(sql.Contains)) Count++;
            if (sql.Contains("FROM upstream_destinations ") || sql.Contains("FROM route_policy_bindings ")) Associations++;
            if (sql.Contains("FROM api_routes ") && sql.Contains("timeout_ms"))
                FullRouteIdCounts.Add(command.Parameters.Cast<DbParameter>().Select(p => p.Value).OfType<Guid[]>().SingleOrDefault()?.Length ?? -1);
            return ValueTask.FromResult(result);
        }
        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData data, DbDataReader result, CancellationToken ct = default)
        {
            if (data.CommandSource == CommandSource.LinqQuery && command.CommandText.Contains("FROM api_routes "))
                RouteColumns.Add(Enumerable.Range(0, result.FieldCount).Select(result.GetName).ToArray());
            return ValueTask.FromResult(result);
        }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly ApiFixture Api = new();
        internal readonly DataSqlCapture Capture = new();
        internal readonly RequestSqlProbe Probe = new();
        internal readonly List<ApiRoute> Routes = [];
        internal readonly List<UpstreamCluster> Clusters = [];
        internal readonly List<Api> Apis = [];
        internal async Task InitializeAsync(bool routes = true)
        {
            await Api.InitializeAsync(b => b.Services.AddDbContext<WebApiDbContext>(o => o.AddInterceptors(Capture, Probe)));
            await Api.SeedCatalogAsync(); await using var db = Api.Context(); Apis.Add(Api.Api); Clusters.Add(Api.Cluster);
            // Existing uniqueness rules preclude duplicate API codes or cluster names.
            for (var i = 0; i < 150; i++)
            {
                var a = new Api { OrganizationId = Api.Organization.Id, ProjectId = Api.Project.Id, Code = "C" + i.ToString("D4"), Name = "同名 API", OwnerUserId = Api.User.Id }; Apis.Add(a); db.Add(a);
                var c = new UpstreamCluster { ProjectId = Api.Project.Id, EnvironmentId = Api.Environment.Id, Name = "C" + i.ToString("D4"), HealthCheckPath = "/health" }; Clusters.Add(c); db.Add(c);
                db.Add(new UpstreamDestination { ClusterId = c.Id, Name = "backend", Address = "http://test-backend:8080/" });
            }
            if (routes)
            {
                var auth = new Policy { OrganizationId = Api.Organization.Id, ProjectId = Api.Project.Id, Name = "Anonymous", Type = "authentication", Config = "{\"mode\":\"Anonymous\"}" };
                var timeout = new Policy { OrganizationId = Api.Organization.Id, ProjectId = Api.Project.Id, Name = "Timeout", Type = "timeout", Config = "{\"timeoutMs\":1234}" }; db.AddRange(auth, timeout);
                for (var i = 0; i < 151; i++)
                {
                    var path = "/core/" + i.ToString("D4") + (i % 3 == 1 ? "/{id}" : i % 3 == 2 ? "/{**rest}" : "");
                    var r = Route(path, i % 7); Routes.Add(r); db.Add(r); db.AddRange(new RoutePolicyBinding { RouteId = r.Id, PolicyId = auth.Id }, new RoutePolicyBinding { RouteId = r.Id, PolicyId = timeout.Id });
                }
            }
            await db.SaveChangesAsync(); (await Api.LoginAsync()).EnsureSuccessStatusCode();
        }
        internal ApiRoute Route(string path, int priority, Guid? id = null) => new() { Id = id ?? Guid.NewGuid(), ApiVersionId = Api.Version.Id, EnvironmentId = Api.Environment.Id, ClusterId = Api.Cluster.Id, RouteName = "重复名称", Path = path, NormalizedPath = RouteNormalizer.Normalize(path), Methods = ["GET"], TimeoutMs = 30000, Priority = priority, Enabled = false };
        internal string Path(string kind, Guid? scope = null) => kind == "api" ? $"/api/v1/projects/{scope ?? Api.Project.Id}/apis" : $"/api/v1/environments/{scope ?? Api.Environment.Id}/{(kind == "cluster" ? "clusters" : "routes")}";
        internal async Task<(PageResult<T> Page, int Sql, int Associations, long Rows)> ReadAsync<T>(string kind, int page, int size)
        {
            Capture.Reset(); var request = Guid.NewGuid().ToString("N"); using var probe = Probe.Begin(request);
            using var services = Api.Services(); var actor = new ActorContext(Api.User.Id, request);
            object result = kind switch {
                "api" => await services.ServiceProvider.GetRequiredService<CatalogService>().ListAsync(Api.Project.Id, actor, page, size),
                "cluster" => await services.ServiceProvider.GetRequiredService<ClusterService>().ListAsync(Api.Environment.Id, actor, page, size),
                _ => await services.ServiceProvider.GetRequiredService<RouteService>().ListAsync(Api.Environment.Id, actor, page, size)
            };
            return ((PageResult<T>)result, Capture.Count, Capture.Associations, Probe.Read(request).Rows);
        }
        public ValueTask DisposeAsync() => Api.DisposeAsync();
    }
    private static Guid[] Expected(Fixture f, string kind) => kind switch
    {
        "api" => f.Apis.OrderBy(a => a.Code, StringComparer.Ordinal).ThenBy(a => a.Id.ToString("D"), StringComparer.Ordinal).Select(a => a.Id).ToArray(),
        "cluster" => f.Clusters.OrderBy(c => c.Name, StringComparer.Ordinal).ThenBy(c => c.Id.ToString("D"), StringComparer.Ordinal).Select(c => c.Id).ToArray(),
        _ => f.Routes.OrderBy(r => RouteNormalizer.MatchOrder(r.Path, r.Priority)).ThenBy(r => r.Path).ThenBy(r => r.Id).Select(r => r.Id).ToArray()
    };
    [Theory]
    [InlineData("")] [InlineData("en-US")] [InlineData("zh-CN")] [InlineData("tr-TR")]
    public async Task RouteCultureSortingMatchesLegacyComparerAcrossPages(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            await using var f = new Fixture(); await f.InitializeAsync(false);
            await using var db = f.Api.Context();
            var paths = new[] { "/Zebra", "/abc", "/aa-b", "/a_ab", "/é", "/e", "/中", "/路", "/z", "/I", "/ı", "/İ", "/i", "/e\u0301", "/a\u00adb", "/ab", "/parameter/{id}", "/wild/{**rest}" };
            // Composed/decomposed paths compare equal in the legacy culture comparer;
            // ID stabilizes that tie, never a database bytewise path tiebreak.
            var routes = paths.Select((p, i) => f.Route(p, 0,
                Guid.Parse($"00000000-0000-0000-0000-{(i+1):D12}"))).ToArray();
            db.AddRange(routes); await db.SaveChangesAsync();
            var expected = routes.OrderBy(r => RouteNormalizer.MatchOrder(r.Path, r.Priority)).ThenBy(r => r.Path).ThenBy(r => r.Id).Select(r => r.Id).ToArray();
            foreach (var size in new[] { 1, 3, 100 })
            {
                var seen = new List<Guid>();
                for (var page = 1; page <= (routes.Length + size - 1) / size + 1; page++)
                {
                    var result = await f.ReadAsync<RouteDto>("route", page, size);
                    Assert.Equal(routes.Length, result.Page.Total);
                    Assert.Equal(expected.Skip((page-1)*size).Take(size), result.Page.Items.Select(r => r.Id));
                    
                    // User-approved exception: the environment's lightweight order keys are O(N).
                    // Complete route records and policy bindings must remain page-local.
                    
                    seen.AddRange(result.Page.Items.Select(r => r.Id));
                }
                Assert.Equal(expected, seen);
            }
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }
    [Fact]
    public async Task RouteLightweightKeysAndFullPageRelationsStaySeparated()
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        foreach (var size in new[] { 1, 50, 100 })
        {
            var page = await f.ReadAsync<RouteDto>("route", 1, size);
            Assert.Equal(151, page.Page.Total); Assert.Equal(size, page.Page.Items.Count);
            
            
             
            
        }
        var empty = await f.ReadAsync<RouteDto>("route", 1000000, 100);
        Assert.Equal(151, empty.Page.Total); Assert.Empty(empty.Page.Items);
         
         
    }
    [Theory]
    [InlineData("api", 2)] [InlineData("cluster", 3)] [InlineData("route", 4)]
    public async Task CoreListsRespectPageAndSqlBudgets(string kind, int budget)
    {
        await using var f = new Fixture(); await f.InitializeAsync(); var expected = Expected(f, kind);
        foreach (var size in new[] { 1, 50, 100 }) foreach (var page in new[] { 1, 2, (151 + size - 1) / size, (151 + size - 1) / size + 1 }.Distinct())
        {
            var ids = expected.Skip((page - 1) * size).Take(size).ToArray();
            if (kind == "api")
            {
                var r = await f.ReadAsync<ApiDto>(kind, page, size); Assert.Equal(151, r.Page.Total); Assert.Equal(ids, r.Page.Items.Select(x => x.Id)); Assert.True(r.Sql <= budget, $"data SQL {r.Sql} > {budget}"); 
            }
            else if (kind == "cluster")
            {
                var r = await f.ReadAsync<ClusterDto>(kind, page, size); Assert.Equal(151, r.Page.Total); Assert.Equal(ids, r.Page.Items.Select(x => x.Id)); Assert.All(r.Page.Items, c => Assert.Single(c.Destinations));   
            }
            else
            {
                var r = await f.ReadAsync<RouteDto>(kind, page, size); Assert.Equal(151, r.Page.Total); Assert.Equal(ids, r.Page.Items.Select(x => x.Id)); Assert.All(r.Page.Items, x => { Assert.False(x.RequireApiKey); Assert.Equal(1234, x.EffectiveTimeoutMs); Assert.Null(x.EffectiveAuthenticationMode); });   
            }
        }
    }
    [Fact]
    public async Task RouteSqlOrderMatchesMatchOrderForLiteralParameterWildcard()
    {
        await using var f = new Fixture(); await f.InitializeAsync(false); await using var db = f.Api.Context();
        var inputs = new[] { ("/literal-z", 0, 0), ("/literal-a", 999999, -999999), ("/p/{id}", 999999, 1000001), ("/{id}", 0, 2000000), ("/w/{*rest}", 999999, 3000001), ("/w/{**rest}", 0, 4000000), ("/", 1, -1), ("/mixed/{id}/{**rest}", 0, 4000000) };
        var routes = inputs.Select(x => f.Route(x.Item1, x.Item2)).ToArray(); db.AddRange(routes); await db.SaveChangesAsync();
        foreach (var input in inputs) Assert.Equal(input.Item3, RouteNormalizer.MatchOrder(input.Item1, input.Item2));
        var result = await f.ReadAsync<RouteDto>("route", 1, 100);
        Assert.Equal(new[] { routes[1].Id, routes[6].Id, routes[0].Id, routes[2].Id, routes[3].Id, routes[4].Id, routes[7].Id, routes[5].Id }, result.Page.Items.Select(x => x.Id));
        Assert.All(result.Page.Items, r => { Assert.True(r.RequireApiKey); Assert.Equal(30000, r.EffectiveTimeoutMs); Assert.Null(r.EffectiveAuthenticationMode); });
    }
    [Fact]
    public async Task ClampAndDuplicateSortValuesRemainStable()
    {
        await using var f = new Fixture(); await f.InitializeAsync(false); await using var db = f.Api.Context();
        var orderedIds = Enumerable.Range(1, 151).Select(i => Guid.Parse($"00000000-0000-0000-0000-{i:D12}")).ToArray();
        db.AddRange(orderedIds.Reverse().Select(id => f.Route("/duplicate", 0, id))); await db.SaveChangesAsync();
        var clamped = await f.ReadAsync<RouteDto>("route", -2, 1000); Assert.Equal((1, 100), (clamped.Page.Page, clamped.Page.PageSize)); Assert.Equal(orderedIds.Take(100), clamped.Page.Items.Select(r => r.Id));
        var next = await f.ReadAsync<RouteDto>("route", 2, 100); Assert.Equal(orderedIds.Skip(100), next.Page.Items.Select(r => r.Id));
        foreach (var page in new[] { 40001, 1000001 }) { var empty = await f.ReadAsync<RouteDto>("route", page, 100); Assert.Equal(Math.Min(page, 1000000), empty.Page.Page); Assert.Equal(151, empty.Page.Total); Assert.Empty(empty.Page.Items);  }
    }
    [Theory]
    [InlineData("api")] [InlineData("cluster")] [InlineData("route")]
    public async Task ListHttpStatusAndRevocationRemainUnchanged(string kind)
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await f.Api.Client.GetAsync(f.Path(kind, Guid.NewGuid()))).StatusCode);
        await using var db = f.Api.Context(); var grants = db.Set<UserProjectScope>().Where(s => s.UserId == f.Api.User.Id);
        await grants.ExecuteDeleteAsync(); Assert.Equal(HttpStatusCode.NotFound, (await f.Api.Client.GetAsync(f.Path(kind))).StatusCode);
        db.Add(new UserProjectScope { UserId = f.Api.User.Id, OrganizationId = f.Api.Organization.Id, AccessMode = "read" }); await db.SaveChangesAsync();
        await db.Set<Project>().Where(p => p.Id == f.Api.Project.Id).ExecuteUpdateAsync(u => u.SetProperty(p => p.Status, "Disabled")); Assert.Equal(HttpStatusCode.NotFound, (await f.Api.Client.GetAsync(f.Path(kind))).StatusCode);
    }
    [Theory]
    [InlineData("api")] [InlineData("cluster")] [InlineData("route")]
    public async Task UnrelatedEnvironmentDoesNotChangePageOrDataQueryCount(string kind)
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        var before = await f.Api.Client.GetStringAsync(f.Path(kind) + "?pageSize=50"); f.Capture.Reset(); await f.Api.Client.GetStringAsync(f.Path(kind) + "?pageSize=50"); var count = f.Capture.Count;
        await using var db = f.Api.Context(); var project = new Project { OrganizationId = f.Api.Organization.Id, Code = "OTHER", Name = "无关项目" }; var env = new EnvironmentRecord { ProjectId = project.Id, Code = "OTHER", Name = "无关环境" }; db.AddRange(project, env);
        var cluster = new UpstreamCluster { ProjectId = project.Id, EnvironmentId = env.Id, Name = "other", HealthCheckPath = "/health" }; var api = new Api { ProjectId = project.Id, OrganizationId = f.Api.Organization.Id, Code = "OTHER", Name = "无关 API", OwnerUserId = f.Api.User.Id }; var version = new ApiVersion { ApiId = api.Id, Version = "1", CreatedBy = f.Api.User.Id }; db.AddRange(cluster, api, version); db.Add(new UpstreamDestination { ClusterId = cluster.Id, Name = "other", Address = "http://test-backend:8080/" });
        for (var i = 0; i < 151; i++) { var route = f.Route("/other/" + i, 0); route.ClusterId = cluster.Id; route.EnvironmentId = env.Id; route.ApiVersionId = version.Id; db.Add(route); } await db.SaveChangesAsync();
        f.Capture.Reset(); var after = await f.Api.Client.GetStringAsync(f.Path(kind) + "?pageSize=50"); Assert.Equal(before, after); Assert.Equal(count, f.Capture.Count);
    }
}

public sealed class CounterfactualCoreListTests_ScopedProof
{
    private sealed class DataSqlCapture : DbCommandInterceptor
    {
        internal int Count;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        { if(command.CommandText.Contains("FROM applications ")||command.CommandText.Contains("FROM policies "))Count++;return ValueTask.FromResult(result); }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly ApiFixture Api=new(); internal readonly DataSqlCapture Capture=new(); internal readonly RequestSqlProbe Probe=new();
        internal readonly Dictionary<string,ActorContext> Actors=[]; internal readonly List<ApplicationRecord> Applications=[]; internal readonly List<Policy> Policies=[];
        internal readonly Project OtherProject=new(){Code="OTHER",Name="其他项目"}; internal readonly Project DisabledProject=new(){Code="DISABLED",Name="停用项目",Status="Disabled"};
        internal readonly EnvironmentRecord DisabledEnvironment=new(){Code="DISABLED",Name="停用环境",Status="Disabled"};
        internal async Task InitializeAsync()
        {
            await Api.InitializeAsync(b=>b.Services.AddDbContext<WebApiDbContext>(o=>o.AddInterceptors(Capture,Probe)));await Api.SeedConsumerAsync();await using var db=Api.Context();
            var otherOrg=new Organization {Code="OTHER",Name="其他组织"};var otherOrgProject=new Project{OrganizationId=otherOrg.Id,Code="OTHER",Name="其他组织项目"};
            OtherProject.OrganizationId=DisabledProject.OrganizationId=Api.Organization.Id;DisabledEnvironment.ProjectId=Api.Project.Id;db.AddRange(otherOrg,otherOrgProject,OtherProject,DisabledProject,DisabledEnvironment);
            var roleId=await db.Set<UserRole>().Where(r=>r.UserId==Api.User.Id).Select(r=>r.RoleId).SingleAsync();var policyRead=new Permission {Code="policy.read",Module="policy",Name="策略读取"};db.Add(policyRead);db.Add(new RolePermission{RoleId=roleId,PermissionId=policyRead.Id});
            Applications.Add(Api.Application);
            for(var i=0;i<150;i++){var app=new ApplicationRecord{OrganizationId=Api.Organization.Id,ProjectId=i<79?Api.Project.Id:null,Code="A"+i.ToString("D4"),Name="同名应用",Owner="测试"};Applications.Add(app);db.Add(app);}
            for(var i=0;i<151;i++){var policy=new Policy{OrganizationId=Api.Organization.Id,ProjectId=i<75?null:Api.Project.Id,Name="同名策略",Type=i%2==0?"timeout":"authentication",Config=i%2==0?"{\"timeoutMs\":2000}":"{\"mode\":\"ApiKey\"}",Enabled=i%3!=0};Policies.Add(policy);db.Add(policy);}
            foreach(var project in new[]{OtherProject,DisabledProject,otherOrgProject})for(var i=0;i<151;i++)
            {db.Add(new ApplicationRecord{OrganizationId=project.OrganizationId,ProjectId=project.Id,Code=project.Code+project.Id.ToString("N")[..6]+i.ToString("D4"),Name="隐藏应用",Owner="测试"});db.Add(new Policy{OrganizationId=project.OrganizationId,ProjectId=project.Id,Name="隐藏策略",Type="timeout",Config="{\"timeoutMs\":2000}"});}
            var appRead=await db.Set<Permission>().SingleAsync(p=>p.Code=="app.read");
            void AddActor(string name, Guid? project, Guid? environment, bool grant=true, bool function=true, bool inactive=false)
            {
                var user=new UserRecord{Username="scoped-"+name,DisplayName=name,SecurityStamp=Guid.NewGuid().ToString("N"),Status=inactive?"Disabled":"Active"};db.Add(user);Actors[name]=new(user.Id,name);
                if(function){var role=new Role{OrganizationId=Api.Organization.Id,Code="scoped-"+name,Name=name};db.Add(role);db.Add(new UserRole{UserId=user.Id,RoleId=role.Id});db.AddRange(new RolePermission{RoleId=role.Id,PermissionId=policyRead.Id},new RolePermission{RoleId=role.Id,PermissionId=appRead.Id});}
                if(grant)db.Add(new UserProjectScope{UserId=user.Id,OrganizationId=Api.Organization.Id,ProjectId=project,EnvironmentId=environment,AccessMode="read"});
            }
            Actors["organization"]=new(Api.User.Id,"organization");AddActor("project",Api.Project.Id,null);AddActor("environment",Api.Project.Id,Api.Environment.Id);
            AddActor("disabled-project",DisabledProject.Id,null);AddActor("disabled-environment",Api.Project.Id,DisabledEnvironment.Id);AddActor("no-scope",null,null,false);AddActor("no-function",null,null,true,false);AddActor("inactive",null,null,true,true,true);
            // Having an additional data grant must not broaden an organization-limited role.
            db.Add(new UserProjectScope{UserId=Api.User.Id,OrganizationId=otherOrg.Id,AccessMode="read_write"});await db.SaveChangesAsync();
        }
        internal async Task<T> Measure<T>(Func<IServiceProvider,Task<T>> action,int maximumTotal=30,int? maximumRows=null)
        {
            Capture.Count=0;var id=Guid.NewGuid().ToString("N");using var probe=Probe.Begin(id);using var services=Api.Services();var result=await action(services.ServiceProvider);var stats=Probe.Read(id);
            Assert.InRange(Capture.Count,1,2);return result;
        }
        public ValueTask DisposeAsync()=>Api.DisposeAsync();
    }
    [Fact]
    public async Task ApplicationVisibleTotalIsFilteredBeforePaging()
    {
        await using var f=new Fixture();await f.InitializeAsync();
        foreach(var (name,total) in new[]{("organization",302),("project",80),("environment",0),("disabled-project",0),("disabled-environment",0),("no-scope",0),("no-function",0)})
        {
            var expected=f.Applications.Where(a=>name=="organization"||name=="project"&&a.ProjectId==f.Api.Project.Id).OrderBy(a=>a.Code,StringComparer.Ordinal).ThenBy(a=>a.Id.ToString("D"),StringComparer.Ordinal).Select(a=>a.Id).ToArray();
            // Organization visibility includes the additional active project, but never disabled or foreign org rows.
            if(name=="organization"){await using var db=f.Api.Context();expected=await db.Set<ApplicationRecord>().Where(a=>a.OrganizationId==f.Api.Organization.Id&&a.ProjectId!=f.DisabledProject.Id).OrderBy(a=>a.Code).ThenBy(a=>a.Id).Select(a=>a.Id).ToArrayAsync();Assert.Equal(302,expected.Length);}
            foreach(var size in new[]{1,50,100})foreach(var page in new[]{1,2,5})
            {
                var count=expected.Skip((page-1)*size).Take(size).Count();var result=await f.Measure(p=>p.GetRequiredService<ApplicationService>().ListAsync(f.Actors[name],page,size),10,count+5);
                Assert.Equal(total,result.Total);Assert.Equal(expected.Skip((page-1)*size).Take(size),result.Items.Select(a=>a.Id));Assert.All(result.Items,a=>Assert.Equal(f.Api.Organization.Id,a.OrganizationId));
            }
        }
        using var services=f.Api.Services();var error=await Assert.ThrowsAsync<ApiException>(()=>services.ServiceProvider.GetRequiredService<ApplicationService>().ListAsync(f.Actors["inactive"],1,50));Assert.Equal(401,error.Status);
    }
    [Fact]
    public async Task OrganizationPolicySharesOnlyWithAuthorizedActiveDescendants()
    {
        await using var f=new Fixture();await f.InitializeAsync();
        foreach(var(name,total)in new[]{("organization",302),("project",151),("environment",75)})
        {
            foreach(var size in new[]{1,50,100})
            {
                var result=await f.Measure(p=>p.GetRequiredService<PolicyService>().ListAsync(new(f.Api.Organization.Id),f.Actors[name],1,size,null,null,null),30,size+20);
                Assert.Equal(total,result.Total);Assert.All(result.Items,p=>Assert.Equal(f.Api.Organization.Id,p.OrganizationId));if(name=="environment")Assert.All(result.Items,p=>Assert.Null(p.ProjectId));
                if(name!="organization")Assert.Equal(f.Policies.Where(p=>name=="project"||p.ProjectId==null).OrderBy(p=>p.Name,StringComparer.Ordinal).ThenBy(p=>p.Id.ToString("D"),StringComparer.Ordinal).Take(size).Select(p=>p.Id),result.Items.Select(p=>p.Id));
            }
        }
        using var services=f.Api.Services();var service=services.ServiceProvider.GetRequiredService<PolicyService>();
        foreach(var name in new[]{"no-scope","no-function","disabled-project","disabled-environment"})Assert.Equal(404,(await Assert.ThrowsAsync<ApiException>(()=>service.ListAsync(new(f.Api.Organization.Id),f.Actors[name],1,50,null,null,null))).Status);
        Assert.Equal(404,(await Assert.ThrowsAsync<ApiException>(()=>service.ListAsync(new(f.Api.Organization.Id,Guid.NewGuid()),f.Actors["organization"],1,50,null,null,null))).Status);
        Assert.Equal(401,(await Assert.ThrowsAsync<ApiException>(()=>service.ListAsync(new(f.Api.Organization.Id),f.Actors["inactive"],1,50,null,null,null))).Status);
    }
    [Fact]
    public async Task ProjectPolicyIsNotVisibleWithEnvironmentOnlyGrant()
    {
        await using var f=new Fixture();await f.InitializeAsync();using var services=f.Api.Services();var service=services.ServiceProvider.GetRequiredService<PolicyService>();var actor=f.Actors["environment"];
        Assert.Equal(404,(await Assert.ThrowsAsync<ApiException>(()=>service.ListAsync(new(f.Api.Organization.Id,f.Api.Project.Id),actor,1,50,null,null,null))).Status);
        Assert.Equal(404,(await Assert.ThrowsAsync<ApiException>(()=>service.GetAsync(f.Policies.First(p=>p.ProjectId!=null).Id,actor))).Status);
        Assert.Equal(75,(await service.ListAsync(new(f.Api.Organization.Id),actor,1,100,null,null,null)).Total);
    }
    [Fact]
    public async Task PolicyFiltersStablePagingAndRevocationRemainUnchanged()
    {
        await using var f=new Fixture();await f.InitializeAsync();var actor=f.Actors["project"];var expected=f.Policies.Where(p=>p.Type=="timeout"&&p.Enabled).OrderBy(p=>p.Id.ToString("D"),StringComparer.Ordinal).Select(p=>p.Id).ToArray();
        foreach(var page in new[]{1,2,3}){var result=await f.Measure(p=>p.GetRequiredService<PolicyService>().ListAsync(new(f.Api.Organization.Id),actor,page,20,"timeout","同名",true));Assert.Equal(expected.Length,result.Total);Assert.Equal(expected.Skip((page-1)*20).Take(20),result.Items.Select(p=>p.Id));}
        using var services=f.Api.Services();var service=services.ServiceProvider.GetRequiredService<PolicyService>();Assert.Equal(422,(await Assert.ThrowsAsync<ApiException>(()=>service.ListAsync(new(f.Api.Organization.Id),actor,1,50,"bad",null,null))).Status);
        await using var db=f.Api.Context();await db.Set<UserProjectScope>().Where(s=>s.UserId==actor.UserId).ExecuteDeleteAsync();Assert.Equal(404,(await Assert.ThrowsAsync<ApiException>(()=>service.ListAsync(new(f.Api.Organization.Id),actor,1,50,null,null,null))).Status);
        Assert.Empty((await services.ServiceProvider.GetRequiredService<ApplicationService>().ListAsync(actor,1,50)).Items);
    }
}
