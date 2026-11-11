using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Contracts.Applications;
using WebApi.Contracts.Common;
using WebApi.Contracts.Runtime;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Security;
using WebApi.Integration.Tests.Support;
using Xunit;
namespace WebApi.Integration.Tests;
public sealed class CredentialImpactTests
{
    private sealed class Clock:TimeProvider{public DateTimeOffset Now=DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());public override DateTimeOffset GetUtcNow()=>Now;}
    private sealed class Fixture:IAsyncDisposable
    {
        public readonly ApiFixture Api=new();public readonly Clock Time=new();public ApplicationCredential Key=null!;public ApplicationCredential? Other;
        public async Task InitializeAsync(bool replacement=false,bool published=false)
        {
            await Api.InitializeAsync(b=>b.Services.AddSingleton<TimeProvider>(Time));await Api.SeedConsumerAsync();using var login=await Api.LoginAsync();login.EnsureSuccessStatusCode();
            await using var db=Api.Context();var material=ApiKeySecret.Create();Key=new(){ApplicationId=Api.Application.Id,OrganizationId=Api.Organization.Id,AccessKey=material.AccessKey,SecretHash=material.Hash,SecretLast4=material.Last4,ValidFrom=Time.Now.AddHours(-1),ExpiresAt=Time.Now.AddHours(1)};db.Add(Key);
            if(replacement){Other=new(){ApplicationId=Api.Application.Id,OrganizationId=Api.Organization.Id,AccessKey="replacement",SecretHash=new string('b',64),SecretLast4="5678",ValidFrom=Time.Now.AddMinutes(-1),ExpiresAt=Time.Now.AddHours(2)};db.Add(Other);}
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
        public async Task<CredentialDto> ReadAsync(long revision)
        {
            using var response=await Api.Client.GetAsync($"/api/v1/credentials/{Key.Id}");
            response.EnsureSuccessStatusCode();
            Assert.Equal(RevisionTag.Format(revision),response.Headers.ETag?.ToString());
            return (await response.Content.ReadFromJsonAsync<CredentialDto>())!;
        }
        public async Task AssertWindowAsync(DateTimeOffset from,DateTimeOffset expires,long revision,long applicationRevision)
        {
            var read=await ReadAsync(revision);
            Assert.Equal((Key.Id,Api.Application.Id,from,expires,"Active",revision),
                (read.Id,read.ApplicationId,read.ValidFrom,read.ExpiresAt,read.Status,read.Revision));
            Assert.Equal("WorkingConfiguration",read.Activation);
            await using var db=Api.Context();
            var saved=await db.Set<ApplicationCredential>().AsNoTracking().SingleAsync();
            Assert.Equal((Key.Id,Api.Application.Id,from,expires,"Active",revision),
                (saved.Id,saved.ApplicationId,saved.ValidFrom,saved.ExpiresAt,saved.Status,saved.Revision));
            Assert.Equal(applicationRevision,await db.Set<ApplicationRecord>().Where(x=>x.Id==Api.Application.Id).Select(x=>x.Revision).SingleAsync());
        }
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
            if(change=="permission")db.Add(new ApplicationApiPermission{ApplicationId=f.Api.Application.Id,ApiId=f.Api.Api.Id,EnvironmentId=f.Api.Environment.Id,Mode="LegacyApi",ValidFrom=f.Time.Now.AddMinutes(-1),CreatedBy=f.Api.User.Id});
            await db.SaveChangesAsync();
        }
        using var response=await f.UpdateAsync(f.Request(hash:hash,confirm:true));Assert.Equal(HttpStatusCode.Conflict,response.StatusCode);Assert.Contains("credential_impact_changed",await response.Content.ReadAsStringAsync());
    }
    [Fact]
    public async Task Stale_etag_precedes_impact_check_and_safe_extension_stays_compatible()
    {
        await using var f=new Fixture();await f.InitializeAsync();using(var stale=await f.UpdateAsync(f.Request(),"\"0\""))Assert.Equal(HttpStatusCode.PreconditionFailed,stale.StatusCode);
        var expires=f.Key.ExpiresAt.AddHours(1);
        using var safe=await f.UpdateAsync(f.Request(status:"Active",expires:expires));safe.EnsureSuccessStatusCode();
        await f.AssertWindowAsync(f.Key.ValidFrom,expires,f.Key.Revision+1,f.Api.Application.Revision+1);
    }
    [Theory][InlineData("shorten")][InlineData("future")]
    public async Task Uncovered_future_windows_require_explicit_risk_confirmation(string change)
    {
        await using var f=new Fixture();await f.InitializeAsync();var from=change=="future"?f.Time.Now.AddMinutes(30):f.Key.ValidFrom;var expires=change=="shorten"?f.Time.Now.AddMinutes(30):f.Key.ExpiresAt;var body=f.Request(status:"Active",from:from,expires:expires);var preview=await f.PreviewAsync(body);using(var missing=await f.UpdateAsync(body))Assert.Equal(HttpStatusCode.Conflict,missing.StatusCode);
        using var confirmed=await f.UpdateAsync(f.Request(status:"Active",hash:preview.GetProperty("impactHash").GetString(),confirm:true,from:from,expires:expires));confirmed.EnsureSuccessStatusCode();
        await f.AssertWindowAsync(from,expires,f.Key.Revision+1,f.Api.Application.Revision+1);
    }
    [Theory]
    [InlineData("create")]
    [InlineData("preview")]
    [InlineData("update")]
    public async Task Credential_manager_with_readonly_scope_can_read_but_cannot_write(string operation)
    {
        await using var f=new Fixture();
        await f.InitializeAsync();
        await using(var db=f.Api.Context())
        {
            var changed=await db.Set<UserProjectScope>().Where(x=>x.UserId==f.Api.User.Id)
                .ExecuteUpdateAsync(s=>s.SetProperty(x=>x.AccessMode,"read"));
            Assert.Equal(1,changed);
        }
        using var identityResponse=await f.Api.Client.GetAsync("/api/v1/auth/me");
        identityResponse.EnsureSuccessStatusCode();
        var identity=(await identityResponse.Content.ReadFromJsonAsync<IdentityDto>())!;
        Assert.Contains("credential.manage",identity.Permissions);
        Assert.Equal(new ScopeGrantDto(new ScopeRef(f.Api.Organization.Id),"read"),Assert.Single(identity.Scopes));
        var before=await f.ReadAsync(f.Key.Revision);
        var proposedEnd=f.Key.ExpiresAt.AddHours(1);
        var path=$"/api/v1/credentials/{f.Key.Id}";
        var tag=RevisionTag.Format(f.Key.Revision);
        using var denied=await (operation switch
        {
            "create"=>f.Api.WriteAsync(HttpMethod.Post,$"/api/v1/applications/{f.Api.Application.Id}/credentials",new CredentialCreateRequest(f.Key.ValidFrom,proposedEnd)),
            "preview"=>f.Api.WriteAsync(HttpMethod.Post,path+"/impact",new CredentialImpactRequest(f.Key.ValidFrom,proposedEnd,"Active"),tag),
            "update"=>f.UpdateAsync(new CredentialUpdateRequest(f.Key.ValidFrom,proposedEnd,"Active"),tag),
            _=>throw new ArgumentOutOfRangeException(nameof(operation))
        });
        Assert.Equal(HttpStatusCode.Forbidden,denied.StatusCode);
        var problem=await denied.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("scope_denied",problem.GetProperty("code").GetString());
        foreach(var field in new[]{"credential","accessKey","secret","apiKey","impactHash","atRiskVisibleEnvironmentIds"})
            Assert.False(problem.TryGetProperty(field,out _));
        await f.AssertWindowAsync(before.ValidFrom,before.ExpiresAt,before.Revision,f.Api.Application.Revision);
    }
    [Theory]
    [InlineData("manager-without-app-read","create")]
    [InlineData("manager-without-app-read","preview")]
    [InlineData("manager-without-app-read","update")]
    [InlineData("project-writer","create")]
    [InlineData("project-writer","preview")]
    [InlineData("project-writer","update")]
    [InlineData("environment-only","create")]
    [InlineData("environment-only","preview")]
    [InlineData("environment-only","update")]
    [InlineData("no-scope","create")]
    [InlineData("no-scope","preview")]
    [InlineData("no-scope","update")]
    [InlineData("reader-without-management","create")]
    [InlineData("reader-without-management","preview")]
    [InlineData("reader-without-management","update")]
    public async Task Valid_credential_operations_respect_function_permissions_and_scope(string persona,string operation)
    {
        await using var f=new Fixture();await f.InitializeAsync();
        var hour=new DateTimeOffset(f.Time.Now.Year,f.Time.Now.Month,f.Time.Now.Day,f.Time.Now.Hour,0,0,TimeSpan.Zero).AddHours(-1);
        var observed=hour.AddMinutes(10);
        await using(var db=f.Api.Context())
        {
            db.Add(new CredentialUsageHourly{ApplicationId=f.Api.Application.Id,CredentialId=f.Key.Id,
                EnvironmentId=f.Api.Environment.Id,HourStart=hour,Count=2,LastUsedAt=observed});
            await db.SaveChangesAsync();
        }
        Assert.Equal(observed,(await f.ReadAsync(f.Key.Revision)).LastUsedAt);
        var allowed=persona is "manager-without-app-read" or "project-writer";
        var canRead=persona is "project-writer" or "reader-without-management";
        await using(var db=f.Api.Context())
        {
            if(persona is "manager-without-app-read" or "reader-without-management")
            {
                var code=persona=="manager-without-app-read"?"app.read":"credential.manage";
                var permission=await db.Set<Permission>().Where(x=>x.Code==code).Select(x=>x.Id).SingleAsync();
                Assert.Equal(1,await db.Set<RolePermission>().Where(x=>x.PermissionId==permission).ExecuteDeleteAsync());
            }
            if(persona=="no-scope")
                Assert.Equal(1,await db.Set<UserProjectScope>().Where(x=>x.UserId==f.Api.User.Id).ExecuteDeleteAsync());
            else if(persona is "project-writer" or "environment-only")
            {
                var scope=await db.Set<UserProjectScope>().SingleAsync(x=>x.UserId==f.Api.User.Id);
                scope.ProjectId=f.Api.Project.Id;
                if(persona=="environment-only")scope.EnvironmentId=f.Api.Environment.Id;
                await db.SaveChangesAsync();
            }
        }
        using var identityResponse=await f.Api.Client.GetAsync("/api/v1/auth/me");identityResponse.EnsureSuccessStatusCode();
        var identity=(await identityResponse.Content.ReadFromJsonAsync<IdentityDto>())!;
        Assert.Equal(f.Api.User.Id,identity.Id);
        Assert.Equal(persona!="reader-without-management",identity.Permissions.Contains("credential.manage"));
        Assert.Equal(persona!="manager-without-app-read",identity.Permissions.Contains("app.read"));
        if(persona=="no-scope")Assert.Empty(identity.Scopes);
        else
        {
            var expectedScope=new ScopeRef(f.Api.Organization.Id,
                persona is "project-writer" or "environment-only"?f.Api.Project.Id:null,
                persona=="environment-only"?f.Api.Environment.Id:null);
            Assert.Equal(new ScopeGrantDto(expectedScope,"read_write"),Assert.Single(identity.Scopes));
        }
        using(var visibility=await f.Api.Client.GetAsync($"/api/v1/credentials/{f.Key.Id}"))
            Assert.Equal(canRead?HttpStatusCode.OK:HttpStatusCode.NotFound,visibility.StatusCode);
        var end=f.Key.ExpiresAt.AddHours(1);var path=$"/api/v1/credentials/{f.Key.Id}";var tag=RevisionTag.Format(f.Key.Revision);
        using var response=await (operation switch
        {
            "create"=>f.Api.WriteAsync(HttpMethod.Post,$"/api/v1/applications/{f.Api.Application.Id}/credentials",new CredentialCreateRequest(f.Key.ValidFrom,end)),
            "preview"=>f.Api.WriteAsync(HttpMethod.Post,path+"/impact",new CredentialImpactRequest(f.Key.ValidFrom,end,"Active"),tag),
            "update"=>f.UpdateAsync(new CredentialUpdateRequest(f.Key.ValidFrom,end,"Active"),tag),
            _=>throw new ArgumentOutOfRangeException(nameof(operation))
        });
        Assert.Equal(allowed?HttpStatusCode.OK:HttpStatusCode.Forbidden,response.StatusCode);
        CredentialCreateResponse? created=null;
        if(!allowed)
        {
            var error=await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(persona=="reader-without-management"?"permission_denied":"scope_denied",error.GetProperty("code").GetString());
            foreach(var field in new[]{"credential","accessKey","secret","secretHash","apiKey","impactHash","atRiskVisibleEnvironmentIds","lastUsedAt"})
                Assert.False(error.TryGetProperty(field,out _));
        }
        else if(operation=="create")
        {
            created=(await response.Content.ReadFromJsonAsync<CredentialCreateResponse>())!;
            Assert.Equal(created.Credential.AccessKey+"."+created.Secret,created.ApiKey);
        }
        else if(operation=="preview")
        {
            var preview=(await response.Content.ReadFromJsonAsync<CredentialImpactDto>())!;
            Assert.False(string.IsNullOrWhiteSpace(preview.ImpactHash));
            if(!canRead)Assert.Empty(preview.AtRiskVisibleEnvironmentIds);
        }
        else
        {
            var updated=(await response.Content.ReadFromJsonAsync<CredentialDto>())!;
            Assert.Equal((f.Key.Id,f.Key.ValidFrom,end,f.Key.Revision+1),(updated.Id,updated.ValidFrom,updated.ExpiresAt,updated.Revision));
            Assert.Equal(canRead?(DateTimeOffset?)observed:null,updated.LastUsedAt);
            Assert.Equal("WorkingConfiguration",updated.Activation);
            Assert.Equal(RevisionTag.Format(updated.Revision),response.Headers.ETag?.ToString());
        }
        await using var check=f.Api.Context();
        var write=allowed&&operation!="preview";
        Assert.Equal(f.Api.Application.Revision+(write?1:0),await check.Set<ApplicationRecord>().Select(x=>x.Revision).SingleAsync());
        Assert.Equal(created is null?1:2,await check.Set<ApplicationCredential>().CountAsync());
        var existing=await check.Set<ApplicationCredential>().AsNoTracking().SingleAsync(x=>x.Id==f.Key.Id);
        var changed=allowed&&operation=="update";
        Assert.Equal((f.Key.ValidFrom,changed?end:f.Key.ExpiresAt,"Active",f.Key.Revision+(changed?1:0)),
            (existing.ValidFrom,existing.ExpiresAt,existing.Status,existing.Revision));
        if(created is not null)
        {
            Assert.NotEqual(f.Key.Id,created.Credential.Id);
            var saved=await check.Set<ApplicationCredential>().AsNoTracking().SingleAsync(x=>x.Id==created.Credential.Id);
            Assert.Equal((f.Api.Application.Id,f.Key.ValidFrom,end,"Active",1L),
                (saved.ApplicationId,saved.ValidFrom,saved.ExpiresAt,saved.Status,saved.Revision));
            Assert.True(ApiKeySecret.Verify(saved.SecretHash,created.Secret));
            Assert.NotEqual(created.Secret,saved.SecretHash);
        }
        Assert.Equal(write?1:0,await check.Set<AuditLog>().CountAsync(x=>x.Action=="credential.create"||x.Action=="credential.update"));
        if(write)Assert.Equal("credential."+operation,(await check.Set<AuditLog>().SingleAsync(x=>x.Action=="credential.create"||x.Action=="credential.update")).Action);
        var retained=await check.Set<CredentialUsageHourly>().SingleAsync();
        Assert.Equal((2L,observed),(retained.Count,retained.LastUsedAt));
    }
    [Fact]
    public async Task Unauthorized_previewer_cannot_receive_hash_or_environment_ids()
    {
        await using var f=new Fixture();await f.InitializeAsync(published:true);await using(var db=f.Api.Context()){var manage=await db.Set<Permission>().Where(x=>x.Code=="credential.manage").Select(x=>x.Id).SingleAsync();await db.Set<RolePermission>().Where(x=>x.PermissionId==manage).ExecuteDeleteAsync();}
        using var response=await f.Api.WriteAsync(HttpMethod.Post,$"/api/v1/credentials/{f.Key.Id}/impact",f.Request(),RevisionTag.Format(1));Assert.Equal(HttpStatusCode.Forbidden,response.StatusCode);Assert.DoesNotContain("impactHash",await response.Content.ReadAsStringAsync());
    }
}
