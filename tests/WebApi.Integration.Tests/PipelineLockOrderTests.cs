using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using WebApi.Infrastructure.Delivery;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;
namespace WebApi.Integration.Tests;
public sealed class PipelineLockOrderTests
{
 [Fact] public async Task AllLockEntryPointsRequireCallerOwnedTransaction()
 {await using var s=new PipelineScenario();await s.InitializeEnvironmentsAsync();await using var db=s.Api.Context();var locks=new DeliveryLockCoordinator(db);await Assert.ThrowsAsync<InvalidOperationException>(()=>locks.LockProjectAsync(s.Api.Project.Id,default));await Assert.ThrowsAsync<InvalidOperationException>(()=>locks.LockEnvironmentsAsync(s.Environments,default));await Assert.ThrowsAsync<InvalidOperationException>(()=>locks.LockAsync(s.Environments[0],s.Environments[1],default));}
 [Fact] public async Task ConnectionAndProjectLockPathsSerializeInEitherEnvironmentOrder()
 {await using var s=new PipelineScenario();await s.InitializeEnvironmentsAsync(4);await using var db=s.Api.Context();await using var tx=await db.Database.BeginTransactionAsync();await new DeliveryLockCoordinator(db).LockProjectAsync(s.Api.Project.Id,default);var reached=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var second=Task.Run(async()=>{await using var other=s.Api.Context();await using var transaction=await other.Database.BeginTransactionAsync();reached.SetResult();await new DeliveryLockCoordinator(other).LockAsync(s.Environments[1],s.Environments[0],default);await transaction.CommitAsync();});await reached.Task;Assert.False(await Task.WhenAny(second,Task.Delay(100))==second);await new DeliveryLockCoordinator(db).LockEnvironmentsAsync(s.Environments.Reverse().ToArray(),default);await tx.CommitAsync();await second.WaitAsync(TimeSpan.FromSeconds(10));}
 [Fact] public async Task AcceptanceLockOwnsProjectRowBeforeBusinessRows()
 {await using var s=new PipelineScenario();await s.InitializeEnvironmentsAsync();using var services=s.Api.Services();var db=services.ServiceProvider.GetRequiredService<WebApi.Infrastructure.Persistence.WebApiDbContext>();await using var tx=await db.Database.BeginTransactionAsync();var method=typeof(TestAcceptanceService).GetMethod("LockAsync",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;await (Task)method.Invoke(services.ServiceProvider.GetRequiredService<TestAcceptanceService>(),[s.Environments[0],s.Api.Project.Id,null,CancellationToken.None])!;await using var c=await s.Api.Database.OpenAsync();await using var command=new NpgsqlCommand($"SELECT id FROM projects WHERE id='{s.Api.Project.Id}' FOR UPDATE NOWAIT",c);Assert.Equal("55P03",(await Assert.ThrowsAsync<PostgresException>(()=>command.ExecuteScalarAsync())).SqlState);await tx.RollbackAsync();}
}
