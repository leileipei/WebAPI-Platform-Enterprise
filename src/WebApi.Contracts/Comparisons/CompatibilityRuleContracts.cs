namespace WebApi.Contracts.Comparisons;
public sealed record CompatibilityRuleDescriptor(string Id,string Family,IReadOnlyList<string> Dialects,IReadOnlyList<string> TestIds,string ProofCondition,string UnknownCondition);
public sealed record CompatibilityProofResult
{
    public string Risk {get;init;}
    public IReadOnlyList<ComparisonFinding> Findings {get;init;}
    public IReadOnlyList<ComparisonCoverageIssue> CoverageIssues {get;init;}
    public IReadOnlyList<string> AppliedRuleIds {get;init;}
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public bool? Changed {get;init;}
    [System.Text.Json.Serialization.JsonConstructor]
    public CompatibilityProofResult(string risk,IReadOnlyList<ComparisonFinding> findings,IReadOnlyList<ComparisonCoverageIssue> coverageIssues,IReadOnlyList<string> appliedRuleIds,bool? changed=null)
        =>(Risk,Findings,CoverageIssues,AppliedRuleIds,Changed)=(risk,findings,coverageIssues,appliedRuleIds,changed);
    public CompatibilityProofResult(string risk,IReadOnlyList<ComparisonFinding> findings,IReadOnlyList<ComparisonCoverageIssue> coverageIssues):this(risk,findings,coverageIssues,[]){}
}
public sealed record SchemaProofLocation(Uri ResourceUri,string Pointer);
