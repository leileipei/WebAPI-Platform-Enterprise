using WebApi.Contracts.OpenApi;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
namespace WebApi.Infrastructure.Catalog;
public sealed record ImportSourceSettings
{
    public bool AllowHttp { get; init; }
    public IReadOnlyList<string> AllowedPrivateCidrs { get; init; } = [];
    public IReadOnlyList<string> FixtureOrigins { get; init; } = [];
    public IReadOnlyList<string> DeniedOrigins { get; init; } = [];
    // Known local platform listeners are reserved even for an explicitly enabled fixture.
    public IReadOnlyList<int> DeniedPorts { get; init; } = [4192, 4194, 4196, 4197, 5432, 6379, 8080];
    public ContractLimits Limits { get; init; } = new();
    public TimeSpan NetworkTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public static ImportSourceSettings Read(IConfiguration configuration, IHostEnvironment environment)
    {
        var value = configuration.GetSection("ContractImport").Get<ImportSourceSettings>() ?? new();
        if (!environment.IsDevelopment() || !configuration.GetValue<bool>("ContractImport:FixtureEnabled")) value = value with { FixtureOrigins = [] };
        ImportSourcePolicyService.RequireLimits(value.Limits, new());
        if (value.NetworkTimeout <= TimeSpan.Zero || value.NetworkTimeout > TimeSpan.FromSeconds(15) || value.ConnectTimeout <= TimeSpan.Zero || value.ConnectTimeout > TimeSpan.FromSeconds(5)) throw new InvalidOperationException("Invalid contract import timeout configuration.");
        return value with { DeniedPorts = value.DeniedPorts.Concat(new ImportSourceSettings().DeniedPorts).Distinct().ToArray() };
    }
}
