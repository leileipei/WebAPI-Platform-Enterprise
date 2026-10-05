namespace WebApi.Infrastructure.Persistence.Entities;
public sealed class SystemSetting
{
    public string Key {get;set;}="";
    public string ScopeType {get;set;}="system";
    public Guid? ScopeId {get;set;}
    public string Value {get;set;}="{}";
    public Guid UpdatedBy {get;set;}
    public DateTimeOffset UpdatedAt {get;set;}=DateTimeOffset.UtcNow;
    public long Revision {get;set;}=1;
}
