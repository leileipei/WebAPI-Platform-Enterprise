using System.Security.Cryptography;
using System.Text.RegularExpressions;
using WebApi.Contracts.Common;
using WebApi.Contracts.Runtime;
using WebApi.Domain.Policies;
using WebApi.Infrastructure.Policies;
namespace WebApi.Gateway.Policies;
public sealed class GatewayCacheSettings
{
    private readonly Lazy<byte[]> hmac;
    public string KeyPrefix { get; }
    public string RedisConnection { get; }
    public int MaxTotalBytes { get; }
    public int MaxEntries { get; }
    public int MaxEntryBytes { get; }
    public int DecisionTimeoutMs { get; }
    private readonly HashSet<string> vary;
    private GatewayCacheSettings(IConfiguration config)
    {
        KeyPrefix = config["GatewayPolicies:Cache:KeyPrefix"] ?? "webapi:responsecache";
        RedisConnection = config["GatewayPolicies:Cache:RedisConnection"] ?? config["Redis:Connection"] ?? "redis:6379,abortConnect=false,connectTimeout=1000,asyncTimeout=1000";
        MaxTotalBytes = config.GetValue<int?>("GatewayPolicies:Cache:MaxTotalBytes") ?? 64 * 1024 * 1024;
        MaxEntries = config.GetValue<int?>("GatewayPolicies:Cache:MaxEntries") ?? 2000;
        MaxEntryBytes = config.GetValue<int?>("GatewayPolicies:Cache:MaxEntryBytes") ?? 1048576;
        DecisionTimeoutMs = config.GetValue<int?>("GatewayPolicies:Cache:DecisionTimeoutMs") ?? 100;
        if (!Regex.IsMatch(KeyPrefix, "^[a-zA-Z0-9:_-]{1,128}$") || string.IsNullOrWhiteSpace(RedisConnection)
            || MaxTotalBytes is <1024 or >67108864 || MaxEntries is <1 or >2000 || DecisionTimeoutMs is <10 or >1000) throw new InvalidOperationException("Invalid cache deployment budget.");
        var rules = new GatewayPolicyDeploymentRules(MaxEntryBytes, config.GetSection("GatewayPolicies:Cache:AllowedVaryHeaders").Get<string[]>());
        vary = new(rules.Limits.AllowedVaryHeaders, StringComparer.OrdinalIgnoreCase);
        var file = config["GatewayPolicies:Cache:HmacSecretFile"];
        hmac = new(() => ReadSecret(file), LazyThreadSafetyMode.ExecutionAndPublication);
    }
    public static GatewayCacheSettings Read(IConfiguration configuration) => new(configuration);
    public bool AllowsVary(string header) => vary.Contains(header);
    internal string Hash(ReadOnlySpan<byte> input) => Convert.ToHexStringLower(HMACSHA256.HashData(hmac.Value, input));
    public void Validate(RuntimeSnapshot snapshot)
    {
        var caches = snapshot.Policies.Where(p => p.Type == "cache").ToArray();
        if (caches.Length == 0) return;
        _ = hmac.Value;
        foreach (var policy in caches)
        {
            var config = PolicyConfigurationValidator.ParseCache(policy.Config);
            if (config.MaxEntryBytes > MaxEntryBytes || config.VaryHeaders.Any(h => !AllowsVary(h))) throw Invalid();
        }
    }
    private static byte[] ReadSecret(string? file)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(file) || !Path.IsPathFullyQualified(file)) throw Invalid();
            if (!OperatingSystem.IsWindows())
            {
                var mode = File.GetUnixFileMode(file);
                if ((mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0) throw Invalid();
            }
            var bytes = Convert.FromBase64String(File.ReadAllText(file).Trim());
            return bytes.Length == 32 ? bytes : throw Invalid();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException)
        { throw Invalid(); }
    }
    private static ApiException Invalid() => new(422, "gateway_cache_config_unsupported", "Cache deployment configuration is unavailable or unsupported.");
}
