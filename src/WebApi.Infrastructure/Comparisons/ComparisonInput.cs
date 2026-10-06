using WebApi.Contracts.Catalog;
namespace WebApi.Infrastructure.Comparisons;
public sealed record ContractVersionInput(VersionDto Version,IReadOnlyList<ParameterDto> Parameters,IReadOnlyList<SchemaDto> Schemas);
public sealed record ComparisonInput(ContractVersionInput From,ContractVersionInput To);
public sealed record ComparisonLimits(int MaxSideBytes=2*1024*1024,int MaxPairBytes=4*1024*1024,int MaxDepth=64,int MaxNodes=50000,int MaxFindings=5000);
