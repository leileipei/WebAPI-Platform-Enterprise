namespace WebApi.Contracts.OpenApi;
public sealed record ContractLimits(
    int MaxDocumentBytes = 2 * 1024 * 1024,
    int MaxBundleBytes = 4 * 1024 * 1024,
    int MaxResources = 16,
    int MaxDepth = 64,
    int MaxNodes = 50000,
    int MaxOperations = 1000,
    int MaxExampleBytes = 256 * 1024,
    int MaxDiagnostics = 500,
    int MaxComparisonSideBytes = 4 * 1024 * 1024,
    int MaxComparisonPairBytes = 8 * 1024 * 1024,
    int MaxFindings = 5000);
