using Microsoft.Extensions.DependencyInjection.Extensions;
using WebApi.Infrastructure.Alerts;
using WebApi.ControlPlane.Alerts;
using WebApi.ControlPlane.Observability;
using WebApi.Infrastructure.Observability;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using WebApi.ControlPlane.Security;
using WebApi.ControlPlane.Governance;
using WebApi.ControlPlane.Catalog;
using WebApi.ControlPlane.Applications;
using WebApi.Infrastructure.Applications;
using WebApi.Infrastructure.Commands;
using WebApi.Infrastructure.Releases;
using WebApi.ControlPlane.Releases;
using WebApi.Contracts.Common;
using WebApi.ControlPlane.Routing;
using WebApi.ControlPlane.Gateway;
using WebApi.Infrastructure.Gateway;
using WebApi.Infrastructure.Catalog;
using WebApi.Infrastructure.Routing;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Security;
namespace WebApi.ControlPlane;
public static class ControlPlaneApp
{
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder=WebApplication.CreateBuilder(args);configure?.Invoke(builder);
        builder.WebHost.ConfigureKestrel(options=>options.Limits.MaxRequestBodySize=8*1024*1024);
        var connection=builder.Configuration.GetConnectionString("WebApi")??DatabaseSettings.ConnectionString();
        builder.Services.AddDbContext<WebApiDbContext>(options=>options.UseNpgsql(connection));
        builder.Services.AddScoped<IPasswordHasher<UserRecord>,PasswordHasher<UserRecord>>();
        builder.Services.AddHttpContextAccessor();builder.Services.AddScoped<IdempotentCommandExecutor>();
        builder.Services.AddScoped(sp=>new CommandRequestContext(sp.GetRequiredService<IHttpContextAccessor>().HttpContext?.Request.Headers["Idempotency-Key"].ToString()??""));builder.Services.AddScoped(sp=>new AuditRequestMetadata(sp.GetRequiredService<IHttpContextAccessor>().HttpContext?.Connection.RemoteIpAddress));
        builder.Services.AddScoped<ApprovalFlowService>();builder.Services.AddScoped<ReleaseService>();builder.Services.AddScoped<ReleaseCandidateBuilder>();
        builder.Services.AddScoped<PublishCoordinator>();builder.Services.AddScoped<SnapshotCompiler>();builder.Services.AddSingleton(new PublishSettings());
        builder.Services.AddScoped<HistoricalSnapshotService>();builder.Services.AddScoped<RollbackService>();
        builder.Services.AddScoped<ReleaseRecoveryService>();
        builder.Services.AddSingleton(NodeEnrollmentSettings.Read(builder.Configuration));builder.Services.AddScoped<NodeIdentityService>();builder.Services.AddScoped<NodeRegistry>();builder.Services.AddScoped<AckService>();builder.Services.AddScoped<ReleaseTimeoutService>();
        builder.Services.AddScoped<GatewayReadService>();
        builder.Services.TryAddSingleton(AlertEvaluationSettings.Read(builder.Configuration));builder.Services.AddScoped<AlertRuleScopeResolver>();builder.Services.AddScoped<AlertRuleService>();
        builder.Services.AddScoped<ApplicationService>();builder.Services.AddScoped<OpenApiImportService>();
        builder.Services.AddScoped<CatalogService>();builder.Services.AddScoped<RouteService>();builder.Services.AddScoped<ClusterService>();
        var origins=builder.Configuration.GetSection("Upstream:AllowedOrigins").Get<string[]>()??["http://test-backend:8080"];
        builder.Services.AddSingleton(new UpstreamAddressPolicy(origins));
        builder.Services.AddScoped<GovernanceService>();builder.Services.AddScoped<ScopeResolver>();builder.Services.AddScoped<AuditedCommandExecutor>();
        builder.Services.AddScoped<AccountService>();builder.Services.AddScoped<AuthorizationService>();
        builder.Services.AddScoped<WebApi.Contracts.Security.IAuthorizationService>(sp=>sp.GetRequiredService<AuthorizationService>());
        builder.Services.AddSingleton(ObservationSourceSettings.Read(builder.Configuration));
        builder.Services.AddHttpClient("observability",client=>client.Timeout=TimeSpan.FromSeconds(10)).ConfigurePrimaryHttpMessageHandler(()=>new SocketsHttpHandler{AllowAutoRedirect=false,UseCookies=false});
        builder.Services.AddScoped<TempoTraceSource>();builder.Services.AddScoped<TraceQueryService>();builder.Services.AddSingleton<ObservationCursorCodec>();builder.Services.AddScoped<ObservationSignalCoverage>();builder.Services.AddScoped<LokiLogSource>();builder.Services.AddScoped<AccessLogQueryService>();builder.Services.AddScoped<ObservationScopeResolver>();builder.Services.AddScoped<ObservationSourceClient>();builder.Services.AddScoped<ObservationCoverageService>();builder.Services.AddScoped<PrometheusMetricSource>();builder.Services.AddScoped<ObservationQueryService>();
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
        app.MapSessions();app.MapGovernance();app.MapCatalog();app.MapRouting();app.MapApplications();app.MapOpenApiImport();app.MapReleases();app.MapInternalNodes();app.MapGatewayRead();app.MapObservationMetrics();app.MapObservationLogs();app.MapObservationTraces();app.MapAlertRules();return app;
    }
}
