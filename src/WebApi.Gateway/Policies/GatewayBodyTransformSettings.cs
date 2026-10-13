using WebApi.Contracts.Common;
using WebApi.Contracts.Runtime;
using WebApi.Domain.Policies;
namespace WebApi.Gateway.Policies;
public sealed class GatewayBodyTransformSettings
{
    public int MaxBodyBytes {get;}
    public int MaxConcurrentBodyTransforms {get;}
    private GatewayBodyTransformSettings(IConfiguration configuration)
    {
        MaxBodyBytes=configuration.GetValue<int?>("GatewayPolicies:Transform:MaxBodyBytes")??1048576;
        MaxConcurrentBodyTransforms=configuration.GetValue<int?>("GatewayPolicies:Transform:MaxConcurrentBodyTransforms")??16;
        if(MaxBodyBytes is <1024 or >1048576||MaxConcurrentBodyTransforms is <1 or >64)throw new InvalidOperationException("Invalid body transform deployment budget.");
    }
    public static GatewayBodyTransformSettings Read(IConfiguration configuration)=>new(configuration);
    public void Validate(RuntimeSnapshot snapshot)
    {
        foreach(var policy in snapshot.Policies.Where(p=>p.Type=="transform"))
        {
            var config=PolicyConfigurationValidator.ParseTransform(policy.Config);
            if(config.RequestBody?.MaxBodyBytes>MaxBodyBytes||config.ResponseBody?.MaxBodyBytes>MaxBodyBytes)throw new ApiException(422,"invalid_policy_config","正文映射超过节点部署限额。");
        }
    }
}
