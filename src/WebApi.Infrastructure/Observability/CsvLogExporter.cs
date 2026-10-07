using System.Globalization;using System.Text;using WebApi.Contracts.Observability;
namespace WebApi.Infrastructure.Observability;
public sealed record ExportOutcome(int Rows,bool Truncated,SourceState SourceState=SourceState.Available);
public static class CsvLogExporter
{
    public const int MaximumBytes=8*1024*1024,MaximumRows=10000;
    public static string Header=>"Time,TraceId,API,App,Method,Path,Status,DurationMs,Outcome,RequestId,MaskedIp,Node,Environment,ConfigVersion,DeploymentSequence,Destination,PolicyDecisions,AttemptCount,CacheDisposition,ForwardAttempts\r\n";
    public static byte[] Row(AccessLogDto log)=>Encoding.UTF8.GetBytes(string.Join(',',new string?[]{log.Time.ToString("O"),log.TraceId,log.ApiId?.ToString(),log.ApplicationKey,log.Method,log.PathTemplate,log.Status?.ToString(CultureInfo.InvariantCulture),log.DurationMs.ToString("R",CultureInfo.InvariantCulture),log.Outcome,log.RequestId,log.MaskedIp,log.NodeName,log.EnvironmentId.ToString(),log.ConfigVersion?.ToString(CultureInfo.InvariantCulture),log.DeploymentSequence?.ToString(CultureInfo.InvariantCulture),log.DestinationId?.ToString(),System.Text.Json.JsonSerializer.Serialize(log.PolicyDecisions,WebApi.Contracts.Common.CanonicalJson.Options),log.AttemptCount?.ToString(CultureInfo.InvariantCulture),log.CacheDisposition,log.ForwardAttempts is null?null:System.Text.Json.JsonSerializer.Serialize(log.ForwardAttempts,WebApi.Contracts.Common.CanonicalJson.Options)}.Select(Cell))+"\r\n");
    public static string Cell(string? value){var text=value??"";var leading=text.TrimStart();if(leading.Length>0&&leading[0] is '=' or '+' or '-' or '@'||text.Length>0&&text[0] is '\t' or '\r' or '\n')text="'"+text;return '"'+text.Replace("\"","\"\"")+'"';}
}
