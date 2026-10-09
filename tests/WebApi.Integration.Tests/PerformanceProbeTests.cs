using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using WebApi.Contracts.Common;
using WebApi.Contracts.Policies;
using WebApi.Domain.Policies;
using WebApi.Gateway.Security;
using WebApi.Domain.Runtime;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Applications;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Policies;
using WebApi.Infrastructure.Security;
using WebApi.Contracts.Security;
using WebApi.Integration.Tests.Support;
using WebApi.PerformanceHost;
using Xunit;
using Xunit.Abstractions;
namespace WebApi.Integration.Tests;
public sealed class PerformanceProbeTests(ITestOutputHelper output)
{
    [Fact]
    public async Task ProbeCountsActualBinaryPayloadBytesWithoutExportingValues()
    {
        await using var fixture=new PostgresDatabase();await fixture.InitializeAsync();
        var probe=new RequestSqlProbe();using var scope=probe.Begin("binary");
        await using var db=new WebApiDbContext(new DbContextOptionsBuilder<WebApiDbContext>().UseNpgsql(fixture.ConnectionString).AddInterceptors(probe).Options);
        var payload=System.Text.Encoding.UTF8.GetBytes("private-payload-canary");
        Assert.Equal(payload,await db.Database.SqlQueryRaw<byte[]>("SELECT @payload AS \"Value\"",new NpgsqlParameter("payload",payload)).SingleAsync());
        using var evidence=JsonDocument.Parse(JsonSerializer.Serialize(probe.Read("binary"),CanonicalJson.Options));
        Assert.Equal(payload.Length,evidence.RootElement.GetProperty("binaryColumnBytes").GetInt64());
        Assert.DoesNotContain("private-payload-canary",evidence.RootElement.ToString());
    }
    [Fact]
    public async Task ReferenceSceneIncludesProtectedFrozenAndMultipleValidRuntimeEnvironments()
    {
        await using var fixture=new PostgresDatabase();await fixture.InitializeAsync();await using var db=fixture.Context();await db.Database.MigrateAsync();
        var directory=Path.Combine(Path.GetTempPath(),"performance-reference-"+Guid.NewGuid().ToString("N"));
        try {
            var now=DateTimeOffset.UtcNow;var spec=new DatasetSpec(100,20261009,now.AddMinutes(-1),now.AddHours(20));
            var manifest=await PerformanceDataset.CreateReferenceSceneAsync(db,spec,directory,CancellationToken.None);
            Assert.True(manifest.Validated);Assert.Equal(116,manifest.Working);Assert.Equal(100,manifest.FrozenRelease);Assert.Equal(3,manifest.Runtime);
            var auth=new AuthorizationService(db);var scopes=new ScopeResolver(db);var references=new PolicyReferenceService(db,new PolicyAccess(db,auth,scopes),auth,scopes);
            var protectedReferences=await references.FindProtectedAsync(manifest.PolicyId);Assert.Equal(219,protectedReferences.Count);
            var admin=await references.ListAsync(manifest.PolicyId,new(ResourceId("user",0),"reference-admin"),1,100);Assert.Equal(219,admin.Total);
            Assert.Equal(manifest.Actors!.Single(a=>a.Kind=="platform-role").Expected.Take(100),admin.Items);
            var reader=await references.ListAsync(manifest.PolicyId,new(ResourceId("user",2),"reference-reader"),1,100);Assert.Equal(201,reader.Total);
            Assert.Equal(manifest.Actors!.Single(a=>a.Kind=="environment-read-only").Expected.Take(100),reader.Items);
            Assert.All(reader.Items,r=>Assert.Equal(ResourceId("environment",0),r.EnvironmentId));
            foreach(var actor in manifest.Actors!) {
                var actorId=actor.Kind=="platform-role"?ResourceId("user",0):ResourceId("user",2);
                for(var page=1;page<=Math.Ceiling(actor.Expected.Count/100d)+1;page++) {
                    var result=await references.ListAsync(manifest.PolicyId,new(actorId,"reference-dto"),page,100);
                    Assert.Equal(actor.Expected.Count,result.Total);
                    Assert.Equal(actor.Expected.Skip((page-1)*100).Take(100),result.Items);
                }
            }
            Assert.True(manifest.CandidateBytes>0);Assert.True(manifest.RuntimeBytes>0);
            foreach(var bytes in await (from e in db.Set<EnvironmentRecord>() join v in db.Set<GatewayConfigVersion>() on new {EnvironmentId=e.Id,Version=e.DesiredConfigVersion} equals new {v.EnvironmentId,Version=(long?)v.VersionNo} join snapshot in db.Set<GatewayConfigSnapshot>() on v.Id equals snapshot.ConfigVersionId where e.DesiredConfigVersion!=null select new {e.Id,snapshot.PayloadBytes}).ToArrayAsync())SnapshotValidator.ParsePayload(bytes.PayloadBytes,bytes.Id);
            Guid ResourceId(string kind,int index)=>PerformanceDataset.ResourceId(spec.Seed,kind,index);
        }finally{if(Directory.Exists(directory))Directory.Delete(directory,true);}
    }
    [Fact]
    public async Task ScopeSceneHasValidCrossOrganizationGrantsAndExplicitActors()
    {
        await using var fixture=new PostgresDatabase();await fixture.InitializeAsync();await using var db=fixture.Context();await db.Database.MigrateAsync();
        var directory=Path.Combine(Path.GetTempPath(),"performance-scope-"+Guid.NewGuid().ToString("N"));
        try {
            var now=DateTimeOffset.UtcNow;var spec=new DatasetSpec(100,20261009,now.AddMinutes(-1),now.AddHours(20));
            var manifest=await PerformanceDataset.CreateScopeSceneAsync(db,spec,directory,CancellationToken.None);
            Assert.True(manifest.Validated);Assert.Equal(9,manifest.Actors.Count);
            Assert.Equal(10,await db.Set<ApplicationRecord>().Select(a=>a.OrganizationId).Distinct().CountAsync());
            Assert.Equal(50,await db.Set<ApplicationRecord>().Where(a=>a.ProjectId!=null).Select(a=>a.ProjectId).Distinct().CountAsync());
            Assert.Equal(0,await (from grant in db.Set<ApplicationApiPermission>() join app in db.Set<ApplicationRecord>() on grant.ApplicationId equals app.Id join api in db.Set<Api>() on grant.ApiId equals api.Id join env in db.Set<EnvironmentRecord>() on grant.EnvironmentId equals env.Id where app.OrganizationId!=api.OrganizationId||app.ProjectId!=null&&app.ProjectId!=api.ProjectId||env.ProjectId!=api.ProjectId select grant.Id).CountAsync());
            var orgReader=manifest.Actors.Single(a=>a.Kind=="organization-role");
            Assert.Equal(10,orgReader.ApplicationIds.Count);Assert.Equal(10,orgReader.PolicyIds.Count);
            var projectReader=manifest.Actors.Single(a=>a.Kind=="project-read-only");Assert.Single(projectReader.ApplicationIds);Assert.Equal(6,projectReader.PolicyIds.Count);
            var environmentReader=manifest.Actors.Single(a=>a.Kind=="environment-read-only");Assert.Empty(environmentReader.ApplicationIds);Assert.Equal(5,environmentReader.PolicyIds.Count);
            Assert.All(manifest.Actors.Where(a=>a.Kind is "no-function" or "no-scope" or "inactive-ancestor" or "cross-organization-role"),a=>{Assert.Empty(a.ApplicationIds);Assert.Equal(404,a.PolicyStatus);});
            var auth=new AuthorizationService(db);var scopes=new ScopeResolver(db);
            var appService=new ApplicationService(db,auth,scopes,null!,null!);
            var policyService=new PolicyService(db,auth,new PolicyAccess(db,auth,scopes),null!,null!,null!,null!,null!,null!);
            for(var i=0;i<manifest.Actors.Count;i++) {
                var expected=manifest.Actors[i];var actor=new ActorContext(PerformanceDataset.ResourceId(spec.Seed,"user",i),"scope-fixture");
                if(expected.ApplicationStatus==200){var page=await appService.ListAsync(actor,1,100,ct:CancellationToken.None);Assert.Equal(expected.ApplicationIds,page.Items.Select(a=>a.Id).ToArray());Assert.Equal(expected.ApplicationIds.Count,page.Total);}
                else Assert.Equal(expected.ApplicationStatus,(await Assert.ThrowsAsync<ApiException>(()=>appService.ListAsync(actor,1,100))).Status);
                if(expected.PolicyStatus==200){var page=await policyService.ListAsync(new(expected.OrganizationId,null),actor,1,100,null,null,null);Assert.Equal(expected.PolicyIds,page.Items.Select(p=>p.Id).ToArray());Assert.Equal(expected.PolicyIds.Count,page.Total);}
                else Assert.Equal(expected.PolicyStatus,(await Assert.ThrowsAsync<ApiException>(()=>policyService.ListAsync(new(expected.OrganizationId,null),actor,1,100,null,null,null))).Status);
            }
        }finally{if(Directory.Exists(directory))Directory.Delete(directory,true);}
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ProbePreservesProviderWriteReadersAndCountsOnlyQueryRows(bool asynchronous)
    {
        await using var fixture=new PostgresDatabase();await fixture.InitializeAsync();
        await using(var migration=fixture.Context())await migration.Database.MigrateAsync();
        var probe=new RequestSqlProbe();using var scope=probe.Begin("write");
        await using var db=new WebApiDbContext(new DbContextOptionsBuilder<WebApiDbContext>().UseNpgsql(fixture.ConnectionString).AddInterceptors(probe).Options);
        db.Add(new Organization {Code="probe-write",Name="Probe write"});
        Assert.Equal(1,asynchronous?await db.SaveChangesAsync():db.SaveChanges());
        Assert.Equal(1,probe.Read("write").Total);Assert.Equal(0,probe.Read("write").Rows);
        var organizations=asynchronous?await db.Set<Organization>().AsNoTracking().ToArrayAsync():db.Set<Organization>().AsNoTracking().ToArray();
        Assert.Single(organizations);Assert.Equal("probe-write",organizations[0].Code);
        Assert.Equal(2,probe.Read("write").Total);Assert.Equal(1,probe.Read("write").Rows);
    }
    [Fact]
    public async Task ManagementSeedCreatesRealRelationsAndFullFrozenRelease()
    {
        await using var fixture=new PostgresDatabase();await fixture.InitializeAsync();await using var db=fixture.Context();await db.Database.MigrateAsync();
        var directory=Path.Combine(Path.GetTempPath(),"performance-seed-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        try {
            var spec=new DatasetSpec(100,20261009,new(2026,10,8,0,0,0,TimeSpan.Zero),new(2026,10,15,0,0,0,TimeSpan.Zero));
            var manifest=await PerformanceDataset.CreateAsync(db,spec,directory,CancellationToken.None);
            Assert.True(manifest.Validated);Assert.Equal(100,await db.Set<Api>().CountAsync());Assert.Equal(100,await db.Set<ReleaseRecord>().CountAsync());
            Assert.Equal(100,await db.Set<AlertRule>().CountAsync());Assert.Equal(100,await db.Set<ApplicationRecord>().CountAsync());
            Assert.Equal(200,await db.Set<UpstreamDestination>().CountAsync());Assert.Equal(200,await db.Set<ApprovalTask>().CountAsync());
            var release=await db.Set<ReleaseRecord>().AsNoTracking().FirstAsync();
            var candidate=JsonSerializer.Deserialize<WebApi.Contracts.Releases.FrozenReleaseCandidate>(release.CandidateBytes!,CanonicalJson.Options)!;
            Assert.Equal(8,candidate.Versions.Count);Assert.NotEmpty(candidate.Clusters);Assert.NotEmpty(candidate.Applications);
            Assert.Equal(600L,manifest.Counts["releaseTargets"]); // 100 releases x 6 distinct target nodes
            Assert.Equal(10,await db.Set<Organization>().CountAsync());Assert.Equal(50,await db.Set<Project>().CountAsync());Assert.Equal(150,await db.Set<EnvironmentRecord>().CountAsync());
        }finally{Directory.Delete(directory,true);}
    }
    [Fact]
    public async Task CountsConcurrentRequestsAndAllCommandKindsWithoutSecrets()
    {
        await using var fixture=new PostgresDatabase();await fixture.InitializeAsync();
        var probe=new RequestSqlProbe();const string secret="secret-canary-sql-should-never-be-recorded";
        async Task Execute(string id,bool synchronous)
        {
            using var scope=probe.Begin(id);
            await using var db=new WebApiDbContext(new DbContextOptionsBuilder<WebApiDbContext>().UseNpgsql(fixture.ConnectionString).AddInterceptors(probe).Options);
            await db.Database.OpenConnectionAsync();
            if(synchronous) {
                db.Database.ExecuteSqlRaw("CREATE TEMP TABLE probe_test(id int)");
                db.Database.ExecuteSqlRaw("INSERT INTO probe_test VALUES (1)");
                Assert.Equal(1,db.Database.SqlQueryRaw<int>("SELECT id AS \"Value\" FROM probe_test").Single());
                Assert.Equal(secret,db.Database.SqlQueryRaw<string>("SELECT @secret AS \"Value\"",new NpgsqlParameter("secret",secret)).Single());
            } else {
                await db.Database.ExecuteSqlRawAsync("CREATE TEMP TABLE probe_test(id int)");
                Assert.Equal(7,await db.Database.SqlQueryRaw<int>("SELECT 7 AS \"Value\"").SingleAsync());
            }
        }
        await Task.WhenAll(Task.Run(()=>Execute("A",true)),Task.Run(()=>Execute("B",false)));
        Assert.Equal(4,probe.Read("A").Total);Assert.Equal(2,probe.Read("B").Total);
        Assert.Equal(2,probe.Read("A").Rows);Assert.Equal(1,probe.Read("B").Rows);
        Assert.DoesNotContain(secret,JsonSerializer.Serialize(probe.Read("A")));
    }
    [Fact]
    public async Task CountsSynchronousAndAsyncScalarCommands()
    {
        await using var fixture=new PostgresDatabase();await fixture.InitializeAsync();
        var probe=new RequestSqlProbe();using var scope=probe.Begin("scalar");
        await using var db=new WebApiDbContext(new DbContextOptionsBuilder<WebApiDbContext>().UseNpgsql(fixture.ConnectionString).AddInterceptors(probe).Options);
        var command=db.GetService<IRelationalCommandBuilderFactory>().Create().Append("SELECT 9").Build();
        var parameters=new RelationalCommandParameterObject(db.GetService<IRelationalConnection>(),null,null,db,db.GetService<IRelationalCommandDiagnosticsLogger>(),CommandSource.Unknown);
        Assert.Equal(9,Convert.ToInt32(command.ExecuteScalar(parameters)));
        Assert.Equal(9,Convert.ToInt32(await command.ExecuteScalarAsync(parameters)));
        Assert.Equal(2,probe.Read("scalar").Total);
        Assert.All(probe.ReadShapes("scalar"),shape=>Assert.Equal("scalar",shape.Kind));
    }
    [Theory]
    [InlineData(100,100)] [InlineData(1000,1000)] [InlineData(10000,5000)]
    public void FixedScaleSnapshotsFitExistingWireBudget(int scale,int expectedApplications)
    {
        var spec=new DatasetSpec(scale,20261009,new(2026,10,8,0,0,0,TimeSpan.Zero),new(2026,10,15,0,0,0,TimeSpan.Zero));
        var snapshot=PerformanceDataset.SnapshotLowerBound(spec);
        SnapshotValidator.Validate(snapshot,snapshot.EnvironmentId);
        var bytes=CanonicalJson.Serialize(snapshot);
        output.WriteLine($"scale={scale}; routes={snapshot.Routes.Count}; applications={snapshot.Applications.Count}; credentials={snapshot.Applications.Sum(a=>a.Credentials.Count)}; windows={snapshot.Applications.Sum(a=>a.Permissions.Count)}; bytes={bytes.Length}; limit={16*1024*1024}");
        Assert.Equal(scale,snapshot.Routes.Count);
        Assert.Equal(expectedApplications,snapshot.Applications.Count);
        Assert.Equal(expectedApplications*2,snapshot.Applications.Sum(a=>a.Credentials.Count));
        Assert.All(snapshot.Applications,a=>Assert.Equal(8,a.Permissions.Count));
        Assert.True(bytes.Length<=16*1024*1024,$"Fixed scale {scale} exceeds wire budget: {bytes.Length} bytes");
        SnapshotValidator.ParsePayload(bytes,snapshot.EnvironmentId);
    }
    [Fact]
    public void LookupMicroProbeMeasuresActualGenerationAtAllPositions()
    {
        var now=DateTimeOffset.UtcNow;using var keys=PerformanceJwtMaterial.Create();
        var snapshot=PerformanceDataset.BuildSnapshot(new(100,20261009,now.AddMinutes(-1),now.AddHours(20)),AuthenticationMode.JWT,"RS256",keys);
        var results=LookupMicroProbe.Measure(snapshot,CanonicalJson.Serialize(snapshot),100);
        Assert.Equal(21,results.Count);Assert.Equal(new[]{"first","last","middle"},results.Select(r=>r.Position).Distinct().Order().ToArray());
        Assert.All(results,r=>{Assert.Equal(100,r.Hits);Assert.Equal(100,r.Iterations);Assert.True(r.ElapsedTicks>0);Assert.Equal("scan",r.Mode);});
    }
    [Fact]
    public void GenerationLifecycleProbeIncludesHeldLeaseAndCollection()
    {
        var now=DateTimeOffset.UtcNow;var snapshot=PerformanceDataset.SnapshotLowerBound(new(100,20261009,now.AddMinutes(-1),now.AddHours(20)));
        var payload=CanonicalJson.Serialize(snapshot);var result=GenerationProbe.MeasureLifecycle(snapshot,payload);
        Assert.True(result.OldLeaseUsable);Assert.True(result.RetiredRemoved);Assert.True(result.RetiredCollected);
        Assert.True(result.NewBuild.AllocatedBytes>0);Assert.True(result.NewBuild.Elapsed>TimeSpan.Zero);
    }
    [Fact]
    public void GenerationAllocationMeasurementScalesAndPreservesPayload()
    {
        var spec=new DatasetSpec(100,20261009,new(2026,10,8,0,0,0,TimeSpan.Zero),new(2026,10,15,0,0,0,TimeSpan.Zero));
        GenerationMeasurement Measure(int scale)
        {
            var snapshot=PerformanceDataset.SnapshotLowerBound(spec with {Scale=scale});var payload=CanonicalJson.Serialize(snapshot);var before=payload.ToArray();
            var result=GenerationProbe.MeasureBuild(snapshot,new(Guid.NewGuid(),1,1,"fixture-hash",payload.Length),payload);
            Assert.Equal(before,payload);Assert.True(result.Elapsed>TimeSpan.Zero);return result;
        }
        var small=Measure(100);var medium=Measure(1000);
        Assert.True(small.AllocatedBytes>0);Assert.True(medium.AllocatedBytes>small.AllocatedBytes);
    }
    [Theory]
    [InlineData("RS256")] [InlineData("ES256")]
    public void JwtFixtureUsesRealSignaturesAndRejectsUnmappedOrTamperedTokens(string algorithm)
    {
        var start=DateTimeOffset.UtcNow.AddMinutes(-1);var end=start.AddHours(20);var app=Guid.NewGuid();
        using var keys=PerformanceJwtMaterial.Create();
        var config=PolicyConfigurationValidator.ParseAuthentication(keys.Configuration(algorithm,[new("app00000",app)])).Jwt!;
        var token=keys.Token(algorithm,"app00000",start,end);var verifier=new JwtTokenVerifier();
        Assert.True(verifier.Verify(token,config,DateTimeOffset.UtcNow).Success);
        Assert.False(verifier.Verify(token[..^2]+"!!",config,DateTimeOffset.UtcNow).Success);
        Assert.False(verifier.Verify(token,config,end.AddSeconds(1)).Success);
        var unknown=verifier.Verify(keys.Token(algorithm,"appXXXXX",start,end),config,DateTimeOffset.UtcNow);
        Assert.True(unknown.Success);Assert.DoesNotContain(config.ApplicationMappings,m=>m.ClaimValue=="appXXXXX");
    }
    [Theory]
    [InlineData("RS256")] [InlineData("ES256")]
    public void LargestJwtSnapshotFitsBudgetAndGroupsMappingsWithin128(string algorithm)
    {
        var spec=new DatasetSpec(10000,20261009,new(2026,10,8,0,0,0,TimeSpan.Zero),new(2026,10,15,0,0,0,TimeSpan.Zero));
        using var keys=PerformanceJwtMaterial.Create();var snapshot=PerformanceDataset.BuildSnapshot(spec,AuthenticationMode.JWT,algorithm,keys);
        Assert.Equal("2.2",snapshot.SchemaVersion);Assert.Equal(10000,snapshot.Routes.Count);Assert.Equal(5000,snapshot.Applications.Count);
        Assert.Equal(40,snapshot.Policies.Count);
        Assert.All(snapshot.Policies,p=>Assert.InRange(PolicyConfigurationValidator.ParseAuthentication(p.Config).Jwt!.ApplicationMappings.Count,1,128));
        var bytes=CanonicalJson.Serialize(snapshot);output.WriteLine($"L JWT {algorithm}: bytes={bytes.Length}, limit={16*1024*1024}");
        SnapshotValidator.ParsePayload(bytes,snapshot.EnvironmentId);
    }
}
