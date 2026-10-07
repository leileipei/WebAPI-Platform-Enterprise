using System.Text.Json;
using WebApi.Contracts.Common;
using WebApi.Contracts.Notifications;
using WebApi.Contracts.Security;
using WebApi.ControlPlane.Security;
using WebApi.Infrastructure.Notifications;
namespace WebApi.ControlPlane.Notifications;
public static class NotificationEndpoints
{
    private static int Integer(HttpContext ctx,string name,int fallback){var value=ctx.Request.Query[name];if(value.Count==0)return fallback;if(value.Count!=1||!int.TryParse(value[0],System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out var parsed))throw new ApiException(422,"invalid_notification_query","通知查询参数不合法。");return parsed;}
    private static Guid? Id(HttpContext ctx,string name){var value=ctx.Request.Query[name];if(value.Count==0)return null;if(value.Count!=1||!Guid.TryParse(value[0],out var id)||id==Guid.Empty)throw new ApiException(422,"invalid_notification_query","通知范围参数不合法。");return id;}
    private static async Task<byte[]> Body(HttpContext ctx,CancellationToken ct)
    {
        const int limit=16384;
        if(ctx.Request.ContentLength>limit)throw new ApiException(413,"notification_request_too_large","通知测试请求不能超过16384字节。");
        using var buffer=new MemoryStream();var bytes=new byte[1024];
        while(true){var count=await ctx.Request.Body.ReadAsync(bytes,ct);if(count==0)break;if(buffer.Length+count>limit)throw new ApiException(413,"notification_request_too_large","通知测试请求不能超过16384字节。");buffer.Write(bytes,0,count);}
        return buffer.ToArray();
    }
    private static async Task<CreateNotificationTestRequest> TestBody(HttpContext ctx,CancellationToken ct)
    {
        var body=await Body(ctx,ct);
        try
        {
            using var document=JsonDocument.Parse(body,new JsonDocumentOptions{MaxDepth=4});
            if(document.RootElement.ValueKind!=JsonValueKind.Object)throw new JsonException();
            var fields=new Dictionary<string,JsonElement>(StringComparer.OrdinalIgnoreCase);
            foreach(var field in document.RootElement.EnumerateObject())if(!new[]{"channel","email"}.Contains(field.Name,StringComparer.OrdinalIgnoreCase)||!fields.TryAdd(field.Name,field.Value))throw new JsonException();
            if(!fields.TryGetValue("channel",out var channel)||channel.ValueKind!=JsonValueKind.String)throw new JsonException();
            var selected=channel.Deserialize<NotificationChannel>();if(!Enum.IsDefined(selected))throw new JsonException();
            string? email=null;if(fields.TryGetValue("email",out var target)&&target.ValueKind!=JsonValueKind.Null){if(target.ValueKind!=JsonValueKind.String)throw new JsonException();email=target.GetString();}
            return new(selected,email);
        }
        catch(JsonException){throw new ApiException(422,"invalid_notification_test","通知测试请求含重复、未知或非法字段。");}
    }
    private static async Task RetryBody(HttpContext ctx,CancellationToken ct)
    {
        var body=await Body(ctx,ct);if(body.Length==0)return;
        try{using var document=JsonDocument.Parse(body,new JsonDocumentOptions{MaxDepth=2});if(document.RootElement.ValueKind!=JsonValueKind.Object||document.RootElement.EnumerateObject().Any())throw new JsonException();}
        catch(JsonException){throw new ApiException(422,"invalid_notification_retry","通知重试不接受目标或预算字段。");}
    }
    public static void MapNotifications(this WebApplication app)
    {
        var group=app.MapGroup("/api/v1").RequireAuthorization();group.AddEndpointFilter(async(context,next)=>{context.HttpContext.Response.Headers.CacheControl="no-store";return await next(context);});
        group.MapPost("/settings/system/notification/tests",async(HttpContext ctx,NotificationTestService service,CancellationToken ct)=>{var request=await TestBody(ctx,ct);var value=await service.CreateAsync(request.Channel,request.Email,ctx.Request.Headers.IfMatch.ToString(),ctx.Actor(),ct);ctx.Response.Headers.ETag=RevisionTag.Format(value.Revision);return Results.Accepted("/api/v1/settings/system/notification/tests/"+value.Id,value);});
        group.MapGet("/settings/system/notification/deployment-policy",async(HttpContext ctx,NotificationTestService service,CancellationToken ct)=>Results.Ok(await service.DeploymentPolicyAsync(ctx.Actor(),ct)));
        group.MapGet("/settings/system/notification/tests/{id:guid}",async(Guid id,HttpContext ctx,NotificationTestService service,CancellationToken ct)=>{var value=await service.GetAsync(id,ctx.Actor(),ct);ctx.Response.Headers.ETag=RevisionTag.Format(value.Revision);return Results.Ok(value);});
        group.MapGet("/alerts/{id:guid}/notifications",async(Guid id,HttpContext ctx,NotificationQueryService service,CancellationToken ct)=>Results.Ok(await service.ListForEventAsync(id,ctx.Actor(),Integer(ctx,"page",1),Integer(ctx,"pageSize",50),ct)));
        group.MapGet("/notification-deliveries/{id:guid}/attempts",async(Guid id,HttpContext ctx,NotificationQueryService service,CancellationToken ct)=>Results.Ok(await service.AttemptsAsync(id,ctx.Actor(),Integer(ctx,"page",1),Integer(ctx,"pageSize",50),ct)));
        group.MapPost("/notification-deliveries/{id:guid}/retry",async(Guid id,HttpContext ctx,NotificationQueryService service,CancellationToken ct)=>{await RetryBody(ctx,ct);var value=await service.RetryAsync(id,ctx.Request.Headers.IfMatch.ToString(),ctx.Actor(),ct);ctx.Response.Headers.ETag=RevisionTag.Format(value.Revision);return Results.Ok(value);});
        group.MapGet("/notification-limits",async(HttpContext ctx,NotificationQueryService service,CancellationToken ct)=>Results.Ok(await service.GetLimitsAsync(new ScopeRef(Id(ctx,"organizationId")??Guid.Empty,Id(ctx,"projectId"),Id(ctx,"environmentId")),ctx.Actor(),ct)));
    }
}
