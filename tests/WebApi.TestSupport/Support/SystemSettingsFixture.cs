using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Infrastructure.Persistence.Entities;
using System.Text.Json;
namespace WebApi.Integration.Tests.Support;
public static class SystemSettingsFixture
{
    public static async Task PromoteAsync(ApiFixture f){await using var db=f.Context();var role=await (from ur in db.Set<UserRole>() join r in db.Set<Role>() on ur.RoleId equals r.Id where ur.UserId==f.User.Id select r).SingleAsync();role.Code="PlatformAdmin";role.IsSystem=true;role.OrganizationId=null;var permission=await db.Set<Permission>().SingleOrDefaultAsync(x=>x.Code=="system.manage");if(permission is null){permission=new Permission{Code="system.manage",Module="system",Name="系统管理"};db.Add(permission);}if(!await db.Set<RolePermission>().AnyAsync(x=>x.RoleId==role.Id&&x.PermissionId==permission.Id))db.Add(new RolePermission{RoleId=role.Id,PermissionId=permission.Id});await db.SaveChangesAsync();}
    public static async Task<HttpResponseMessage> SendAsync(ApiFixture f,HttpMethod method,string group,string suffix,object body,string? tag=null,string? key=null){var csrf=await f.CsrfAsync();using var req=new HttpRequestMessage(method,"/api/v1/settings/system/"+group+suffix){Content=JsonContent.Create(body)};req.Headers.Add("X-CSRF-Token",csrf);req.Headers.Add("Idempotency-Key",key??Guid.NewGuid().ToString("N"));if(tag is not null)req.Headers.Add("If-Match",tag);return await f.Client.SendAsync(req);}
    public static async Task<(JsonElement Preview,string Tag)> PreviewAsync(ApiFixture f,string group,object values){using var read=await f.Client.GetAsync("/api/v1/settings/system/"+group);read.EnsureSuccessStatusCode();var tag=read.Headers.ETag!.Tag;using var preview=await SendAsync(f,HttpMethod.Post,group,"/preview",new{values},tag);preview.EnsureSuccessStatusCode();return (await preview.Content.ReadFromJsonAsync<JsonElement>(),tag);}
    public static async Task<HttpResponseMessage> SaveAsync(ApiFixture f,string group,object values){var (preview,tag)=await PreviewAsync(f,group,values);return await SendAsync(f,HttpMethod.Put,group,"",new{values,confirmationToken=preview.GetProperty("confirmationToken").GetString()},tag);}
}
