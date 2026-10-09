using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using WebApi.Contracts.Common;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using WebApi.PerformanceHost;
using Xunit;
namespace WebApi.Integration.Tests;
public sealed class PerformanceExplainTests
{
    private static readonly HashSet<string> Empty=[];
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ExplainsRealBoundParametersWithoutChangingResultsOrQueryCounts(bool asynchronous)
    {
        await using var fixture=new PostgresDatabase();await fixture.InitializeAsync();
        var explain=new ExplainQueryProbe(fixture.ConnectionString,Empty,Empty);var sql=new RequestSqlProbe();
        using var scope=explain.Begin("parameters");using var queryScope=sql.Begin("parameters");
        await using var db=new WebApiDbContext(new DbContextOptionsBuilder<WebApiDbContext>().UseNpgsql(fixture.ConnectionString).AddInterceptors(sql,explain).Options);
        const string canary="explain-parameter-secret-canary";
        for(var i=0;i<2;i++){
            var query=db.Database.SqlQueryRaw<string>("SELECT @secret AS \"Value\"",new NpgsqlParameter("secret",canary));
            Assert.Equal(canary,asynchronous?await query.SingleAsync():query.Single());
        }
        var evidence=explain.Read("parameters");Assert.Equal(1,evidence.Explained);Assert.Equal(1,evidence.Repeated);
        Assert.Equal(2,sql.Read("parameters").Total);Assert.Equal(2,sql.Read("parameters").Rows);
        var shape=Assert.Single(sql.ReadShapes("parameters"));Assert.Equal(shape.Hash,Assert.Single(evidence.Queries).Hash);
        Assert.Equal(1,evidence.Queries[0].Plan.Plan.Metrics["Actual Rows"]);Assert.True(evidence.Queries[0].Plan.ExecutionMs>=0);
        var exported=JsonSerializer.Serialize(evidence,CanonicalJson.Options);Assert.DoesNotContain(canary,exported);Assert.DoesNotContain("SELECT",exported);Assert.DoesNotContain("@secret",exported);
    }
    [Fact]
    public async Task ReadOnlyExplainRejectsVolatileWritesBeforeRealQueryAndDoesNotLeakError()
    {
        await using var fixture=new PostgresDatabase();await fixture.InitializeAsync();await using(var setup=fixture.Context()){
            await setup.Database.ExecuteSqlRawAsync("CREATE TABLE explain_write_guard (id int); CREATE FUNCTION explain_write_guard_fn() RETURNS int LANGUAGE plpgsql AS 'BEGIN INSERT INTO explain_write_guard VALUES (1); RETURN 1; END'");
        }
        var explain=new ExplainQueryProbe(fixture.ConnectionString,Empty,Empty);using var scope=explain.Begin("write-function");
        await using var db=new WebApiDbContext(new DbContextOptionsBuilder<WebApiDbContext>().UseNpgsql(fixture.ConnectionString).AddInterceptors(explain).Options);
        var error=await Assert.ThrowsAsync<InvalidDataException>(()=>db.Database.SqlQueryRaw<int>("SELECT explain_write_guard_fn() AS \"Value\"").SingleAsync());
        Assert.Equal("EXPLAIN diagnostic failed.",error.Message);Assert.Null(error.InnerException);
        Assert.Equal(0,await db.Database.SqlQueryRaw<int>("SELECT COUNT(*)::int AS \"Value\" FROM explain_write_guard").SingleAsync());
    }
    [Fact]
    public async Task DiagnosticsAreRequestScopedAndLeaveProviderWriteReadersNative()
    {
        await using var fixture=new PostgresDatabase();await fixture.InitializeAsync();await using(var setup=fixture.Context())await setup.Database.MigrateAsync();
        var explain=new ExplainQueryProbe(fixture.ConnectionString,new HashSet<string>{"organizations"},Empty);
        async Task Query(string id,int value){using var scope=explain.Begin(id);await using var db=new WebApiDbContext(new DbContextOptionsBuilder<WebApiDbContext>().UseNpgsql(fixture.ConnectionString).AddInterceptors(explain).Options);Assert.Equal(value,await db.Database.SqlQueryRaw<int>("SELECT @value AS \"Value\"",new NpgsqlParameter("value",value)).SingleAsync());}
        await Task.WhenAll(Query("A",17),Query("B",29));Assert.Equal(1,explain.Read("A").Explained);Assert.Equal(1,explain.Read("B").Explained);
        using(var scope=explain.Begin("writer")){
            await using var db=new WebApiDbContext(new DbContextOptionsBuilder<WebApiDbContext>().UseNpgsql(fixture.ConnectionString).AddInterceptors(explain).Options);
            db.Add(new Organization{Code="explain-write",Name="Explain write"});Assert.Equal(1,await db.SaveChangesAsync());Assert.Equal(0,explain.Read("writer").Explained);
            Assert.Single(await db.Set<Organization>().AsNoTracking().ToArrayAsync());Assert.Equal(1,explain.Read("writer").Explained);
        }
        await using(var unscoped=new WebApiDbContext(new DbContextOptionsBuilder<WebApiDbContext>().UseNpgsql(fixture.ConnectionString).AddInterceptors(explain).Options))Assert.Equal(37,await unscoped.Database.SqlQueryRaw<int>("SELECT 37 AS \"Value\"").SingleAsync());
        Assert.Equal(0,explain.Read("unknown").Explained);Assert.Throws<InvalidOperationException>(()=>explain.Begin("A"));
    }
    [Fact]
    public void PlanProjectionDropsExpressionsAndUntrustedIdentifiersButRetainsNumericBuffers()
    {
        const string json="""[{"Planning Time":0.3,"Execution Time":1.2,"Plan":{"Node Type":"Index Scan","Relation Name":"organizations","Index Name":"trusted_index","Alias":"private-canary","Index Cond":"(secret = 'private-canary')","Filter":"private-canary","Actual Rows":7,"Actual Loops":1,"Shared Hit Blocks":12,"Shared Read Blocks":2,"Plans":[{"Node Type":"Result","Relation Name":"private-canary","Index Name":"private-canary","Output":["private-canary"],"Actual Rows":7}]}}]""";
        var evidence=ExplainQueryProbe.Parse(json,new HashSet<string>{"organizations"},new HashSet<string>{"trusted_index"});
        Assert.Equal("organizations",evidence.Plan.Relation);Assert.Equal("trusted_index",evidence.Plan.Index);Assert.Equal(12,evidence.Plan.Metrics["Shared Hit Blocks"]);
        Assert.Null(Assert.Single(evidence.Plan.Children).Relation);Assert.Null(evidence.Plan.Children[0].Index);
        var exported=JsonSerializer.Serialize(evidence,CanonicalJson.Options);Assert.DoesNotContain("private-canary",exported);Assert.DoesNotContain("Index Cond",exported);Assert.DoesNotContain("Output",exported);
    }
    [Fact]
    public void PlanProjectionRejectsInvalidOrUnboundedPlans()
    {
        foreach(var json in new[]{"[]","{}","[{\"Planning Time\":0,\"Execution Time\":1,\"Plan\":{\"Node Type\":\"private-canary\"}}]","[{\"Planning Time\":0,\"Execution Time\":1,\"Plan\":{\"Node Type\":\"Result\",\"Actual Rows\":-1}}]"})Assert.Throws<InvalidDataException>(()=>ExplainQueryProbe.Parse(json,Empty,Empty));
        Assert.Throws<InvalidDataException>(()=>ExplainQueryProbe.Parse(new string(' ',8*1024*1024+1),Empty,Empty));
    }
}
