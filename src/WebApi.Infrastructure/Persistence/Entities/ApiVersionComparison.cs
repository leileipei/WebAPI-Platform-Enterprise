namespace WebApi.Infrastructure.Persistence.Entities;
public sealed class ApiVersionComparison
{
    public Guid Id {get;set;}=Guid.NewGuid();
    public Guid OrganizationId {get;set;}
    public Guid ProjectId {get;set;}
    public Guid ApiId {get;set;}
    public Guid FromVersionId {get;set;}
    public Guid ToVersionId {get;set;}
    public long FromRevision {get;set;}
    public long ToRevision {get;set;}
    public string FromVersion {get;set;}="";
    public string ToVersion {get;set;}="";
    public string EngineVersion {get;set;}="";
    public string InputFingerprint {get;set;}="";
    public string ReportHash {get;set;}="";
    public string Coverage {get;set;}="";
    public string CountsJson {get;set;}="{}";
    public byte[] InputBytes {get;set;}=[];
    public byte[] ReportBytes {get;set;}=[];
    public Guid CreatedBy {get;set;}
    public DateTimeOffset CreatedAt {get;set;}=DateTimeOffset.UtcNow;
}
