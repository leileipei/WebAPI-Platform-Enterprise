using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Settings;
using WebApi.Infrastructure.Sso;
using WebApi.Integration.Tests.Support;
using Xunit;
namespace WebApi.Integration.Tests;
public sealed class SsoLoginAttemptTests
{
    [Fact] public async Task AttemptClaimHasOneWinnerAndSurvivesNewCoordinator()
    {
        await using var f=new SsoFixture();await f.InitializeAsync();var provider=await f.EnableAsync(await f.CreateAsync());
        await using var db=f.Api.Context();var service=new SsoLoginCoordinator(db,new(db),f.Time);var pending=await service.BeginAsync(provider.Id,"/apis");
        async Task<bool> Claim(){await using var context=f.Api.Context();try{await new SsoLoginCoordinator(context,new(context),f.Time).ClaimAsync(pending.AttemptId,provider.Id,pending.Provider.Revision);return true;}catch(ApiException){return false;}}
        var results=await Task.WhenAll(Claim(),Claim(),Claim());Assert.Equal(1,results.Count(result=>result));
        Assert.False(await Claim());Assert.Equal("Processing",(await db.Set<SsoLoginAttempt>().AsNoTracking().SingleAsync()).State);
    }
    [Theory][InlineData("name")][InlineData("default")][InlineData("expired")][InlineData("disabled")]
    public async Task ChangedRevisionAndExpiredAttemptCannotRedeem(string change)
    {
        await using var f=new SsoFixture();await f.InitializeAsync();var provider=await f.EnableAsync(await f.CreateAsync());
        await using var db=f.Api.Context();var service=new SsoLoginCoordinator(db,new(db),f.Time);var pending=await service.BeginAsync(provider.Id,null);
        switch(change){
            case "name":(await f.SendAsync(HttpMethod.Put,$"/{provider.Id}",SsoFixture.Body("改名"),RevisionTag.Format(provider.Revision))).EnsureSuccessStatusCode();break;
            case "default":(await f.SendAsync(HttpMethod.Post,$"/{provider.Id}/default",new{},RevisionTag.Format(provider.Revision))).EnsureSuccessStatusCode();break;
            case "disabled":(await f.SendAsync(HttpMethod.Post,$"/{provider.Id}/disable",new{},RevisionTag.Format(provider.Revision))).EnsureSuccessStatusCode();break;
            default:f.Time.Now+=TimeSpan.FromMinutes(5);break;
        }
        await Assert.ThrowsAsync<ApiException>(()=>service.ClaimAsync(pending.AttemptId,provider.Id,pending.Provider.Revision));
    }
    [Fact] public async Task BeginRejectsUnsafePathsAndDisabledSourcesAndFailStoresOnlySafeCode()
    {
        await using var f=new SsoFixture();await f.InitializeAsync();var provider=await f.CreateAsync();
        await using var db=f.Api.Context();var service=new SsoLoginCoordinator(db,new(db),f.Time);
        await Assert.ThrowsAsync<ApiException>(()=>service.BeginAsync(provider.Id,null));provider=await f.EnableAsync(provider);
        await Assert.ThrowsAsync<ApiException>(()=>service.BeginAsync(provider.Id,"//evil.example/"));
        var pending=await service.BeginAsync(provider.Id,null);await service.FailAsync(pending.AttemptId,"unsafe-subject-or-token");
        var row=await db.Set<SsoLoginAttempt>().AsNoTracking().SingleAsync();Assert.Equal("Failed",row.State);Assert.Equal("protocol_error",row.FailureCode);
    }
}
