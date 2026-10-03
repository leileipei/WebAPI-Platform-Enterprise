using WebApi.Contracts.Common;
namespace WebApi.Domain.Releases;
public static class ReleaseStateMachine
{
    public static void Require(string current,params string[] allowed) {if(!allowed.Contains(current)) throw new ApiException(409,"invalid_release_state","当前发布状态不允许此操作。");}
    public static bool Terminal(string state)=>state is "Succeeded" or "Failed" or "Cancelled" or "Rejected" or "RolledBack";
}
