using System.Text.Json.Serialization;

namespace WebApi.Contracts.Policies;

public sealed record CorsConfiguration
{
    public IReadOnlyList<string> AllowedOrigins { get; }
    public bool AllowCredentials { get; }
    public IReadOnlyList<string> AllowedMethods { get; }
    public IReadOnlyList<string> AllowedHeaders { get; }
    public IReadOnlyList<string> ExposedHeaders { get; }
    public int MaxAgeSeconds { get; }

    public CorsConfiguration(IReadOnlyList<string> allowedOrigins, bool allowCredentials,
        IReadOnlyList<string> allowedMethods, IReadOnlyList<string> allowedHeaders,
        IReadOnlyList<string> exposedHeaders, int maxAgeSeconds)
    {
        AllowedOrigins = Array.AsReadOnly(allowedOrigins.ToArray());
        AllowCredentials = allowCredentials;
        AllowedMethods = Array.AsReadOnly(allowedMethods.ToArray());
        AllowedHeaders = Array.AsReadOnly(allowedHeaders.ToArray());
        ExposedHeaders = Array.AsReadOnly(exposedHeaders.ToArray());
        MaxAgeSeconds = maxAgeSeconds;
    }
}

[JsonConverter(typeof(JsonStringEnumConverter<IpFilterMode>))]
public enum IpFilterMode { AllowList, DenyList }

public sealed record IpFilterConfiguration
{
    public IpFilterMode Mode { get; }
    public IReadOnlyList<string> Ranges { get; }
    public IpFilterConfiguration(IpFilterMode mode, IReadOnlyList<string> ranges)
    {
        Mode = mode;
        Ranges = Array.AsReadOnly(ranges.ToArray());
    }
}
