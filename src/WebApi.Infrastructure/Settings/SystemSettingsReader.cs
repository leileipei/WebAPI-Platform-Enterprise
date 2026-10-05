using Microsoft.EntityFrameworkCore;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Settings;
public sealed class SystemSettingsReader(WebApiDbContext db)
{
    public async Task<SettingsStoredValue> ReadAsync(string group,CancellationToken ct=default){var fallback=SettingsValueCodec.Default(group);var key="system."+group;var row=await db.Set<SystemSetting>().AsNoTracking().SingleOrDefaultAsync(x=>x.Key==key,ct);return row is null?new(fallback,0,false):new(SettingsValueCodec.Decode(group,row.Value),row.Revision,true);}
    public async Task<SecuritySettings> SecurityAsync(CancellationToken ct=default)=>(SecuritySettings)(await ReadAsync("security",ct)).Value;
    public async Task<GatewaySettings> GatewayAsync(CancellationToken ct=default)=>(GatewaySettings)(await ReadAsync("gateway",ct)).Value;
    public async Task<AuditSettings> AuditAsync(CancellationToken ct=default)=>(AuditSettings)(await ReadAsync("audit",ct)).Value;
}
