using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using WebApi.Integration.Tests.Support;
using Xunit;
namespace WebApi.Integration.Tests;
public sealed class SsoPublicDiscoveryTests
{
    [Fact] public async Task PublicDtoHasOnlyThreeFieldsAndExplicitOrganizationEntry()
    {
        await using var f=new SsoFixture();await f.InitializeAsync();var platform=await f.EnableAsync(await f.CreateAsync());await f.CreateAsync(SsoFixture.Body("停用源"));
        var org=await f.EnableAsync(await f.CreateAsync(SsoFixture.Body("组织源",f.Api.Organization.Id)));
        using var client=new HttpClient{BaseAddress=f.Api.Client.BaseAddress};
        using var response=await client.GetAsync("/api/v1/auth/sso/providers");response.EnsureSuccessStatusCode();Assert.Contains("no-store",response.Headers.CacheControl!.ToString());
        var publicRows=await response.Content.ReadFromJsonAsync<JsonElement>();Assert.Equal(1,publicRows.GetArrayLength());Assert.Equal(platform.Id,publicRows[0].GetProperty("id").GetGuid());
        Assert.Equal(new[]{"id","isDefault","name"},publicRows[0].EnumerateObject().Select(p=>p.Name).Order().ToArray());
        var orgRows=(await client.GetFromJsonAsync<JsonElement>($"/api/v1/auth/sso/providers?organizationId={f.Api.Organization.Id}"));Assert.Equal(2,orgRows.GetArrayLength());Assert.Contains(orgRows.EnumerateArray(),item=>item.GetProperty("id").GetGuid()==org.Id);
        Assert.Equal(HttpStatusCode.NotFound,(await client.GetAsync($"/api/v1/auth/sso/providers?organizationId={Guid.NewGuid()}")).StatusCode);
    }
}
