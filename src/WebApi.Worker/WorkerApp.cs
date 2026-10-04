using WebApi.Infrastructure.Alerts;
using WebApi.Infrastructure.Observability;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
builder.Services.AddScoped<AuthorizationService>();builder.Services.AddScoped<ScopeResolver>();builder.Services.AddScoped<AuditedCommandExecutor>();builder.Services.AddScoped<IdempotentCommandExecutor>();builder.Services.AddScoped<ReleaseCandidateBuilder>();builder.Services.AddScoped<ReleaseService>();builder.Services.AddScoped<PublishCoordinator>();builder.Services.AddScoped<SnapshotCompiler>();builder.Services.AddScoped<OutboxDispatcher>();builder.Services.AddSingleton(new CommandRequestContext(""));
builder.Services.AddSingleton(new PublishSettings(AckTimeoutSeconds:int.Parse(builder.Configuration["Publish:AckTimeoutSeconds"]??"120")));
builder.Services.AddScoped<HistoricalSnapshotService>();
builder.Services.AddScoped<RouteService>();
builder.Services.AddSingleton(new UpstreamAddressPolicy(builder.Configuration.GetSection("Upstream:AllowedOrigins").Get<string[]>()??["http://test-backend:8080"]));
builder.Services.AddSingleton(new RedisSnapshotStore(builder.Configuration["Redis:Connection"]??"redis:6379,abortConnect=false",builder.Configuration["Redis:Prefix"]??"webapi:runtime"));
builder.Services.AddSingleton(AlertEvaluationSettings.Read(builder.Configuration));builder.Services.AddScoped<AlertRuleScopeResolver>();builder.Services.AddScoped<AlertEvaluationLeaseStore>();builder.Services.AddScoped<AlertEvaluationService>();builder.Services.AddScoped<AlertSilenceExpiryService>();builder.Services.AddHostedService<AlertSilenceExpiryWorker>();
builder.Services.AddSingleton(ObservationSourceSettings.Read(builder.Configuration));builder.Services.AddHttpClient("observability",client=>client.Timeout=TimeSpan.FromSeconds(10)).ConfigurePrimaryHttpMessageHandler(()=>new SocketsHttpHandler{AllowAutoRedirect=false,UseCookies=false});builder.Services.AddScoped<ObservationSourceClient>();builder.Services.AddScoped<ObservationCoverageService>();builder.Services.AddScoped<PrometheusMetricSource>();builder.Services.AddHostedService<AlertEvaluationWorker>();
builder.Services.AddScoped<ReleaseTimeoutService>();builder.Services.AddHostedService<ReleaseTimeoutWorker>();builder.Services.AddHostedService<ReleaseBuildWorker>();builder.Services.AddHostedService<OutboxWorker>();return builder.Build();

 }
}
