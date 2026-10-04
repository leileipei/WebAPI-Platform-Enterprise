using WebApi.Contracts.Common;
namespace WebApi.Infrastructure.Observability;
public sealed record ObservationSourceSettings(bool Enabled,Uri PrometheusUrl,Uri LokiUrl,Uri TempoUrl,string? CredentialSecretFile,string? IpHmacSecretFile,double TraceSampleRatio)
{
    public static ObservationSourceSettings Read(Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        var s=configuration.GetSection("Observability");
        Uri Endpoint(string key,string fallback){if(!Uri.TryCreate(s[key]??fallback,UriKind.Absolute,out var uri)||uri.Scheme is not("http" or "https")||uri.UserInfo.Length>0||uri.Query.Length>0||uri.Fragment.Length>0)throw new InvalidOperationException("Invalid observation source configuration.");return uri;}
        return new(bool.TryParse(s["Enabled"],out var enabled)&&enabled,Endpoint("PrometheusUrl","http://prometheus:9090"),Endpoint("LokiUrl","http://loki:3100"),Endpoint("TempoUrl","http://tempo:3200"),s["CredentialSecretFile"],s["IpHmacSecretFile"],double.TryParse(s["TraceSampleRatio"],System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var ratio)&&ratio is >=0 and <=1?ratio:0.1);
    }
    public static ApiException Unavailable()=>new(503,"observability_source_unavailable","观测数据源暂不可用，请稍后重试。");
}
