using WebApi.Contracts.Catalog;
using WebApi.Contracts.OpenApi;
using System.Text.Json.Serialization;
namespace WebApi.Infrastructure.Comparisons;
public sealed record ComparisonSourceInput(Uri RootUri,IReadOnlyList<ContractSource> Documents,string BundleHash,ContractSourceMetadata Metadata);
public sealed record ContractVersionInput(VersionDto Version,IReadOnlyList<ParameterDto> Parameters,IReadOnlyList<SchemaDto> Schemas,[property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] ComparisonSourceInput? Sources=null);
public sealed record ComparisonInput(ContractVersionInput From,ContractVersionInput To,[property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] string? FormatMode=null,[property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] string? AdapterVersion=null);
public sealed record ComparisonLimits(int MaxSideBytes=4*1024*1024,int MaxPairBytes=8*1024*1024,int MaxDepth=64,int MaxNodes=50000,int MaxFindings=5000);
