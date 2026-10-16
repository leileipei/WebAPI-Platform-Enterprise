using Microsoft.AspNetCore.Authorization;
using WebApi.Contracts.Common;
using WebApi.Infrastructure.Security;
namespace WebApi.ControlPlane.Security;

public sealed class PasswordChangeRequiredMiddleware(RequestDelegate next)
{
    // Populated only from the live database during cookie validation, never from a cookie claim.
    public static readonly object RequiredItemKey=new();
    public async Task InvokeAsync(HttpContext context,PasswordLifecycleGuard lifecycle)
    {
        var path=context.Request.Path;
        if(context.User.Identity?.IsAuthenticated==true&&path.StartsWithSegments("/api/v1")&&
            context.GetEndpoint()?.Metadata.GetMetadata<IAllowAnonymous>() is null&&
            path!="/api/v1/auth/csrf"&&path!="/api/v1/auth/me"&&path!="/api/v1/auth/logout"&&path!="/api/v1/auth/change-password")
        {
            if(context.Items.TryGetValue(RequiredItemKey,out var value)&&value is bool required)
            {
                if(required)throw new ApiException(403,"password_change_required","请先修改初始密码。");
            }
            else await lifecycle.RequireBusinessAccessAsync(context.Actor(),context.RequestAborted);
        }
        await next(context);
    }
}
