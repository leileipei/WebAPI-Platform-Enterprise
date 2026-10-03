using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;
namespace WebApi.Integration.Tests;
public sealed class IdempotencyTests
{
    [Fact] public async Task TwoConcurrentSameKeysReturnOneCommittedResult()
    {
        await using var api=new ApiFixture();await api.InitializeAsync();await api.SeedReleaseAsync();using var login=await api.LoginAsync();var token=await api.CsrfAsync();var key=Guid.NewGuid().ToString("N");
        async Task<HttpResponseMessage> Send() {using var req=new HttpRequestMessage(HttpMethod.Post,$"/api/v1/environments/{api.Environment.Id}/releases") {Content=JsonContent.Create(api.ReleaseRequest())};req.Headers.Add("X-CSRF-Token",token);req.Headers.Add("Idempotency-Key",key);return await api.Client.SendAsync(req);}
        var responses=await Task.WhenAll(Send(),Send());try {Assert.All(responses,r=>Assert.Equal(HttpStatusCode.OK,r.StatusCode));Assert.Equal(await responses[0].Content.ReadAsStringAsync(),await responses[1].Content.ReadAsStringAsync());}finally {foreach(var r in responses) r.Dispose();}
        await using var db=api.Context();Assert.Single(await db.Set<ReleaseRecord>().ToListAsync());Assert.Single(await db.Set<IdempotencyRecord>().ToListAsync());Assert.Equal(1,await db.Set<AuditLog>().CountAsync(a=>a.Action=="release.create"));
        using var different=await ApiFixture.CommandAsync(api.Client,$"/api/v1/environments/{api.Environment.Id}/releases",new {baseConfigVersion=1L,versionIds=new[]{api.Version.Id},resourceRevisions=Array.Empty<object>()},key);Assert.Equal(HttpStatusCode.Conflict,different.StatusCode);
    }
    [Fact] public async Task ImportRetryIsPersistedAndCannotBypassRevokedScope()
    {
        await using var api=new ApiFixture();await api.InitializeAsync();await api.SeedCatalogAsync();using var login=await api.LoginAsync();var key=Guid.NewGuid().ToString("N");var body=new {input=new {projectId=api.Project.Id,environmentId=api.Environment.Id,clusterId=api.Cluster.Id,source=ImportAndCredentialTests.Source},targets=new[]{new {operationId="readOne",newApiCode="IDEMPOTENT",newApiName="幂等",version="1.0.0"}}};
        using var first=await ApiFixture.CommandAsync(api.Client,"/api/v1/openapi/import-commit",body,key);Assert.Equal(HttpStatusCode.OK,first.StatusCode);using var retry=await ApiFixture.CommandAsync(api.Client,"/api/v1/openapi/import-commit",body,key);Assert.Equal(HttpStatusCode.OK,retry.StatusCode);Assert.Equal(await first.Content.ReadAsStringAsync(),await retry.Content.ReadAsStringAsync());
        await using(var db=api.Context()) {Assert.Equal(2,await db.Set<Api>().CountAsync());Assert.Single(await db.Set<IdempotencyRecord>().ToListAsync());Assert.Equal(1,await db.Set<AuditLog>().CountAsync(a=>a.Action=="openapi.import"));await db.Set<UserProjectScope>().ExecuteDeleteAsync();}
        using var denied=await ApiFixture.CommandAsync(api.Client,"/api/v1/openapi/import-commit",body,key);Assert.Equal(HttpStatusCode.Forbidden,denied.StatusCode);
    }
}
