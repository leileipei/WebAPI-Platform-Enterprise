using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Policies;
using WebApi.Infrastructure.Persistence.Entities;
using Xunit;

namespace WebApi.Integration.Tests;

public sealed class TransformPolicyManagementTests
{
    [Theory]
    [InlineData("cors", "{\"allowedOrigins\":[\"https://portal.example.com\"],\"allowCredentials\":false,\"allowedMethods\":[\"GET\"],\"allowedHeaders\":[],\"exposedHeaders\":[],\"maxAgeSeconds\":600}")]
    [InlineData("ip_filter", "{\"mode\":\"AllowList\",\"ranges\":[\"192.0.2.0/24\"]}")]
    [InlineData("transform",TransformPreviewTests.Config)]
    public async Task NewTypes_CanCreateFilterCopyAndRespectEtag(string type,string config)
    {
        await using var api=await TransformPreviewTests.PrepareAsync();var path=$"/api/v1/organizations/{api.Organization.Id}/policies";
        using var saved=await api.WriteAsync(HttpMethod.Post,path,new SavePolicyRequest("network",type,config));saved.EnsureSuccessStatusCode();var original=(await saved.Content.ReadFromJsonAsync<PolicyDto>())!;
        using var filter=await api.Client.GetAsync(path+"?type="+type);filter.EnsureSuccessStatusCode();var rows=(await filter.Content.ReadFromJsonAsync<PageResult<PolicyDto>>())!;Assert.Equal(1,rows.Total);Assert.Equal(original.Id,Assert.Single(rows.Items).Id);
        using var copy=await api.WriteAsync(HttpMethod.Post,$"/api/v1/policies/{original.Id}/copy",new CopyPolicyRequest("copy",null));copy.EnsureSuccessStatusCode();var duplicate=(await copy.Content.ReadFromJsonAsync<PolicyDto>())!;Assert.NotEqual(original.Id,duplicate.Id);Assert.Equal(original.Config,duplicate.Config);
        using var updated=await api.WriteAsync(HttpMethod.Put,$"/api/v1/policies/{original.Id}",new SavePolicyRequest("changed",type,config),"\"1\"");updated.EnsureSuccessStatusCode();
        using var stale=await api.WriteAsync(HttpMethod.Put,$"/api/v1/policies/{original.Id}",new SavePolicyRequest("stale",type,config),"\"1\"");Assert.Equal(HttpStatusCode.PreconditionFailed,stale.StatusCode);
    }

    [Fact]
    public async Task Transform_DeploymentLimit_IsVisibleAndBlocksSaveValidationAndCopy()
    {
        await using var api=await TransformPreviewTests.PrepareAsync(maxBytes:1024);var config=TransformPreviewTests.Config.Replace("\"maxBodyBytes\":1024","\"maxBodyBytes\":2048");
        using var limits=await api.Client.GetAsync($"/api/v1/policies/limits?organizationId={api.Organization.Id}");limits.EnsureSuccessStatusCode();using var doc=JsonDocument.Parse(await limits.Content.ReadAsStringAsync());Assert.Equal(1024,doc.RootElement.GetProperty("transformMaxBodyBytes").GetInt32());
        using var saved=await api.WriteAsync(HttpMethod.Post,$"/api/v1/organizations/{api.Organization.Id}/policies",new SavePolicyRequest("too-large","transform",config));Assert.Equal(HttpStatusCode.UnprocessableEntity,saved.StatusCode);
        using var validate=await api.WriteAsync(HttpMethod.Post,"/api/v1/policies/validate",new ValidatePolicyRequest(new(api.Organization.Id),"transform",config));Assert.Equal(HttpStatusCode.UnprocessableEntity,validate.StatusCode);
        Guid id;await using(var db=api.Context()){var p=new Policy {OrganizationId=api.Organization.Id,Name="existing",Type="transform",Config=config};id=p.Id;db.Add(p);await db.SaveChangesAsync();}
        using var copy=await api.WriteAsync(HttpMethod.Post,$"/api/v1/policies/{id}/copy",new CopyPolicyRequest("copied",null));Assert.Equal(HttpStatusCode.UnprocessableEntity,copy.StatusCode);
    }
}
