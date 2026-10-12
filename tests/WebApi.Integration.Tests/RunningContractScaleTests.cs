using System.Data.Common;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using WebApi.Contracts.Common;
using WebApi.Contracts.Governance;
using WebApi.Contracts.Releases;
using WebApi.Contracts.Runtime;
using WebApi.Infrastructure.Catalog;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using WebApi.PerformanceHost;
using Xunit;
using Xunit.Abstractions;
namespace WebApi.Integration.Tests;

public sealed class RunningContractScaleTests(ITestOutputHelper output)
{
    private sealed class ContractSql : DbCommandInterceptor
    {
        public string? Text;
        public NpgsqlParameter[] Parameters=[];
        public int Bodies;
        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command,CommandExecutedEventData data,DbDataReader result,CancellationToken ct=default)
        {
            if(command.CommandText.Contains("release_frozen_contracts")&&Enumerable.Range(0,result.FieldCount).Any(i=>result.GetName(i)=="frozen_json"))
            {
                Bodies++;Text=command.CommandText;Parameters=command.Parameters.Cast<NpgsqlParameter>().Select(p=>new NpgsqlParameter(p.ParameterName,p.NpgsqlDbType){Value=p.Value}).ToArray();
            }
            return ValueTask.FromResult(result);
        }
    }
    [Theory]
    [InlineData(100)]
    [InlineData(1000)]
    [InlineData(10000)]
    public async Task HistoryGrowthKeepsReadsBoundedAndUsesLookupIndex(int history)
    {
        var probe=new RequestSqlProbe();var sql=new ContractSql();await using var s=new DeploymentScenario();
        await s.InitializeAsync(b=>b.Services.AddDbContext<WebApiDbContext>(o=>o.AddInterceptors(probe,sql)));
        const string document="""{"openapi":"3.0.3","info":{"title":"Scale","version":"1"},"paths":{"/orders":{"get":{"responses":{"200":{"description":"ok"}}}}}}""";
        await using(var db=s.Api.Context())
        {
            var v=await db.Set<ApiVersion>().SingleAsync();v.OpenapiDocument=document;v.OpenapiSource=document;
            (await db.Set<EnvironmentRecord>().SingleAsync()).GatewayPublicUrl="https://api.test";await db.SaveChangesAsync();
        }
        var release=await s.PublishAsync(false);var nowVersion=(long)history+1;
        await using(var db=s.Api.Context())
        {
            // A controlled, confirmed high-version fixture: snapshot and ACKs use the
            // same actual hash/version. This is a query-scale diagnostic, not a series
            // of 10,000 executed deployments or a production traffic benchmark.
            var r=await db.Set<ReleaseRecord>().SingleAsync(x=>x.Id==release);var config=await db.Set<GatewayConfigVersion>().SingleAsync();var snapshot=await db.Set<GatewayConfigSnapshot>().SingleAsync();
            var runtime=JsonSerializer.Deserialize<RuntimeSnapshot>(snapshot.PayloadBytes,CanonicalJson.Options)!;
            snapshot.PayloadBytes=CanonicalJson.Serialize(runtime with {ConfigVersion=nowVersion});snapshot.Payload=Encoding.UTF8.GetString(snapshot.PayloadBytes);snapshot.SizeBytes=snapshot.PayloadBytes.Length;config.SnapshotHash=Convert.ToHexStringLower(SHA256.HashData(snapshot.PayloadBytes));config.VersionNo=nowVersion;
            r.ToConfigVersion=nowVersion;(await db.Set<EnvironmentRecord>().SingleAsync()).DesiredConfigVersion=nowVersion;
            (await db.Set<ReleaseFrozenContract>().SingleAsync()).ConfigVersion=nowVersion;foreach(var node in await db.Set<GatewayNode>().ToArrayAsync())node.TargetConfigVersion=nowVersion;await db.SaveChangesAsync();
            var frozen=JsonSerializer.Deserialize<FrozenReleaseCandidate>(r.CandidateBytes!,CanonicalJson.Options)!;var body=Encoding.UTF8.GetString(CanonicalJson.Serialize(frozen.Versions[0]));
            // Actual relational rows and JSON bodies, with unique historical config versions.
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO release_records(id,environment_id,release_no,from_config_version,to_config_version,release_type,status,requested_by,created_at,baseline_config_version,candidate_bytes,approval_policy,deployment_sequence) SELECT gen_random_uuid(),{s.Api.Environment.Id},'SCALE-'||i,0,i,'publish','Failed',{s.Api.User.Id},now(),0,{r.CandidateBytes},'[]'::jsonb,-i FROM generate_series(1,{history}) i");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO release_frozen_contracts(release_id,ordinal,environment_id,config_version,api_id,api_version_id,frozen_json) SELECT id,1,environment_id,to_config_version,{s.Api.Api.Id},{s.Api.Version.Id},{body}::jsonb FROM release_records WHERE release_no LIKE 'SCALE-%'");
            await db.Database.ExecuteSqlRawAsync("ANALYZE release_records; ANALYZE release_frozen_contracts");
        }
        await s.AckAllAsync(release);sql.Bodies=0;
        using var services=s.Api.Services();var actor=new WebApi.Contracts.Security.ActorContext(s.Api.User.Id,"scale");
        var addressId=Guid.NewGuid().ToString("N");using(probe.Begin(addressId))
        {
            var address=await services.ServiceProvider.GetRequiredService<EnvironmentApiAddressService>().GetAsync(s.Api.Environment.Id,s.Api.Api.Id,null,AccessView.Running,actor);Assert.Single(address.Routes);
        }
        Assert.Equal(0,sql.Bodies);var addresses=probe.Read(addressId);Assert.InRange(addresses.Rows,1,300);
        var request=Guid.NewGuid().ToString("N");var clock=Stopwatch.StartNew();long responseBytes;
        using(probe.Begin(request))
        {
            using var doc=await services.ServiceProvider.GetRequiredService<EnvironmentApiDocumentService>().BuildAsync(s.Api.Environment.Id,s.Api.Api.Id,null,AccessView.Running,actor);
            Assert.Equal("Scale",doc.RootElement.GetProperty("info").GetProperty("title").GetString());responseBytes=Encoding.UTF8.GetByteCount(doc.RootElement.GetRawText());
        }
        clock.Stop();var stats=probe.Read(request);Assert.Equal(1,sql.Bodies);Assert.InRange(stats.Rows,1,300);Assert.InRange(responseBytes,1,2000);
        await using var connection=await s.Api.Database.OpenAsync();await using var explain=new NpgsqlCommand("EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) "+sql.Text,connection);explain.Parameters.AddRange(sql.Parameters);
        var plan=(string)(await explain.ExecuteScalarAsync())!;
        output.WriteLine("RUNNING_CONTRACT_SCALE "+JsonSerializer.Serialize(new{history,addressSql=addresses.Total,addressRows=addresses.Rows,addressBinaryBytes=addresses.BinaryColumnBytes,documentSql=stats.Total,documentRows=stats.Rows,documentBinaryBytes=stats.BinaryColumnBytes,responseBytes,elapsedMs=clock.Elapsed.TotalMilliseconds,contractBodies=sql.Bodies,plan=JsonDocument.Parse(plan).RootElement}));
        output.WriteLine("CONTRACT_QUERY "+sql.Text);
        Assert.Contains("ix_frozen_contract_lookup",plan);
        using var parsed=JsonDocument.Parse(plan);var nodes=Walk(parsed.RootElement[0].GetProperty("Plan")).ToArray();
        var contractScan=Assert.Single(nodes,n=>n.TryGetProperty("Relation Name",out var relation)&&relation.GetString()=="release_frozen_contracts");
        Assert.Equal("ix_frozen_contract_lookup",contractScan.GetProperty("Index Name").GetString());Assert.Equal(1,contractScan.GetProperty("Actual Rows").GetInt32());Assert.Equal(1,contractScan.GetProperty("Actual Loops").GetInt32());
        if(history>=1000)Assert.All(nodes.Where(n=>n.TryGetProperty("Relation Name",out var relation)&&relation.GetString()=="release_records"),n=>{Assert.Equal("Index Scan",n.GetProperty("Node Type").GetString());Assert.Equal(1,n.GetProperty("Actual Rows").GetInt32());Assert.Equal(1,n.GetProperty("Actual Loops").GetInt32());});
    }
    private static IEnumerable<JsonElement> Walk(JsonElement node)
    {
        yield return node;if(node.TryGetProperty("Plans",out var children))foreach(var child in children.EnumerateArray())foreach(var item in Walk(child))yield return item;
    }
}
