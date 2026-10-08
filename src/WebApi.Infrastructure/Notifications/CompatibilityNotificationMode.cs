using Microsoft.Extensions.Configuration;
using WebApi.Contracts.Common;
namespace WebApi.Infrastructure.Notifications;
// Recovery-only bridge. It never sends notifications or rewrites new definitions.
public sealed class CompatibilityNotificationMode
{
    private CompatibilityNotificationMode(bool readOnly){IsReadOnly=readOnly;}
    public bool IsReadOnly {get;}
    public static CompatibilityNotificationMode Disabled {get;}=new(false);
    public static CompatibilityNotificationMode ReadOnly {get;}=new(true);
    public static CompatibilityNotificationMode Read(IConfiguration configuration)=>configuration["CompatibilityNotifications:Mode"] switch {null or "Disabled"=>Disabled,"ReadOnly"=>ReadOnly,_=>throw new InvalidOperationException("Invalid recovery compatibility mode.")};
    public void RequireWritable(){if(IsReadOnly)throw new ApiException(423,"notification_maintenance_mode","恢复桥接模式：通知设置与告警规则暂时只读，请恢复候选版本后再修改。");}
}
