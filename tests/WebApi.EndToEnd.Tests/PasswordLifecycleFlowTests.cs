using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;
namespace WebApi.EndToEnd.Tests;
public sealed class PasswordLifecycleFlowTests
{
    [Fact]
    public async Task New_local_account_changes_before_business_and_keeps_assigned_scope()
    {
        var contextFile=Environment.GetEnvironmentVariable("WEBAPI_E2E_CONTEXT_FILE");Assert.NotNull(contextFile);
        var context=JsonDocument.Parse(await File.ReadAllTextAsync(contextFile)).RootElement;
        using var admin=new HttpClient(new HttpClientHandler{CookieContainer=new CookieContainer(),AllowAutoRedirect=false}){BaseAddress=new("http://control-plane:8080")};
        var adminFile=context.TryGetProperty("passwordFiles",out var files)&&files.TryGetProperty("e2e-admin",out var adminPath)?adminPath.GetString()!:context.GetProperty("passwordFile").GetString()!;
        var adminIdentity=await ExternalScenario.WriteAsync(admin,"/auth/login",new{username="e2e-admin",password=await File.ReadAllTextAsync(adminFile)});
        Assert.False(adminIdentity.GetProperty("mustChangePassword").GetBoolean());
        var username="p4-sdk-"+Guid.NewGuid().ToString("N");var initial=Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))+"Aa1!";var changed=Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))+"Bb2!";
        var user=await ExternalScenario.WriteAsync(admin,"/users",new{username,displayName="独立SDK首次改密",password=initial});Assert.True(user.GetProperty("mustChangePassword").GetBoolean());
        var id=user.GetProperty("id").GetGuid();var roles=await admin.GetFromJsonAsync<JsonElement>("/api/v1/roles");var role=roles.EnumerateArray().Single(x=>x.GetProperty("code").GetString()=="ApiDeveloper"&&x.GetProperty("isSystem").GetBoolean()).GetProperty("id").GetGuid();
        user=await ExternalScenario.WriteAsync(admin,$"/users/{id}/roles",new{roleIds=new[]{role}},"\""+user.GetProperty("revision").GetInt64()+"\"","PUT");
        await ExternalScenario.WriteAsync(admin,$"/users/{id}/scopes",new{scopes=new[]{new{scope=new{organizationId=context.GetProperty("organizationId").GetGuid()},accessMode="read"}}},"\""+user.GetProperty("revision").GetInt64()+"\"","PUT");
        using var local=new HttpClient(new HttpClientHandler{CookieContainer=new CookieContainer(),AllowAutoRedirect=false}){BaseAddress=admin.BaseAddress};
        var restricted=await ExternalScenario.WriteAsync(local,"/auth/login",new{username,password=initial});Assert.True(restricted.GetProperty("mustChangePassword").GetBoolean());Assert.Empty(restricted.GetProperty("permissions").EnumerateArray());
        using(var denied=await local.GetAsync("/api/v1/organizations")){Assert.Equal(HttpStatusCode.Forbidden,denied.StatusCode);Assert.Equal("password_change_required",(await denied.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());}
        var complete=await ExternalScenario.WriteAsync(local,"/auth/change-password",new{currentPassword=initial,newPassword=changed,confirmPassword=changed});Assert.False(complete.GetProperty("mustChangePassword").GetBoolean());Assert.NotEmpty(complete.GetProperty("scopes").EnumerateArray());
        using var allowed=await local.GetAsync("/api/v1/organizations");Assert.Equal(HttpStatusCode.OK,allowed.StatusCode);
    }
}
