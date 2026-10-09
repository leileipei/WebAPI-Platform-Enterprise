using WebApi.Infrastructure.Comparisons;
using WebApi.Infrastructure.Policies;
using WebApi.Infrastructure.Catalog;
using WebApi.Infrastructure.Settings;
using WebApi.Infrastructure.Notifications;
using WebApi.Infrastructure.Alerts;
using WebApi.Infrastructure.Observability;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using WebApi.Contracts.Common;
using WebApi.Infrastructure.Commands;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Gateway;
using WebApi.Infrastructure.Messaging;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Releases;
using WebApi.Infrastructure.Routing;
using WebApi.Infrastructure.Runtime;
using WebApi.Infrastructure.Security;
using WebApi.Worker.Workers;
namespace WebApi.Worker;
public static class WorkerApp
{
 public static IHost Build(string[] args,Action<HostApplicationBuilder>? configure=null)
 {
var builder=Host.CreateApplicationBuilder(args);configure?.Invoke(builder);
builder.Services.AddDbContext<WebApiDbContext>(o=>o.UseNpgsql(builder.Configuration["ConnectionStrings:WebApi"]??DatabaseSettings.ConnectionString()));
builder.Services.AddScoped<AuthorizationService>();builder.Services.AddScoped<ScopeResolver>();builder.Services.AddScoped<AuditedCommandExecutor>();builder.Services.AddScoped<IdempotentCommandExecutor>();builder.Services.AddScoped<ReleaseCandidateBuilder>();builder.Services.AddScoped<ReleaseService>();builder.Services.AddScoped<WebApi.Infrastructure.Delivery.DeliveryReleaseGuard>();builder.Services.AddScoped<WebApi.Infrastructure.Delivery.PromotionExecutionService>();builder.Services.AddScoped<WebApi.Infrastructure.Delivery.PromotionReadService>();builder.Services.AddScoped<WebApi.Infrastructure.Delivery.PromotionMappingService>();builder.Services.AddScoped<WebApi.Infrastructure.Delivery.PromotionCredentialSelection>();builder.Services.AddScoped<WebApi.Infrastructure.Delivery.DeliveryLockCoordinator>();builder.Services.AddScoped<RunningDeploymentReader>();builder.Services.AddScoped<WebApi.Infrastructure.Delivery.ReleaseArtifactService>();builder.Services.AddScoped<WebApi.Infrastructure.Delivery.TestAcceptanceService>();builder.Services.AddScoped<ReleaseAccessContextService>();builder.Services.AddScoped<ApprovalEligibilityService>();builder.Services.AddScoped<PublishCoordinator>();builder.Services.AddScoped<SnapshotCompiler>();builder.Services.AddScoped<OutboxDispatcher>();builder.Services.AddSingleton(new CommandRequestContext(""));
builder.Services.AddSingleton(new PublishSettings(AckTimeoutSeconds:int.Parse(builder.Configuration["Publish:AckTimeoutSeconds"]??"120")));
builder.Services.AddScoped<HistoricalSnapshotService>();
PersistentProtectionConfiguration.Configure(builder.Services,builder.Configuration,readOnly:true);builder.Services.AddSingleton<ContractComparisonEngine>();builder.Services.AddScoped<ComparisonCursorCodec>();builder.Services.AddScoped<VersionComparisonService>();builder.Services.AddScoped<VersionRiskReviewService>();builder.Services.TryAddSingleton(new WebApi.Infrastructure.Contracts.SchemaValidationSettings());builder.Services.AddSingleton<WebApi.Infrastructure.Contracts.ContractProcessRunner>();builder.Services.AddScoped<VersionContractSourceService>();builder.Services.AddScoped<SchemaValidationService>();builder.Services.AddScoped<CatalogService>();
builder.Services.TryAddSingleton(NotificationDeploymentSettings.Read(builder.Configuration,builder.Environment));builder.Services.TryAddSingleton<INotificationSecretResolver,NotificationSecretResolver>();builder.Services.AddSingleton<NotificationSecretVersion>();builder.Services.AddScoped<NotificationConfigurationService>();builder.Services.AddScoped<NotificationPlanner>();builder.Services.TryAddSingleton<INotificationDnsResolver,NotificationDnsResolver>();builder.Services.AddScoped<INotificationAddressPolicy,NotificationAddressPolicy>();builder.Services.AddScoped<SmtpNotificationTransport>();builder.Services.AddScoped<WebhookNotificationTransport>();builder.Services.TryAddScoped<INotificationTransport,NotificationTransport>();builder.Services.AddScoped<NotificationDeliveryStore>();builder.Services.AddScoped<NotificationDispatcher>();builder.Services.AddScoped<NotificationQueryService>();builder.Services.AddScoped<NotificationTestService>();builder.Services.AddHostedService<NotificationDeliveryWorker>();
builder.Services.TryAddSingleton(GatewayPolicyDeploymentRules.Read(builder.Configuration,builder.Environment));builder.Services.AddScoped<PolicyAccess>();builder.Services.AddScoped<PolicyReferenceService>();builder.Services.AddScoped<JwtApplicationBindingService>();
builder.Services.AddScoped<SystemSettingsReader>();builder.Services.AddScoped<RouteService>();
builder.Services.AddSingleton(new UpstreamAddressPolicy(UpstreamAddressPolicy.ReadAllowedOrigins(builder.Configuration)));
builder.Services.AddSingleton(new RedisSnapshotStore(builder.Configuration["Redis:Connection"]??"redis:6379,abortConnect=false",builder.Configuration["Redis:Prefix"]??"webapi:runtime"));
builder.Services.AddSingleton(AlertEvaluationSettings.Read(builder.Configuration));builder.Services.AddScoped<AlertRuleScopeResolver>();builder.Services.AddScoped<AlertEvaluationLeaseStore>();builder.Services.AddScoped<AlertEvaluationService>();builder.Services.AddScoped<AlertSilenceExpiryService>();builder.Services.AddHostedService<AlertSilenceExpiryWorker>();
builder.Services.AddSingleton(ObservationSourceSettings.Read(builder.Configuration));builder.Services.AddHttpClient("observability",client=>client.Timeout=TimeSpan.FromSeconds(10)).ConfigurePrimaryHttpMessageHandler(()=>new SocketsHttpHandler{AllowAutoRedirect=false,UseCookies=false});builder.Services.AddScoped<ObservationSourceClient>();builder.Services.AddScoped<ObservationCoverageService>();builder.Services.AddScoped<CollectorSignalCoverage>();builder.Services.AddScoped<PrometheusMetricSource>();builder.Services.AddHostedService<AlertEvaluationWorker>();
builder.Services.AddSingleton(TimeProvider.System);builder.Services.AddScoped<ImportPreviewCleanupService>();builder.Services.AddHostedService<ImportPreviewCleanupWorker>();
builder.Services.AddScoped<ReleaseTimeoutService>();builder.Services.AddHostedService<ReleaseTimeoutWorker>();builder.Services.AddHostedService<ReleaseBuildWorker>();builder.Services.AddHostedService<OutboxWorker>();return builder.Build();

 }
}
