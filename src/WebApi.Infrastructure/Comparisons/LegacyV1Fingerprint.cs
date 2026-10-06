namespace WebApi.Infrastructure.Comparisons;
public static class LegacyV1Fingerprint
{
    public const string EngineVersion="compatibility-v1";
    // Keep the original explicit Version projection and normalization in ContractNormalizer.
    // New provenance fields must never enter this historical fingerprint.
    public static string Compute(ComparisonInput input)=>ContractNormalizer.Fingerprint(input,EngineVersion);
}
