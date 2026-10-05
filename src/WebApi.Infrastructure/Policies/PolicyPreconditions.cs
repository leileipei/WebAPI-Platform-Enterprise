using WebApi.Contracts.Common;
namespace WebApi.Infrastructure.Policies;
public static class PolicyPreconditions
{
    public static void Require(string? ifMatch,long current)
    {if(string.IsNullOrWhiteSpace(ifMatch)) throw new ApiException(428,"precondition_required","请先读取最新版本并携带If-Match。");RevisionTag.Require(ifMatch,current);}
}
