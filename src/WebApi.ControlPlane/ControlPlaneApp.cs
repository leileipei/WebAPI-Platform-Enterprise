using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using WebApi.ControlPlane.Security;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Security;
namespace WebApi.ControlPlane;
public static class ControlPlaneApp
{
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder=WebApplication.CreateBuilder(args);configure?.Invoke(builder);
        var connection=builder.Configuration.GetConnectionString("WebApi")??DatabaseSettings.ConnectionString();
        builder.Services.AddDbContext<WebApiDbContext>(options=>options.UseNpgsql(connection));
        builder.Services.AddScoped<IPasswordHasher<UserRecord>,PasswordHasher<UserRecord>>();
        builder.Services.AddScoped<AccountService>();builder.Services.AddScoped<AuthorizationService>();
        builder.Services.AddScoped<WebApi.Contracts.Security.IAuthorizationService>(sp=>sp.GetRequiredService<AuthorizationService>());
        var local=builder.Environment.IsDevelopment();
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options=>{
            options.Cookie.Name="WebApi.Session";options.Cookie.HttpOnly=true;options.Cookie.SameSite=SameSiteMode.Strict;
            options.Cookie.SecurePolicy=local?CookieSecurePolicy.SameAsRequest:CookieSecurePolicy.Always;
            options.ExpireTimeSpan=TimeSpan.FromHours(8);options.SlidingExpiration=false;
            options.Events.OnRedirectToLogin=ctx=>ProblemDetailsMapping.WriteAsync(ctx.HttpContext,401,"authentication_required","请先登录。");
            options.Events.OnRedirectToAccessDenied=ctx=>ProblemDetailsMapping.WriteAsync(ctx.HttpContext,403,"permission_denied","无权访问。");
            options.Events.OnValidatePrincipal=async ctx=>{
                if(!Guid.TryParse(ctx.Principal?.FindFirstValue(ClaimTypes.NameIdentifier),out var id)) {ctx.RejectPrincipal();return;}
                var db=ctx.HttpContext.RequestServices.GetRequiredService<WebApiDbContext>();
                var stamp=ctx.Principal?.FindFirstValue("security_stamp");
                if(!await db.Set<UserRecord>().AsNoTracking().AnyAsync(x=>x.Id==id && x.Status=="Active" && x.SecurityStamp==stamp,ctx.HttpContext.RequestAborted)) ctx.RejectPrincipal();
            };
        });
        builder.Services.AddAuthorization();
        builder.Services.AddAntiforgery(options=>{options.HeaderName="X-CSRF-Token";options.Cookie.Name="WebApi.Csrf";options.Cookie.HttpOnly=true;options.Cookie.SameSite=SameSiteMode.Strict;options.Cookie.SecurePolicy=local?CookieSecurePolicy.SameAsRequest:CookieSecurePolicy.Always;});
        var app=builder.Build();
        app.Use(ProblemDetailsMapping.HandleAsync);
        app.UseAuthentication();app.UseAuthorization();
        app.Use(async(ctx,next)=>{
            if(ctx.Request.Path.StartsWithSegments("/api/v1") && !HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method) && !HttpMethods.IsOptions(ctx.Request.Method))
            {
                var origin=ctx.Request.Headers.Origin.ToString();
                if(origin.Length>0 && (!Uri.TryCreate(origin,UriKind.Absolute,out var uri) || uri.Scheme!=ctx.Request.Scheme || uri.Authority!=ctx.Request.Host.Value)) {await ProblemDetailsMapping.WriteAsync(ctx,403,"cross_origin_write","请求来源不匹配。");return;}
                try {await ctx.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(ctx);} catch(AntiforgeryValidationException) {await ProblemDetailsMapping.WriteAsync(ctx,403,"csrf_required","缺少或无效的防伪令牌。");return;}
            }
            await next();
        });
        app.MapGet("/health/live",()=>Results.Ok(new { status="live" })).AllowAnonymous();
        app.MapSessions();return app;
    }
}
