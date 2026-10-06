namespace WebApi.Contracts.Comparisons;
public sealed record CompatibilityRuleDescriptor(string Id,string Family,IReadOnlyList<string> Dialects,IReadOnlyList<string> TestIds,string ProofCondition,string UnknownCondition);
public sealed record CompatibilityProofResult(string Risk,IReadOnlyList<ComparisonFinding> Findings,IReadOnlyList<ComparisonCoverageIssue> CoverageIssues,IReadOnlyList<string> AppliedRuleIds)
{
    [System.Text.Json.Serialization.JsonConstructor]
    public CompatibilityProofResult(string risk,IReadOnlyList<ComparisonFinding> findings,IReadOnlyList<ComparisonCoverageIssue> issues):this(risk,findings,issues,[]){}
}
public sealed record SchemaProofLocation(Uri ResourceUri,string Pointer);
