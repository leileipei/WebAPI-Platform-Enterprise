using System.Globalization;
using System.Text.RegularExpressions;
using WebApi.Contracts.Alerts;
namespace WebApi.Domain.Alerts;
public static partial class AlertExpressionParser
{
    private static readonly HashSet<string> Metrics=["request_rps","error_5xx_ratio","latency_p95_ms","unhealthy_destinations"];
    [GeneratedRegex(@"\A(?:\+)?(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+)(?:[eE][+-]?[0-9]+)?\z",RegexOptions.CultureInvariant,100)]
    private static partial Regex Number();
    public static AlertRuleExpression Parse(string metric,string expression)
    {
        if(metric is null||!Metrics.Contains(metric)||expression is null||expression.Length>256)throw new ArgumentException("不支持的告警指标或表达式。");
        var tokens=expression.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries);
        if(tokens.Length!=3||tokens[0]!=metric||tokens[1] is not (">" or ">=" or "<" or "<=" or "==" or "!=")||!Number().IsMatch(tokens[2])||!double.TryParse(tokens[2],NumberStyles.Float,CultureInfo.InvariantCulture,out var value)||!double.IsFinite(value)||value<0||(metric=="error_5xx_ratio"&&value>1))throw new ArgumentException("表达式必须为匹配指标、比较操作符和有限非负阈值；比例阈值为0至1。");
        return new(metric,tokens[1],value);
    }
    public static bool? Compare(AlertRuleExpression expression,double? value)
    {
        if(value is null||!double.IsFinite(value.Value))return null;
        return expression.Operator switch {">"=>value>expression.Threshold,">="=>value>=expression.Threshold,"<"=>value<expression.Threshold,"<="=>value<=expression.Threshold,"=="=>value==expression.Threshold,"!="=>value!=expression.Threshold,_=>throw new ArgumentException("不支持的比较操作符。")};
    }
}
public enum EvaluationPhase { Inactive,Pending,Firing,SuppressedUntilRecovery }
public sealed record EvaluationStateSnapshot(EvaluationPhase Phase,DateTimeOffset? PendingSince,DateTimeOffset? LastSuccessAt,bool? LastCondition,DateTimeOffset? SuppressedAt);
public sealed record EvaluationDecision(EvaluationPhase NewPhase,DateTimeOffset? PendingSince,bool? LastCondition,bool CreateEvent,string? ResolveReason);
