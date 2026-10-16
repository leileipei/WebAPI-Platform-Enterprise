using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Contracts.Common;
using WebApi.Contracts.Runtime;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Security;
using WebApi.Integration.Tests.Support;
using Xunit;
namespace WebApi.Integration.Tests;
public sealed class CredentialImpactTests
{
    private sealed class Clock:TimeProvider{public DateTimeOffset Now=DateTimeOffset.UtcNow;public override DateTimeOffset GetUtcNow()=>Now;}
    private sealed class Fixture:IAsyncDisposable
    {
        public readonly ApiFixture Api=new();public readonly Clock Time=new();public ApplicationCredential Key=null!;public ApplicationCredential? Other;
        public async Task InitializeAsync(bool replacement=false,bool published=false)
        {
            await Api.InitializeAsync(b=>b.Services.AddSingleton<TimeProvider>(Time));await Api.SeedConsumerAsync();using var login=await Api.LoginAsync();login.EnsureSuccessStatusCode();
            await using var db=Api.Context();var material=ApiKeySecret.Create();Key=new(){ApplicationId=Api.Application.Id,AccessKey=material.AccessKey,SecretHash=material.Hash,SecretLast4=material.Last4,ValidFrom=Time.Now.AddHours(-1),ExpiresAt=Time.Now.AddHours(1)};db.Add(Key);
            if(replacement){Other=new(){ApplicationId=Api.Application.Id,AccessKey="replacement",SecretHash=new string('b',64),SecretLast4="5678",ValidFrom=Time.Now.AddMinutes(-1),ExpiresAt=Time.Now.AddHours(2)};db.Add(Other);}
            if(published)
            {
                var payload=JsonSerializer.SerializeToUtf8Bytes(new RuntimeSnapshot("2.0",Api.Environment.Id,1,Time.Now,[],[],[],[new RuntimeApplication(Api.Application.Id,"Active",[new RuntimeCredential(Key.Id,Key.AccessKey,Key.SecretHash,"Active",Key.ValidFrom,Key.ExpiresAt)],[])]),CanonicalJson.Options);
                var version=new GatewayConfigVersion{EnvironmentId=Api.Environment.Id,CreatedBy=Api.User.Id,VersionNo=1,SnapshotHash=Convert.ToHexStringLower(SHA256.HashData(payload))};db.Add(version);db.Add(new GatewayConfigSnapshot{ConfigVersionId=version.Id,PayloadBytes=payload,SizeBytes=payload.Length});var env=await db.Set<EnvironmentRecord>().SingleAsync();env.DesiredConfigVersion=1;env.DeploymentSequence=1;
            }
            await db.SaveChangesAsync();
        }
        public object Request(string status="Revoked",string? hash=null,bool confirm=false,DateTimeOffset? from=null,DateTimeOffset? expires=null)=>new{validFrom=from??Key.ValidFrom,expiresAt=expires??Key.ExpiresAt,status,impactConfirmation=hash is null?null:new{impactHash=hash,confirmCloseAllAccess=confirm}};
        public async Task<JsonElement> PreviewAsync(object? request=null,string? tag=null)
        {var input=JsonSerializer.SerializeToElement(request??Request());var body=new{validFrom=input.GetProperty("validFrom").GetDateTimeOffset(),expiresAt=input.GetProperty("expiresAt").GetDateTimeOffset(),status=input.GetProperty("status").GetString()};using var response=await Api.WriteAsync(HttpMethod.Post,$"/api/v1/credentials/{Key.Id}/impact",body,tag??RevisionTag.Format(Key.Revision));response.EnsureSuccessStatusCode();return await response.Content.ReadFromJsonAsync<JsonElement>();}
        public Task<HttpResponseMessage> UpdateAsync(object body,string? tag=null)=>Api.WriteAsync(HttpMethod.Put,$"/api/v1/credentials/{Key.Id}",body,tag??RevisionTag.Format(Key.Revision));
        public ValueTask DisposeAsync()=>Api.DisposeAsync();
    }
    [Fact]
    public async Task Unpublished_replacement_does_not_remove_running_risk()
    {
        await using var f=new Fixture();await f.InitializeAsync(replacement:true,published:true);var preview=await f.PreviewAsync();Assert.False(preview.GetProperty("lastWorkingCredential").GetBoolean());Assert.Contains(preview.GetProperty("atRiskVisibleEnvironmentIds").EnumerateArray(),x=>x.GetGuid()==f.Api.Environment.Id);
        using var missing=await f.UpdateAsync(f.Request());Assert.Equal(HttpStatusCode.Conflict,missing.StatusCode);
    }
    [Fact]
    public async Task Explicit_close_all_requires_authorized_confirmation()
    {
        await using var f=new Fixture();await f.InitializeAsync();var preview=await f.PreviewAsync();Assert.True(preview.GetProperty("lastWorkingCredential").GetBoolean());var hash=preview.GetProperty("impactHash").GetString()!;
        using(var missing=await f.UpdateAsync(f.Request()))Assert.Equal(HttpStatusCode.Conflict,missing.StatusCode);
        using(var noClose=await f.UpdateAsync(f.Request(hash:hash)))Assert.Equal(HttpStatusCode.Conflict,noClose.StatusCode);
        using var success=await f.UpdateAsync(f.Request(hash:hash,confirm:true));success.EnsureSuccessStatusCode();await using var db=f.Api.Context();Assert.Equal("Revoked",(await db.Set<ApplicationCredential>().SingleAsync()).Status);
    }
    [Theory][InlineData("revision")][InlineData("window")][InlineData("time")][InlineData("publish")][InlineData("permission")]
    public async Task Changed_revision_or_window_invalidates_impact_hash(string change)
    {
        await using var f=new Fixture();await f.InitializeAsync(replacement:true,published:true);if(change=="time"){await using var before=f.Api.Context();await before.Set<ApplicationCredential>().Where(x=>x.Id==f.Other!.Id).ExecuteUpdateAsync(s=>s.SetProperty(x=>x.ExpiresAt,f.Time.Now.AddMinutes(1)));}
        var preview=await f.PreviewAsync();var hash=preview.GetProperty("impactHash").GetString()!;
        await using(var db=f.Api.Context())
        {
            if(change=="revision")await db.Set<ApplicationRecord>().ExecuteUpdateAsync(s=>s.SetProperty(x=>x.Revision,x=>x.Revision+1));
            if(change=="window")await db.Set<ApplicationCredential>().Where(x=>x.Id==f.Other!.Id).ExecuteUpdateAsync(s=>s.SetProperty(x=>x.ValidFrom,f.Time.Now.AddMinutes(30)));
            if(change=="time")f.Time.Now=f.Time.Now.AddMinutes(2);
            if(change=="publish")await db.Set<EnvironmentRecord>().ExecuteUpdateAsync(s=>s.SetProperty(x=>x.DeploymentSequence,2));
            if(change=="permission")db.Add(new ApplicationApiPermission{ApplicationId=f.Api.Application.Id,ApiId=f.Api.Api.Id,EnvironmentId=f.Api.Environment.Id,ValidFrom=f.Time.Now.AddMinutes(-1),CreatedBy=f.Api.User.Id});
            await db.SaveChangesAsync();
        }
        using var response=await f.UpdateAsync(f.Request(hash:hash,confirm:true));Assert.Equal(HttpStatusCode.Conflict,response.StatusCode);Assert.Contains("credential_impact_changed",await response.Content.ReadAsStringAsync());
    }
    [Fact]
    public async Task Stale_etag_precedes_impact_check_and_safe_extension_stays_compatible()
    {
        await using var f=new Fixture();await f.InitializeAsync();using(var stale=await f.UpdateAsync(f.Request(),"\"0\""))Assert.Equal(HttpStatusCode.PreconditionFailed,stale.StatusCode);
        using var safe=await f.UpdateAsync(f.Request(status:"Active",expires:f.Key.ExpiresAt.AddHours(1)));safe.EnsureSuccessStatusCode();
    }
    [Theory][InlineData("shorten")][InlineData("future")]
    public async Task Uncovered_future_windows_require_explicit_risk_confirmation(string change)
    {
        await using var f=new Fixture();await f.InitializeAsync();var from=change=="future"?f.Time.Now.AddMinutes(30):f.Key.ValidFrom;var expires=change=="shorten"?f.Time.Now.AddMinutes(30):f.Key.ExpiresAt;var body=f.Request(status:"Active",from:from,expires:expires);var preview=await f.PreviewAsync(body);using(var missing=await f.UpdateAsync(body))Assert.Equal(HttpStatusCode.Conflict,missing.StatusCode);
        using var confirmed=await f.UpdateAsync(f.Request(status:"Active",hash:preview.GetProperty("impactHash").GetString(),confirm:true,from:from,expires:expires));confirmed.EnsureSuccessStatusCode();
    }
    [Fact]
    public async Task Unauthorized_previewer_cannot_receive_hash_or_environment_ids()
    {
        await using var f=new Fixture();await f.InitializeAsync(published:true);await using(var db=f.Api.Context()){var manage=await db.Set<Permission>().Where(x=>x.Code=="credential.manage").Select(x=>x.Id).SingleAsync();await db.Set<RolePermission>().Where(x=>x.PermissionId==manage).ExecuteDeleteAsync();}
        using var response=await f.Api.WriteAsync(HttpMethod.Post,$"/api/v1/credentials/{f.Key.Id}/impact",f.Request(),RevisionTag.Format(1));Assert.Equal(HttpStatusCode.Forbidden,response.StatusCode);Assert.DoesNotContain("impactHash",await response.Content.ReadAsStringAsync());
    }
}
