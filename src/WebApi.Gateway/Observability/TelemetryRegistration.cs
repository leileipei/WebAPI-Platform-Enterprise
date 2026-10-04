using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
namespace WebApi.Gateway.Observability;
public static class TelemetryRegistration
{
    public static void AddGatewayTelemetry(this IServiceCollection services,IConfiguration configuration,GatewaySettings gateway)
    {
        var settings=TelemetrySettings.Read(configuration);services.AddSingleton(settings);if(!settings.Enabled)return;
        services.TryAddSingleton<TelemetryDropTracker>();services.AddSingleton<TelemetrySanitizer>();services.AddSingleton<GatewayHealthObserver>();
        services.TryAddSingleton<ITelemetryBatchSink,OtlpJsonTelemetrySink>();services.AddSingleton<BoundedTelemetryBuffer>();services.AddHostedService(sp=>sp.GetRequiredService<BoundedTelemetryBuffer>());services.AddSingleton<GatewayTelemetryRecorder>();
        services.AddSingleton(sp=>{
            var recorder=sp.GetRequiredService<GatewayTelemetryRecorder>();
            return Sdk.CreateTracerProviderBuilder().AddSource(recorder.MeterName).SetSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(settings.TraceSampleRatio))).AddProcessor(new TelemetryActivityProcessor(sp.GetRequiredService<BoundedTelemetryBuffer>(),sp.GetRequiredService<TelemetryDropTracker>(),recorder.MeterName)).Build();
        });
        services.AddSingleton(sp=>{
            var recorder=sp.GetRequiredService<GatewayTelemetryRecorder>();
            return Sdk.CreateMeterProviderBuilder().AddMeter(recorder.MeterName).AddView(instrument=>instrument.Name=="webapi_gateway_request_duration_seconds"?new ExplicitBucketHistogramConfiguration{Boundaries=[0.005,0.01,0.025,0.05,0.1,0.25,0.5,1,2.5,5,10,30,60],CardinalityLimit=10000}:new MetricStreamConfiguration{CardinalityLimit=10000})
                .AddReader(new PeriodicExportingMetricReader(new TelemetryMetricExporter(sp.GetRequiredService<BoundedTelemetryBuffer>(),sp.GetRequiredService<TelemetryDropTracker>()),settings.MetricExportIntervalMs,5000)).Build();
        });
        services.AddHostedService<ProviderActivation>();
    }
    private sealed class ProviderActivation(TracerProvider traces,MeterProvider metrics) : IHostedService
    {
        public Task StartAsync(CancellationToken ct){_ = traces;_ = metrics;return Task.CompletedTask;}
        public Task StopAsync(CancellationToken ct){traces.ForceFlush(1000);metrics.ForceFlush(1000);return Task.CompletedTask;}
    }
}
