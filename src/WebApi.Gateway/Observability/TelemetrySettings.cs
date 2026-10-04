using System.Globalization;
namespace WebApi.Gateway.Observability;
public sealed record TelemetrySettings(bool Enabled,Uri CollectorEndpoint,string? CredentialSecretFile,string? IpHmacSecretFile,double TraceSampleRatio,int QueueCapacity,int BatchDelayMs,int MetricExportIntervalMs)
{
    public static TelemetrySettings Read(IConfiguration configuration)
    {
        var section=configuration.GetSection("Observability");
        var enabled=section.GetValue<bool>("Enabled");
        if(!Uri.TryCreate(section["CollectorEndpoint"]??"http://collector:4318",UriKind.Absolute,out var endpoint)||endpoint.Scheme is not("http" or "https")||endpoint.UserInfo.Length>0||endpoint.Query.Length>0||endpoint.Fragment.Length>0)
            throw new InvalidOperationException("Invalid observability Collector endpoint configuration.");
        var ratio=double.Parse(section["TraceSampleRatio"]??"0.1",CultureInfo.InvariantCulture);
        var capacity=section.GetValue<int?>("QueueCapacity")??2048;
        var delay=section.GetValue<int?>("BatchDelayMs")??1000;
        var interval=section.GetValue<int?>("MetricExportIntervalMs")??15000;
        if(!double.IsFinite(ratio)||ratio is <0 or >1||capacity is <1 or >2048||delay is <10 or >5000||interval is <100 or >60000)
            throw new InvalidOperationException("Invalid bounded observability configuration.");
        if(enabled&&string.IsNullOrWhiteSpace(section["IpHmacSecretFile"]))throw new InvalidOperationException("Observability requires an IP HMAC secret file.");
        return new(enabled,endpoint,section["CredentialSecretFile"],section["IpHmacSecretFile"],ratio,capacity,delay,interval);
    }
}
