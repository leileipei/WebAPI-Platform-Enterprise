using WebApi.ControlPlane.Sso;
using WebApi.ControlPlane.Comparisons;
using WebApi.Infrastructure.Comparisons;
using WebApi.Infrastructure.Sso;
using WebApi.ControlPlane.Settings;
using WebApi.Infrastructure.Settings;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WebApi.Infrastructure.Policies;
using WebApi.ControlPlane.Policies;
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
        builder.Services.TryAddSingleton(AlertEvaluationSettings.Read(builder.Configuration));builder.Services.AddScoped<AlertRuleScopeResolver>();builder.Services.AddScoped<AlertRuleService>();builder.Services.AddScoped<AlertEventService>();builder.Services.AddScoped<AlertSilenceExpiryService>();
        builder.Services.AddScoped<ApplicationService>();builder.Services.AddScoped<OpenApiImportService>();
        builder.Services.TryAddSingleton(ImportSourceSettings.Read(builder.Configuration,builder.Environment));
        builder.Services.TryAddSingleton<WebApi.Infrastructure.Contracts.IContractDnsResolver,WebApi.Infrastructure.Contracts.SystemContractDnsResolver>();
        builder.Services.AddScoped<ImportSourcePolicyService>();builder.Services.AddScoped<ImportSourceFetcher>();builder.Services.AddScoped<ImportBatchWriter>();builder.Services.AddScoped<ImportPreviewService>();builder.Services.AddScoped<ImportPreviewCleanupService>();
        builder.Services.AddScoped<PolicyAccess>();builder.Services.AddScoped<PolicyService>();builder.Services.AddScoped<PolicyReferenceService>();builder.Services.AddScoped<RoutePolicyService>();
        builder.Services.AddScoped<CatalogService>();builder.Services.AddScoped<RouteService>();builder.Services.AddScoped<ClusterService>();
        builder.Services.AddSingleton<ContractComparisonEngine>();builder.Services.AddScoped<ComparisonCursorCodec>();builder.Services.AddScoped<VersionComparisonService>();builder.Services.AddScoped<VersionRiskReviewService>();
        var origins=UpstreamAddressPolicy.ReadAllowedOrigins(builder.Configuration);
        builder.Services.AddSingleton(new UpstreamAddressPolicy(origins));
        builder.Services.TryAddSingleton<TimeProvider>(TimeProvider.System);builder.Services.AddScoped<SettingsPreviewProtector>();builder.Services.AddScoped<SystemSettingsService>();builder.Services.AddScoped<SystemSettingsReader>();builder.Services.AddScoped<AuditAccessQuery>();builder.Services.AddScoped<AuditExportService>();builder.Services.AddScoped<GovernanceService>();builder.Services.AddScoped<ScopeResolver>();builder.Services.AddScoped<AuditedCommandExecutor>();
        builder.Services.AddScoped<AccountService>();builder.Services.AddScoped<AuthorizationService>();
        builder.Services.Configure<SsoOptions>(builder.Configuration.GetSection("Sso"));
        // URI keys contain ':' and cannot be represented by flattened configuration paths.
        // This explicit development-fixture adapter preserves exact origins for the socket override.
        builder.Services.PostConfigure<SsoOptions>(settings=>{
            var raw=builder.Configuration["Sso:FixtureConnectOverridesJson"];
            if(!builder.Environment.IsDevelopment()||!settings.FixtureEnabled||string.IsNullOrEmpty(raw))return;
            if(raw.Length>4096)throw new InvalidOperationException("Invalid SSO fixture connection configuration.");
            var overrides=System.Text.Json.JsonSerializer.Deserialize<Dictionary<string,string>>(raw,new System.Text.Json.JsonSerializerOptions{MaxDepth=4})??throw new InvalidOperationException("Invalid SSO fixture connection configuration.");
            if(overrides.Count>16)throw new InvalidOperationException("Invalid SSO fixture connection configuration.");
            settings.FixtureConnectOverrides=overrides;
        });
        builder.Services.TryAddSingleton<ISsoDnsResolver,SystemSsoDnsResolver>();
        builder.Services.TryAddSingleton<IOidcAddressPolicy,OidcAddressPolicy>();
        builder.Services.TryAddSingleton<ISsoSecretResolver,SsoSecretResolver>();
        builder.Services.TryAddSingleton<IOidcMetadataClient>(sp=>new OidcMetadataClient(
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<SsoOptions>>(),sp.GetRequiredService<IOidcAddressPolicy>(),
            sp.GetRequiredService<TimeProvider>(),new HttpClient(new SsoBackchannelHandler(sp.GetRequiredService<IOidcAddressPolicy>())){Timeout=Timeout.InfiniteTimeSpan}));
        builder.Services.AddSingleton<SsoSecretVersion>();builder.Services.AddScoped<SsoProviderService>();
        builder.Services.AddScoped<LocalAdministratorGuard>();builder.Services.AddScoped<ExternalIdentityService>();
        builder.Services.AddScoped<SsoLoginCoordinator>();
        builder.Services.AddSingleton<OidcSchemeRegistry>();builder.Services.AddScoped<PlatformSessionIssuer>();
        builder.Services.AddScoped<SsoSessionValidator>();builder.Services.AddScoped<SsoAttemptCleanupService>();builder.Services.AddHostedService<SsoAttemptCleanupWorker>();
        builder.Services.AddScoped<WebApi.Contracts.Security.IAuthorizationService>(sp=>sp.GetRequiredService<AuthorizationService>());
        builder.Services.AddSingleton(ObservationSourceSettings.Read(builder.Configuration));
        builder.Services.AddHttpClient("observability",client=>client.Timeout=TimeSpan.FromSeconds(10)).ConfigurePrimaryHttpMessageHandler(()=>new SocketsHttpHandler{AllowAutoRedirect=false,UseCookies=false});
        builder.Services.AddScoped<TempoTraceSource>();builder.Services.AddScoped<TraceQueryService>();builder.Services.AddSingleton<ObservationCursorCodec>();builder.Services.AddScoped<ObservationSignalCoverage>();builder.Services.AddScoped<LokiLogSource>();builder.Services.AddScoped<AccessLogQueryService>();builder.Services.AddScoped<ObservationScopeResolver>();builder.Services.AddScoped<ObservationSourceClient>();builder.Services.AddScoped<ObservationCoverageService>();builder.Services.AddScoped<CollectorSignalCoverage>();builder.Services.AddScoped<PrometheusMetricSource>();builder.Services.AddScoped<ObservationQueryService>();
        PersistentDataProtection.Configure(builder.Services,builder.Configuration);
        var local=builder.Environment.IsDevelopment();
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options=>{
            options.Cookie.Name=builder.Configuration["Authentication:CookieName"]??"WebApi.Session";options.Cookie.HttpOnly=true;options.Cookie.SameSite=SameSiteMode.Strict;
            options.Cookie.SecurePolicy=local?CookieSecurePolicy.SameAsRequest:CookieSecurePolicy.Always;
            options.ExpireTimeSpan=TimeSpan.FromHours(8);options.SlidingExpiration=false;
            options.Events.OnRedirectToLogin=ctx=>ProblemDetailsMapping.WriteAsync(ctx.HttpContext,401,"authentication_required","请先登录。");
            options.Events.OnRedirectToAccessDenied=ctx=>ProblemDetailsMapping.WriteAsync(ctx.HttpContext,403,"permission_denied","无权访问。");
            options.Events.OnValidatePrincipal=async ctx=>{
                if(!Guid.TryParse(ctx.Principal?.FindFirstValue(ClaimTypes.NameIdentifier),out var id)) {ctx.RejectPrincipal();return;}
                var db=ctx.HttpContext.RequestServices.GetRequiredService<WebApiDbContext>();
                var stamp=ctx.Principal?.FindFirstValue("security_stamp");
                var providerValue=ctx.Principal?.FindFirstValue("sso_provider");
                var revisionValue=ctx.Principal?.FindFirstValue("sso_auth_revision");var bindingValue=ctx.Principal?.FindFirstValue("sso_binding");
                if(providerValue is not null||revisionValue is not null||bindingValue is not null)
                {
                    if(!Guid.TryParse(providerValue,out var provider)||!long.TryParse(revisionValue,System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out var revision)||
                        !Guid.TryParse(bindingValue,out var binding)||!await ctx.HttpContext.RequestServices.GetRequiredService<SsoSessionValidator>().ValidateAsync(id,stamp??"",provider,revision,binding,ctx.HttpContext.RequestAborted))ctx.RejectPrincipal();
                }
                else if(!await db.Set<UserRecord>().AsNoTracking().AnyAsync(x=>x.Id==id && x.Status=="Active" && x.SecurityStamp==stamp,ctx.HttpContext.RequestAborted)) ctx.RejectPrincipal();
            };
        });
        builder.Services.AddAuthentication().AddOpenIdConnect("oidc-template",_=>{});
        builder.Services.AddSingleton<Microsoft.AspNetCore.Authentication.IAuthenticationSchemeProvider,OidcSchemeProvider>();
        builder.Logging.AddFilter("Microsoft.AspNetCore.Authentication.OpenIdConnect",LogLevel.None);
        builder.Logging.AddFilter("Microsoft.IdentityModel",LogLevel.None);
        builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics",LogLevel.Warning);
        builder.Services.AddAuthorization();
        builder.Services.AddAntiforgery(options=>{options.HeaderName="X-CSRF-Token";options.Cookie.Name=builder.Configuration["Antiforgery:CookieName"]??"WebApi.Csrf";options.Cookie.HttpOnly=true;options.Cookie.SameSite=SameSiteMode.Strict;options.Cookie.SecurePolicy=local?CookieSecurePolicy.SameAsRequest:CookieSecurePolicy.Always;});
        var app=builder.Build();
        app.Use(ProblemDetailsMapping.HandleAsync);
        app.UseMiddleware<SsoProtocolMiddleware>();app.UseAuthentication();app.UseAuthorization();
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
        app.MapSsoLogin();app.MapExternalIdentities();app.MapSsoProviders();app.MapSystemSettings();app.MapSessions();app.MapGovernance();app.MapCatalog();app.MapImportSourcePolicies();app.MapImportSessions();app.MapComparisons();app.MapRouting();app.MapPolicies();app.MapApplications();app.MapOpenApiImport();app.MapReleases();app.MapInternalNodes();app.MapGatewayRead();app.MapObservationMetrics();app.MapObservationLogs();app.MapObservationTraces();app.MapAlertRules();app.MapAlertEvents();return app;
    }
}
