using System.Text.RegularExpressions;
namespace WebApi.Gateway.Policies;
public sealed record TrafficPolicySettings(string RedisConnection,string KeyPrefix="webapi:traffic",int DecisionTimeoutMs=100)
{
    public static TrafficPolicySettings Read(IConfiguration config)
    {
        var connection=config["TrafficPolicies:RedisConnection"]??config["Redis:Connection"]??"redis:6379,abortConnect=false,connectTimeout=1000,asyncTimeout=1000";
        var prefix=config["TrafficPolicies:KeyPrefix"]??"webapi:traffic";var budget=config.GetValue<int?>("TrafficPolicies:DecisionTimeoutMs")??100;
        if(string.IsNullOrWhiteSpace(connection)||!Regex.IsMatch(prefix,"^[a-zA-Z0-9:_-]{1,128}$")||budget is <10 or >5000) throw new InvalidOperationException("Invalid traffic policy storage configuration.");return new(connection,prefix,budget);
    }
}
