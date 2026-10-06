using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Integration.Tests.Support;
public sealed class ComparisonFixture : IAsyncDisposable
{
    public const string EmptyDocument="{\"openapi\":\"3.0.3\",\"paths\":{}}";
    public ApiFixture Api {get;}=new();
    public ApiVersion Target {get;}=new(){Version="2.0.0",Status="Draft",ChangeType="compatible",OpenapiDocument=EmptyDocument,SourceFormat="json"};
    public string Path=>$"/api/v1/apis/{Api.Api.Id}/version-comparisons";
    public async Task InitializeAsync(bool releases=false)
    {
        await Api.InitializeAsync();if(releases)await Api.SeedReleaseAsync();else await Api.SeedCatalogAsync();
        await using(var db=Api.Context())
        {
            var from=await db.Set<ApiVersion>().SingleAsync(x=>x.Id==Api.Version.Id);from.OpenapiDocument=EmptyDocument;from.SourceFormat="json";
            Target.ApiId=Api.Api.Id;Target.CreatedBy=Api.User.Id;db.Add(Target);
            db.AddRange(new ApiParameter {ApiVersionId=from.Id,Location="query",Name="name",DataType="string",Required=false,Schema="{\"type\":\"string\"}"},new ApiParameter {ApiVersionId=Target.Id,Location="query",Name="name",DataType="string",Required=true,Schema="{\"type\":\"string\"}"});await db.SaveChangesAsync();
        }
        using var login=await Api.LoginAsync();login.EnsureSuccessStatusCode();
    }
    public object Request(Guid? from=null,Guid? to=null,long fromRevision=1,long toRevision=1)=>new {fromVersionId=from??Api.Version.Id,toVersionId=to??Target.Id,expectedFromRevision=fromRevision,expectedToRevision=toRevision};
    public Task<HttpResponseMessage> CreateAsync(string? key=null,object? request=null)=>ApiFixture.CommandAsync(Api.Client,Path,request??Request(),key);
    public async Task<Guid> CreateIdAsync(){using var response=await CreateAsync();response.EnsureSuccessStatusCode();return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();}
    public async Task RevokeAsync(string code)
    {await using var db=Api.Context();var permission=await db.Set<Permission>().SingleAsync(p=>p.Code==code);var roles=await db.Set<UserRole>().Where(x=>x.UserId==Api.User.Id).Select(x=>x.RoleId).ToArrayAsync();db.RemoveRange(await db.Set<RolePermission>().Where(x=>roles.Contains(x.RoleId)&&x.PermissionId==permission.Id).ToArrayAsync());await db.SaveChangesAsync();}
    public async Task GrantReviewAsync()
    {await using var db=Api.Context();var role=await db.Set<UserRole>().Where(x=>x.UserId==Api.User.Id).Select(x=>x.RoleId).SingleAsync();var p=await db.Set<Permission>().SingleOrDefaultAsync(x=>x.Code=="api.approve");if(p is null){p=new Permission {Code="api.approve",Module="api",Name="approve"};db.Add(p);}if(!await db.Set<RolePermission>().AnyAsync(x=>x.RoleId==role&&x.PermissionId==p.Id))db.Add(new RolePermission {RoleId=role,PermissionId=p.Id});await db.SaveChangesAsync();}
    public async Task SetNoRiskAsync()
    {await using var db=Api.Context();foreach(var p in await db.Set<ApiParameter>().Where(x=>x.ApiVersionId==Target.Id).ToArrayAsync())p.Required=false;await db.SaveChangesAsync();}
    public async Task<(Guid Id,string Hash)> ComparisonAsync()
    {using var r=await CreateAsync();r.EnsureSuccessStatusCode();var v=await r.Content.ReadFromJsonAsync<JsonElement>();return (v.GetProperty("id").GetGuid(),v.GetProperty("reportHash").GetString()!);}
    public Task<HttpResponseMessage> ReviewAsync(Guid id,string hash,string decision="AcceptedRisk",string? comment="已评估风险并确认承担兼容影响",bool confirm=true,string? key=null)=>ApiFixture.CommandAsync(Api.Client,$"/api/v1/version-comparisons/{id}/reviews",new {expectedReportHash=hash,decision,comment,confirmRisk=confirm},key);
    public ValueTask DisposeAsync()=>Api.DisposeAsync();
}
