using System.Diagnostics;
using WebApi.Contracts.Policies;
namespace WebApi.Gateway.Observability;

public sealed record RequestTelemetryContext
{
    public Guid EnvironmentId {get;init;}
    public string NodeName {get;init;}="";
    public Guid LogId {get;init;}=Guid.NewGuid();
    public DateTimeOffset Time {get;init;}=DateTimeOffset.UtcNow;
    public Guid? ApiId {get;init;}
    public Guid? ApiVersionId {get;init;}
    public Guid? RouteId {get;init;}
    public Guid? RuntimeClusterId {get;init;}
    public Guid? ClusterId {get;init;}
    public Guid? DestinationId {get;init;}
    public string ApplicationKey {get;init;}="Unknown";
    public long? ConfigVersion {get;init;}
    public long? DeploymentSequence {get;init;}
    public string Method {get;init;}="OTHER";
    public string PathTemplate {get;init;}="[unmatched]";
    public string RequestId {get;init;}="";
    public string TraceId {get;init;}="";
    public string SpanId {get;init;}="";
    public int? Status {get;init;}
    public double DurationSeconds {get;init;}
    public string Outcome {get;init;}="Unknown";
    public string MaskedIp {get;init;}="Unknown";
    public string IpHmac {get;init;}="";
    public IReadOnlyList<PolicyDecisionDto> PolicyDecisions {get;init;}=[];
    public int? AttemptCount {get;init;}
    public int? AttemptNumber {get;init;}
    public string? CacheDisposition {get;init;}
    public IReadOnlyList<ForwardAttemptObservation>? ForwardAttempts {get;init;}
    public bool Success=>Outcome=="Completed"&&Status is >=200 and <400;
}
public sealed class RequestTelemetryState(RequestTelemetryContext context)
{
    public static readonly object Item=new();
    public RequestTelemetryContext Context {get;set;}=context;
    public Activity? ServerActivity {get;set;}
    public static RequestTelemetryState? From(HttpContext context)=>context.Items.TryGetValue(Item,out var value)?value as RequestTelemetryState:null;
}
