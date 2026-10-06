namespace WebApi.Infrastructure.Persistence.Entities;
public sealed class ApiVersionRiskReview
{
    public Guid Id {get;set;}=Guid.NewGuid();
    public Guid ComparisonId {get;set;}
    public Guid OrganizationId {get;set;}
    public Guid ProjectId {get;set;}
    public Guid ApiId {get;set;}
    public string InputFingerprint {get;set;}="";
    public string ReportHash {get;set;}="";
    public string Decision {get;set;}="";
    public string? Comment {get;set;}
    public Guid ActorId {get;set;}
    public DateTimeOffset CreatedAt {get;set;}=DateTimeOffset.UtcNow;
}
