using System.Text;
using System.Text.Json;
using WebApi.Contracts.Comparisons;
using WebApi.Contracts.Common;
namespace WebApi.Infrastructure.Comparisons;
public sealed record ComparisonExport(byte[] Bytes,string ContentType,string FileName);
public static class ComparisonExporter
{
    public static ComparisonExport Export(VersionComparisonView view,string format)
    {
        if(format=="json")return new(ContractNormalizer.CanonicalBytes(view),"application/json; charset=utf-8",$"version-comparison-{view.Id}.json");
        if(format!="csv")throw new ApiException(400,"unsupported_comparison_export","仅支持 JSON 或 CSV 导出。");
        var rows=new List<string>{"recordType,engineVersion,fingerprint,coverage,source,operation,pointer,change,risk,reason,before,after"};
        foreach(var f in view.Report.Findings)rows.Add(Row("Finding",view.Report.EngineVersion,view.Report.InputFingerprint,view.Report.Coverage,f.Source,f.Operation,f.Pointer,f.ChangeKind,f.Risk,f.Reason,f.Before?.GetRawText(),f.After?.GetRawText()));
        foreach(var issue in view.Report.CoverageIssues)rows.Add(Row("CoverageIssue",view.Report.EngineVersion,view.Report.InputFingerprint,view.Report.Coverage,issue.Source,null,issue.Pointer,null,"Unknown",issue.Reason,null,null));
        rows.Add(Row("Summary",view.Report.EngineVersion,view.Report.InputFingerprint,view.Report.Coverage,null,null,null,null,null,JsonSerializer.Serialize(view.Report.Counts),null,null));
        return new(Encoding.UTF8.GetBytes(string.Join("\r\n",rows)+"\r\n"),"text/csv; charset=utf-8",$"version-comparison-{view.Id}.csv");
    }
    private static string Row(params string?[] values)=>string.Join(',',values.Select(Cell));
    private static string Cell(string? input)
    {var value=input??"";var first=value.FirstOrDefault(c=>!char.IsWhiteSpace(c)&&!char.IsControl(c));if(first is '=' or '+' or '-' or '@')value="'"+value;return "\""+value.Replace("\"","\"\"",StringComparison.Ordinal)+"\"";}
}
