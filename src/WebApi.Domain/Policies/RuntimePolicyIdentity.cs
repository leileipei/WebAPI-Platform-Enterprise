using System.Security.Cryptography;
using WebApi.Contracts.Common;
namespace WebApi.Domain.Policies;
public static class RuntimePolicyIdentity
{
    public static Guid Create(Guid sourceId,long revision,string type,string normalizedConfig)
    {
        if(sourceId==Guid.Empty||revision<1) throw new ApiException(422,"invalid_policy_identity","运行策略来源或修订不合法。");
        var config=PolicyConfigurationValidator.Normalize(type,normalizedConfig);
        var hash=SHA256.HashData(CanonicalJson.Serialize(new {sourceId,revision,type,config}));return new Guid(hash.AsSpan(0,16),bigEndian:true);
    }
}
