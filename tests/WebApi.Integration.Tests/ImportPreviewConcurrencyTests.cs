using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Infrastructure.Catalog;
using WebApi.Infrastructure.Persistence.Entities;
using Xunit;
using Npgsql;
using WebApi.Integration.Tests.Support;
namespace WebApi.Integration.Tests;
public sealed class ImportPreviewConcurrencyTests
{
    [Fact] public async Task ConcurrentSixthPreviewCannotExceedUserQuota()
    {
        await using var f=await ImportSessionTests.Setup();for(var i=0;i<4;i++)await ImportSessionTests.Preview(f);
        var responses=await Task.WhenAll(Enumerable.Range(0,2).Select(_=>f.WriteAsync(HttpMethod.Post,"/api/v1/openapi/import-previews",ImportSessionTests.Input(f))));try{Assert.Single(responses,x=>x.IsSuccessStatusCode);Assert.Single(responses,x=>(int)x.StatusCode==429);}finally{foreach(var r in responses)r.Dispose();}await using var db=f.Context();Assert.Equal(5,await db.Set<ApiImportPreview>().CountAsync(x=>x.Status=="Active"));
    }
    [Fact] public async Task ConcurrentProject101stPreviewCannotExceedProjectQuota()
    {
        await using var f=await ImportSessionTests.Setup();await ImportSessionTests.Preview(f);await using(var db=f.Context()){var template=await db.Set<ApiImportPreview>().AsNoTracking().SingleAsync();for(var u=0;u<20;u++){var actor=new UserRecord{Username="quota_"+Guid.NewGuid().ToString("N"),DisplayName="Quota"};db.Add(actor);for(var n=0;n<(u==19?3:5);n++)db.Add(new ApiImportPreview{ActorId=actor.Id,OrganizationId=template.OrganizationId,ProjectId=template.ProjectId,EnvironmentId=template.EnvironmentId,ClusterId=template.ClusterId,SourceHash=template.SourceHash,BundleHash=template.BundleHash,CreatedAt=DateTimeOffset.UtcNow,ExpiresAt=DateTimeOffset.UtcNow.AddMinutes(20)});}await db.SaveChangesAsync();Assert.Equal(99,await db.Set<ApiImportPreview>().CountAsync());}
        var responses=await Task.WhenAll(Enumerable.Range(0,2).Select(_=>f.WriteAsync(HttpMethod.Post,"/api/v1/openapi/import-previews",ImportSessionTests.Input(f))));try{Assert.Single(responses,x=>x.IsSuccessStatusCode);Assert.Single(responses,x=>(int)x.StatusCode==429);}finally{foreach(var r in responses)r.Dispose();}await using var read=f.Context();Assert.Equal(100,await read.Set<ApiImportPreview>().CountAsync());
    }
    [Fact] public async Task RevokeAndCommitAreAtomicUnderContention()
    {
        await using var f=await ImportSessionTests.Setup();var p=await ImportSessionTests.Preview(f);var path=$"/api/v1/openapi/import-previews/{p.PreviewId}";
        using var hold=f.Context();await using var tx=await hold.Database.BeginTransactionAsync();await hold.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(8901202)");var commit=f.WriteAsync(HttpMethod.Post,path+"/commit",ImportSessionTests.Commit(p));var revoke=f.WriteAsync(HttpMethod.Delete,path);await WaitForGovernanceWaiters(f,2);await tx.CommitAsync();using var c=await commit;using var r=await revoke;
        await using var db=f.Context();var row=await db.Set<ApiImportPreview>().SingleAsync();if(c.IsSuccessStatusCode){Assert.Equal("Committed",row.Status);Assert.Equal(HttpStatusCode.Conflict,r.StatusCode);Assert.Equal(2,await db.Set<Api>().CountAsync());Assert.Single(await db.Set<ApiRoute>().ToArrayAsync());}else{Assert.Equal("Revoked",row.Status);Assert.Equal(HttpStatusCode.Conflict,c.StatusCode);Assert.Equal(1,await db.Set<Api>().CountAsync());Assert.Empty(await db.Set<ApiRoute>().ToArrayAsync());}
    }
    [Fact] public async Task CleanupIsBoundedAndDropsOnlyUncommittedExpiredRows()
    {
        await using var f=await ImportSessionTests.Setup();for(var i=0;i<3;i++)await ImportSessionTests.Preview(f);await using(var db=f.Context())await db.Set<ApiImportPreview>().ExecuteUpdateAsync(s=>s.SetProperty(x=>x.CreatedAt,DateTimeOffset.UtcNow.AddHours(-2)).SetProperty(x=>x.ExpiresAt,DateTimeOffset.UtcNow.AddHours(-1)));
        using(var scope=f.Services())Assert.Equal(2,await scope.ServiceProvider.GetRequiredService<ImportPreviewCleanupService>().RunAsync(2,default));await using var read=f.Context();Assert.Equal(1,await read.Set<ApiImportPreview>().CountAsync());
    }
    [Fact] public async Task CleanupAndExpiredCommitCannotWritePartialContent()
    {
        await using var f=await ImportSessionTests.Setup();var p=await ImportSessionTests.Preview(f);await using(var db=f.Context())await db.Set<ApiImportPreview>().ExecuteUpdateAsync(s=>s.SetProperty(x=>x.CreatedAt,DateTimeOffset.UtcNow.AddHours(-2)).SetProperty(x=>x.ExpiresAt,DateTimeOffset.UtcNow.AddHours(-1)));
        using var hold=f.Context();await using var tx=await hold.Database.BeginTransactionAsync();await hold.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(8901202)");using var services=f.Services();var commit=f.WriteAsync(HttpMethod.Post,$"/api/v1/openapi/import-previews/{p.PreviewId}/commit",ImportSessionTests.Commit(p));var cleanup=services.ServiceProvider.GetRequiredService<ImportPreviewCleanupService>().RunAsync(100,default);await WaitForGovernanceWaiters(f,2);await tx.CommitAsync();using var result=await commit;Assert.Contains(result.StatusCode,new[]{HttpStatusCode.Conflict,HttpStatusCode.NotFound});Assert.Equal(1,await cleanup);await using var read=f.Context();Assert.Equal(1,await read.Set<Api>().CountAsync());Assert.Empty(await read.Set<ApiRoute>().ToArrayAsync());Assert.Empty(await read.Set<ApiVersionContractSources>().ToArrayAsync());Assert.Empty(await read.Set<ApiImportPreview>().ToArrayAsync());
    }
    private static async Task WaitForGovernanceWaiters(ApiFixture f,int count)
    {
        await using var connection=await f.Database.OpenAsync();var deadline=System.Diagnostics.Stopwatch.StartNew();while(deadline.Elapsed<TimeSpan.FromSeconds(3)){await using var check=new NpgsqlCommand("SELECT count(*)::int FROM pg_stat_activity WHERE datname=current_database() AND wait_event='advisory'",connection);if((int)(await check.ExecuteScalarAsync())!>=count)return;await Task.Delay(10);}Assert.Fail("Both commands must be blocked on the real governance transaction.");
    }
}
