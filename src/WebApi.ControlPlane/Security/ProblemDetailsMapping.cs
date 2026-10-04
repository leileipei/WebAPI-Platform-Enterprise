using Microsoft.EntityFrameworkCore;
using Npgsql;
using WebApi.Contracts.Common;
namespace WebApi.ControlPlane.Security;
public static class ProblemDetailsMapping
{
    public static Task WriteAsync(HttpContext context,int status,string code,string title,string? sourceType=null)
    {
        var extensions=new Dictionary<string,object?>{["code"]=code,["traceId"]=context.TraceIdentifier};
        if(sourceType is not null){extensions["sourceType"]=sourceType;if(status==503){extensions["retryAfterSeconds"]=5;context.Response.Headers["Retry-After"]="5";}}
        return Results.Problem(statusCode:status,title:title,extensions:extensions).ExecuteAsync(context);
    }
    public static async Task HandleAsync(HttpContext context,Func<Task> next)
    {
        try {await next();}
        catch(ApiException e) {await WriteAsync(context,e.Status,e.Code,e.Message,e.SourceType);}
        catch(DbUpdateConcurrencyException) {await WriteAsync(context,412,"stale_revision","数据已更新，请刷新。");}
        catch(DbUpdateException e) when(e.InnerException is PostgresException p && p.SqlState==PostgresErrorCodes.UniqueViolation) {await WriteAsync(context,409,"duplicate_resource","数据已存在或发生并发冲突。");}
        catch(DbUpdateException e) when(e.InnerException is PostgresException p && p.SqlState is PostgresErrorCodes.ForeignKeyViolation or PostgresErrorCodes.CheckViolation) {await WriteAsync(context,422,"invalid_reference","资源引用或字段约束不合法。");}
        catch(BadHttpRequestException e) {await WriteAsync(context,e.StatusCode,"invalid_request","请求结构不合法。");}
        catch(Exception e) when(e is not OperationCanceledException) {context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("ApiErrors").LogError("Request failed: {Type}; traceId={TraceId}",e.GetType().Name,context.TraceIdentifier);await WriteAsync(context,500,"internal_error","请求处理失败，请联系管理员。");}
    }
}
