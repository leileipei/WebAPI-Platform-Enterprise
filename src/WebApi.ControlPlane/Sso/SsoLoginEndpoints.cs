using System.Globalization;
using Microsoft.AspNetCore.Authentication;
using WebApi.Contracts.Common;
using WebApi.Infrastructure.Sso;
namespace WebApi.ControlPlane.Sso;
public static class SsoLoginEndpoints
{
    public static void MapSsoLogin(this WebApplication app)
    {
        app.MapPost("/api/v1/auth/sso/{providerId:guid}/start",async(Guid providerId,HttpContext context,SsoLoginCoordinator coordinator,OidcSchemeRegistry registry,CancellationToken ct)=>{
            context.Response.Headers.CacheControl="no-store";context.Response.Headers["Referrer-Policy"]="no-referrer";
            if(context.User.Identity?.IsAuthenticated==true)throw new ApiException(409,"sso_account_switch_required","请先退出当前账号，再选择企业登录。");
            if(!context.Request.HasFormContentType||context.Request.ContentLength>4096)throw new ApiException(422,"invalid_sso_request","企业登录需要同源表单。");
            var form=await context.Request.ReadFormAsync(ct);
            if(form.Keys.Any(key=>key is not ("__RequestVerificationToken" or "returnPath" or "reauthenticate"))||form.Any(value=>value.Value.Count>1))throw new ApiException(422,"invalid_sso_request","企业登录表单字段不合法。");
            var pending=await coordinator.BeginAsync(providerId,form["returnPath"].FirstOrDefault(),ct);
            try
            {
                var scheme=await registry.PrepareAsync(providerId,ct);
                var properties=new AuthenticationProperties{RedirectUri="/auth/sso/complete"};
                properties.Items["ssoAttempt"]=pending.AttemptId.ToString();properties.Items["ssoRevision"]=pending.Provider.Revision.ToString(CultureInfo.InvariantCulture);
                properties.Items["ssoReauthenticate"]=form["reauthenticate"]=="true"?"true":"false";
                return Results.Challenge(properties,[scheme]);
            }
            catch(ApiException){await coordinator.FailAsync(pending.AttemptId,"configuration_unavailable",ct);throw;}
        }).AllowAnonymous();
    }
}
